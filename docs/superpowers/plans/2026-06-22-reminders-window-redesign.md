# Reminders Window Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-alert WinForms popup with a single Outlook-styled WPF "Reminders" window that aggregates all active meetings, eliminating the stacking/cutoff problem.

**Architecture:** Hybrid WinForms + WPF. The tray icon, menu, and polling loop stay in WinForms. A single reusable WPF `Window` (shell defined in an embedded XAML resource, rows built in F# with direct event handlers) renders the current reminders, follows the Windows light/dark theme, and flashes its own taskbar button. A pure `ReminderList` module holds the list state so it is unit-testable without a UI.

**Tech Stack:** F#, .NET 8 (`net8.0-windows10.0.19041.0`), WPF (`UseWPF`), WinForms (tray), NAudio (chime), xUnit + FsUnit (tests).

## Global Constraints

- Target framework stays `net8.0-windows10.0.19041.0`; the test project stays `net8.0` and source-links only WPF-free files.
- F# compile order matters: every new file must be inserted in `MeetingNotifier.fsproj` at the position stated in its task.
- `ReminderList.fs` must have **no** WPF/WinForms/Windows dependencies (it is source-linked into the `net8.0` test project).
- Functional style: pure functions and immutable data where practical; the reminder list is rebuilt from immutable state on every change.
- Config defaults (verbatim): `KeepOnTop = true`, `SoundEnabled = true`, `Volume = 70` (0–100).
- Snooze duration stays 2 minutes (matches the removed toast).
- No native Windows toast: the WPF window is the single visible surface.
- Commit messages must not wrap lines. End each commit message with the `Co-Authored-By` trailer.
- Build: `dotnet build src/MeetingNotifier/MeetingNotifier.fsproj -c Debug`
- Test: `dotnet test tests/MeetingNotifier.Tests/MeetingNotifier.Tests.fsproj`

---

### Task 1: Config — new settings

**Files:**
- Modify: `src/MeetingNotifier/Config.fs`

**Interfaces:**
- Produces: `Config.AppConfig` record gains `KeepOnTop: bool`, `SoundEnabled: bool`, `Volume: int`.

- [ ] **Step 1: Add the three fields to the record**

In `src/MeetingNotifier/Config.fs`, change the `AppConfig` record (currently lines 13–18) to:

```fsharp
    [<CLIMutable>]
    type AppConfig =
        { ClientId: string
          TenantId: string
          AlertMinutesBefore: int[]
          AudioDeviceName: string
          PollIntervalSeconds: int
          KeepOnTop: bool
          SoundEnabled: bool
          Volume: int }
```

- [ ] **Step 2: Add defaults**

Change the `defaults` value (currently lines 28–33) to:

```fsharp
    let private defaults : AppConfig =
        { ClientId = ""
          TenantId = ""
          AlertMinutesBefore = [| 10; 2 |]
          AudioDeviceName = ""
          PollIntervalSeconds = 60
          KeepOnTop = true
          SoundEnabled = true
          Volume = 70 }
```

- [ ] **Step 3: Clamp Volume on load**

In `loadOrDefault`, inside the `try` branch, add a `volume` binding next to the existing `poll` binding and include it in the returned record. Replace the returned record (currently lines 74–79) with:

```fsharp
                let volume =
                    if cfg.Volume < 0 then 0
                    elif cfg.Volume > 100 then 100
                    else cfg.Volume
                { cfg with
                    AlertMinutesBefore = thresholds
                    PollIntervalSeconds = poll
                    Volume = volume
                    ClientId = (if isNull cfg.ClientId then "" else cfg.ClientId)
                    TenantId = (if isNull cfg.TenantId then "" else cfg.TenantId)
                    AudioDeviceName = (if isNull cfg.AudioDeviceName then "" else cfg.AudioDeviceName) }
```

Note: a config.json written before this change has no `keepOnTop`/`soundEnabled`/`volume` keys. `System.Text.Json` leaves missing value-type members at their default (`false`/`0`). That is acceptable for an existing install (the user can re-enable via the tray menu), and any freshly written config gets the correct defaults. Do not add migration logic — YAGNI.

- [ ] **Step 4: Build**

Run: `dotnet build src/MeetingNotifier/MeetingNotifier.fsproj -c Debug`
Expected: build succeeds (the rest of the app does not yet read the new fields).

- [ ] **Step 5: Commit**

```bash
git add src/MeetingNotifier/Config.fs
git commit -m "Add KeepOnTop, SoundEnabled, Volume config settings

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: ReminderList — pure list state + tests

**Files:**
- Create: `src/MeetingNotifier/ReminderList.fs`
- Modify: `src/MeetingNotifier/MeetingNotifier.fsproj` (compile order)
- Modify: `tests/MeetingNotifier.Tests/MeetingNotifier.Tests.fsproj` (source-link + test file)
- Test: `tests/MeetingNotifier.Tests/ReminderListTests.fs`

**Interfaces:**
- Consumes: `Domain.Meeting`, `Domain.MeetingId`, `Domain.PendingAlert`.
- Produces:
  - `ReminderList.Reminder = { Id: MeetingId; Subject: string; Start: DateTimeOffset; JoinUrl: string option; Subtitle: string }`
  - `ReminderList.ReminderList` (alias for `Reminder list`)
  - `ReminderList.empty : ReminderList`
  - `ReminderList.add : PendingAlert -> ReminderList -> ReminderList` (dedupes by meeting id, appends/replaces, preserves order)
  - `ReminderList.remove : MeetingId -> ReminderList -> ReminderList`
  - `ReminderList.dismissAll : ReminderList -> ReminderList`
  - `ReminderList.isEmpty : ReminderList -> bool`

- [ ] **Step 1: Write the failing tests**

Create `tests/MeetingNotifier.Tests/ReminderListTests.fs`:

```fsharp
module MeetingNotifier.Tests.ReminderListTests

open System
open Xunit
open FsUnit.Xunit
open MeetingNotifier
open MeetingNotifier.Domain

let private now = DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)

