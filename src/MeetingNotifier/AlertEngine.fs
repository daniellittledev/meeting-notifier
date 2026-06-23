namespace MeetingNotifier

open System
open MeetingNotifier.Domain

/// Pure alert logic. No I/O — given a snapshot of meetings, the current
/// time, and the set of previously fired alerts, decide which alerts need
/// to fire right now and compute the updated state.
///
/// Keeping this pure means we can (a) exhaustively unit-test it and (b)
/// snapshot-test the decision boundary around thresholds without needing
/// fakes for HTTP, toast, or audio.
module AlertEngine =

    /// Is `meeting` currently snoozed according to `state`?
    let private isSnoozed (now: DateTimeOffset) (state: AlertState) (meetingId: MeetingId) =
        match Map.tryFind meetingId state.Snoozed with
        | Some until -> until > now
        | None -> false

    /// Should an alert fire for this (meeting, threshold) at `now`?
    /// We fire when the meeting's start is within the threshold window
    /// ahead of `now` and hasn't already passed. Equality at the boundary
    /// fires (e.g. exactly 10m ahead triggers the 10m alert).
    let private shouldFire
        (now: DateTimeOffset)
        (meeting: Meeting)
        (AlertThreshold mins)
        =
        let leadTime = meeting.Start - now
        let windowStart = TimeSpan.FromMinutes(float mins)
        leadTime <= windowStart && leadTime > TimeSpan.Zero

    /// Derive a meeting's alert thresholds from its own Outlook reminder
    /// settings. A meeting with reminders switched off (e.g. "Focus time")
    /// raises no alerts. Otherwise it alerts at its own reminder lead time
    /// plus an always-on final reminder `finalMinutes` before start. The two
    /// are de-duplicated (a meeting whose own reminder is already the final
    /// value yields a single threshold) and non-positive values are dropped
    /// (a "0 minutes / at start" reminder can never fire a positive window).
    let thresholdsFor (finalMinutes: int) (meeting: Meeting) : AlertThreshold list =
        if not meeting.IsReminderOn then []
        else
            [ meeting.ReminderMinutesBeforeStart; finalMinutes ]
            |> List.filter (fun m -> m > 0)
            |> List.distinct
            |> List.map AlertThreshold

    /// Compute the set of alerts that should fire on this tick, plus the
    /// updated state. Thresholds are derived per meeting from its own reminder
    /// settings and the configured final-reminder minutes. State is threaded
    /// immutably.
    let computePending
        (now: DateTimeOffset)
        (finalMinutes: int)
        (meetings: Meeting list)
        (state: AlertState)
        : PendingAlert list * AlertState
        =
        let mutable fired = state.Fired
        let pending = ResizeArray<PendingAlert>()
        for m in meetings do
            if not m.IsCancelled
               && not (isSnoozed now state m.Id)
               && not (Set.contains m.Id state.Dismissed) then
                for t in thresholdsFor finalMinutes m do
                    let key : AlertKey = { Meeting = m.Id; Threshold = t }
                    if not (Set.contains key fired) && shouldFire now m t then
                        pending.Add { Meeting = m; Threshold = t }
                        fired <- Set.add key fired
        let newState = { state with Fired = fired }
        List.ofSeq pending, newState

    /// Mark a meeting as snoozed until `until`. Also clears any already-fired
    /// entries for this meeting so the next threshold crossing (after the
    /// snooze expires) re-fires.
    let markSnoozedUntil
        (meetingId: MeetingId)
        (until: DateTimeOffset)
        (state: AlertState)
        : AlertState
        =
        let fired' =
            state.Fired |> Set.filter (fun k -> k.Meeting <> meetingId)
        { state with
            Fired = fired'
            Snoozed = Map.add meetingId until state.Snoozed }

    /// Mark a meeting as snoozed for `minutes` from `now`. Thin wrapper over
    /// `markSnoozedUntil` kept for duration-based callers.
    let markSnoozed
        (now: DateTimeOffset)
        (meetingId: MeetingId)
        (minutes: int)
        (state: AlertState)
        : AlertState
        =
        markSnoozedUntil meetingId (now + TimeSpan.FromMinutes(float minutes)) state

    /// Mark a meeting as dismissed so none of its remaining thresholds fire
    /// (no second chime or reminder once the user has dismissed it).
    let markDismissed (meetingId: MeetingId) (state: AlertState) : AlertState =
        { state with Dismissed = Set.add meetingId state.Dismissed }

    /// Drop state for meetings that are no longer upcoming (start <= now)
    /// so the Fired/Snoozed maps don't grow without bound. Call once per
    /// poll tick before `computePending`.
    let pruneStale
        (now: DateTimeOffset)
        (meetings: Meeting list)
        (state: AlertState)
        : AlertState
        =
        let upcomingIds =
            meetings
            |> List.filter (fun m -> m.Start > now)
            |> List.map (fun m -> m.Id)
            |> Set.ofList
        let fired' =
            state.Fired |> Set.filter (fun k -> Set.contains k.Meeting upcomingIds)
        let snoozed' =
            state.Snoozed
            |> Map.filter (fun id _ -> Set.contains id upcomingIds)
        let dismissed' =
            state.Dismissed |> Set.filter (fun id -> Set.contains id upcomingIds)
        { Fired = fired'; Snoozed = snoozed'; Dismissed = dismissed' }
