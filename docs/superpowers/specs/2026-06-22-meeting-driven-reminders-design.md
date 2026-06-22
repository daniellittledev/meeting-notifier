# Meeting-Driven Reminders — Design

**Date:** 2026-06-22
**Status:** Approved

## Problem

Three related changes to how reminders fire and behave:

1. The reminders window can't be minimized — the user wants to push it out of the way but
   have it pop back when the next reminder fires.
2. Sound currently plays for every alert. The user wants the option to chime only for the
   final reminder (2 minutes before start).
3. Per-meeting control: a "Focus time" block should show no reminder at all, while normal
   meetings reminder as usual. Rather than maintain a config rule list, drive this from the
   meeting's own Outlook reminder settings.

## Goals

- Reminders are driven by each meeting's own Outlook reminder settings, not a global list.
  - Reminders off (e.g. "Focus time") → no alerts.
  - Reminders on → an initial alert at the meeting's own reminder time, plus a guaranteed
    final alert 2 minutes before start (deduplicated).
- A config option to chime only on the final (2-minute) reminder.
- The window minimizes (including via the title-bar X) instead of closing, and pops back to
  front when a new reminder fires.

## Non-Goals

- A settings dialog — new fields are edited in `config.json` via the existing Settings menu.
- Per-meeting config rules (subject matching, etc.) — superseded by the meeting's own
  reminder settings.
- Removing the manual Snooze button — it stays as-is.
- Live re-reading of config without restart (unchanged from today).

## Alerting model

Today a global `AlertMinutesBefore = [10; 2]` fires for every meeting. This is replaced by
per-meeting derivation.

Each `Meeting` carries `IsReminderOn` and `ReminderMinutesBeforeStart` from Graph. For a
given meeting and a configured `finalReminderMinutes` (default 2):

- If `IsReminderOn = false` → no thresholds (no alerts).
- If `IsReminderOn = true` → thresholds are the distinct set of
  `{ ReminderMinutesBeforeStart; finalReminderMinutes }`, keeping only values `> 0`.
  - A meeting whose own reminder is already 2 → a single threshold (deduped).
  - A meeting with a 0-minute ("at start") reminder → just the final 2-minute threshold;
    a 0 threshold never fires because `shouldFire` requires lead time `> 0`.

`AlertEngine` changes:

- New pure helper `thresholdsFor : finalMinutes:int -> Meeting -> AlertThreshold list`
  implementing the rule above.
- `computePending` drops its global `thresholds` parameter and instead takes
  `finalReminderMinutes` and derives each meeting's thresholds via `thresholdsFor`.
- `markSnoozed`, `pruneStale`, `shouldFire`, and `AlertState` (Fired keyed by
  `{ Meeting; Threshold }`) are unchanged.

## Sound

Add `SoundOnFinalOnly : bool` (default `false`). The chime decision per pending alert:

```
shouldChime = cfg.SoundEnabled
              && (not cfg.SoundOnFinalOnly
                  || threshold = AlertThreshold cfg.FinalReminderMinutes)
```

Implemented as a pure helper so it's unit-testable, then consumed by `fireAlert`/`TrayApp`.
When `SoundOnFinalOnly` is on, the initial reminder shows silently and only the final
2-minute reminder chimes. The final reminder also pops the window to front and flashes via
the existing `showAndFlash` path. A new checkable tray item "Only chime on final reminder"
toggles this and persists to config, alongside the existing "Play sound" item.

## Minimize instead of close

`ReminderWindow`:

- `ResizeMode <- ResizeMode.CanMinimize` so the window gets a minimize button (no maximize).
- Handle `window.Closing`: when the app is not shutting down, cancel the close and set
  `WindowState <- WindowState.Minimized`. So the title-bar X minimizes; reminders stay
  pending.
- `showAndFlash` restores a minimized window: set `WindowState <- WindowState.Normal` before
  `Activate`/flash, so the next reminder pops it back to front. (Today's
  `if not window.IsVisible then window.Show()` is insufficient because a minimized window is
  still `IsVisible`.)
- Add a `Shutdown()` method (or internal flag set by a new close path) so `TrayApp`'s
  dispose-time `Close()` bypasses the Closing-interceptor and actually closes the window on
  app exit.

## Config

`AppConfig`:

- **Remove** `AlertMinutesBefore : int[]`.
- **Add** `FinalReminderMinutes : int` (default `2`).
- **Add** `SoundOnFinalOnly : bool` (default `false`).

`sanitize`:

- Clamp `FinalReminderMinutes` to `>= 1` (default if `<= 0`).
- Clamp `PollIntervalSeconds` so a tick never steps over the final-reminder window:
  `poll = max 1 (min configuredPoll (FinalReminderMinutes * 60))`. (Initial reminders at a
  meeting's own time remain best-effort if that time is smaller than the poll interval.)
- Existing null-guards and volume clamp unchanged.

## Data plumbing

- `Domain.Meeting` gains `IsReminderOn : bool` and `ReminderMinutesBeforeStart : int`.
- `GraphParsing`: add `isReminderOn` and `reminderMinutesBeforeStart` to `GraphEvent`, to the
  `$select` in `buildUrl`, and to `toDomain`. Default a missing/absent reminder value
  sensibly (reminder off / 0 minutes).
- All other `Meeting` constructions (tests) updated for the new fields.

## Data flow

1. Poll loop fetches meetings (now including reminder settings).
2. `AlertEngine.computePending now finalReminderMinutes meetings state` derives per-meeting
   thresholds and returns the pending alerts + new state.
3. For each pending alert, `TrayApp` posts to the UI thread and calls `fireAlert`, which
   chimes only when `shouldChime` is true and adds/updates the reminder row (showing/restoring
   and flashing the window).
4. User row actions (Join / Snooze / Dismiss / Dismiss all) behave as today.

## Error handling

Unchanged in style: window, XAML, registry, audio, and flash operations stay wrapped so a
failure logs to stderr and degrades gracefully.

## Testing

- `AlertEngineTests`: reminders-off → no alerts; reminders-on → initial + final thresholds;
  dedup when own reminder equals the final minutes; `computePending` over the new signature.
- `GraphParsingTests`: parse `isReminderOn` / `reminderMinutesBeforeStart`, including
  absent/null defaults.
- `ConfigTests`: defaults for the new fields, removal of `AlertMinutesBefore`, and the
  `FinalReminderMinutes`-based poll clamp.
- Sound decision helper: unit tests for the `shouldChime` truth table.
- WPF minimize/restore verified manually (build + run).

## Files touched

- `src/MeetingNotifier/Domain.fs` — `IsReminderOn`, `ReminderMinutesBeforeStart` on `Meeting`.
- `src/MeetingNotifier/GraphParsing.fs` — DTO + `$select` + `toDomain` for reminder fields.
- `src/MeetingNotifier/AlertEngine.fs` — `thresholdsFor`; `computePending` signature change.
- `src/MeetingNotifier/Config.fs` — remove `AlertMinutesBefore`; add `FinalReminderMinutes`,
  `SoundOnFinalOnly`; clamp updates.
- `src/MeetingNotifier/Notifications.fs` — `shouldChime` helper; `fireAlert` honours it.
- `src/MeetingNotifier/ReminderWindow.fs` — `CanMinimize`; Closing→minimize; restore on show;
  `Shutdown()` path.
- `src/MeetingNotifier/TrayApp.fs` — new `computePending` call; "Only chime on final reminder"
  tray toggle; shutdown-close wiring.
- `tests/MeetingNotifier.Tests/` — `AlertEngine`, `GraphParsing`, `Config` updates + sound
  helper tests.
</content>
</invoke>
