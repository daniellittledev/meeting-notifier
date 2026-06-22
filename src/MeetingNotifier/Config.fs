namespace MeetingNotifier

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Custom

/// Loads and persists the on-disk JSON configuration at
/// `%APPDATA%\MeetingNotifier\config.json`.
module Config =

    [<CLIMutable>]
    type AppConfig =
        { ClientId: string
          TenantId: string
          /// Minutes before start for the always-on final reminder. The
          /// initial reminder comes from each meeting's own Outlook setting.
          FinalReminderMinutes: int
          AudioDeviceName: string
          PollIntervalSeconds: int
          KeepOnTop: bool
          SoundEnabled: bool
          /// When true, only the final reminder chimes; earlier reminders
          /// show silently.
          SoundOnFinalOnly: bool
          Volume: int }

    let appDataDir =
        let root = Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
        Path.Combine(root, "MeetingNotifier")

    let configPath = Path.Combine(appDataDir, "config.json")

    let msalCachePath = Path.Combine(appDataDir, "msal.cache")

    let private defaults : AppConfig =
        { ClientId = ""
          TenantId = ""
          FinalReminderMinutes = 2
          AudioDeviceName = ""
          PollIntervalSeconds = 60
          KeepOnTop = true
          SoundEnabled = true
          SoundOnFinalOnly = false
          Volume = 70 }

    let private jsonOptions () =
        let opts =
            JsonSerializerOptions(
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            )
        opts.Converters.Add(OptionJsonConverter())
        opts

    let private ensureDir () =
        if not (Directory.Exists appDataDir) then
            Directory.CreateDirectory appDataDir |> ignore

    /// Pure normalisation of a freshly-deserialised config: replace nulls,
    /// default a non-positive final-reminder window, and clamp the poll
    /// interval so it's never slower than that window — otherwise a tick could
    /// step clean over it and miss the final alert entirely. Kept pure so it
    /// can be unit-tested.
    let sanitize (cfg: AppConfig) : AppConfig =
        let finalMinutes =
            if cfg.FinalReminderMinutes <= 0 then defaults.FinalReminderMinutes
            else cfg.FinalReminderMinutes
        let configuredPoll =
            if cfg.PollIntervalSeconds <= 0 then defaults.PollIntervalSeconds
            else cfg.PollIntervalSeconds
        let finalWindowSecs = finalMinutes * 60
        let poll = max 1 (min configuredPoll finalWindowSecs)
        let volume = max 0 (min 100 cfg.Volume)
        { cfg with
            FinalReminderMinutes = finalMinutes
            PollIntervalSeconds = poll
            Volume = volume
            ClientId = (if isNull cfg.ClientId then "" else cfg.ClientId)
            TenantId = (if isNull cfg.TenantId then "" else cfg.TenantId)
            AudioDeviceName =
                (if isNull cfg.AudioDeviceName then "" else cfg.AudioDeviceName) }

    let save (cfg: AppConfig) : unit =
        ensureDir ()
        let json = JsonSerializer.Serialize(cfg, jsonOptions ())
        File.WriteAllText(configPath, json)

    /// Load the config; if the file is missing or unparseable, write a
    /// default file and return the defaults. Intentionally permissive so
    /// the first-run experience just works.
    let loadOrDefault () : AppConfig =
        ensureDir ()
        if not (File.Exists configPath) then
            save defaults
            defaults
        else
            try
                let json = File.ReadAllText configPath
                let cfg = JsonSerializer.Deserialize<AppConfig>(json, jsonOptions ())
                // Guard against obviously bad values coming out of JSON.
                sanitize cfg
            with _ ->
                defaults
