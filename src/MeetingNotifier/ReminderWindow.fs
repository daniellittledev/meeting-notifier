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
open System.Windows.Threading
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
          Accent: Color
          ButtonBg: Color
          ButtonHover: Color }

    let private rgb (r, g, b) = Color.FromRgb(byte r, byte g, byte b)

    let private dark =
        { IsDark = true
          WindowBg = rgb (32, 32, 32)
          HeaderBg = rgb (43, 43, 43)
          Surface = rgb (43, 43, 43)
          Text = rgb (255, 255, 255)
          Subtle = rgb (170, 170, 170)
          Accent = rgb (96, 205, 255)
          ButtonBg = rgb (60, 60, 60)
          ButtonHover = rgb (75, 75, 75) }

    let private light =
        { IsDark = false
          WindowBg = rgb (243, 243, 243)
          HeaderBg = rgb (255, 255, 255)
          Surface = rgb (255, 255, 255)
          Text = rgb (26, 26, 26)
          Subtle = rgb (96, 96, 96)
          Accent = rgb (0, 95, 184)
          ButtonBg = rgb (225, 225, 225)
          ButtonHover = rgb (210, 210, 210) }

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

    // Minutes before a meeting's start that Snooze (per-row and "all")
    // re-reminds at.
    let snoozeLeadMinutes = 2.0

    let palette = ReminderTheme.current ()
    let brush (c: Color) = SolidColorBrush c :> Brush

    let window = Window()
    let mutable reminders : ReminderList.ReminderList = ReminderList.empty
    let mutable callbacks : Map<MeetingId, AlertAction -> unit> = Map.empty
    let mutable keepOnTop = true
    let mutable hwnd = IntPtr.Zero
    // Set only during app shutdown so the Closing handler lets the window
    // actually close instead of minimising it.
    let mutable shuttingDown = false

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
    let snoozeAllButton = shell.FindName "SnoozeAllButton" :?> Button

    // Ticks while the window is open to keep each row's "in N mins" countdown
    // honest without the user having to do anything.
    let countdownTimer = DispatcherTimer(Interval = TimeSpan.FromSeconds 30.0)

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
        let untilStr = ReminderList.timeUntilText DateTimeOffset.Now r.Start
        let subText =
            [ timeStr; untilStr; r.Subtitle ]
            |> List.filter (String.IsNullOrEmpty >> not)
            |> String.concat " · "
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
        snooze.ToolTip <- "Snooze until 2 min before start"
        snooze.Click.Add(fun _ ->
            onAction (Snooze (r.Start.AddMinutes(-snoozeLeadMinutes)))
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

    let snoozeAll () =
        for r in reminders do
            match Map.tryFind r.Id callbacks with
            | Some cb -> cb (Snooze (r.Start.AddMinutes(-snoozeLeadMinutes)))
            | None -> ()
        reminders <- ReminderList.empty
        callbacks <- Map.empty
        window.Hide()

    do
        window.Title <- "Reminders"
        window.WindowStyle <- WindowStyle.SingleBorderWindow
        // CanMinimize gives a minimize button (no maximize). The minimize
        // button pushes the window aside (reminders stay pending), while the
        // title-bar X is repurposed to snooze-all (see Closing below).
        window.ResizeMode <- ResizeMode.CanMinimize
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
        window.Resources.["ButtonBg"] <- brush palette.ButtonBg
        window.Resources.["ButtonHoverBg"] <- brush palette.ButtonHover
        scroll.MaxHeight <- SystemParameters.WorkArea.Height * 0.6
        dismissAllButton.Click.Add(fun _ -> dismissAll ())
        snoozeAllButton.Click.Add(fun _ -> snoozeAll ())
        // Repurpose the title-bar X: snooze-all instead of closing, unless the
        // app is shutting down. Each pending reminder is re-armed for 2 minutes
        // before its start and the window hides.
        window.Closing.Add(fun e ->
            if not shuttingDown then
                e.Cancel <- true
                snoozeAll ())
        countdownTimer.Tick.Add(fun _ ->
            if window.IsVisible
               && window.WindowState <> WindowState.Minimized
               && not (ReminderList.isEmpty reminders) then
                render ())
        countdownTimer.Start()
        window.Content <- shell

    let showAndFlash () =
        if not window.IsVisible then window.Show()
        // A minimised window is still IsVisible, so restore it explicitly so
        // the next reminder pops it back to the foreground.
        if window.WindowState = WindowState.Minimized then
            window.WindowState <- WindowState.Normal
        hwnd <- WindowInteropHelper(window).Handle
        WindowInterop.setDarkTitleBar hwnd palette.IsDark
        position ()
        // Pop to front once even when not pinned, then settle to the pref.
        window.Topmost <- true
        window.Activate() |> ignore
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

    member _.Close() : unit =
        // Bypass the snooze-all-on-close handler so the window really closes
        // when the app is exiting.
        shuttingDown <- true
        try countdownTimer.Stop() with _ -> ()
        try window.Close() with _ -> ()