let private pending id subject joinUrl =
    { Meeting =
        { Id = MeetingId id
          Subject = subject
          Start = now
          End = now + TimeSpan.FromMinutes 30.0
          Location = None
          JoinUrl = joinUrl
          IsCancelled = false }
      Threshold = AlertThreshold 10 }

[<Fact>]
let ``add appends a reminder`` () =
    let list = ReminderList.empty |> ReminderList.add (pending "m1" "Standup" None)
    list |> List.length |> should equal 1
    list.Head.Subject |> should equal "Standup"

[<Fact>]
let ``add dedupes by meeting id and keeps the latest`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "Standup" None)
        |> ReminderList.add (pending "m1" "Standup (updated)" None)
    list |> List.length |> should equal 1
    list.Head.Subject |> should equal "Standup (updated)"

[<Fact>]
let ``add preserves insertion order across distinct meetings`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "First" None)
        |> ReminderList.add (pending "m2" "Second" None)
    list |> List.map (fun r -> r.Subject) |> should equal [ "First"; "Second" ]

[<Fact>]
let ``subtitle is the Teams label when a join url is present`` () =
    let list = ReminderList.empty |> ReminderList.add (pending "m1" "Sync" (Some "https://teams"))
    list.Head.Subtitle |> should equal "Microsoft Teams Meeting"

[<Fact>]
let ``remove drops the matching reminder`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "First" None)
        |> ReminderList.add (pending "m2" "Second" None)
        |> ReminderList.remove (MeetingId "m1")
    list |> List.map (fun r -> r.Subject) |> should equal [ "Second" ]

[<Fact>]
let ``dismissAll empties the list`` () =
    let list =
        ReminderList.empty
        |> ReminderList.add (pending "m1" "First" None)
        |> ReminderList.dismissAll
    ReminderList.isEmpty list |> should be True
```

- [ ] **Step 2: Create the source file**

Create `src/MeetingNotifier/ReminderList.fs`:

```fsharp
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
```

- [ ] **Step 3: Wire compile order in the main project**

In `src/MeetingNotifier/MeetingNotifier.fsproj`, add the `ReminderList.fs` compile entry immediately after `AumidRegistration.fs` and before `Notifications.fs`:

```xml
    <Compile Include="AumidRegistration.fs" />
    <Compile Include="ReminderList.fs" />
    <Compile Include="Notifications.fs" />
```

- [ ] **Step 4: Wire the test project**

In `tests/MeetingNotifier.Tests/MeetingNotifier.Tests.fsproj`, add the source-link for `ReminderList.fs` after the `AlertEngine.fs` link and add the test file before `Program.fs`:

```xml
    <Compile Include="..\..\src\MeetingNotifier\AlertEngine.fs"
             Link="_src\AlertEngine.fs" />
    <Compile Include="..\..\src\MeetingNotifier\ReminderList.fs"
             Link="_src\ReminderList.fs" />
    <Compile Include="AlertEngineTests.fs" />
    <Compile Include="ReminderListTests.fs" />
    <Compile Include="Program.fs" />
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/MeetingNotifier.Tests/MeetingNotifier.Tests.fsproj`
Expected: all tests pass, including the six new `ReminderListTests`.

