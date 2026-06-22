# Reminders Window Redesign — Design

**Date:** 2026-06-22
**Status:** Approved

## Problem

The app currently fires three things per alert (`Notifications.fireAlert`): a native
Windows toast, a taskbar flash, an audio chime, plus a bare WinForms `AlertForm`. The
`AlertForm` is a 360×120 window holding one centered label. Every alert spawns its own
form pinned to the **same** bottom-right corner, so concurrent reminders stack on top of
each other and the second one is clipped/cut off.

We want the reminder UI to look like the Outlook "Reminders" window: a single window that
aggregates multiple meetings in a scrolling list with per-row actions and a shared
"Dismiss all" button. We also want to move the UI off WinForms onto XAML/WPF, which is far
better suited to that visual style.

## Goals

- A single, reusable, Outlook-styled "Reminders" window that aggregates all active alerts.
- Eliminate the stacking/cutoff problem structurally.
- Move the reminder UI to WPF/XAML (hybrid with the existing WinForms tray).
- Follow the Windows light/dark theme.
- Single visible surface: drop the native Windows toast.
- New user controls: keep-on-top toggle, sound on/off toggle, chime volume.

## Non-Goals

- Replacing the tray icon / context menu / polling loop — these stay in WinForms.
- Live theme switching while the window is open (YAGNI).
- A full MVVM / data-binding / `ICommand` layer — event handlers are wired directly in F#.

## Architecture: hybrid WinForms + WPF

Enable `<UseWPF>true</UseWPF>` in `MeetingNotifier.fsproj` (already targets
`net8.0-windows`, so this only adds WPF support alongside the existing
`UseWindowsForms`). There is no clean WPF equivalent for a tray `NotifyIcon`, so the tray
plumbing in `TrayApp.fs` is unchanged.

The reminder UI becomes a single **WPF `Window`**. Its visuals are defined in **XAML**,
embedded as a resource and loaded at runtime via `System.Windows.Markup.XamlReader`. Named
elements (`x:Name`) are located with `FindName` and event handlers attached in F#. This
keeps the markup declarative and themeable while avoiding MVVM ceremony.

WPF windows run fine inside the existing WinForms message loop: the main thread is STA, and
the WinForms pump dispatches WPF messages. The window is created and manipulated only on the
UI thread, which `TrayApp` already routes to via `postToUi`. The window provides its own
brush resources (see Theme), so it does not depend on a WPF `Application` instance.

## Components

### `ReminderList.fs` (new) — pure state

A pure, immutable module modeling the set of active reminders, decoupled from WPF so the
logic is unit-testable:

- A `ReminderRow` record (meeting id, subject, local start time, join url option,
  location/"Microsoft Teams Meeting" subtitle).
- `add : PendingAlert -> ReminderList -> ReminderList` — adds, or updates an existing row
  for the same meeting id (no duplicate rows when multiple thresholds fire).
- `remove : MeetingId -> ReminderList -> ReminderList`.
- `dismissAll : ReminderList -> ReminderList` → empty.
- `isEmpty`.

### `ReminderWindow.fs` (new) — WPF shell

A class owning the single WPF window and rendering the current `ReminderList`:

- Loads the shell XAML (header "Reminders", a `ScrollViewer` containing a `StackPanel` of
  rows, footer with "Dismiss all").
- Renders one row per reminder from a row XAML template: calendar glyph, **bold subject**,
  `h:mm tt · Microsoft Teams Meeting` subtitle, and action buttons Join (🎥), Snooze (⏰),
  Dismiss (✕).
- `addReminder (pa) (callbacks)` — adds/updates a row and shows the window.
- `removeReminder (meetingId)` — removes a row; hides the window when the list becomes empty.
- `dismissAll ()` — clears and hides.
- `setKeepOnTop (bool)` — toggles `Topmost`.
- Positioning: bottom-right of `SystemParameters.WorkArea`, width ~380. Height grows with
  content up to a max (~60% of screen height), after which the inner `ScrollViewer` scrolls.
  Reposition on every add/remove.
- Callbacks are plain F# functions (`onJoin`, `onSnooze`, `onDismiss`) — no `ICommand`.

### Theme

On show, read `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`
`AppsUseLightTheme` (DWORD; `0` = dark, `1`/absent = light). Select a light or dark brush set
(background, surface, text, subtle text, accent, hover) and apply it to the window's
resources. No live switching while open.

### `Notifications.fs` (rewrite)

- Delete the `Toast` module and the `AlertForm` type (single surface; toast removed).
- `Audio.play` gains parameters for **enabled** and **volume** (0–100). When disabled, it
  no-ops. Volume is applied to the NAudio reader (`reader.Volume <- volume / 100f` on
  `AudioFileReader`).
- `fireAlert` becomes: if sound enabled, play the chime at configured volume; add/update the
  reminder row in the singleton window (passed in); flash the window's taskbar button via the
  existing `FlashWindowEx` (using the WPF window's HWND from `WindowInteropHelper`).

### `TrayApp.fs`

- Lazily create and own the singleton `ReminderWindow` on the UI thread.
- Route alert actions to the window:
  - Join → open url, remove row.
  - Snooze → existing `AlertEngine.markSnoozed`, remove row.
  - Dismiss → remove row.
  - Dismiss all → clear window.
- Two new checkable menu items, persisted to config:
  - **"Keep reminders on top"** → `KeepOnTop`; applied via `setKeepOnTop`.
  - **"Play sound"** → `SoundEnabled`.

### `Config.fs`

Add fields with defaults:

- `KeepOnTop : bool` (default `true`) — ON = pinned above all windows; OFF = window pops to
  front and flashes once, then is clickable-behind.
- `SoundEnabled : bool` (default `true`).
- `Volume : int` (default `70`) — config-file value, 0–100.

## Data flow

1. Poll loop computes `PendingAlert`s (unchanged in `AlertEngine`).
2. For each pending alert, `TrayApp` posts to the UI thread and calls `fireAlert`.
3. `fireAlert` plays the chime (if enabled) and calls `window.addReminder`, which updates the
   `ReminderList`, re-renders rows, repositions/shows the window, and flashes the taskbar.
4. User clicks a row action (or "Dismiss all"); the F# callback runs the corresponding
   `AlertAction` handling and updates/removes rows.

## Error handling

- Window creation, XAML load, theme registry read, and flash are wrapped so a failure logs to
  stderr and degrades gracefully (consistent with current `try/with` style). If the window
  cannot be shown, the chime + flash still fire.
- Audio failures remain swallowed as today.

## Testing

- New unit tests for `ReminderList` (add dedup by meeting id, remove, dismiss-all, isEmpty).
- Existing `AlertEngine` tests are unaffected.
- WPF rendering is verified manually (build + run); no UI automation.

## Files touched

- `src/MeetingNotifier/MeetingNotifier.fsproj` — `UseWPF`; embed shell/row XAML.
- `src/MeetingNotifier/ReminderList.fs` — **new**, pure state.
- `src/MeetingNotifier/ReminderWindow.fs` — **new**, WPF shell + theme + positioning.
- `src/MeetingNotifier/Notifications.fs` — rewrite `fireAlert`; delete `Toast`/`AlertForm`;
  add enabled/volume to `Audio.play`.
- `src/MeetingNotifier/TrayApp.fs` — own singleton window; two menu toggles; action routing.
- `src/MeetingNotifier/Config.fs` — `KeepOnTop`, `SoundEnabled`, `Volume`.
- `tests/MeetingNotifier.Tests/` — `ReminderList` tests.
