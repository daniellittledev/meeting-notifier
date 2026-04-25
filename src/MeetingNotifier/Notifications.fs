namespace MeetingNotifier

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Windows.Forms
open NAudio.CoreAudioApi
open NAudio.Wave
open MeetingNotifier.Domain

/// All three flavours of "look at me!": a Windows toast with Join/Snooze
/// actions, a taskbar flash that continues until the window is focused,
/// and an audio chime routed to a non-headphone render device.
module Notifications =

    // ============ Taskbar flashing (FlashWindowEx) ============

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private FLASHWINFO =
        val mutable cbSize: uint32
        val mutable hwnd: IntPtr
        val mutable dwFlags: uint32
        val mutable uCount: uint32
        val mutable dwTimeout: uint32

    [<DllImport("user32.dll")>]
    extern bool private FlashWindowEx(FLASHWINFO& pwfi)

    let private FLASHW_TRAY = 0x00000002u
    let private FLASHW_TIMERNOFG = 0x0000000Cu

    module Taskbar =
        /// Flash the taskbar button for `form` until the user brings it to
        /// the foreground. `form` must have an allocated HWND (i.e. already
        /// shown at least once).
        let flash (form: Form) =
            let mutable info = Unchecked.defaultof<FLASHWINFO>
            info.cbSize <- uint32 (Marshal.SizeOf<FLASHWINFO>())
            info.hwnd <- form.Handle
            info.dwFlags <- FLASHW_TRAY ||| FLASHW_TIMERNOFG
            info.uCount <- UInt32.MaxValue
            info.dwTimeout <- 0u
            FlashWindowEx(&info) |> ignore

    // ============ Audio playback via a chosen render device ============

    module Audio =

        /// Pick the audio device to play the chime through. Preference
        /// order: exact friendly-name match from config, first active
        /// non-headphone/headset device, then the system default.
        let pickDevice (configuredName: string) : MMDevice option =
            try
                use enum' = new MMDeviceEnumerator()
                let all =
                    enum'.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    |> Seq.cast<MMDevice>
                    |> Seq.toList
                let byName =
                    if String.IsNullOrWhiteSpace configuredName then None
                    else
                        all
                        |> List.tryFind (fun d ->
                            String.Equals(
                                d.FriendlyName,
                                configuredName,
                                StringComparison.OrdinalIgnoreCase))
                match byName with
                | Some d -> Some d
                | None ->
                    let isHeadphoneLike (d: MMDevice) =
                        let name = d.FriendlyName.ToLowerInvariant()
                        name.Contains "headphone" || name.Contains "headset"
                    match all |> List.tryFind (fun d -> not (isHeadphoneLike d)) with
                    | Some d -> Some d
                    | None ->
                        try
                            Some (enum'.GetDefaultAudioEndpoint(
                                    DataFlow.Render, Role.Multimedia))
                        with _ -> None
            with _ -> None

        /// Extract `alert.wav` from embedded resources into a temp file so
        /// NAudio's WaveFileReader can stream from disk (simpler than
        /// wrapping the stream in a lifetime-managed provider).
        let private embeddedWavPath () =
            let asm = Assembly.GetExecutingAssembly()
            let resourceName =
                asm.GetManifestResourceNames()
                |> Array.tryFind (fun n -> n.EndsWith "alert.wav")
            match resourceName with
            | None -> None
            | Some name ->
                let tempPath =
                    Path.Combine(Path.GetTempPath(), "meeting-notifier-alert.wav")
                if not (File.Exists tempPath) then
                    use rs = asm.GetManifestResourceStream(name)
                    use fs = File.Create tempPath
                    rs.CopyTo(fs)
                Some tempPath

        /// Fire-and-forget playback. We spin up a WasapiOut bound to the
        /// chosen device and tear it down when playback finishes.
        let play (cfg: Config.AppConfig) =
            System.Threading.Tasks.Task.Run(fun () ->
                try
                    match embeddedWavPath () with
                    | None -> ()
                    | Some wav ->
                        match pickDevice cfg.AudioDeviceName with
                        | None -> ()
                        | Some device ->
                            use reader = new AudioFileReader(wav)
                            use out =
                                new WasapiOut(device, AudioClientShareMode.Shared,
                                              true, 100)
                            out.Init(reader)
                            let waiter = new System.Threading.ManualResetEventSlim(false)
                            out.PlaybackStopped.Add(fun _ -> waiter.Set())
                            out.Play()
                            waiter.Wait(TimeSpan.FromSeconds 10.0) |> ignore
                with _ -> ()) |> ignore

    // ============ Toast notifications (raw Windows.UI.Notifications) ============

    module Toast =
        open Windows.Data.Xml.Dom
        open Windows.Foundation
        open Windows.UI.Notifications

        let private xmlEscape (s: string) =
            if isNull s then ""
            else
                s.Replace("&", "&amp;")
                 .Replace("<", "&lt;")
                 .Replace(">", "&gt;")
                 .Replace("\"", "&quot;")
                 .Replace("'", "&apos;")

        /// Human-readable "in 10 minutes" style hint for the toast body.
        let private leadLabel (AlertThreshold mins) =
            if mins = 1 then "in 1 minute"
            elif mins < 60 then sprintf "in %d minutes" mins
            else sprintf "in %dh %dm" (mins / 60) (mins % 60)

        let private buildXml (pa: PendingAlert) =
            let m = pa.Meeting
            let localStart = m.Start.ToLocalTime().ToString("t") // short time
            let lead = leadLabel pa.Threshold
            let locationLine =
                match m.JoinUrl, m.Location with
                | Some _, _ -> "Teams meeting"
                | None, Some loc when loc <> "" -> xmlEscape loc
                | _ -> ""
            let joinAction =
                match m.JoinUrl with
                | Some url ->
                    let args = $"action=join&url={Uri.EscapeDataString url}"
                    $"""<action content="Join" arguments="{xmlEscape args}" activationType="foreground"/>"""
                | None -> ""
            let snoozeAction =
                let id = xmlEscape (match m.Id with MeetingId s -> s)
                $"""<action content="Snooze 2 min" arguments="action=snooze&amp;id={id}&amp;mins=2" activationType="foreground"/>"""
            $"""
<toast scenario="reminder" duration="long">
  <visual>
    <binding template="ToastGeneric">
      <text>{xmlEscape m.Subject}</text>
      <text>Starts {xmlEscape localStart} ({lead})</text>
      <text>{locationLine}</text>
    </binding>
  </visual>
  <audio src="ms-winsoundevent:Notification.Reminder"/>
  <actions>
    {joinAction}
    {snoozeAction}
    <action content="Dismiss" arguments="action=dismiss" activationType="background"/>
  </actions>
</toast>"""

        let private parseArgs (s: string) =
            if isNull s then Map.empty
            else
                s.Split('&')
                |> Array.choose (fun kv ->
                    let idx = kv.IndexOf '='
                    if idx <= 0 then None
                    else
                        let k = kv.Substring(0, idx)
                        let v = Uri.UnescapeDataString(kv.Substring(idx + 1))
                        Some (k, v))
                |> Map.ofArray

        let private interpret (args: string) (pa: PendingAlert) : AlertAction =
            let map = parseArgs args
            match Map.tryFind "action" map with
            | Some "join" ->
                match Map.tryFind "url" map with
                | Some u -> Join u
                | None ->
                    match pa.Meeting.JoinUrl with
                    | Some u -> Join u
                    | None -> Dismiss
            | Some "snooze" ->
                let mins =
                    match Map.tryFind "mins" map with
                    | Some m ->
                        match Int32.TryParse m with
                        | true, n -> n
                        | _ -> 2
                    | None -> 2
                Snooze mins
            | _ -> Dismiss

        let show (aumid: string) (pa: PendingAlert) (onAction: AlertAction -> unit) =
            try
                let xmlText = buildXml pa
                let doc = XmlDocument()
                doc.LoadXml xmlText
                let toast = ToastNotification(doc)
                // Subscribe with an explicit TypedEventHandler so we don't
                // depend on how F# projects WinRT events.
                toast.add_Activated(
                    TypedEventHandler<ToastNotification, obj>(fun _ evt ->
                        let args =
                            match evt with
                            | :? ToastActivatedEventArgs as a -> a.Arguments
                            | _ -> ""
                        onAction (interpret args pa)))
                toast.add_Dismissed(
                    TypedEventHandler<ToastNotification, ToastDismissedEventArgs>(fun _ _ ->
                        onAction Dismiss))
                ToastNotificationManager.CreateToastNotifier(aumid).Show(toast)
            with ex ->
                eprintfn "Toast failed: %s" ex.Message

    // ============ Flashing alert form ============

    /// A tiny always-on-top form used purely so FlashWindowEx has a taskbar
    /// button to flash. Shown minimized; clicking dismisses.
    type AlertForm(pa: PendingAlert, onDismiss: unit -> unit) as this =
        inherit Form()
        do
            this.Text <- sprintf "Meeting: %s" pa.Meeting.Subject
            this.ShowInTaskbar <- true
            this.TopMost <- true
            this.FormBorderStyle <- FormBorderStyle.FixedToolWindow
            this.StartPosition <- FormStartPosition.Manual
            this.Width <- 360
            this.Height <- 120
            let screen = Screen.PrimaryScreen.WorkingArea
            this.Location <- System.Drawing.Point(screen.Right - this.Width - 16,
                                                  screen.Bottom - this.Height - 16)
            let lbl = new Label()
            lbl.Dock <- DockStyle.Fill
            lbl.TextAlign <- System.Drawing.ContentAlignment.MiddleCenter
            lbl.Font <- new System.Drawing.Font("Segoe UI", 11.0f,
                                                System.Drawing.FontStyle.Bold)
            lbl.Text <-
                sprintf "%s\nStarts %s"
                    pa.Meeting.Subject
                    (pa.Meeting.Start.ToLocalTime().ToString("t"))
            this.Controls.Add lbl
            this.Click.Add(fun _ -> this.Close())
            lbl.Click.Add(fun _ -> this.Close())
            this.FormClosed.Add(fun _ -> onDismiss())

    // ============ Unified fire function ============

    /// Fire all three alerts for a pending alert. Toast and flash run on
    /// the UI thread; audio runs on a worker thread so we don't block the
    /// polling loop or the message pump.
    let fireAlert
        (aumid: string)
        (cfg: Config.AppConfig)
        (pa: PendingAlert)
        (onAction: AlertAction -> unit)
        =
        // Audio is independent and cheap to kick off first.
        Audio.play cfg
        // Toast.
        Toast.show aumid pa onAction
        // Flashing form — show then flash. Close hook is the engine's
        // dismiss signal.
        let form = new AlertForm(pa, fun () -> onAction Dismiss)
        form.Show()
        Taskbar.flash form
