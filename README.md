# meeting-notifier

A Windows tray app (F#, .NET 8) that polls Microsoft Graph for upcoming
calendar events and fires **aggressive, hard-to-miss** alerts when a meeting
is imminent. Designed for people who keep missing Teams/Outlook meetings
because toasts get buried and audio plays to headphones they aren't wearing.

Reminders follow each meeting's own Outlook reminder setting: a meeting with
its reminder switched off (e.g. a "Focus time" block) stays silent, while a
meeting with a reminder on alerts at its own lead time and again 2 minutes
before start. When an alert fires, the app surfaces it hard-to-miss:

1. An aggregating **Reminders window** (Outlook-styled) listing every active
   meeting, with per-row Join (Teams) / Snooze / Dismiss actions. Closing it
   only minimizes it; the next reminder pops it back to the front.
2. **Taskbar flashing** — `FlashWindowEx` with `FLASHW_TRAY |
   FLASHW_TIMERNOFG`; flashing continues until you click the window.
3. **Audio chime** routed to a *non-headphone* render device picked via
   NAudio (so speakers get it even if headphones are the default output). You
   can choose to chime on the final 2-minute reminder only.

## Requirements

- Windows 10 1809+ / Windows 11
- .NET 8 SDK (for building) or the published runtime binary
- A Microsoft 365 tenant where you can register (or have IT register) a
  public-client app with:
  - **Delegated** `Calendars.Read` permission (admin-consented if the tenant
    requires it)
  - Redirect URI `http://localhost` (mobile and desktop applications)

## First-run setup

The first time you launch Meeting Notifier (or any time `clientId`/`tenantId`
are missing), a small setup dialog asks for the **Application (client) ID**
and **Directory (tenant) ID** of an Azure AD app registration — no JSON
editing required. Click **Save** and the app continues straight into
sign-in.

To get those two values, register a public-client app in the
[Azure portal](https://portal.azure.com) → *App registrations* → *New
registration*:

- **Supported account types:** accounts in your organizational directory.
- **Redirect URI:** *Mobile and desktop applications* → `http://localhost`.
- **API permissions:** *Microsoft Graph* → *Delegated* → `Calendars.Read`
  (admin-consented if your tenant requires it).

Copy the **Application (client) ID** and **Directory (tenant) ID** from the
app's Overview page into the dialog. (`tenantId` also accepts a domain such
as `contoso.onmicrosoft.com`.)

## Configuration

Config lives at `%APPDATA%\MeetingNotifier\config.json`. The first-run dialog
writes `clientId` / `tenantId` for you; the remaining settings are edited in
this file directly:

```json
{
  "clientId": "00000000-0000-0000-0000-000000000000",
  "tenantId": "contoso.onmicrosoft.com",
  "finalReminderMinutes": 2,
  "audioDeviceName": "",
  "pollIntervalSeconds": 60,
  "soundOnFinalOnly": false
}
```

Reminders follow each meeting's own Outlook reminder setting. A meeting with
its reminder switched off (e.g. a "Focus time" block) raises no alert. A
meeting with a reminder on alerts at its own lead time (the *initial*
reminder) and again `finalReminderMinutes` before start (the always-on
*final* reminder); the two are de-duplicated.

- `finalReminderMinutes` — minutes before start for the always-on final
  reminder, which pops the window to the front, flashes, and chimes
  (default: 2). Non-positive values fall back to the default.
- `soundOnFinalOnly` — when `true`, only the final reminder chimes; earlier
  reminders show silently (default: `false`). Also toggleable from the tray
  menu ("Only chime on final reminder").
- `audioDeviceName` — exact friendly name of the render device to play the
  chime through (e.g. `"Speakers (Realtek(R) Audio)"`). Leave empty to let
  the app auto-pick the first active non-headphone device.
- `pollIntervalSeconds` — Graph calendarView poll cadence. It is
  automatically capped so it's never slower than the final-reminder window
  (e.g. a 2-minute final reminder forces a poll of at most 120s), otherwise a
  tick could step over the window and miss the alert.

Configuration is read once at launch, so **edits only take effect after you
restart Meeting Notifier**. The Settings menu item reminds you of this when
it opens the file.

## Build and run

```powershell
dotnet build MeetingNotifier.sln -c Release
dotnet run --project src/MeetingNotifier
```

First launch opens the system browser for delegated OAuth sign-in. The
MSAL token cache at `%APPDATA%\MeetingNotifier\msal.cache` persists the
refresh token (DPAPI-protected), so subsequent launches are silent.

## Install & auto-update

Released builds are packaged with [Velopack](https://velopack.io/). Grab
`MeetingNotifier-win-Setup.exe` from the
[Releases](https://github.com/daniellittledev/meeting-notifier/releases) page
and run it — it's a per-user install (no admin prompt) and registers the
Start-Menu shortcut needed for toasts.

The app checks GitHub Releases for a newer version on startup and downloads
it in the background; when one is ready the tray menu shows **Restart to
apply update**. You can also trigger a check manually via **Check for
updates…**. Updates are skipped automatically when running an uninstalled
build (e.g. `dotnet run`).

## Releasing

Releases are cut by pushing a `vX.Y.Z` tag; the `release.yml` workflow
publishes a self-contained `win-x64` build, packs it with Velopack, and
uploads the installer + delta packages to a GitHub Release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Tray menu

Right-click the tray icon for the menu. The top item is a **connection
indicator** showing whether the background poll is reaching Microsoft Graph:

- 🟢 **Connected** — the last poll succeeded and meetings are being tracked.
- 🟡 **Connecting...** — startup, or a poll is in flight (no result yet).
- 🔴 **Not connected** — the last poll failed; hover the item for the reason.

The tray-icon tooltip mirrors the same state, and clicking the indicator
forces an immediate poll instead of waiting for the next tick.

## Run at startup

Right-click the tray icon → **Run at startup** (toggle). This writes
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MeetingNotifier`
pointing at the current executable.

## Tests

Pure alert-engine logic is fully unit-tested:

```powershell
dotnet test tests/MeetingNotifier.Tests
```

## Replacing the chime

`src/MeetingNotifier/alert.wav` is a placeholder two-tone chime. Replace
it with anything louder/more distinctive — it's embedded as a resource at
build time, so just drop in a new `alert.wav` and rebuild.

## Architecture at a glance

```
Program.fs          entry point, wiring
  └─ TrayApp.fs           NotifyIcon + polling loop + UI marshalling
       ├─ Config.fs            %APPDATA%\...\config.json load/save
       ├─ Auth.fs              MSAL public client + persistent cache
       ├─ GraphParsing.fs      PURE: Graph JSON → domain Meeting (+ paging)
       ├─ GraphClient.fs       ICalendarSource + Graph implementation
       ├─ AlertEngine.fs       PURE: which alerts fire this tick
       ├─ Notifications.fs     Toast + FlashWindowEx + NAudio
       ├─ AumidRegistration.fs Start-Menu .lnk with AUMID for toasts
       ├─ Startup.fs           HKCU Run key toggle
       ├─ Updater.fs           Velopack self-update against GitHub Releases
       └─ SetupDialog.fs       First-run WinForms dialog for clientId/tenantId
  └─ Domain.fs              shared records and DUs
```

`ICalendarSource` is the seam for a future ICS/Exchange source — swap the
implementation without touching AlertEngine or TrayApp.

## Why a Start-Menu shortcut appears on first run

Unpackaged Win32 apps can't send Windows toast notifications without an
AUMID registered in a Start-Menu `.lnk`. `AumidRegistration.ensure` writes
`%AppData%\Microsoft\Windows\Start Menu\Programs\MeetingNotifier.lnk` on
first run. Delete it and the shortcut will be rewritten on the next launch.
