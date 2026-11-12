module MeetingNotifier.Tests.EmailStoreTests

open System
open Xunit
open FsUnit.Xunit
open MeetingNotifier
open MeetingNotifier.Domain

let private now = DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)

let private alert id (received: DateTimeOffset) =
    { Email =
        { Id = EmailId id
          Subject = "Review requested " + id
          FromName = "GitHub"
          FromAddress = "notifications@github.com"
          Received = received
          WebLink = Some("https://outlook/" + id) }
      RuleName = "PR reviews" }

[<Fact>]
let ``addPending ignores emails the store already knows`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "a" now ]
        |> EmailStore.addPending [ alert "a" now; alert "b" now ]
    s.Pending |> List.map (fun a -> a.Email.Id) |> should equal [ EmailId "a"; EmailId "b" ]

[<Fact>]
let ``markDone moves a pending email to completed`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "a" now ]
        |> EmailStore.markDone (EmailId "a")
    s.Pending |> should be Empty
    s.Completed |> Map.containsKey (EmailId "a") |> should be True
    EmailStore.knows (EmailId "a") s |> should be True

[<Fact>]
let ``a checked-off email is never re-added`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "a" now ]
        |> EmailStore.markDone (EmailId "a")
        |> EmailStore.addPending [ alert "a" now ]
    s.Pending |> should be Empty

[<Fact>]
let ``markDone on an unknown id is a no-op`` () =
    EmailStore.markDone (EmailId "zz") EmailStore.empty |> should equal EmailStore.empty

[<Fact>]
let ``prune forgets completed emails past the retention window`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "old" (now - TimeSpan.FromDays 40.0); alert "new" now ]
        |> EmailStore.markDone (EmailId "old")
        |> EmailStore.markDone (EmailId "new")
        |> EmailStore.prune now
    s.Completed |> Map.keys |> List.ofSeq |> should equal [ EmailId "new" ]

[<Fact>]
let ``startSince uses the lookback on a first run`` () =
    EmailStore.startSince now 60 EmailStore.empty |> should equal (now - TimeSpan.FromMinutes 60.0)

[<Fact>]
let ``startSince resumes from the saved cursor`` () =
    let saved = now - TimeSpan.FromHours 5.0
    EmailStore.startSince now 60 (EmailStore.withSince saved EmailStore.empty)
    |> should equal saved

[<Fact>]
let ``startSince caps catch-up after a long absence`` () =
    let saved = now - TimeSpan.FromDays 10.0
    EmailStore.startSince now 60 (EmailStore.withSince saved EmailStore.empty)
    |> should equal (now - EmailStore.maxCatchUp)

[<Fact>]
let ``json round-trips the whole store`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "a" now; alert "b" (now - TimeSpan.FromMinutes 3.0) ]
        |> EmailStore.markDone (EmailId "b")
        |> EmailStore.withSince now
    EmailStore.ofJson (EmailStore.toJson s) |> should equal s

[<Fact>]
let ``json round-trips a missing web link`` () =
    let a = alert "a" now
    let s = EmailStore.addPending [ { a with Email = { a.Email with WebLink = None } } ] EmailStore.empty
    EmailStore.ofJson (EmailStore.toJson s) |> should equal s

[<Fact>]
let ``unreadable json yields the empty store`` () =
    EmailStore.ofJson "{ not json" |> should equal EmailStore.empty
    EmailStore.ofJson "null" |> should equal EmailStore.empty
