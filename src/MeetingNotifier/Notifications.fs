namespace MeetingNotifier

open System
open System.IO
open System.Reflection
open NAudio.CoreAudioApi
open NAudio.Wave
open MeetingNotifier.Domain

/// The "look at me!" surfaces: an audio chime routed to a non-headphone
/// render device, plus the aggregating WPF reminders window (which owns
/// its own taskbar flash).
module Notifications =

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

        /// Extract an embedded `.wav` from resources into a temp file so
        /// NAudio's reader can stream from disk. The temp file name is
        /// stamped with the assembly version so a new build (potentially
        /// shipping a different chime) doesn't keep replaying a stale
        /// extraction left behind by an older version.
        let private embeddedWavPath (fileName: string) =
            let asm = Assembly.GetExecutingAssembly()
            let resourceName =
                asm.GetManifestResourceNames()
                |> Array.tryFind (fun n -> n.EndsWith("." + fileName) || n = fileName)
            match resourceName with
            | None -> None
            | Some name ->
                let version =
                    match asm.GetName().Version with
                    | null -> "0"
                    | v -> string v
                let tempPath =
                    Path.Combine(
                        Path.GetTempPath(),
                        sprintf "meeting-notifier-%s-%s.wav"
                            (Path.GetFileNameWithoutExtension fileName) version)
                if not (File.Exists tempPath) then
                    use rs = asm.GetManifestResourceStream name
                    use fs = File.Create tempPath
                    rs.CopyTo fs
                Some tempPath

        /// Fire-and-forget playback of an embedded wav at the configured
        /// volume (0-100).
        let private playResource (fileName: string) (cfg: Config.AppConfig) =
            System.Threading.Tasks.Task.Run(fun () ->
                try
                    match embeddedWavPath fileName with
                    | None -> ()
                    | Some wav ->
                        match pickDevice cfg.AudioDeviceName with
                        | None -> ()
                        | Some device ->
                            use reader = new AudioFileReader(wav)
                            let v = float32 (max 0 (min 100 cfg.Volume)) / 100.0f
                            reader.Volume <- v
                            use out =
                                new WasapiOut(device, AudioClientShareMode.Shared,
                                              true, 100)
                            out.Init(reader)
                            use waiter = new System.Threading.ManualResetEventSlim(false)
                            out.PlaybackStopped.Add(fun _ -> waiter.Set())
                            out.Play()
                            waiter.Wait(TimeSpan.FromSeconds 10.0) |> ignore
                with _ -> ()) |> ignore

        /// The big two-tone meeting chime.
        let play (cfg: Config.AppConfig) = playResource "alert.wav" cfg

        /// The small, soft ding for email reminders.
        let playSoft (cfg: Config.AppConfig) = playResource "email.wav" cfg

    // ============ Unified fire function ============

    /// Decide whether this alert should chime. Sound must be enabled overall;
    /// when `SoundOnFinalOnly` is set, only the final reminder (the threshold
    /// matching `FinalReminderMinutes`) chimes while earlier ones stay silent.
    /// Pure so the truth table can be unit-tested.
    let shouldChime (cfg: Config.AppConfig) (pa: PendingAlert) : bool =
        cfg.SoundEnabled
        && (not cfg.SoundOnFinalOnly
            || pa.Threshold = AlertThreshold cfg.FinalReminderMinutes)

    /// Fire the alert: chime (when `shouldChime`) on a worker thread, and a
    /// row added to the singleton reminders window (which shows + flashes).
    /// Must be called on the UI thread because it touches the window.
    let fireAlert
        (cfg: Config.AppConfig)
        (window: ReminderWindow)
        (pa: PendingAlert)
        (onAction: AlertAction -> unit)
        =
        if shouldChime cfg pa then Audio.play cfg
        window.AddReminder(pa, onAction)

    /// Surface an email reminder: a soft ding (when sound is on and `chime`
    /// is set, so a batch dings once) and a row in the reminders window.
    /// Must be called on the UI thread.
    let fireEmailAlert
        (cfg: Config.AppConfig)
        (window: ReminderWindow)
        (chime: bool)
        (ea: EmailAlert)
        (onAction: EmailAction -> unit)
        =
        if chime && cfg.SoundEnabled then Audio.playSoft cfg
        window.AddEmail(ea, onAction)