- [ ] **Step 6: Commit**

```bash
git add src/MeetingNotifier/ReminderList.fs src/MeetingNotifier/MeetingNotifier.fsproj tests/MeetingNotifier.Tests/ReminderListTests.fs tests/MeetingNotifier.Tests/MeetingNotifier.Tests.fsproj
git commit -m "Add pure ReminderList state module with tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: WPF project wiring + XAML shell + window stub

**Files:**
- Modify: `src/MeetingNotifier/MeetingNotifier.fsproj` (enable WPF, embed XAML, compile entry)
- Create: `src/MeetingNotifier/ReminderShell.xaml`
- Create: `src/MeetingNotifier/ReminderWindow.fs` (stub, fully implemented in Task 4)

**Interfaces:**
- Produces: `ReminderWindow` type with members `AddReminder(pa: PendingAlert, onAction: AlertAction -> unit) : unit`, `RemoveReminder(id: MeetingId) : unit`, `DismissAll() : unit`, `SetKeepOnTop(v: bool) : unit`. The stub defines these signatures so Tasks 5 and 6 compile; Task 4 fills in the bodies.

- [ ] **Step 1: Enable WPF and embed the XAML**

In `src/MeetingNotifier/MeetingNotifier.fsproj`, add `<UseWPF>true</UseWPF>` in the first `PropertyGroup` directly under the existing `<UseWindowsForms>true</UseWindowsForms>` line:

```xml
    <UseWindowsForms>true</UseWindowsForms>
    <UseWPF>true</UseWPF>
```

In the `ItemGroup` that holds `EmbeddedResource Include="alert.wav"`, add the XAML as an embedded resource and stop WPF from compiling it as a `Page`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="alert.wav" />
    <Page Remove="ReminderShell.xaml" />
    <EmbeddedResource Include="ReminderShell.xaml" />
    <Content Include="appicon.ico">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 2: Add the compile entry**

In `src/MeetingNotifier/MeetingNotifier.fsproj`, add `ReminderWindow.fs` immediately after `ReminderList.fs` and before `Notifications.fs`:

```xml
    <Compile Include="ReminderList.fs" />
    <Compile Include="ReminderWindow.fs" />
    <Compile Include="Notifications.fs" />
```

- [ ] **Step 3: Create the XAML shell**

Create `src/MeetingNotifier/ReminderShell.xaml` (raw XAML, parsed at runtime via `XamlReader`; brushes come from window resources set in code):

```xml
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid.RowDefinitions>
    <RowDefinition Height="*" />
    <RowDefinition Height="Auto" />
  </Grid.RowDefinitions>
  <ScrollViewer Grid.Row="0" x:Name="RowsScroll" VerticalScrollBarVisibility="Auto">
    <StackPanel x:Name="RowsPanel" Margin="0,2,0,8" />
  </ScrollViewer>
  <Border Grid.Row="1" Background="{DynamicResource HeaderBg}" Padding="10,8">
    <Button x:Name="DismissAllButton" Content="Dismiss all"
            HorizontalAlignment="Right" Padding="14,6"
            Foreground="{DynamicResource TextBrush}" />
  </Border>
</Grid>
```

- [ ] **Step 4: Create the window stub**

Create `src/MeetingNotifier/ReminderWindow.fs` with signatures only (bodies added in Task 4):

```fsharp
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
```

- [ ] **Step 5: Build**

Run: `dotnet build src/MeetingNotifier/MeetingNotifier.fsproj -c Debug`
Expected: build succeeds with WPF enabled and the XAML embedded.

- [ ] **Step 6: Commit**

```bash
git add src/MeetingNotifier/MeetingNotifier.fsproj src/MeetingNotifier/ReminderShell.xaml src/MeetingNotifier/ReminderWindow.fs
git commit -m "Enable WPF and scaffold reminders window shell

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Implement the WPF reminders window

**Files:**
- Modify: `src/MeetingNotifier/ReminderWindow.fs` (replace the stub)

**Interfaces:**
- Consumes: `ReminderList` (all functions), `Domain.PendingAlert`, `Domain.AlertAction`, `Domain.MeetingId`, embedded `ReminderShell.xaml`.
- Produces: same public members as the Task 3 stub, now functional.

- [ ] **Step 1: Replace the stub with the full implementation**

