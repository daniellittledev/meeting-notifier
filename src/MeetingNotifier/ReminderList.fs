namespace MeetingNotifier

open System
open MeetingNotifier.Domain

/// Pure, immutable model of the reminders currently shown in the window.
/// No UI/framework references so it can be unit-tested on the plain
/// net8.0 test TFM (source-linked like Domain/AlertEngine).
module ReminderList =

    /// Identifies a row: meetings and emails live in separate id spaces.
    type ReminderKey =
        | MeetingKey of MeetingId
        | EmailKey of EmailId

    type ReminderKind =
        | MeetingReminder of start: DateTimeOffset * joinUrl: string option
        | EmailReminder of received: DateTimeOffset * webLink: string option

    type Reminder =
        { Key: ReminderKey
          Subject: string
          Subtitle: string
          Kind: ReminderKind }

    type ReminderList = Reminder list

    let empty : ReminderList = []

    let private subtitleOf (m: Meeting) =
        match m.JoinUrl, m.Location with
        | Some _, _ -> "Microsoft Teams Meeting"
        | None, Some loc when loc <> "" -> loc
        | _ -> ""

    let private ofPending (pa: PendingAlert) : Reminder =
        let m = pa.Meeting
        { Key = MeetingKey m.Id
          Subject = m.Subject
          Subtitle = subtitleOf m
          Kind = MeetingReminder(m.Start, m.JoinUrl) }

    let private senderOf (e: Email) =
        if String.IsNullOrWhiteSpace e.FromName then e.FromAddress else e.FromName

    let private ofEmailAlert (ea: EmailAlert) : Reminder =
        let e = ea.Email
        { Key = EmailKey e.Id
          Subject = e.Subject
          Subtitle =
            [ senderOf e; ea.RuleName ]
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> String.concat " · "
          Kind = EmailReminder(e.Received, e.WebLink) }

    let private upsert (r: Reminder) (list: ReminderList) : ReminderList =
        (list |> List.filter (fun x -> x.Key <> r.Key)) @ [ r ]

    /// Add a meeting reminder, replacing any existing row for the same
    /// meeting so the same meeting firing at multiple thresholds never
    /// duplicates.
    let add (pa: PendingAlert) (list: ReminderList) : ReminderList =
        upsert (ofPending pa) list

    /// Add an email reminder (de-duplicated by message id).
    let addEmail (ea: EmailAlert) (list: ReminderList) : ReminderList =
        upsert (ofEmailAlert ea) list

    let remove (key: ReminderKey) (list: ReminderList) : ReminderList =
        list |> List.filter (fun x -> x.Key <> key)

    let isMeeting (r: Reminder) =
        match r.Kind with
        | MeetingReminder _ -> true
        | EmailReminder _ -> false

    let meetings (list: ReminderList) : ReminderList = list |> List.filter isMeeting

    let emails (list: ReminderList) : ReminderList = list |> List.filter (isMeeting >> not)

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

    /// Human-friendly "how long ago" for an email row, e.g. "5 mins ago".
    let timeAgoText (now: DateTimeOffset) (received: DateTimeOffset) : string =
        let mins = int (floor (now - received).TotalMinutes)
        let plural n unit = if n = 1 then sprintf "%d %s" n unit else sprintf "%d %ss" n unit
        if mins < 1 then "just now"
        elif mins < 60 then sprintf "%s ago" (plural mins "min")
        elif mins < 24 * 60 then sprintf "%s ago" (plural (mins / 60) "hr")
        else sprintf "%s ago" (plural (mins / (24 * 60)) "day")
