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
      Volume = 70 }

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
