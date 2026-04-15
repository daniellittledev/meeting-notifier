# meeting-notifier

A Windows tray app (F#, .NET 8) that polls Microsoft Graph for upcoming
calendar events and fires **aggressive, hard-to-miss** alerts when a meeting
is imminent. Designed for people who keep missing Teams/Outlook meetings
because toasts get buried and audio plays to headphones they aren't wearing.

When a meeting is coming up, the app fires three things simultaneously:

1. **Windows toast** with a Join button (Teams) and a Snooze 2 min button.
2. **Taskbar flashing** — `FlashWindowEx` with `FLASHW_TRAY |
   FLASHW_TIMERNOFG` against a small popup window; flashing continues until
   you click it.
3. **Audio chime** routed to a *non-headphone* render device picked via
   NAudio (so speakers get it even if headphones are the default output).

## Requirements

- Windows 10 1809+ / Windows 11
- .NET 8 SDK (for building) or the published runtime binary
- A Microsoft 365 tenant where you can register (or have IT register) a
  public-client app with:
  - **Delegated** `Calendars.Read` permission (admin-consented if the tenant
    requires it)
  - Redirect URI `http://localhost` (mobile and desktop applications)

## Configuration

Config lives at `%APPDATA%\MeetingNotifier\config.json`. On first run the
file is created with defaults; edit it and fill in `clientId` / `tenantId`:

```json
{
  "clientId": "00000000-0000-0000-0000-000000000000",
  "tenantId": "contoso.onmicrosoft.com",
  "alertMinutesBefore": [10, 2],
  "audioDeviceName": "",
  "pollIntervalSeconds": 60
}
```

- `alertMinutesBefore` — array of thresholds. An alert fires when the
  meeting start is that many minutes away (default: 10m and 2m before).
- `audioDeviceName` — exact friendly name of the render device to play the
  chime through (e.g. `"Speakers (Realtek(R) Audio)"`). Leave empty to let
  the app auto-pick the first active non-headphone device.
- `pollIntervalSeconds` — Graph calendarView poll cadence.

## Build and run

```powershell
dotnet build MeetingNotifier.sln -c Release
dotnet run --project src/MeetingNotifier
```

First launch opens the system browser for delegated OAuth sign-in. The
MSAL token cache at `%APPDATA%\MeetingNotifier\msal.cache` persists the
refresh token (DPAPI-protected), so subsequent launches are silent.

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
       ├─ GraphClient.fs       ICalendarSource + Graph implementation
       ├─ AlertEngine.fs       PURE: which alerts fire this tick
       ├─ Notifications.fs     Toast + FlashWindowEx + NAudio
       ├─ AumidRegistration.fs Start-Menu .lnk with AUMID for toasts
       └─ Startup.fs           HKCU Run key toggle
  └─ Domain.fs              shared records and DUs
```

`ICalendarSource` is the seam for a future ICS/Exchange source — swap the
implementation without touching AlertEngine or TrayApp.

## Why a Start-Menu shortcut appears on first run

Unpackaged Win32 apps can't send Windows toast notifications without an
AUMID registered in a Start-Menu `.lnk`. `AumidRegistration.ensure` writes
`%AppData%\Microsoft\Windows\Start Menu\Programs\MeetingNotifier.lnk` on
first run. Delete it and the shortcut will be rewritten on the next launch.
