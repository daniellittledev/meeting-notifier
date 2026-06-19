namespace MeetingNotifier

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

/// Loads and persists the on-disk JSON configuration at
/// `%APPDATA%\MeetingNotifier\config.json`.
module Config =

    [<CLIMutable>]
    type AppConfig =
        { ClientId: string
          TenantId: string
          AlertMinutesBefore: int[]
          AudioDeviceName: string
          PollIntervalSeconds: int }

    let appDataDir =
        let root = Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
        Path.Combine(root, "MeetingNotifier")

    let configPath = Path.Combine(appDataDir, "config.json")

    let msalCachePath = Path.Combine(appDataDir, "msal.cache")

    let private defaults : AppConfig =
        { ClientId = ""
          TenantId = ""
          AlertMinutesBefore = [| 10; 2 |]
          AudioDeviceName = ""
          PollIntervalSeconds = 60 }

    let private jsonOptions () =
        let opts =
            JsonSerializerOptions(
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            )
        opts.Converters.Add(JsonFSharpConverter())
        opts

    let private ensureDir () =
        if not (Directory.Exists appDataDir) then
            Directory.CreateDirectory appDataDir |> ignore

    /// Pure normalisation of a freshly-deserialised config: replace nulls,
    /// drop non-positive alert thresholds (which would silently never fire),
    /// and clamp the poll interval so it's never slower than the smallest
    /// alert window — otherwise a tick could step clean over that window and
    /// miss the alert entirely. Kept pure so it can be unit-tested.
    let sanitize (cfg: AppConfig) : AppConfig =
        let thresholds =
            let valid =
                if isNull cfg.AlertMinutesBefore then [||]
                else cfg.AlertMinutesBefore |> Array.filter (fun m -> m > 0)
            if valid.Length = 0 then defaults.AlertMinutesBefore else valid
        let configuredPoll =
            if cfg.PollIntervalSeconds <= 0 then defaults.PollIntervalSeconds
            else cfg.PollIntervalSeconds
        let smallestWindowSecs = (Array.min thresholds) * 60
        let poll = max 1 (min configuredPoll smallestWindowSecs)
        { cfg with
            AlertMinutesBefore = thresholds
            PollIntervalSeconds = poll
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