Replace the entire contents of `src/MeetingNotifier/ReminderWindow.fs` with:

```fsharp
namespace MeetingNotifier

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Windows
open System.Windows.Controls
open System.Windows.Media
open System.Windows.Markup
open System.Windows.Interop
open System.Windows.Input
open Microsoft.Win32
open MeetingNotifier.Domain

/// Light/dark palette picked from the Windows personalization setting.
module private ReminderTheme =

    type Palette =
        { IsDark: bool
          WindowBg: Color
          HeaderBg: Color
          Surface: Color
          Text: Color
          Subtle: Color
          Accent: Color }

    let private rgb (r, g, b) = Color.FromRgb(byte r, byte g, byte b)

    let private dark =
        { IsDark = true
          WindowBg = rgb (32, 32, 32)
          HeaderBg = rgb (43, 43, 43)
          Surface = rgb (43, 43, 43)
          Text = rgb (255, 255, 255)
          Subtle = rgb (170, 170, 170)
          Accent = rgb (96, 205, 255) }

    let private light =
        { IsDark = false
          WindowBg = rgb (243, 243, 243)
          HeaderBg = rgb (255, 255, 255)
          Surface = rgb (255, 255, 255)
          Text = rgb (26, 26, 26)
          Subtle = rgb (96, 96, 96)
          Accent = rgb (0, 95, 184) }

    let current () =
        let useLight =
            try
                use key =
                    Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                match key with
                | null -> true
                | k ->
                    match k.GetValue "AppsUseLightTheme" with
                    | :? int as v -> v <> 0
                    | _ -> true
            with _ -> true
        if useLight then light else dark

/// Taskbar flashing + immersive dark title bar P/Invoke.
module private WindowInterop =

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type FLASHWINFO =
        val mutable cbSize: uint32
        val mutable hwnd: IntPtr
        val mutable dwFlags: uint32
        val mutable uCount: uint32
        val mutable dwTimeout: uint32

    [<DllImport("user32.dll")>]
    extern bool FlashWindowEx(FLASHWINFO& pwfi)

    [<DllImport("dwmapi.dll")>]
    extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int& value, int size)

    let private FLASHW_TRAY = 0x00000002u
    let private FLASHW_TIMERNOFG = 0x0000000Cu

    let flash (hwnd: IntPtr) =
        if hwnd <> IntPtr.Zero then
            let mutable info = Unchecked.defaultof<FLASHWINFO>
            info.cbSize <- uint32 (Marshal.SizeOf<FLASHWINFO>())
            info.hwnd <- hwnd
            info.dwFlags <- FLASHW_TRAY ||| FLASHW_TIMERNOFG
            info.uCount <- UInt32.MaxValue
            info.dwTimeout <- 0u
            FlashWindowEx(&info) |> ignore

    let setDarkTitleBar (hwnd: IntPtr) (dark: bool) =
        if hwnd <> IntPtr.Zero then
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+/Win11)
            let mutable v = if dark then 1 else 0
            DwmSetWindowAttribute(hwnd, 20, &v, 4) |> ignore

/// Single reusable Outlook-styled reminders window. All members must be
/// called on the UI (STA) thread; TrayApp routes via its UI SynchronizationContext.
type ReminderWindow() =

    let palette = ReminderTheme.current ()
    let brush (c: Color) = SolidColorBrush c :> Brush

    let window = Window()
    let mutable reminders : ReminderList.ReminderList = ReminderList.empty
    let mutable callbacks : Map<MeetingId, AlertAction -> unit> = Map.empty
    let mutable keepOnTop = true
    let mutable hwnd = IntPtr.Zero

    let loadShell () =
        let asm = Assembly.GetExecutingAssembly()
        let resName =
            asm.GetManifestResourceNames()
            |> Array.find (fun n -> n.EndsWith "ReminderShell.xaml")
        use s = asm.GetManifestResourceStream resName
        XamlReader.Load s :?> FrameworkElement

    let shell = loadShell ()
    let panel = shell.FindName "RowsPanel" :?> StackPanel
    let scroll = shell.FindName "RowsScroll" :?> ScrollViewer
    let dismissAllButton = shell.FindName "DismissAllButton" :?> Button

    let mkIconButton (glyph: string) (tip: string) =
        let b = Button()
        b.Content <- glyph
        b.FontFamily <- FontFamily "Segoe MDL2 Assets"
        b.FontSize <- 16.0
        b.Foreground <- brush palette.Text
        b.Background <- Brushes.Transparent
        b.BorderThickness <- Thickness 0.0
        b.Padding <- Thickness(8.0, 4.0, 8.0, 4.0)
        b.Margin <- Thickness(2.0, 0.0, 0.0, 0.0)
        b.Cursor <- Cursors.Hand
        b.ToolTip <- tip
        b

    let position () =
        window.UpdateLayout()
        let wa = SystemParameters.WorkArea
        window.Left <- wa.Right - window.ActualWidth - 12.0
        window.Top <- wa.Bottom - window.ActualHeight - 12.0

    let rec render () =
        panel.Children.Clear()
        for r in reminders do
            panel.Children.Add(buildRow r) |> ignore

    and removeReminder (id: MeetingId) =
        reminders <- ReminderList.remove id reminders
        callbacks <- Map.remove id callbacks
        if ReminderList.isEmpty reminders then
            window.Hide()
        else
            render ()
            position ()

    and buildRow (r: ReminderList.Reminder) : UIElement =
        let onAction =
            match Map.tryFind r.Id callbacks with
            | Some cb -> cb
            | None -> ignore

        let border = Border()
        border.Background <- brush palette.Surface
        border.CornerRadius <- CornerRadius 6.0
        border.Margin <- Thickness(8.0, 6.0, 8.0, 0.0)
        border.Padding <- Thickness 10.0

        let grid = Grid()
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))

        let icon =
            TextBlock(
                Text = "", // calendar glyph
                FontFamily = FontFamily "Segoe MDL2 Assets",
                FontSize = 20.0,
                Foreground = brush palette.Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = Thickness(2.0, 0.0, 10.0, 0.0))
        Grid.SetColumn(icon, 0)

        let texts = StackPanel(Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center)
        let subject =
            TextBlock(
                Text = r.Subject,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.0,
                Foreground = brush palette.Text,
                TextTrimming = TextTrimming.CharacterEllipsis)
        let timeStr = r.Start.ToLocalTime().ToString "h:mm tt"
        let subText =
            if String.IsNullOrEmpty r.Subtitle then timeStr
            else sprintf "%s · %s" timeStr r.Subtitle
        let subtitle =
            TextBlock(Text = subText, FontSize = 12.0, Foreground = brush palette.Subtle)
        texts.Children.Add subject |> ignore
        texts.Children.Add subtitle |> ignore
        Grid.SetColumn(texts, 1)

        let actions = StackPanel(Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center)
        match r.JoinUrl with
        | Some url ->
            let join = mkIconButton "" "Join" // video glyph
            join.Click.Add(fun _ ->
                onAction (Join url)
                removeReminder r.Id)
            actions.Children.Add join |> ignore
        | None -> ()

        let snooze = mkIconButton "" "Snooze 2 min" // clock glyph
        snooze.Click.Add(fun _ ->
            onAction (Snooze 2)
            removeReminder r.Id)
        actions.Children.Add snooze |> ignore

        let dismiss = mkIconButton "" "Dismiss" // clear glyph
        dismiss.Click.Add(fun _ ->
            onAction Dismiss
            removeReminder r.Id)
        actions.Children.Add dismiss |> ignore
        Grid.SetColumn(actions, 2)

        grid.Children.Add icon |> ignore
        grid.Children.Add texts |> ignore
        grid.Children.Add actions |> ignore
        border.Child <- grid
        border :> UIElement

    let dismissAll () =
        for r in reminders do
            match Map.tryFind r.Id callbacks with
            | Some cb -> cb Dismiss
            | None -> ()
        reminders <- ReminderList.empty
        callbacks <- Map.empty
        window.Hide()

    do
        window.Title <- "Reminders"
        window.WindowStyle <- WindowStyle.SingleBorderWindow
        window.ResizeMode <- ResizeMode.NoResize
        window.ShowInTaskbar <- true
        window.Topmost <- keepOnTop
        window.Width <- 380.0
        window.SizeToContent <- SizeToContent.Height
        window.MaxHeight <- SystemParameters.WorkArea.Height * 0.6
        window.ShowActivated <- true
        window.Background <- brush palette.WindowBg
        window.Resources.["WindowBg"] <- brush palette.WindowBg
        window.Resources.["HeaderBg"] <- brush palette.HeaderBg
        window.Resources.["TextBrush"] <- brush palette.Text
        scroll.MaxHeight <- SystemParameters.WorkArea.Height * 0.6
        dismissAllButton.Click.Add(fun _ -> dismissAll ())
        window.Content <- shell

    let showAndFlash () =
        if not window.IsVisible then window.Show()
        hwnd <- WindowInteropHelper(window).Handle
        WindowInterop.setDarkTitleBar hwnd palette.IsDark
        position ()
        // Pop to front once even when not pinned, then settle to the pref.
        window.Topmost <- true
        window.Activate()
        window.Topmost <- keepOnTop
        WindowInterop.flash hwnd

    member _.AddReminder(pa: PendingAlert, onAction: AlertAction -> unit) : unit =
        reminders <- ReminderList.add pa reminders
        callbacks <- Map.add pa.Meeting.Id onAction callbacks
        render ()
        showAndFlash ()

    member _.RemoveReminder(id: MeetingId) : unit = removeReminder id

    member _.DismissAll() : unit = dismissAll ()

    member _.SetKeepOnTop(v: bool) : unit =
        keepOnTop <- v
        window.Topmost <- v
```

