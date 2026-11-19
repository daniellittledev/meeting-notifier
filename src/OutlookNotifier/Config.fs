namespace OutlookNotifier

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Custom

/// Loads and persists the on-disk JSON configuration at
/// `%APPDATA%\OutlookNotifier\config.json`.
module Config =

    /// A rule that turns a matching incoming email into a check-off reminder.
    /// `From`, `RecipientContains` and `SubjectContains` are case-insensitive
    /// partial matches; every criterion that is set must match. A rule with
    /// none set is ignored.
    [<CLIMutable>]
    type EmailRule =
        { Name: string
          /// Matched against the sender's address and display name, e.g.
          /// "notifications@github.com" or just "github.com".
          From: string
          /// Matched against the To and Cc recipients' addresses and display
          /// names, e.g. GitHub's reason address
          /// "approval_requested@noreply.github.com" (which rides in Cc).
          RecipientContains: string
          SubjectContains: string }

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
          Volume: int
          /// Rules for emails to surface as reminders. Empty disables email
          /// polling entirely (and the Mail.Read permission isn't requested).
          EmailRules: EmailRule[]
          /// How far back to look for matching emails when the app starts,
          /// so reminders that arrived just before a restart aren't lost.
          EmailLookbackMinutes: int
          /// Extra folders (by display name, case-insensitive) to scan for
          /// reminders in addition to the Inbox. Empty means Inbox only. A
          /// sub-folder is only scanned when it's named here in its own right.
          EmailIncludedFolders: string[]
          /// When true, a single left-click on the tray icon opens the
          /// Settings window; otherwise it just shows the context menu.
          TrayClickOpensSettings: bool
          /// Email reminders are silent to-dos by default. When true they get
          /// their own soft chime, independent of the meeting-reminder sound.
          EmailSoundEnabled: bool
          /// Volume (0-100) for the email reminder chime, kept separate from
          /// the meeting-reminder `Volume`.
          EmailVolume: int }

    let appDataDir =
        let root = Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
        Path.Combine(root, "OutlookNotifier")

    let configPath = Path.Combine(appDataDir, "config.json")

    let msalCachePath = Path.Combine(appDataDir, "msal.cache")

    /// The app was previously called "Meeting Notifier" and stored its state
    /// under %APPDATA%\MeetingNotifier. On the first run after the rename,
    /// carry the existing config and cached sign-in across so users keep their
    /// settings and don't have to re-authenticate.
    let private legacyAppDataDir =
        let root = Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
        Path.Combine(root, "MeetingNotifier")

    let private defaults : AppConfig =
        { ClientId = ""
          TenantId = ""
          FinalReminderMinutes = 2
          AudioDeviceName = ""
          PollIntervalSeconds = 60
          KeepOnTop = true
          SoundEnabled = true
          SoundOnFinalOnly = false
          Volume = 70
          EmailRules = [||]
          EmailLookbackMinutes = 60
          EmailIncludedFolders = [||]
          TrayClickOpensSettings = true
          EmailSoundEnabled = false
          EmailVolume = 50 }

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

    /// Copy config and MSAL cache from the pre-rename folder when the new one
    /// has no config yet. Best-effort: a failure just means a fresh first run.
    let private migrateLegacy () =
        try
            let legacyConfig = Path.Combine(legacyAppDataDir, "config.json")
            if not (File.Exists configPath) && File.Exists legacyConfig then
                ensureDir ()
                File.Copy(legacyConfig, configPath, false)
                // A pre-rename config predates the tray-click setting, whose
                // intended default is "on"; a missing JSON bool would load as
                // false, so set it explicitly when the key is absent.
                let raw = File.ReadAllText configPath
                if not (raw.Contains "trayClickOpensSettings") then
                    try
                        let migrated =
                            JsonSerializer.Deserialize<AppConfig>(raw, jsonOptions ())
                        File.WriteAllText(
                            configPath,
                            JsonSerializer.Serialize(
                                { migrated with TrayClickOpensSettings = true },
                                jsonOptions ()))
                    with _ -> ()
                let legacyCache = Path.Combine(legacyAppDataDir, "msal.cache")
                if File.Exists legacyCache && not (File.Exists msalCachePath) then
                    File.Copy(legacyCache, msalCachePath, false)
        with _ -> ()

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
        let orEmpty (s: string) = if isNull s then "" else s
        let rules =
            (if isNull cfg.EmailRules then [||] else cfg.EmailRules)
            |> Array.filter (fun r -> not (isNull (box r)))
            |> Array.map (fun r ->
                { Name = orEmpty r.Name
                  From = orEmpty r.From
                  RecipientContains = orEmpty r.RecipientContains
                  SubjectContains = orEmpty r.SubjectContains })
        // A missing field deserialises to 0, so treat non-positive as "use
        // the default" rather than "no lookback".
        let lookback =
            if cfg.EmailLookbackMinutes <= 0 then defaults.EmailLookbackMinutes
            else cfg.EmailLookbackMinutes
        { cfg with
            FinalReminderMinutes = finalMinutes
            PollIntervalSeconds = poll
            Volume = volume
            EmailVolume = max 0 (min 100 cfg.EmailVolume)
            EmailRules = rules
            EmailLookbackMinutes = lookback
            EmailIncludedFolders =
                (if isNull cfg.EmailIncludedFolders then [||] else cfg.EmailIncludedFolders)
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.map (fun f -> f.Trim())
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
        migrateLegacy ()
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
