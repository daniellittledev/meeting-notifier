module OutlookNotifier.Tests.EmailRulesTests

open System
open Xunit
open FsUnit.Xunit
open OutlookNotifier
open OutlookNotifier.Domain

let private now = DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)

let private rule name from subject : Config.EmailRule =
    { Name = name; From = from; RecipientContains = ""; SubjectContains = subject
      SubjectExcludesPrefix = "" }

let private recipientRule recipient : Config.EmailRule =
    { Name = ""; From = ""; RecipientContains = recipient; SubjectContains = ""
      SubjectExcludesPrefix = "" }

let private email id fromName fromAddress subject (received: DateTimeOffset) =
    { Id = EmailId id
      Subject = subject
      FromName = fromName
      FromAddress = fromAddress
      Recipients = []
      Received = received
      WebLink = None }

// A GitHub Actions deployment-approval email: the reason address that marks it
// as "your approval is requested" rides in Cc, not From.
let private deployApproval =
    { email "e1" "GitHub" "notifications@github.com" "Deployment review in org/repo" now with
        Recipients = [ "org/repo"; "repo@noreply.github.com"
                       "Approval requested"; "approval_requested@noreply.github.com" ] }

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
let ``recipientContains makes a rule active`` () =
    EmailRules.isActive (recipientRule "approval_requested@noreply.github.com") |> should be True

[<Fact>]
let ``recipientContains matches a cc address partially and case-insensitively`` () =
    EmailRules.matches (recipientRule "APPROVAL_REQUESTED@noreply") deployApproval |> should be True

[<Fact>]
let ``recipientContains also matches a recipient display name`` () =
    EmailRules.matches (recipientRule "approval requested") deployApproval |> should be True

[<Fact>]
let ``recipientContains does not match when no recipient contains it`` () =
    // `pr` has no recipients, and its From is notifications@github.com.
    EmailRules.matches (recipientRule "approval_requested") pr |> should be False

[<Fact>]
let ``recipient and subject together both must match`` () =
    let r subject : Config.EmailRule =
        { Name = ""; From = ""; RecipientContains = "approval_requested"; SubjectContains = subject
          SubjectExcludesPrefix = "" }
    EmailRules.matches (r "deployment review") deployApproval |> should be True
    EmailRules.matches (r "merged") deployApproval |> should be False

[<Fact>]
let ``label prefers the name and falls back to the criteria`` () =
    EmailRules.label (rule "PR reviews" "github.com" "") |> should equal "PR reviews"
    EmailRules.label (rule "" "github.com" "review") |> should equal "github.com · \"review\""
    EmailRules.label (recipientRule "approval_requested@noreply.github.com")
    |> should equal "to/cc approval_requested@noreply.github.com"

// ----- multi-value From and subject exclusion

// Both authors send as notifications@github.com, so only the display name
// tells them apart.
let private fromTom =
    email "t1" "Tom Dullemond" "notifications@github.com" "[org/repo] New PR #7" now

let private fromAdam =
    email "a1" "Adam Grande" "notifications@github.com" "[org/repo] New PR #8" now

let private fromReply =
    email "r1" "Tom Dullemond" "notifications@github.com" "Re: [org/repo] New PR #7" now

[<Fact>]
let ``from matches any one of a comma-separated list of senders`` () =
    let r = rule "" "Tom Dullemond, Adam Grande" ""
    EmailRules.matches r fromTom |> should be True
    EmailRules.matches r fromAdam |> should be True

[<Fact>]
let ``a multi-value from does not match a sender outside the list`` () =
    let r = rule "" "Tom Dullemond, Adam Grande" ""
    let other = email "o1" "Someone Else" "notifications@github.com" "hi" now
    EmailRules.matches r other |> should be False

[<Fact>]
let ``a from of only commas is inactive and matches nothing`` () =
    let r = rule "" " , " ""
    EmailRules.isActive r |> should be False
    EmailRules.matches r fromTom |> should be False

[<Fact>]
let ``subjectExcludesPrefix drops a subject starting with a listed prefix`` () =
    let r = { rule "" "notifications@github.com" "" with SubjectExcludesPrefix = "Re:, Fwd:" }
    EmailRules.matches r fromTom |> should be True
    EmailRules.matches r fromReply |> should be False
    let fwd = email "f1" "Tom Dullemond" "notifications@github.com" "Fwd: heads up" now
    EmailRules.matches r fwd |> should be False

[<Fact>]
let ``subjectExcludesPrefix is a prefix, not a substring`` () =
    // "Core: sprint" contains "re:" but does not start with it.
    let r = { rule "" "notifications@github.com" "" with SubjectExcludesPrefix = "Re:" }
    let core = email "c1" "GitHub" "notifications@github.com" "Core: sprint planning" now
    EmailRules.matches r core |> should be True

[<Fact>]
let ``subjectExcludesPrefix is case-insensitive and ignores leading space`` () =
    let r = { rule "" "notifications@github.com" "" with SubjectExcludesPrefix = "re:" }
    let reply = email "r2" "GitHub" "notifications@github.com" "  RE: hello" now
    EmailRules.matches r reply |> should be False

[<Fact>]
let ``an exclusion alone does not make a rule active`` () =
    let r = { rule "" "" "" with SubjectExcludesPrefix = "Re:" }
    EmailRules.isActive r |> should be False
    EmailRules.matches r fromReply |> should be False

[<Fact>]
let ``label notes the subject exclusion`` () =
    let r = { rule "" "github.com" "" with SubjectExcludesPrefix = "Re:, Fwd:" }
    EmailRules.label r |> should equal "github.com · not Re:, Fwd:"

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

// ----- folder inclusion

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
let ``included names match case-insensitively and trim, exact folder only`` () =
    EmailRules.includedFolderIds tree [ " newsLETTERS " ]
    |> should equal (set [ "news" ])

[<Fact>]
let ``a sub-folder is only included when named in its own right`` () =
    EmailRules.includedFolderIds tree [ "Newsletters"; "Weekly" ]
    |> should equal (set [ "news"; "news-sub" ])

[<Fact>]
let ``several names resolve to their folder ids`` () =
    EmailRules.includedFolderIds tree [ "GitHub"; "Newsletters" ]
    |> should equal (set [ "gh"; "news" ])

[<Fact>]
let ``no configured names includes nothing beyond the inbox`` () =
    EmailRules.includedFolderIds tree [] |> should equal (Set.empty: Set<string>)

[<Fact>]
let ``an unknown folder name resolves to nothing`` () =
    EmailRules.includedFolderIds tree [ "Nope" ] |> should equal (Set.empty: Set<string>)

[<Fact>]
let ``naming the Inbox is a no-op since it is always scanned`` () =
    EmailRules.includedFolderIds tree [ "inbox"; "Newsletters" ]
    |> should equal (set [ "news" ])