- [ ] **Step 2: Build**

Run: `dotnet build src/MeetingNotifier/MeetingNotifier.fsproj -c Debug`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/MeetingNotifier/ReminderWindow.fs
git commit -m "Implement Outlook-styled WPF reminders window

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Rewire Notifications onto the window

**Files:**
- Modify: `src/MeetingNotifier/Notifications.fs`

**Interfaces:**
- Consumes: `ReminderWindow`, `Config.AppConfig`, `Domain.PendingAlert`, `Domain.AlertAction`.
- Produces: `Notifications.fireAlert : Config.AppConfig -> ReminderWindow -> PendingAlert -> (AlertAction -> unit) -> unit`. The `Toast` module, `AlertForm` type, and `Taskbar` module are removed.

- [ ] **Step 1: Apply volume + an enabled-aware play, drop Forms/Toast/Flash**

Replace the entire contents of `src/MeetingNotifier/Notifications.fs` with:

```fsharp
namespace MeetingNotifier

open System
open System.IO
open System.Reflection
open NAudio.CoreAudioApi
open NAudio.Wave
open MeetingNotifier.Domain

/// The "look at me!" surfaces: an audio chime routed to a non-headphone
/// render device, plus the aggregating WPF reminders window (which owns
/// its own taskbar flash).
module Notifications =

    // ============ Audio playback via a chosen render device ============

    module Audio =

        /// Pick the audio device to play the chime through. Preference
        /// order: exact friendly-name match from config, first active
        /// non-headphone/headset device, then the system default.
        let pickDevice (configuredName: string) : MMDevice option =
            try
                use enum' = new MMDeviceEnumerator()
                let all =
                    enum'.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    |> Seq.cast<MMDevice>
                    |> Seq.toList
                let byName =
                    if String.IsNullOrWhiteSpace configuredName then None
                    else
                        all
                        |> List.tryFind (fun d ->
                            String.Equals(
                                d.FriendlyName,
                                configuredName,
                                StringComparison.OrdinalIgnoreCase))
                match byName with
                | Some d -> Some d
                | None ->
                    let isHeadphoneLike (d: MMDevice) =
                        let name = d.FriendlyName.ToLowerInvariant()
                        name.Contains "headphone" || name.Contains "headset"
                    match all |> List.tryFind (fun d -> not (isHeadphoneLike d)) with
                    | Some d -> Some d
                    | None ->
                        try
                            Some (enum'.GetDefaultAudioEndpoint(
                                    DataFlow.Render, Role.Multimedia))
                        with _ -> None
            with _ -> None

        /// Extract `alert.wav` from embedded resources into a temp file so
        /// NAudio's reader can stream from disk.
        let private embeddedWavPath () =
            let asm = Assembly.GetExecutingAssembly()
            let resourceName =
                asm.GetManifestResourceNames()
                |> Array.tryFind (fun n -> n.EndsWith "alert.wav")
            match resourceName with
            | None -> None
            | Some name ->
                let tempPath =
                    Path.Combine(Path.GetTempPath(), "meeting-notifier-alert.wav")
                if not (File.Exists tempPath) then
                    use rs = asm.GetManifestResourceStream name
                    use fs = File.Create tempPath
                    rs.CopyTo fs
                Some tempPath

        /// Fire-and-forget playback at the configured volume (0-100).
        let play (cfg: Config.AppConfig) =
            System.Threading.Tasks.Task.Run(fun () ->
                try
                    match embeddedWavPath () with
                    | None -> ()
                    | Some wav ->
                        match pickDevice cfg.AudioDeviceName with
                        | None -> ()
                        | Some device ->
                            use reader = new AudioFileReader(wav)
                            let v = float32 (max 0 (min 100 cfg.Volume)) / 100.0f
                            reader.Volume <- v
                            use out =
                                new WasapiOut(device, AudioClientShareMode.Shared,
                                              true, 100)
                            out.Init reader
                            let waiter = new System.Threading.ManualResetEventSlim(false)
                            out.PlaybackStopped.Add(fun _ -> waiter.Set())
                            out.Play()
                            waiter.Wait(TimeSpan.FromSeconds 10.0) |> ignore
                with _ -> ()) |> ignore

    // ============ Unified fire function ============

    /// Fire the alert: chime (if sound enabled) on a worker thread, and a
    /// row added to the singleton reminders window (which shows + flashes).
    /// Must be called on the UI thread because it touches the window.
    let fireAlert
        (cfg: Config.AppConfig)
        (window: ReminderWindow)
        (pa: PendingAlert)
        (onAction: AlertAction -> unit)
        =
        if cfg.SoundEnabled then Audio.play cfg
        window.AddReminder(pa, onAction)
```

