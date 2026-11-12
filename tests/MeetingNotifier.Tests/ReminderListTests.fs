module MeetingNotifier.Tests.ReminderListTests

open System
open Xunit
open FsUnit.Xunit
open MeetingNotifier
open MeetingNotifier.Domain

let private now = DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)

let private pending id subject joinUrl =
    { Meeting =
        { Id = MeetingId id
          Subject = subject
          Start = now
          End = now + TimeSpan.FromMinutes 30.0
          Location = None
          JoinUrl = joinUrl
          IsCancelled = false
          IsReminderOn = true
          ReminderMinutesBeforeStart = 15 }
      Threshold = AlertThreshold 10 }

[<Fact>]
let ``add appends a reminder`` () =
    let list = ReminderList.empty |> ReminderList.add (pending "m1" "Standup" None)
    list |> List.length |> should equal 1
    list.Head.Subject |> should equal "Standup"

[<Fact>]
let ``add dedupes by meeting id and keeps the latest`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "Standup" None)
        |> ReminderList.add (pending "m1" "Standup (updated)" None)
    list |> List.length |> should equal 1
    list.Head.Subject |> should equal "Standup (updated)"

[<Fact>]
let ``add preserves insertion order across distinct meetings`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "First" None)
        |> ReminderList.add (pending "m2" "Second" None)
    list |> List.map (fun r -> r.Subject) |> should equal [ "First"; "Second" ]

[<Fact>]
let ``subtitle is the Teams label when a join url is present`` () =
    let list = ReminderList.empty |> ReminderList.add (pending "m1" "Sync" (Some "https://teams"))
    list.Head.Subtitle |> should equal "Microsoft Teams Meeting"

[<Fact>]
let ``remove drops the matching reminder`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "First" None)
        |> ReminderList.add (pending "m2" "Second" None)
        |> ReminderList.remove (ReminderList.MeetingKey (MeetingId "m1"))
    list |> List.map (fun r -> r.Subject) |> should equal [ "Second" ]

let private emailAlert id subject =
    { Email =
        { Id = EmailId id
          Subject = subject
          FromName = "GitHub"
          FromAddress = "notifications@github.com"
          Received = now
          WebLink = Some "https://outlook/msg" }
      RuleName = "PR reviews" }

[<Fact>]
let ``addEmail adds an email row with sender and rule in the subtitle`` () =
    let list = ReminderList.empty |> ReminderList.addEmail (emailAlert "e1" "Review requested")
    list.Head.Subject |> should equal "Review requested"
    list.Head.Subtitle |> should equal "GitHub · PR reviews"

[<Fact>]
let ``meeting and email ids never collide`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "x" "Meeting" None)
        |> ReminderList.addEmail (emailAlert "x" "Email")
    list |> List.length |> should equal 2

[<Fact>]
let ``meetings and emails partition the list`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "Meeting" None)
        |> ReminderList.addEmail (emailAlert "e1" "Email")
    ReminderList.meetings list |> List.map (fun r -> r.Subject) |> should equal [ "Meeting" ]
    ReminderList.emails list |> List.map (fun r -> r.Subject) |> should equal [ "Email" ]

[<Fact>]
let ``remove drops an email row by key`` () =
    let list =
        ReminderList.empty
        |> ReminderList.addEmail (emailAlert "e1" "Email")
        |> ReminderList.remove (ReminderList.EmailKey (EmailId "e1"))
    ReminderList.isEmpty list |> should be True

[<Fact>]
let ``emailsByNewest puts the latest email first`` () =
    let older = { emailAlert "e1" "Older" with Email = { (emailAlert "e1" "Older").Email with Received = now - TimeSpan.FromMinutes 10.0 } }
    let list =
        ReminderList.empty
        |> ReminderList.addEmail older
        |> ReminderList.addEmail (emailAlert "e2" "Newer")
    ReminderList.emailsByNewest list |> List.map (fun r -> r.Subject) |> should equal [ "Newer"; "Older" ]

[<Fact>]
let ``meetingsBySoonest puts the next meeting first`` () =
    let later = pending "m1" "Later" None
    let later = { later with Meeting = { later.Meeting with Start = now + TimeSpan.FromHours 1.0 } }
    let list =
        ReminderList.empty
        |> ReminderList.add later
        |> ReminderList.add (pending "m2" "Sooner" None)
    ReminderList.meetingsBySoonest list |> List.map (fun r -> r.Subject) |> should equal [ "Sooner"; "Later" ]

[<Fact>]
let ``timeAgoText renders recent and older emails`` () =
    ReminderList.timeAgoText now (now - TimeSpan.FromSeconds 20.0) |> should equal "just now"
    ReminderList.timeAgoText now (now - TimeSpan.FromMinutes 1.0) |> should equal "1 min ago"
    ReminderList.timeAgoText now (now - TimeSpan.FromMinutes 45.0) |> should equal "45 mins ago"
    ReminderList.timeAgoText now (now - TimeSpan.FromMinutes 130.0) |> should equal "2 hrs ago"

[<Fact>]
let ``dismissAll empties the list`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "First" None)
        |> ReminderList.dismissAll
    ReminderList.isEmpty list |> should be True

[<Fact>]
let ``timeUntilText renders whole minutes`` () =
    ReminderList.timeUntilText now (now + TimeSpan.FromMinutes 5.0)
    |> should equal "in 5 mins"

[<Fact>]
let ``timeUntilText rounds partial minutes up`` () =
    ReminderList.timeUntilText now (now + TimeSpan.FromSeconds 90.0)
    |> should equal "in 2 mins"

[<Fact>]
let ``timeUntilText uses the singular for one minute`` () =
    ReminderList.timeUntilText now (now + TimeSpan.FromMinutes 1.0)
    |> should equal "in 1 min"

[<Fact>]
let ``timeUntilText says now once the meeting has started`` () =
    ReminderList.timeUntilText now (now - TimeSpan.FromMinutes 1.0)
    |> should equal "now"

[<Fact>]
let ``timeUntilText renders hours and minutes`` () =
    ReminderList.timeUntilText now (now + TimeSpan.FromMinutes 65.0)
    |> should equal "in 1 hr 5 mins"
