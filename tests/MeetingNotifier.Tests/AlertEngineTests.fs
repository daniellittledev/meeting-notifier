module MeetingNotifier.Tests.AlertEngineTests

open System
open Xunit
open FsUnit.Xunit
open MeetingNotifier
open MeetingNotifier.Domain

let private now = DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)

let private meeting id subject minutesFromNow =
    { Id = MeetingId id
      Subject = subject
      Start = now + TimeSpan.FromMinutes(float minutesFromNow)
      End = now + TimeSpan.FromMinutes(float minutesFromNow + 30.0)
      Location = None
      JoinUrl = None
      IsCancelled = false }

let private thresholds = [ AlertThreshold 10; AlertThreshold 2 ]

[<Fact>]
let ``fires 10-minute alert when meeting is exactly 10 minutes out`` () =
    let m = meeting "m1" "Standup" 10
    let pending, newState =
        AlertEngine.computePending now thresholds [m] AlertState.empty

    pending |> List.length |> should equal 1
    let alert = pending.Head
    alert.Threshold |> should equal (AlertThreshold 10)
    newState.Fired |> Set.contains { Meeting = m.Id; Threshold = AlertThreshold 10 }
        |> should be True

[<Fact>]
let ``does not fire the same threshold twice`` () =
    let m = meeting "m1" "Standup" 5
    // First tick: fires 10-min alert (since 5 is within 10)
    let pending1, state1 =
        AlertEngine.computePending now thresholds [m] AlertState.empty
    pending1 |> List.length |> should equal 1

    // Second tick at the same instant: 10-min already fired, no new alert.
    let pending2, _ =
        AlertEngine.computePending now thresholds [m] state1
    pending2 |> should be Empty

[<Fact>]
let ``fires the 2-minute alert separately from the 10-minute alert`` () =
    let m = meeting "m1" "Standup" 9
    // First tick at T-9: only 10-min alert fires.
    let pending1, state1 =
        AlertEngine.computePending now thresholds [m] AlertState.empty
    pending1 |> List.length |> should equal 1
    pending1.Head.Threshold |> should equal (AlertThreshold 10)

    // Later tick at T-1.5: 10-min already fired, 2-min should fire now.
    let later = now + TimeSpan.FromMinutes 7.5
    let pending2, _ =
        AlertEngine.computePending later thresholds [m] state1
    pending2 |> List.length |> should equal 1
    pending2.Head.Threshold |> should equal (AlertThreshold 2)

[<Fact>]
let ``does not fire for cancelled meetings`` () =
    let m = { meeting "m1" "Cancelled" 5 with IsCancelled = true }
    let pending, _ =
        AlertEngine.computePending now thresholds [m] AlertState.empty
    pending |> should be Empty

[<Fact>]
let ``does not fire for meetings that have already started`` () =
    let m = meeting "m1" "In progress" -1
    let pending, _ =
        AlertEngine.computePending now thresholds [m] AlertState.empty
    pending |> should be Empty

[<Fact>]
let ``does not fire for meetings outside the threshold window`` () =
    let m = meeting "m1" "Later" 45
    let pending, _ =
        AlertEngine.computePending now thresholds [m] AlertState.empty
    pending |> should be Empty

[<Fact>]
let ``markSnoozed suppresses alerts until snooze expires`` () =
    let m = meeting "m1" "Busy" 5
    let snoozed = AlertEngine.markSnoozed now m.Id 3 AlertState.empty

    // During the snooze window, no alerts fire even though we're in range.
    let pending1, _ =
        AlertEngine.computePending now thresholds [m] snoozed
    pending1 |> should be Empty

    // After the snooze expires, the next threshold crossing fires again.
    let later = now + TimeSpan.FromMinutes 4.0
    let pending2, _ =
        AlertEngine.computePending later thresholds [m] snoozed
    pending2 |> List.length |> should be (greaterThan 0)

[<Fact>]
let ``pruneStale drops state for meetings no longer upcoming`` () =
    let past = { meeting "old" "Past" -60 with Start = now - TimeSpan.FromMinutes 60.0 }
    let future = meeting "new" "Future" 15
    let dirtyState =
        { Fired = Set.ofList [
            { Meeting = past.Id; Threshold = AlertThreshold 10 }
            { Meeting = future.Id; Threshold = AlertThreshold 10 } ]
          Snoozed = Map.ofList [
            past.Id, now + TimeSpan.FromMinutes 5.0
            future.Id, now + TimeSpan.FromMinutes 5.0 ] }

    let pruned =
        AlertEngine.pruneStale now [past; future] dirtyState

    pruned.Fired |> Set.toList
        |> List.map (fun k -> k.Meeting)
        |> should equal [future.Id]
    pruned.Snoozed |> Map.toList
        |> List.map fst
        |> should equal [future.Id]

[<Fact>]
let ``fires both thresholds on the same tick if both are in range`` () =
    // Meeting is 1 minute out — both the 10- and 2-minute windows include it.
    let m = meeting "m1" "Imminent" 1
    let pending, _ =
        AlertEngine.computePending now thresholds [m] AlertState.empty
    pending |> List.length |> should equal 2
    let kinds = pending |> List.map (fun p -> p.Threshold) |> Set.ofList
    kinds |> should equal (Set.ofList [AlertThreshold 10; AlertThreshold 2])
