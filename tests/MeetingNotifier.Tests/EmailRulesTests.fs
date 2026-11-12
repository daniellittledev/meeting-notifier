module MeetingNotifier.Tests.EmailRulesTests

open System
open Xunit
open FsUnit.Xunit
open MeetingNotifier
open MeetingNotifier.Domain

let private now = DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)

let private rule name from subject : Config.EmailRule =
    { Name = name; From = from; SubjectContains = subject }

let private email id fromName fromAddress subject (received: DateTimeOffset) =
    { Id = EmailId id
      Subject = subject
      FromName = fromName
      FromAddress = fromAddress
      Received = received
      WebLink = None }

let private pr =
    email "e1" "GitHub" "notifications@github.com" "[org/repo] Review requested on #42" now

// ----- matching

[<Fact>]
let ``a rule with no criteria is inactive and never matches`` () =
    let r = rule "Everything" "" "  "
    EmailRules.isActive r |> should be False
    EmailRules.matches r pr |> should be False

[<Fact>]
let ``from matches the address partially and case-insensitively`` () =
    EmailRules.matches (rule "" "GITHUB.com" "") pr |> should be True

[<Fact>]
let ``from also matches the display name`` () =
    EmailRules.matches (rule "" "github" "") pr |> should be True

[<Fact>]
let ``subject is a case-insensitive partial match`` () =
    EmailRules.matches (rule "" "" "review REQUESTED") pr |> should be True
    EmailRules.matches (rule "" "" "merged") pr |> should be False

[<Fact>]
let ``when both criteria are set both must match`` () =
    EmailRules.matches (rule "" "github.com" "review requested") pr |> should be True
    EmailRules.matches (rule "" "gitlab.com" "review requested") pr |> should be False
    EmailRules.matches (rule "" "github.com" "deploy failed") pr |> should be False

[<Fact>]
let ``criteria are trimmed before matching`` () =
    EmailRules.matches (rule "" "  github.com " "") pr |> should be True

[<Fact>]
let ``label prefers the name and falls back to the criteria`` () =
    EmailRules.label (rule "PR reviews" "github.com" "") |> should equal "PR reviews"
    EmailRules.label (rule "" "github.com" "review") |> should equal "github.com · \"review\""

// ----- polling cursor

let private rules = [ rule "PRs" "github.com" "review requested" ]

let private start : EmailRules.EmailState =
    { Since = now - TimeSpan.FromMinutes 60.0; Seen = Map.empty }

let private batch emails : MailBatch =
    { Emails = emails; Truncated = false; NewestReceived = None }

[<Fact>]
let ``computeNew alerts only for matching emails`` () =
    let other = email "e2" "Bob" "bob@example.com" "Lunch?" now
    let alerts, _ = EmailRules.computeNew now rules (batch [ pr; other ]) start
    alerts |> List.map (fun a -> a.Email.Id) |> should equal [ EmailId "e1" ]
    alerts.Head.RuleName |> should equal "PRs"

[<Fact>]
let ``computeNew never alerts the same email twice`` () =
    let _, s1 = EmailRules.computeNew now rules (batch [ pr ]) start
    let alerts, _ = EmailRules.computeNew (now + TimeSpan.FromMinutes 1.0) rules (batch [ pr ]) s1
    alerts |> should be Empty

[<Fact>]
let ``computeNew returns alerts oldest first`` () =
    let older = { pr with Id = EmailId "old"; Received = now - TimeSpan.FromMinutes 5.0 }
    let alerts, _ = EmailRules.computeNew now rules (batch [ pr; older ]) start
    alerts |> List.map (fun a -> a.Email.Id) |> should equal [ EmailId "old"; EmailId "e1" ]

[<Fact>]
let ``cursor trails now by the overlap and never moves backwards`` () =
    let _, s1 = EmailRules.computeNew now rules (batch []) start
    s1.Since |> should equal (now - EmailRules.overlap)
    let _, s2 = EmailRules.computeNew (now - TimeSpan.FromHours 1.0) rules (batch []) s1
    s2.Since |> should equal s1.Since

[<Fact>]
let ``seen entries older than the cursor are pruned`` () =
    let old = { pr with Received = now - TimeSpan.FromMinutes 30.0 }
    let _, s1 = EmailRules.computeNew now rules (batch [ old ]) start
    s1.Seen |> Map.isEmpty |> should be True

[<Fact>]
let ``an email still inside the overlap stays seen`` () =
    let recent = { pr with Received = now - TimeSpan.FromMinutes 5.0 }
    let _, s1 = EmailRules.computeNew now rules (batch [ recent ]) start
    s1.Seen |> Map.containsKey recent.Id |> should be True

[<Fact>]
let ``a truncated batch only advances the cursor to the newest fetched`` () =
    let newest = now - TimeSpan.FromMinutes 50.0
    let b = { Emails = [ { pr with Received = newest } ]; Truncated = true; NewestReceived = Some newest }
    let _, s1 = EmailRules.computeNew now rules b start
    s1.Since |> should equal newest
    // The boundary email stays seen, so re-fetching from `newest` won't re-alert it.
    s1.Seen |> Map.containsKey pr.Id |> should be True

[<Fact>]
let ``a truncated batch with nothing fetched leaves the cursor alone`` () =
    let b = { Emails = []; Truncated = true; NewestReceived = None }
    let _, s1 = EmailRules.computeNew now rules b start
    s1.Since |> should equal start.Since

// ----- folder exclusion

let private folder id name parent : MailFolder =
    { FolderId = id; DisplayName = name; ParentFolderId = parent }

let private tree =
    [ folder "inbox" "Inbox" "root"
      folder "gh" "GitHub" "inbox"
      folder "deleted" "Deleted Items" "root"
      folder "deleted-sub" "Old stuff" "deleted"
      folder "deleted-sub-sub" "Older" "deleted-sub"
      folder "news" "Newsletters" "root"
      folder "news-sub" "Weekly" "news" ]

[<Fact>]
let ``built-in roots exclude their whole subtree`` () =
    EmailRules.excludedFolderIds tree (set [ "deleted" ]) []
    |> should equal (set [ "deleted"; "deleted-sub"; "deleted-sub-sub" ])

[<Fact>]
let ``configured names match case-insensitively and include sub-folders`` () =
    EmailRules.excludedFolderIds tree Set.empty [ " newsLETTERS " ]
    |> should equal (set [ "news"; "news-sub" ])

[<Fact>]
let ``folders outside excluded subtrees are kept`` () =
    let excluded = EmailRules.excludedFolderIds tree (set [ "deleted" ]) [ "Newsletters" ]
    excluded |> Set.contains "inbox" |> should be False
    excluded |> Set.contains "gh" |> should be False

[<Fact>]
let ``a root id missing from the tree is still excluded`` () =
    EmailRules.excludedFolderIds [] (set [ "sent" ]) [] |> should equal (set [ "sent" ])
