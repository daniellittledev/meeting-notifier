module MeetingNotifier.Tests.ConfigTests

open Xunit
open FsUnit.Xunit
open MeetingNotifier

let private baseCfg : Config.AppConfig =
    { ClientId = "cid"
      TenantId = "tid"
      FinalReminderMinutes = 2
      AudioDeviceName = "Speakers"
      PollIntervalSeconds = 60
      KeepOnTop = true
      SoundEnabled = true
      SoundOnFinalOnly = false
      Volume = 70
      EmailRules = [||]
      EmailLookbackMinutes = 60
      EmailExcludedFolders = [||] }

[<Fact>]
let ``sanitize defaults a non-positive final reminder`` () =
    let cfg = { baseCfg with FinalReminderMinutes = 0 }
    (Config.sanitize cfg).FinalReminderMinutes |> should equal 2

[<Fact>]
let ``sanitize keeps a positive final reminder`` () =
    let cfg = { baseCfg with FinalReminderMinutes = 5 }
    (Config.sanitize cfg).FinalReminderMinutes |> should equal 5

[<Fact>]
let ``sanitize clamps poll interval to the final-reminder window`` () =
    // Final reminder is 2 min => a 120s window; a 300s poll would step clean
    // over it and never fire, so it must be clamped down.
    let cfg =
        { baseCfg with
            FinalReminderMinutes = 2
            PollIntervalSeconds = 300 }
    (Config.sanitize cfg).PollIntervalSeconds |> should equal 120

[<Fact>]
let ``sanitize keeps a poll interval already within the window`` () =
    let cfg =
        { baseCfg with
            FinalReminderMinutes = 2
            PollIntervalSeconds = 45 }
    (Config.sanitize cfg).PollIntervalSeconds |> should equal 45

[<Fact>]
let ``sanitize replaces a non-positive poll interval with the default`` () =
    // Default poll is 60; the window from a 10-min final reminder is 600s, so
    // 60 stands.
    let cfg =
        { baseCfg with
            FinalReminderMinutes = 10
            PollIntervalSeconds = 0 }
    (Config.sanitize cfg).PollIntervalSeconds |> should equal 60

[<Fact>]
let ``sanitize clamps an over-range volume down to 100`` () =
    let cfg = { baseCfg with Volume = 150 }
    (Config.sanitize cfg).Volume |> should equal 100

[<Fact>]
let ``sanitize clamps a negative volume up to 0`` () =
    let cfg = { baseCfg with Volume = -5 }
    (Config.sanitize cfg).Volume |> should equal 0

[<Fact>]
let ``sanitize leaves an in-range volume unchanged`` () =
    let cfg = { baseCfg with Volume = 40 }
    (Config.sanitize cfg).Volume |> should equal 40

[<Fact>]
let ``sanitize replaces null strings with empty`` () =
    let cfg =
        { baseCfg with
            ClientId = null
            TenantId = null
            AudioDeviceName = null }
    let s = Config.sanitize cfg
    s.ClientId |> should equal ""
    s.TenantId |> should equal ""
    s.AudioDeviceName |> should equal ""

[<Fact>]
let ``sanitize replaces null email rules with an empty array`` () =
    let cfg = { baseCfg with EmailRules = null }
    (Config.sanitize cfg).EmailRules |> should equal ([||] : Config.EmailRule[])

[<Fact>]
let ``sanitize replaces null rule fields with empty strings`` () =
    let rule : Config.EmailRule = { Name = null; From = "github.com"; SubjectContains = null }
    let cfg = { baseCfg with EmailRules = [| rule |] }
    let r = (Config.sanitize cfg).EmailRules.[0]
    r.Name |> should equal ""
    r.From |> should equal "github.com"
    r.SubjectContains |> should equal ""

[<Fact>]
let ``sanitize defaults a missing email lookback`` () =
    let cfg = { baseCfg with EmailLookbackMinutes = 0 }
    (Config.sanitize cfg).EmailLookbackMinutes |> should equal 60

[<Fact>]
let ``sanitize trims excluded folders and drops blanks`` () =
    let cfg = { baseCfg with EmailExcludedFolders = [| " Archive "; ""; null; "News" |] }
    (Config.sanitize cfg).EmailExcludedFolders |> should equal [| "Archive"; "News" |]

[<Fact>]
let ``sanitize replaces null excluded folders`` () =
    let cfg = { baseCfg with EmailExcludedFolders = null }
    (Config.sanitize cfg).EmailExcludedFolders |> should equal ([||] : string[])
