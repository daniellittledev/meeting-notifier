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
          IsCancelled: bool }

    /// Minutes-before-meeting at which an alert should fire.
    type AlertThreshold = AlertThreshold of minutesBefore: int

    /// Uniquely identifies one fired alert (same meeting at different
    /// thresholds counts as distinct alerts).
    type AlertKey = { Meeting: MeetingId; Threshold: AlertThreshold }

    /// Alert-engine state: which (meeting, threshold) pairs have already
    /// been fired, and which meetings are snoozed until when.
    type AlertState =
        { Fired: Set<AlertKey>
          Snoozed: Map<MeetingId, DateTimeOffset> }

    module AlertState =
        let empty = { Fired = Set.empty; Snoozed = Map.empty }

    /// A single alert that needs to be delivered on this tick.
    type PendingAlert =
        { Meeting: Meeting
          Threshold: AlertThreshold }

    /// The user action taken on a toast notification.
    type AlertAction =
        | Join of url: string
        | Snooze of minutes: int
        | Dismiss
