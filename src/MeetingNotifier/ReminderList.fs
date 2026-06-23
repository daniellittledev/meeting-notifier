namespace MeetingNotifier

open System
open MeetingNotifier.Domain

/// Pure, immutable model of the reminders currently shown in the window.
/// No UI/framework references so it can be unit-tested on the plain
/// net8.0 test TFM (source-linked like Domain/AlertEngine).
module ReminderList =

    type Reminder =
        { Id: MeetingId
          Subject: string
          Start: DateTimeOffset
          JoinUrl: string option
          Subtitle: string }

    type ReminderList = Reminder list

    let empty : ReminderList = []

    let private subtitleOf (m: Meeting) =
        match m.JoinUrl, m.Location with
        | Some _, _ -> "Microsoft Teams Meeting"
        | None, Some loc when loc <> "" -> loc
        | _ -> ""

    let private ofPending (pa: PendingAlert) : Reminder =
        let m = pa.Meeting
        { Id = m.Id
          Subject = m.Subject
          Start = m.Start
          JoinUrl = m.JoinUrl
          Subtitle = subtitleOf m }

    /// Add a reminder, replacing any existing row for the same meeting so
    /// the same meeting firing at multiple thresholds never duplicates.
    let add (pa: PendingAlert) (list: ReminderList) : ReminderList =
        let r = ofPending pa
        (list |> List.filter (fun x -> x.Id <> r.Id)) @ [ r ]

    let remove (id: MeetingId) (list: ReminderList) : ReminderList =
        list |> List.filter (fun x -> x.Id <> id)

    let dismissAll (_: ReminderList) : ReminderList = empty

    let isEmpty (list: ReminderList) = List.isEmpty list

    /// Human-friendly "time until start" for a reminder row, e.g. "in 5 mins".
    /// Partial minutes round up (4m30s reads as "in 5 mins") and a start that
    /// has already passed reads as "now".
    let timeUntilText (now: DateTimeOffset) (start: DateTimeOffset) : string =
        let mins = int (ceil (start - now).TotalMinutes)
        let plural n unit = if n = 1 then sprintf "%d %s" n unit else sprintf "%d %ss" n unit
        if mins <= 0 then "now"
        elif mins < 60 then sprintf "in %s" (plural mins "min")
        else
            let h, m = mins / 60, mins % 60
            if m = 0 then sprintf "in %s" (plural h "hr")
            else sprintf "in %s %s" (plural h "hr") (plural m "min")
