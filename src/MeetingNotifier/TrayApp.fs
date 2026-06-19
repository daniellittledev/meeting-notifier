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

    type TrayContext(cfg: Config.AppConfig,
                     source: GraphClient.ICalendarSource,
                     tokenProvider: Auth.TokenProvider,
                     uiContext: SynchronizationContext) =
        inherit ApplicationContext()

        let mutable state = AlertState.empty
        let mutable lastSnapshot : Meeting list = []
        let stateLock = obj()
        let cts = new CancellationTokenSource()
        let notifyIcon = new NotifyIcon()
        let trayIcon, ownsTrayIcon = loadTrayIcon ()
        let menu = new ContextMenuStrip()
        let upcomingItem = new ToolStripMenuItem("Upcoming meetings")
        let startupItem = new ToolStripMenuItem("Run at startup")
        let settingsItem = new ToolStripMenuItem("Settings")
        let reauthItem = new ToolStripMenuItem("Re-authenticate...")
        let exitItem = new ToolStripMenuItem("Exit")

        let postToUi (f: unit -> unit) =
            uiContext.Post(SendOrPostCallback(fun _ -> f ()), null)

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
                | Ok meetings ->
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

            exitItem.Click.Add(fun _ ->
                cts.Cancel()
                notifyIcon.Visible <- false
                Application.Exit())

            menu.Items.Add(upcomingItem :> ToolStripItem) |> ignore
            menu.Items.Add(new ToolStripSeparator() :> ToolStripItem) |> ignore
            menu.Items.Add(startupItem :> ToolStripItem) |> ignore
            menu.Items.Add(settingsItem :> ToolStripItem) |> ignore
            menu.Items.Add(reauthItem :> ToolStripItem) |> ignore
            menu.Items.Add(new ToolStripSeparator() :> ToolStripItem) |> ignore
            menu.Items.Add(exitItem :> ToolStripItem) |> ignore

            notifyIcon.Icon <- trayIcon
            notifyIcon.Text <- "Meeting Notifier"
            notifyIcon.Visible <- true
            notifyIcon.ContextMenuStrip <- menu
            refreshStartupCheckmark ()

            // Kick off the polling loop.
            Async.Start(pollLoop (), cts.Token)

        override this.Dispose(disposing: bool) =
            if disposing then
                try cts.Cancel() with _ -> ()
                try notifyIcon.Dispose() with _ -> ()
                if ownsTrayIcon then (try trayIcon.Dispose() with _ -> ())
                try menu.Dispose() with _ -> ()
                try cts.Dispose() with _ -> ()
            base.Dispose(disposing)