- [ ] **Step 2: Build (expected to fail in TrayApp)**

Run: `dotnet build src/MeetingNotifier/MeetingNotifier.fsproj -c Debug`
Expected: FAIL — `TrayApp.fs` still calls the old `fireAlert aumid cfg pa ...` signature. This is fixed in Task 6. (If you want a clean green build at this step, do Task 6 immediately after.)

- [ ] **Step 3: Commit**

```bash
git add src/MeetingNotifier/Notifications.fs
git commit -m "Route notifications through the reminders window, drop toast

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: Wire TrayApp to the window + add menu toggles

**Files:**
- Modify: `src/MeetingNotifier/TrayApp.fs`

**Interfaces:**
- Consumes: `ReminderWindow`, the new `Notifications.fireAlert` signature, `Config.save`, the new config fields.
- Produces: no new public surface (`TrayContext` constructor signature unchanged).

- [ ] **Step 1: Make cfg mutable and add window + menu-item fields**

In `src/MeetingNotifier/TrayApp.fs`, inside `TrayContext`, change the start of the class body. Replace the field block (currently lines 37–48, `let mutable state = ...` through `let exitItem = ...`) with:

```fsharp
        let mutable cfg = cfg
        let mutable state = AlertState.empty
        let mutable lastSnapshot : Meeting list = []
        let mutable reminderWindow : ReminderWindow option = None
        let stateLock = obj()
        let cts = new CancellationTokenSource()
        let notifyIcon = new NotifyIcon()
        let menu = new ContextMenuStrip()
        let upcomingItem = new ToolStripMenuItem("Upcoming meetings")
        let startupItem = new ToolStripMenuItem("Run at startup")
        let keepOnTopItem = new ToolStripMenuItem("Keep reminders on top")
        let soundItem = new ToolStripMenuItem("Play sound")
        let settingsItem = new ToolStripMenuItem("Settings")
        let reauthItem = new ToolStripMenuItem("Re-authenticate...")
        let exitItem = new ToolStripMenuItem("Exit")
