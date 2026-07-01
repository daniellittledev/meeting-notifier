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

        /// Extract an embedded `.wav` into a temp file so NAudio's reader can
        /// stream from disk. `wavFileName` is the resource's file name (e.g.
        /// `alert.wav`, `chime.wav`). The temp file name is stamped with the
        /// assembly version so a new build (potentially shipping a different
        /// sound) doesn't keep replaying a stale extraction left behind by an
        /// older version.
        let private embeddedWavPath (wavFileName: string) =
            let asm = Assembly.GetExecutingAssembly()
            let resourceName =
                asm.GetManifestResourceNames()
                |> Array.tryFind (fun n -> n.EndsWith wavFileName)
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
                        sprintf "meeting-notifier-%s-%s" version wavFileName)
                if not (File.Exists tempPath) then
                    use rs = asm.GetManifestResourceStream name
                    use fs = File.Create tempPath
                    rs.CopyTo fs
                Some tempPath

        /// Fire-and-forget playback of the named embedded `.wav` at the
        /// configured volume (0-100).
        let play (cfg: Config.AppConfig) (wavFileName: string) =
            System.Threading.Tasks.Task.Run(fun () ->
                try
                    match embeddedWavPath wavFileName with
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

    // ============ Unified fire function ============

    /// The friendly short tone played for earlier reminders.
    let [<Literal>] ChimeWav = "chime.wav"
    /// The attention-grabbing sound reserved for the final reminder.
    let [<Literal>] AlertWav = "alert.wav"

    /// Is this the always-on final reminder (the threshold matching
    /// `FinalReminderMinutes`) rather than an earlier meeting-driven one?
    let isFinalReminder (cfg: Config.AppConfig) (pa: PendingAlert) : bool =
        pa.Threshold = AlertThreshold cfg.FinalReminderMinutes

    /// Decide whether this alert should chime. Sound must be enabled overall;
    /// when `SoundOnFinalOnly` is set, only the final reminder chimes while
    /// earlier ones stay silent. Pure so the truth table can be unit-tested.
    let shouldChime (cfg: Config.AppConfig) (pa: PendingAlert) : bool =
        cfg.SoundEnabled && (not cfg.SoundOnFinalOnly || isFinalReminder cfg pa)

    /// Which embedded sound this alert plays: the current attention-grabbing
    /// `alert.wav` for the final reminder, and a friendly short `chime.wav`
    /// for earlier reminders.
    let soundFor (cfg: Config.AppConfig) (pa: PendingAlert) : string =
        if isFinalReminder cfg pa then AlertWav else ChimeWav

    /// Fire the alert: chime (when `shouldChime`) on a worker thread, and a
    /// row added to the singleton reminders window (which shows + flashes).
    /// Must be called on the UI thread because it touches the window.
    let fireAlert
        (cfg: Config.AppConfig)
        (window: ReminderWindow)
        (pa: PendingAlert)
        (onAction: AlertAction -> unit)
        =
        if shouldChime cfg pa then Audio.play cfg (soundFor cfg pa)
        window.AddReminder(pa, onAction)
