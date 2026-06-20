namespace MeetingNotifier

open System
open System.Diagnostics
open System.Drawing
open System.IO
open System.Net.Http
open System.Reflection
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms
open MeetingNotifier.Domain

/// Tray lifecycle: builds the NotifyIcon + context menu, owns the polling
/// loop, threads AlertEngine state immutably, and dispatches fireAlert
/// onto the UI thread when needed.
module TrayApp =

    [<Literal>]
    let private aumid = "MeetingNotifier.App"

    /// Returns the tray icon plus a flag for whether we own it (and so must
    /// dispose it). The shared `SystemIcons.Application` must never be
    /// disposed, so the fallback is reported as not-owned.
    let private loadTrayIcon () : Icon * bool =
        let asm = Assembly.GetExecutingAssembly()
        // Prefer the embedded icon if present, otherwise fall back to the
        // system application icon so the tray isn't empty.
        let onDisk =
            let dir = Path.GetDirectoryName asm.Location
            Path.Combine(dir, "appicon.ico")
        if File.Exists onDisk then new Icon(onDisk), true
        else SystemIcons.Application, false

    /// Whether the background poll is currently reaching the calendar source.
    /// Surfaced as a coloured indicator at the top of the tray menu so the
    /// user can tell at a glance that meetings are actually being tracked.
    type private ConnectionStatus =
        | Connecting
        | Connected
        | Disconnected of reason: string

    /// A small filled circle used as the status glyph in the menu. One
    /// bitmap is created per colour and reused for the tray's lifetime.
    let private makeDot (color: Color) : Bitmap =
        let bmp = new Bitmap(12, 12)
        use g = Graphics.FromImage bmp
        g.SmoothingMode <- Drawing2D.SmoothingMode.AntiAlias
        use brush = new SolidBrush(color)
        g.FillEllipse(brush, 1, 1, 10, 10)
        bmp

    type TrayContext(cfg: Config.AppConfig,
                     source: GraphClient.ICalendarSource,
                     tokenProvider: Auth.TokenProvider,
                     uiContext: SynchronizationContext) =
        inherit ApplicationContext()

        let mutable state = AlertState.empty
        let mutable lastSnapshot : Meeting list = []
        // A downloaded, ready-to-apply update, once a check has found one.
        let mutable preparedUpdate
            : (Velopack.UpdateManager * Velopack.UpdateInfo) option = None
        let stateLock = obj()
        let cts = new CancellationTokenSource()
        let notifyIcon = new NotifyIcon()
        let trayIcon, ownsTrayIcon = loadTrayIcon ()
        let menu = new ContextMenuStrip()
        let statusItem = new ToolStripMenuItem("Connecting...")
        let connectedDot = makeDot (Color.FromArgb(0x2E, 0xCC, 0x71))
        let connectingDot = makeDot (Color.FromArgb(0xF1, 0xC4, 0x0F))
        let disconnectedDot = makeDot (Color.FromArgb(0xE7, 0x4C, 0x3C))
        let upcomingItem = new ToolStripMenuItem("Upcoming meetings")
        let startupItem = new ToolStripMenuItem("Run at startup")
        let settingsItem = new ToolStripMenuItem("Settings")
        let reauthItem = new ToolStripMenuItem("Re-authenticate...")
        let updateItem = new ToolStripMenuItem("Check for updates...")
        let exitItem = new ToolStripMenuItem("Exit")

        let postToUi (f: unit -> unit) =
            uiContext.Post(SendOrPostCallback(fun _ -> f ()), null)

        // Reflect the latest poll outcome in the status indicator and the
        // tray-icon tooltip. Touches WinForms controls, so it must run on the
        // UI thread — background callers marshal through postToUi.
        let setConnection (status: ConnectionStatus) =
            let text, dot, tip =
                match status with
                | Connecting -> "Connecting...", connectingDot, null
                | Connected -> "Connected", connectedDot, null
                | Disconnected reason -> "Not connected", disconnectedDot, reason
            statusItem.Text <- text
            statusItem.Image <- dot
            statusItem.ToolTipText <- tip
            notifyIcon.Text <- sprintf "Meeting Notifier — %s" text

        let snapshotMeetings () =
            lock stateLock (fun () -> lastSnapshot)

        let updateState (f: AlertState -> AlertState) =
            lock stateLock (fun () -> state <- f state)

        let refreshUpcomingMenu () =
            upcomingItem.DropDownItems.Clear()
            let now = DateTimeOffset.Now
            let upcoming =
                snapshotMeetings ()
                |> List.filter (fun m -> not m.IsCancelled && m.End > now)
                |> List.sortBy (fun m -> m.Start)
                |> List.truncate 5
            if List.isEmpty upcoming then
                let none = new ToolStripMenuItem("(none in the next 24h)")
                none.Enabled <- false
                upcomingItem.DropDownItems.Add(none :> ToolStripItem) |> ignore
            else
                for m in upcoming do
                    let label =
                        sprintf "%s — %s"
                            (m.Start.ToLocalTime().ToString("t"))
                            m.Subject
                    let item = new ToolStripMenuItem(label)
                    match m.JoinUrl with
                    | Some url ->
                        item.Click.Add(fun _ ->
                            try
                                Process.Start(
                                    ProcessStartInfo(url, UseShellExecute = true))
                                |> ignore
                            with _ -> ())
                    | None ->
                        item.Enabled <- false
                    upcomingItem.DropDownItems.Add(item :> ToolStripItem) |> ignore

        let refreshStartupCheckmark () =
            try startupItem.Checked <- Startup.isEnabled ()
            with _ -> startupItem.Checked <- false

        let openSettings () =
            try
                // Ensure the file exists before asking the shell to open it.
                if not (File.Exists Config.configPath) then
                    Config.save cfg
                // Config is read once at launch, so changes only take effect
                // on restart. Say so up front rather than leaving the user to
                // wonder why their edit did nothing.
                MessageBox.Show(
                    "Edit and save config.json, then restart Meeting Notifier "
                    + "for the changes to take effect.",
                    "Meeting Notifier — settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information)
                |> ignore
                Process.Start(
                    ProcessStartInfo(Config.configPath, UseShellExecute = true))
                |> ignore
            with ex ->
                MessageBox.Show(sprintf "Could not open config: %s" ex.Message)
                |> ignore

        // Mark the menu so the user can apply an update we've already
        // fetched in the background; the next click applies and restarts.
        let markUpdateReady () =
            updateItem.Text <- "Restart to apply update"

        let applyPreparedUpdate () =
            match preparedUpdate with
            | Some(mgr, info) ->
                try Updater.applyAndRestart mgr info
                with ex -> eprintfn "apply update failed: %s" ex.Message
            | None -> ()

        // Manual "Check for updates" flow: check + download, then offer to
        // restart. `interactive` controls whether we tell the user when
        // there's nothing to do (we stay quiet on the background check).
        let runUpdateCheck (interactive: bool) =
            if interactive then
                updateItem.Enabled <- false
                updateItem.Text <- "Checking for updates..."
            Async.Start(async {
                let! result = Updater.checkForUpdate ()
                postToUi (fun () ->
                    updateItem.Enabled <- true
                    match result with
                    | Some _ ->
                        preparedUpdate <- result
                        markUpdateReady ()
                        if interactive then
                            let restart =
                                MessageBox.Show(
                                    "An update was downloaded. Restart now to "
                                    + "apply it?",
                                    "Meeting Notifier — update",
                                    MessageBoxButtons.YesNo,
                                    MessageBoxIcon.Information)
                            if restart = DialogResult.Yes then applyPreparedUpdate ()
                    | None ->
                        if interactive then
                            updateItem.Text <- "Check for updates..."
                            MessageBox.Show(
                                "You're running the latest version.",
                                "Meeting Notifier — update",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information)
                            |> ignore)
            }, cts.Token)

        let handleAlertAction (pa: PendingAlert) (action: AlertAction) =
            match action with
            | Join url ->
                try
                    Process.Start(ProcessStartInfo(url, UseShellExecute = true))
                    |> ignore
                with _ -> ()
            | Snooze mins ->
                let now = DateTimeOffset.Now
                updateState (AlertEngine.markSnoozed now pa.Meeting.Id mins)
            | Dismiss -> ()

        let runTick () =
            async {
                let now = DateTimeOffset.Now
                let endT = now + TimeSpan.FromHours 24.0
                let! result = source.GetMeetingsInWindow(now, endT)
                match result with
                | Error msg ->
                    eprintfn "calendarView failed: %s" msg
                    postToUi (fun () -> setConnection (Disconnected msg))
                | Ok meetings ->
                    postToUi (fun () -> setConnection Connected)
                    let thresholds =
                        cfg.AlertMinutesBefore
                        |> Array.map AlertThreshold
                        |> Array.toList
                    let snapshot = meetings
                    let pending =
                        lock stateLock (fun () ->
                            lastSnapshot <- snapshot
                            let pruned =
                                AlertEngine.pruneStale now snapshot state
                            let p, s =
                                AlertEngine.computePending
                                    now thresholds snapshot pruned
                            state <- s
                            p)
                    for pa in pending do
                        postToUi (fun () ->
                            Notifications.fireAlert aumid cfg postToUi pa
                                (handleAlertAction pa))
            }

        let rec pollLoop () =
            async {
                let period =
                    TimeSpan.FromSeconds(float cfg.PollIntervalSeconds)
                use timer = new PeriodicTimer(period)
                // Run once immediately so the user doesn't wait a full
                // tick after opening the app.
                try
                    do! runTick ()
                with ex -> eprintfn "tick failed: %s" ex.Message
                let mutable keepGoing = true
                while keepGoing && not cts.IsCancellationRequested do
                    try
                        let! next =
                            timer.WaitForNextTickAsync(cts.Token).AsTask()
                            |> Async.AwaitTask
                        if next then
                            try do! runTick ()
                            with ex -> eprintfn "tick failed: %s" ex.Message
                        else
                            keepGoing <- false
                    with
                    | :? OperationCanceledException -> keepGoing <- false
                    | ex ->
                        eprintfn "poll loop error: %s" ex.Message
            }

        // ----- Construction -----
        do
            // The indicator doubles as a manual refresh: clicking it forces an
            // immediate poll instead of waiting for the next scheduled tick.
            statusItem.Click.Add(fun _ ->
                setConnection Connecting
                Async.Start(async {
                    try do! runTick ()
                    with ex -> eprintfn "manual refresh failed: %s" ex.Message
                }, cts.Token))

            upcomingItem.DropDownOpening.Add(fun _ -> refreshUpcomingMenu ())

            startupItem.Click.Add(fun _ ->
                try
                    Startup.setEnabled (not (Startup.isEnabled ()))
                    refreshStartupCheckmark ()
                with ex ->
                    MessageBox.Show(sprintf "Startup toggle failed: %s" ex.Message)
                    |> ignore)

            settingsItem.Click.Add(fun _ -> openSettings ())

            reauthItem.Click.Add(fun _ ->
                Async.StartImmediate(async {
                    try
                        do! tokenProvider.SignOut()
                        let! _ = tokenProvider.GetAccessToken()
                        return ()
                    with ex ->
                        eprintfn "re-auth failed: %s" ex.Message
                }))

            updateItem.Click.Add(fun _ ->
                match preparedUpdate with
                | Some _ -> applyPreparedUpdate ()
                | None -> runUpdateCheck true)

            exitItem.Click.Add(fun _ ->
                cts.Cancel()
                notifyIcon.Visible <- false
                Application.Exit())

            menu.Items.Add(statusItem :> ToolStripItem) |> ignore
            menu.Items.Add(new ToolStripSeparator() :> ToolStripItem) |> ignore
            menu.Items.Add(upcomingItem :> ToolStripItem) |> ignore
            menu.Items.Add(new ToolStripSeparator() :> ToolStripItem) |> ignore
            menu.Items.Add(startupItem :> ToolStripItem) |> ignore
            menu.Items.Add(settingsItem :> ToolStripItem) |> ignore
            menu.Items.Add(reauthItem :> ToolStripItem) |> ignore
            menu.Items.Add(updateItem :> ToolStripItem) |> ignore
            menu.Items.Add(new ToolStripSeparator() :> ToolStripItem) |> ignore
            menu.Items.Add(exitItem :> ToolStripItem) |> ignore

            notifyIcon.Icon <- trayIcon
            notifyIcon.Visible <- true
            notifyIcon.ContextMenuStrip <- menu
            refreshStartupCheckmark ()
            // Seed the indicator (glyph + tray tooltip) until the first tick
            // resolves it to Connected / Not connected.
            setConnection Connecting

            // Kick off the polling loop.
            Async.Start(pollLoop (), cts.Token)

            // Quietly look for an update in the background; if one is found
            // it's downloaded and the menu flips to "Restart to apply update".
            runUpdateCheck false

        override this.Dispose(disposing: bool) =
            if disposing then
                try cts.Cancel() with _ -> ()
                try notifyIcon.Dispose() with _ -> ()
                if ownsTrayIcon then (try trayIcon.Dispose() with _ -> ())
                try menu.Dispose() with _ -> ()
                try connectedDot.Dispose() with _ -> ()
                try connectingDot.Dispose() with _ -> ()
                try disconnectedDot.Dispose() with _ -> ()
                try cts.Dispose() with _ -> ()
            base.Dispose(disposing)
