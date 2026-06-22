module MeetingNotifier.Tests.GraphParsingTests

open System
open Xunit
open FsUnit.Xunit
open MeetingNotifier
open MeetingNotifier.Domain

[<Fact>]
let ``parseGraphDateTime reads a UTC value as a zero-offset DateTimeOffset`` () =
    let dt : GraphParsing.GraphDateTimeTz =
        { dateTime = "2026-04-15T10:00:00.0000000"; timeZone = "UTC" }
    let result = GraphParsing.parseGraphDateTime dt
    result.Offset |> should equal TimeSpan.Zero
    result.UtcDateTime
        |> should equal (DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc))

[<Fact>]
let ``parseGraphDateTime returns MinValue for a null payload`` () =
    let nullDt = Unchecked.defaultof<GraphParsing.GraphDateTimeTz>
    GraphParsing.parseGraphDateTime nullDt |> should equal DateTimeOffset.MinValue

[<Fact>]
let ``toDomain substitutes a placeholder subject and tolerates null sub-objects`` () =
    let ev : GraphParsing.GraphEvent =
        { id = "abc"
          subject = null
          start = { dateTime = "2026-04-15T10:00:00.0000000"; timeZone = "UTC" }
          ``end`` = { dateTime = "2026-04-15T10:30:00.0000000"; timeZone = "UTC" }
          location = Unchecked.defaultof<GraphParsing.GraphLocation>
          onlineMeeting = Unchecked.defaultof<GraphParsing.GraphOnlineMeeting>
          isCancelled = false
          isReminderOn = true
          reminderMinutesBeforeStart = 15 }
    let m = GraphParsing.toDomain ev
    m.Subject |> should equal "(no subject)"
    m.Location |> should equal None
    m.JoinUrl |> should equal None
    m.Id |> should equal (MeetingId "abc")
    m.IsReminderOn |> should be True
    m.ReminderMinutesBeforeStart |> should equal 15

[<Fact>]
let ``parsePage parses meetings and reports no nextLink`` () =
    let body =
        """{ "value": [
              { "id": "m1",
                "subject": "Standup",
                "start": { "dateTime": "2026-04-15T10:00:00.0000000", "timeZone": "UTC" },
                "end": { "dateTime": "2026-04-15T10:30:00.0000000", "timeZone": "UTC" },
                "location": { "displayName": "Room 1" },
                "onlineMeeting": { "joinUrl": "https://teams.example/join" },
                "isReminderOn": true,
                "reminderMinutesBeforeStart": 15,
                "isCancelled": false } ] }"""
    let meetings, next = GraphParsing.parsePage body
    meetings |> List.length |> should equal 1
    next |> should equal None
    let m = meetings.Head
    m.Subject |> should equal "Standup"
    m.Location |> should equal (Some "Room 1")
    m.JoinUrl |> should equal (Some "https://teams.example/join")
    m.IsReminderOn |> should be True
    m.ReminderMinutesBeforeStart |> should equal 15

[<Fact>]
let ``parsePage defaults reminder fields when absent`` () =
    let body =
        """{ "value": [
              { "id": "m1",
                "subject": "No reminder fields",
                "start": { "dateTime": "2026-04-15T10:00:00.0000000", "timeZone": "UTC" },
                "end": { "dateTime": "2026-04-15T10:30:00.0000000", "timeZone": "UTC" },
                "isCancelled": false } ] }"""
    let meetings, _ = GraphParsing.parsePage body
    let m = meetings.Head
    m.IsReminderOn |> should be False
    m.ReminderMinutesBeforeStart |> should equal 0

[<Fact>]
let ``parsePage surfaces the odata nextLink for pagination`` () =
    let body =
        """{ "value": [], "@odata.nextLink": "https://graph.microsoft.com/next-page" }"""
    let meetings, next = GraphParsing.parsePage body
    meetings |> should be Empty
    next |> should equal (Some "https://graph.microsoft.com/next-page")

[<Fact>]
let ``buildUrl emits the UTC window and select/top parameters`` () =
    let startT = DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)
    let endT = DateTimeOffset(2026, 4, 16, 10, 0, 0, TimeSpan.Zero)
    let url = GraphParsing.buildUrl startT endT
    url |> should haveSubstring "startDateTime=2026-04-15T10:00:00Z"
    url |> should haveSubstring "endDateTime=2026-04-16T10:00:00Z"
    url |> should haveSubstring "$top=50"
