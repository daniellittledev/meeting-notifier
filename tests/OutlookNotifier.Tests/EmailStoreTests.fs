module OutlookNotifier.Tests.EmailStoreTests

open System
open Xunit
open FsUnit.Xunit
open OutlookNotifier
open OutlookNotifier.Domain

let private now = DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)

let private alert id (received: DateTimeOffset) =
    { Email =
        { Id = EmailId id
          Subject = "Review requested " + id
          FromName = "GitHub"
          FromAddress = "notifications@github.com"
          Recipients = []
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
        |> EmailStore.markDone [ EmailId "a" ]
    s.Pending |> should be Empty
    s.Completed |> Map.containsKey (EmailId "a") |> should be True
    EmailStore.knows (EmailId "a") s |> should be True

[<Fact>]
let ``a checked-off email is never re-added`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "a" now ]
        |> EmailStore.markDone [ EmailId "a" ]
        |> EmailStore.addPending [ alert "a" now ]
    s.Pending |> should be Empty

[<Fact>]
let ``markDone on an unknown id is a no-op`` () =
    EmailStore.markDone [ EmailId "zz" ] EmailStore.empty |> should equal EmailStore.empty

[<Fact>]
let ``prune forgets completed emails past the retention window`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "old" (now - TimeSpan.FromDays 40.0); alert "new" now ]
        |> EmailStore.markDone [ EmailId "old" ]
        |> EmailStore.markDone [ EmailId "new" ]
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

let private rule name from recipient subject : Config.EmailRule =
    { Name = name; From = from; RecipientContains = recipient; SubjectContains = subject }

let private approvals = rule "Approvals" "notifications@github.com" "approval_requested@noreply.github.com" ""
let private reviews = rule "Reviews" "notifications@github.com" "review_requested@noreply.github.com" ""

[<Fact>]
let ``rulesSignature ignores name, order and case`` () =
    let a = EmailStore.rulesSignature [ approvals; reviews ]
    let b = EmailStore.rulesSignature [ { reviews with Name = "Renamed" }
                                        { approvals with From = "Notifications@GitHub.com" } ]
    a |> should equal b

[<Fact>]
let ``rulesSignature changes when a rule is added`` () =
    (EmailStore.rulesSignature [ approvals ] = EmailStore.rulesSignature [ approvals; reviews ])
    |> should be False

[<Fact>]
let ``startSinceForRules uses the lookback on a first run`` () =
    let sign = EmailStore.rulesSignature [ approvals ]
    EmailStore.startSinceForRules now 60 sign EmailStore.empty
    |> should equal (now - TimeSpan.FromMinutes 60.0)

[<Fact>]
let ``startSinceForRules resumes from the saved cursor when the rules are unchanged`` () =
    let sign = EmailStore.rulesSignature [ approvals ]
    let saved =
        EmailStore.empty
        |> EmailStore.withSince (now - TimeSpan.FromMinutes 5.0)
        |> EmailStore.withRulesSignature sign
    EmailStore.startSinceForRules now 60 sign saved
    |> should equal (now - TimeSpan.FromMinutes 5.0)

[<Fact>]
let ``startSinceForRules pulls the cursor back to the lookback when the rules changed`` () =
    let saved =
        EmailStore.empty
        |> EmailStore.withSince (now - TimeSpan.FromMinutes 5.0)
        |> EmailStore.withRulesSignature (EmailStore.rulesSignature [ approvals ])
    EmailStore.startSinceForRules now 60 (EmailStore.rulesSignature [ approvals; reviews ]) saved
    |> should equal (now - TimeSpan.FromMinutes 60.0)

[<Fact>]
let ``startSinceForRules keeps a cursor already older than the lookback`` () =
    let saved =
        EmailStore.empty
        |> EmailStore.withSince (now - TimeSpan.FromHours 5.0)
        |> EmailStore.withRulesSignature (EmailStore.rulesSignature [ approvals ])
    EmailStore.startSinceForRules now 60 (EmailStore.rulesSignature [ approvals; reviews ]) saved
    |> should equal (now - TimeSpan.FromHours 5.0)

[<Fact>]
let ``json round-trips the rules signature`` () =
    let s =
        EmailStore.empty
        |> EmailStore.withSince now
        |> EmailStore.withRulesSignature (EmailStore.rulesSignature [ approvals ])
    EmailStore.ofJson (EmailStore.toJson s) |> should equal s

[<Fact>]
let ``needsSave persists a changed rules signature`` () =
    let saved = EmailStore.withSince now EmailStore.empty
    let current = EmailStore.withRulesSignature "sig" saved
    EmailStore.needsSave saved current |> should be True

[<Fact>]
let ``json round-trips the whole store`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "a" now; alert "b" (now - TimeSpan.FromMinutes 3.0) ]
        |> EmailStore.markDone [ EmailId "b" ]
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

[<Fact>]
let ``markDone checks off several emails in one go`` () =
    let s =
        EmailStore.empty
        |> EmailStore.addPending [ alert "a" now; alert "b" now; alert "c" now ]
        |> EmailStore.markDone [ EmailId "a"; EmailId "c" ]
    s.Pending |> List.map (fun a -> a.Email.Id) |> should equal [ EmailId "b" ]
    s.Completed |> Map.count |> should equal 2

[<Fact>]
let ``needsSave is false when nothing changed`` () =
    let s = EmailStore.withSince now EmailStore.empty
    EmailStore.needsSave s s |> should be False

[<Fact>]
let ``needsSave ignores small cursor moves`` () =
    let saved = EmailStore.withSince now EmailStore.empty
    let current = EmailStore.withSince (now + TimeSpan.FromMinutes 1.0) saved
    EmailStore.needsSave saved current |> should be False

[<Fact>]
let ``needsSave persists a cursor that has drifted`` () =
    let saved = EmailStore.withSince now EmailStore.empty
    let current = EmailStore.withSince (now + EmailStore.cursorSaveInterval) saved
    EmailStore.needsSave saved current |> should be True

[<Fact>]
let ``needsSave persists the first cursor`` () =
    EmailStore.needsSave EmailStore.empty (EmailStore.withSince now EmailStore.empty)
    |> should be True

[<Fact>]
let ``needsSave persists pending and completed changes`` () =
    let saved = EmailStore.withSince now EmailStore.empty
    let added = EmailStore.addPending [ alert "a" now ] saved
    EmailStore.needsSave saved added |> should be True
    EmailStore.needsSave added (EmailStore.markDone [ EmailId "a" ] added) |> should be True
