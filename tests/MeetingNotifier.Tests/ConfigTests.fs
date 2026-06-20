module MeetingNotifier.Tests.ConfigTests

open Xunit
open FsUnit.Xunit
open MeetingNotifier

let private baseCfg : Config.AppConfig =
    { ClientId = "cid"
      TenantId = "tid"
      AlertMinutesBefore = [| 10; 2 |]
      AudioDeviceName = "Speakers"
      PollIntervalSeconds = 60 }

[<Fact>]
let ``sanitize drops non-positive thresholds`` () =
    let cfg = { baseCfg with AlertMinutesBefore = [| 10; 0; -5; 2 |] }
    (Config.sanitize cfg).AlertMinutesBefore |> should equal [| 10; 2 |]

[<Fact>]
let ``sanitize falls back to defaults when all thresholds invalid`` () =
    let cfg = { baseCfg with AlertMinutesBefore = [| 0; -1 |] }
    (Config.sanitize cfg).AlertMinutesBefore |> should equal [| 10; 2 |]

[<Fact>]
let ``sanitize falls back to defaults when thresholds null`` () =
    let cfg = { baseCfg with AlertMinutesBefore = null }
    (Config.sanitize cfg).AlertMinutesBefore |> should equal [| 10; 2 |]

[<Fact>]
let ``sanitize clamps poll interval to the smallest alert window`` () =
    // Smallest threshold is 2 min => a 120s window; a 300s poll would step
    // clean over it and never fire, so it must be clamped down.
    let cfg =
        { baseCfg with
            AlertMinutesBefore = [| 10; 2 |]
            PollIntervalSeconds = 300 }
    (Config.sanitize cfg).PollIntervalSeconds |> should equal 120

[<Fact>]
let ``sanitize keeps a poll interval already within the window`` () =
    let cfg =
        { baseCfg with
            AlertMinutesBefore = [| 10; 2 |]
            PollIntervalSeconds = 45 }
    (Config.sanitize cfg).PollIntervalSeconds |> should equal 45

[<Fact>]
let ``sanitize replaces a non-positive poll interval with the default`` () =
    // Default poll is 60; smallest window from [|10|] is 600s, so 60 stands.
    let cfg =
        { baseCfg with
            AlertMinutesBefore = [| 10 |]
            PollIntervalSeconds = 0 }
    (Config.sanitize cfg).PollIntervalSeconds |> should equal 60

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
