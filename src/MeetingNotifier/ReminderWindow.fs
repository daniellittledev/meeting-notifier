namespace MeetingNotifier

open MeetingNotifier.Domain

/// Single reusable Outlook-styled WPF reminders window. Implemented in
/// Task 4; this stub fixes the public surface so dependents compile.
type ReminderWindow() =
    member _.AddReminder(pa: PendingAlert, onAction: AlertAction -> unit) : unit =
        ignore (pa, onAction)
    member _.RemoveReminder(id: MeetingId) : unit = ignore id
    member _.DismissAll() : unit = ()
    member _.SetKeepOnTop(v: bool) : unit = ignore v
