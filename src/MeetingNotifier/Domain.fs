namespace MeetingNotifier

open System

/// Core domain types shared across the app. No I/O or framework references
/// live in here — keeping this module dependency-free lets AlertEngine stay
/// pure and unit-testable.
module Domain =

    type MeetingId = MeetingId of string

    type Meeting =
        { Id: MeetingId
          Subject: string
          Start: DateTimeOffset
          End: DateTimeOffset
          Location: string option
          JoinUrl: string option
          IsCancelled: bool
          /// Whether the meeting's own Outlook reminder is switched on. When
          /// off (e.g. a "Focus time" block) the meeting raises no alerts.
          IsReminderOn: bool
          /// The meeting's own reminder lead time in minutes, used as the
          /// initial alert threshold alongside the always-on final reminder.
          ReminderMinutesBeforeStart: int }

    /// Minutes-before-meeting at which an alert should fire.
    type AlertThreshold = AlertThreshold of minutesBefore: int

    /// Uniquely identifies one fired alert (same meeting at different
    /// thresholds counts as distinct alerts).
    type AlertKey = { Meeting: MeetingId; Threshold: AlertThreshold }

    /// Alert-engine state: which (meeting, threshold) pairs have already
    /// been fired, which meetings are snoozed until when, and which meetings
    /// the user has dismissed (so no further thresholds fire for them).
    type AlertState =
        { Fired: Set<AlertKey>
          Snoozed: Map<MeetingId, DateTimeOffset>
          Dismissed: Set<MeetingId> }

    module AlertState =
        let empty =
            { Fired = Set.empty
              Snoozed = Map.empty
              Dismissed = Set.empty }

    /// A single alert that needs to be delivered on this tick.
    type PendingAlert =
        { Meeting: Meeting
          Threshold: AlertThreshold }

    /// The user action taken on a toast notification. Snooze carries the
    /// absolute time to re-remind at (the button always targets a couple of
    /// minutes before the meeting start) rather than a duration, so it is
    /// immune to clock skew between the UI click and the next poll tick.
    type AlertAction =
        | Join of url: string
        | Snooze of until: DateTimeOffset
        | Dismiss

    // ----- Email reminders

    /// Graph message id. Requested as an immutable id so it survives the
    /// message being moved between folders.
    type EmailId = EmailId of string

    type Email =
        { Id: EmailId
          Subject: string
          FromName: string
          FromAddress: string
          Received: DateTimeOffset
          WebLink: string option }

    /// An email that matched one of the configured rules and should be
    /// surfaced as a check-off reminder.
    type EmailAlert =
        { Email: Email
          /// Human-readable label of the rule that matched.
          RuleName: string }

    /// The user action taken on email reminder rows. Opening keeps the row
    /// (it's a to-do until checked off); Done checks off one or more rows in
    /// one go (a single Done for "Dismiss all", so it's one store write).
    type EmailAction =
        | OpenEmail of url: string
        | Done of EmailId list

    /// A mailbox folder, used to work out which folders (and their
    /// sub-folders) to skip.
    type MailFolder =
        { FolderId: string
          DisplayName: string
          ParentFolderId: string }

    /// One poll's worth of mail, oldest first. `Truncated` means the fetch
    /// hit its page cap, so `NewestReceived` (the newest message fetched,
    /// before folder filtering) is as far as the cursor may advance.
    type MailBatch =
        { Emails: Email list
          Truncated: bool
          NewestReceived: DateTimeOffset option }