```

- [ ] **Step 2: Add a UI-thread window accessor**

In `src/MeetingNotifier/TrayApp.fs`, add this right after the `postToUi` definition (currently lines 50–51):

```fsharp
        // Lazily create the single reminders window. Must run on the UI
        // thread, so only call it from inside postToUi.
        let getWindow () =
            match reminderWindow with
            | Some w -> w
            | None ->
                let w = ReminderWindow()
                w.SetKeepOnTop cfg.KeepOnTop
                reminderWindow <- Some w
                w
```

- [ ] **Step 3: Update the fireAlert call site**

In `src/MeetingNotifier/TrayApp.fs`, in `runTick`, replace the dispatch block (currently lines 142–145):

```fsharp
                    for pa in pending do
                        postToUi (fun () ->
                            Notifications.fireAlert aumid cfg pa
                                (handleAlertAction pa))
```

with:

```fsharp
                    for pa in pending do
                        postToUi (fun () ->
                            let w = getWindow ()
                            Notifications.fireAlert cfg w pa (handleAlertAction pa))
```

- [ ] **Step 4: Remove the now-unused aumid literal**

In `src/MeetingNotifier/TrayApp.fs`, delete the `aumid` literal (currently lines 19–20):

```fsharp
    [<Literal>]
    let private aumid = "MeetingNotifier.App"
```

- [ ] **Step 5: Add toggle handlers and wire the menu**

In `src/MeetingNotifier/TrayApp.fs`, add these handlers next to `refreshStartupCheckmark` (after its definition, around line 92):

```fsharp
        let applyKeepOnTop (v: bool) =
            cfg <- { cfg with KeepOnTop = v }
            try Config.save cfg with _ -> ()
            reminderWindow |> Option.iter (fun w -> w.SetKeepOnTop v)

        let applySound (v: bool) =
            cfg <- { cfg with SoundEnabled = v }
            try Config.save cfg with _ -> ()
```

In the construction `do` block, set up the two checkable items. Add this right after the `startupItem.Click.Add(...)` handler (which currently ends at line 185):

```fsharp
            keepOnTopItem.CheckOnClick <- true
            keepOnTopItem.Checked <- cfg.KeepOnTop
            keepOnTopItem.Click.Add(fun _ -> applyKeepOnTop keepOnTopItem.Checked)

            soundItem.CheckOnClick <- true
            soundItem.Checked <- cfg.SoundEnabled
            soundItem.Click.Add(fun _ -> applySound soundItem.Checked)
```

Then add the two items to the menu. Replace the menu-assembly block (currently lines 204–210) with:

```fsharp
            menu.Items.Add(upcomingItem :> ToolStripItem) |> ignore
            menu.Items.Add(new ToolStripSeparator() :> ToolStripItem) |> ignore
            menu.Items.Add(startupItem :> ToolStripItem) |> ignore
            menu.Items.Add(keepOnTopItem :> ToolStripItem) |> ignore
            menu.Items.Add(soundItem :> ToolStripItem) |> ignore
            menu.Items.Add(settingsItem :> ToolStripItem) |> ignore
            menu.Items.Add(reauthItem :> ToolStripItem) |> ignore
            menu.Items.Add(new ToolStripSeparator() :> ToolStripItem) |> ignore
            menu.Items.Add(exitItem :> ToolStripItem) |> ignore
```

- [ ] **Step 6: Build**

Run: `dotnet build src/MeetingNotifier/MeetingNotifier.fsproj -c Debug`
Expected: build succeeds.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test tests/MeetingNotifier.Tests/MeetingNotifier.Tests.fsproj`
Expected: all tests pass (AlertEngine + ReminderList).

- [ ] **Step 8: Manual smoke check**

Run: `dotnet run --project src/MeetingNotifier/MeetingNotifier.fsproj`
To force an alert quickly, temporarily add a large value (e.g. `1440`) to `alertMinutesBefore` in `%APPDATA%\MeetingNotifier\config.json` so any meeting in the next 24h fires. Verify: a single "Reminders" window appears bottom-right styled per the Windows theme; a second concurrent meeting adds a second row (no cutoff); Join/Snooze/Dismiss act on a row; "Dismiss all" clears and hides the window; toggling "Keep reminders on top" and "Play sound" in the tray menu persists to config and takes effect. Revert the config change afterward.

- [ ] **Step 9: Commit**

```bash
git add src/MeetingNotifier/TrayApp.fs
git commit -m "Wire tray to reminders window with on-top and sound toggles

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- Hybrid WinForms + WPF (`UseWPF`, tray unchanged) → Task 3.
- Single aggregating window, scroll past max height (fixes cutoff) → Task 4 (`MaxHeight`, `ScrollViewer`, rebuild-from-state).
- Outlook-styled rows (icon, subject, time · subtitle, Join/Snooze/Dismiss) + Dismiss all → Task 4.
- Follow Windows theme (registry `AppsUseLightTheme`, dark title bar) → Task 4.
- Drop native toast; chime + flash remain → Task 5 (Toast/AlertForm/Taskbar removed; window owns flash).
- Volume applied to NAudio → Task 5; `SoundEnabled` gate → Task 5/6.
- `KeepOnTop`/`SoundEnabled`/`Volume` config + defaults → Task 1; tray toggles persisting to config → Task 6.
- Pure `ReminderList` with tests → Task 2.

**Placeholder scan:** No TBD/TODO; every code step shows complete code; commands have expected output.

**Type consistency:** `ReminderWindow` members (`AddReminder`, `RemoveReminder`, `DismissAll`, `SetKeepOnTop`) match across Tasks 3, 4, 5, 6. `fireAlert` signature `(cfg, window, pa, onAction)` matches between Task 5 (definition) and Task 6 (call site). `ReminderList` functions match between Task 2 (definition), Task 4 (use), and the tests. Config field names (`KeepOnTop`, `SoundEnabled`, `Volume`) consistent across Tasks 1, 5, 6.
