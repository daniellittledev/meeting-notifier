namespace OutlookNotifier

open System
open System.Diagnostics
open System.Reflection
open System.Runtime.InteropServices
open System.Windows
open System.Windows.Controls
open System.Windows.Controls.Primitives
open System.Windows.Documents
open System.Windows.Input
open System.Windows.Media
open System.Windows.Markup
open System.Windows.Interop
open Microsoft.Win32

/// The Windows-styled Settings window. Built in code (like ReminderWindow) so
/// the control wiring is compiler-checked. Every change is applied live via
/// `OnChanged`, which the tray uses to persist and push the new config into
/// the running app.
module SettingsWindow =

    /// What the window needs from the host (the tray app) to apply changes and
    /// show live status.
    type Callbacks =
        { /// Persist + live-apply a changed config.
          OnChanged: Config.AppConfig -> unit
          /// Force an immediate poll ("Check now").
          OnCheckNow: unit -> unit
          /// Re-run interactive sign-in (new IDs / expired consent).
          OnReconnect: unit -> unit
          /// Sign out of the Microsoft account.
          OnSignOut: unit -> unit
          /// Friendly names of the active audio render devices.
          DeviceNames: unit -> string list
          /// A short connection-status line for the Account page.
          StatusText: unit -> string }

    // ----- Theme (mirrors ReminderWindow's light/dark pick) -----

    type private Palette =
        { IsDark: bool
          WindowBg: Color
          CardBg: Color
          CardBorder: Color
          Text: Color
          Subtle: Color
          Accent: Color
          ControlBg: Color
          ToggleBorder: Color }

    let private rgb (r, g, b) = Color.FromRgb(byte r, byte g, byte b)

    let private darkPalette =
        { IsDark = true
          WindowBg = rgb (32, 32, 32)
          CardBg = rgb (43, 43, 43)
          CardBorder = rgb (60, 60, 60)
          Text = rgb (255, 255, 255)
          Subtle = rgb (170, 170, 170)
          Accent = rgb (96, 165, 250)
          ControlBg = rgb (55, 55, 55)
          ToggleBorder = rgb (160, 160, 160) }

    let private lightPalette =
        { IsDark = false
          WindowBg = rgb (243, 243, 243)
          CardBg = rgb (251, 251, 251)
          CardBorder = rgb (229, 229, 229)
          Text = rgb (26, 26, 26)
          Subtle = rgb (96, 96, 96)
          Accent = rgb (0, 95, 184)
          ControlBg = rgb (255, 255, 255)
          ToggleBorder = rgb (120, 120, 120) }

    let private currentPalette () =
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
        if useLight then lightPalette else darkPalette

    [<DllImport("dwmapi.dll")>]
    extern int private DwmSetWindowAttribute(IntPtr hwnd, int attr, int& value, int size)

    let private setDarkTitleBar (hwnd: IntPtr) (dark: bool) =
        if hwnd <> IntPtr.Zero then
            let mutable v = if dark then 1 else 0
            DwmSetWindowAttribute(hwnd, 20, &v, 4) |> ignore

    // The toggle-switch look: a ToggleButton re-templated as a 40x20 track with
    // a sliding thumb. Colours come from DynamicResource keys set on the window.
    let private toggleTemplateXaml =
        """<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
             <Border x:Name="track" Width="40" Height="20" CornerRadius="10" Background="Transparent"
                     BorderThickness="1" BorderBrush="{DynamicResource ToggleBorder}" SnapsToDevicePixels="True">
               <Ellipse x:Name="thumb" Width="12" Height="12" HorizontalAlignment="Left" VerticalAlignment="Center"
                        Margin="4,0,0,0" Fill="{DynamicResource Subtle}"/>
             </Border>
             <ControlTemplate.Triggers>
               <Trigger Property="IsChecked" Value="True">
                 <Setter TargetName="track" Property="Background" Value="{DynamicResource Accent}"/>
                 <Setter TargetName="track" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                 <Setter TargetName="thumb" Property="HorizontalAlignment" Value="Right"/>
                 <Setter TargetName="thumb" Property="Margin" Value="0,0,4,0"/>
                 <Setter TargetName="thumb" Property="Fill" Value="White"/>
               </Trigger>
             </ControlTemplate.Triggers>
           </ControlTemplate>"""

    /// Single reusable Settings window. All members run on the UI (STA) thread.
    type SettingsView(initial: Config.AppConfig, callbacks: Callbacks) =

        let palette = currentPalette ()
        let brush (c: Color) = SolidColorBrush c :> Brush
        let fontFamily = FontFamily "Segoe UI Variable Text, Segoe UI"
        let toggleTemplate = XamlReader.Parse toggleTemplateXaml :?> ControlTemplate

        let mutable cfg = initial
        let window = Window()
        let mutable hwnd = IntPtr.Zero

        // Push the latest config out to the host (persist + live-apply).
        let change (f: Config.AppConfig -> Config.AppConfig) =
            cfg <- f cfg
            callbacks.OnChanged cfg

        // ----- small builders -----

        let text (s: string) size weight color =
            TextBlock(
                Text = s,
                FontSize = size,
                FontWeight = weight,
                FontFamily = fontFamily,
                Foreground = brush color,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center)

        let sectionHeader (s: string) =
            let t = text s 13.0 FontWeights.SemiBold palette.Subtle
            t.Margin <- Thickness(2.0, 18.0, 2.0, 6.0)
            t :> UIElement

        let pageTitle (s: string) =
            let t = text s 26.0 FontWeights.SemiBold palette.Text
            t.Margin <- Thickness(2.0, 6.0, 2.0, 14.0)
            t :> UIElement

        let note (s: string) =
            let t = text s 12.0 FontWeights.Normal palette.Subtle
            t.Margin <- Thickness(4.0, 6.0, 4.0, 0.0)
            t.MaxWidth <- 540.0
            t.HorizontalAlignment <- HorizontalAlignment.Left
            t :> UIElement

        let styleButton (b: Button) =
            b.FontFamily <- fontFamily
            b.FontSize <- 13.0
            b.Foreground <- brush palette.Text
            b.Background <- brush palette.ControlBg
            b.BorderBrush <- brush palette.CardBorder
            b.BorderThickness <- Thickness 1.0
            b.Padding <- Thickness(14.0, 5.0, 14.0, 5.0)
            b.Cursor <- Cursors.Hand
            b

        let button (label: string) (onClick: unit -> unit) : UIElement =
            let b = styleButton (Button(Content = label))
            b.Click.Add(fun _ -> onClick ())
            b :> UIElement

        // A card is a bordered row: title/subtitle on the left, a control on the
        // right. `indent` nudges it in to read as a sub-setting.
        let card (title: string) (subtitle: string) (control: UIElement) (indent: bool) =
            let border = Border()
            border.Background <- brush palette.CardBg
            border.BorderBrush <- brush palette.CardBorder
            border.BorderThickness <- Thickness 1.0
            border.CornerRadius <- CornerRadius 6.0
            border.Padding <- Thickness(16.0, 12.0, 16.0, 12.0)
            border.Margin <- Thickness((if indent then 24.0 else 0.0), 0.0, 0.0, 3.0)
            let grid = Grid()
            grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
            grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
            let texts = StackPanel(Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center)
            texts.Children.Add(text title 14.0 FontWeights.Normal palette.Text) |> ignore
            if not (String.IsNullOrEmpty subtitle) then
                let s = text subtitle 12.0 FontWeights.Normal palette.Subtle
                s.Margin <- Thickness(0.0, 2.0, 0.0, 0.0)
                texts.Children.Add s |> ignore
            Grid.SetColumn(texts, 0)
            let holder = ContentControl(Content = control, VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(12.0, 0.0, 0.0, 0.0))
            Grid.SetColumn(holder, 1)
            grid.Children.Add texts |> ignore
            grid.Children.Add holder |> ignore
            border.Child <- grid
            border :> UIElement

        let makeToggle (isOn: bool) (onChange: bool -> unit) : UIElement =
            let tb = ToggleButton(IsChecked = Nullable isOn, Template = toggleTemplate, Cursor = Cursors.Hand)
            tb.Background <- Brushes.Transparent
            tb.BorderThickness <- Thickness 0.0
            tb.Checked.Add(fun _ -> onChange true)
            tb.Unchecked.Add(fun _ -> onChange false)
            tb :> UIElement

        // A combo bound to a list of (label, value); calls back with the value.
        let combo (items: (string * 'a) list) (selected: 'a) (onChange: 'a -> unit) : UIElement =
            let cb = ComboBox(FontFamily = fontFamily, FontSize = 13.0, MinWidth = 160.0)
            for (label, _) in items do cb.Items.Add label |> ignore
            let idx = items |> List.tryFindIndex (fun (_, v) -> v = selected) |> Option.defaultValue 0
            cb.SelectedIndex <- idx
            cb.SelectionChanged.Add(fun _ ->
                let i = cb.SelectedIndex
                if i >= 0 && i < List.length items then onChange (snd items.[i]))
            cb :> UIElement

        // A stepper: [-] value [+], clamped to [lo, hi].
        let stepper (value: int) (lo: int) (hi: int) (unit': string) (onChange: int -> unit) : UIElement =
            let panel = StackPanel(Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center)
            let valueText = text "" 13.0 FontWeights.Normal palette.Text
            valueText.MinWidth <- 54.0
            valueText.TextAlignment <- TextAlignment.Center
            let mutable v = value
            let render () = valueText.Text <- sprintf "%d %s" v unit'
            let set nv =
                v <- max lo (min hi nv)
                render ()
                onChange v
            let minus = styleButton (Button(Content = "−", Width = 30.0))
            let plus = styleButton (Button(Content = "+", Width = 30.0))
            minus.Click.Add(fun _ -> set (v - 1))
            plus.Click.Add(fun _ -> set (v + 1))
            render ()
            panel.Children.Add minus |> ignore
            panel.Children.Add valueText |> ignore
            panel.Children.Add plus |> ignore
            panel :> UIElement

        // A labelled slider (0-100) with a live percentage readout.
        let volumeSlider (value: int) (onChange: int -> unit) : UIElement =
            let panel = StackPanel(Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center)
            let slider = Slider(Minimum = 0.0, Maximum = 100.0, Value = float value, Width = 200.0, VerticalAlignment = VerticalAlignment.Center)
            let readout = text (sprintf "%d%%" value) 13.0 FontWeights.Normal palette.Subtle
            readout.MinWidth <- 44.0
            readout.Margin <- Thickness(12.0, 0.0, 0.0, 0.0)
            readout.TextAlignment <- TextAlignment.Right
            slider.ValueChanged.Add(fun _ ->
                let v = int (Math.Round slider.Value)
                readout.Text <- sprintf "%d%%" v
                onChange v)
            panel.Children.Add slider |> ignore
            panel.Children.Add readout |> ignore
            panel :> UIElement

        // ----- modal helpers (defined before the pages that use them) -----

        let showInputDialog (title: string) (prompt: string) : string option =
            let dlg = Window(Title = title, Width = 380.0, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = window, ResizeMode = ResizeMode.NoResize, Background = brush palette.WindowBg, FontFamily = fontFamily, Foreground = brush palette.Text)
            let panel = StackPanel(Margin = Thickness 16.0)
            panel.Children.Add(text prompt 13.0 FontWeights.Normal palette.Text) |> ignore
            let box = TextBox(Margin = Thickness(0.0, 10.0, 0.0, 0.0), FontSize = 13.0, Padding = Thickness(6.0, 4.0, 6.0, 4.0))
            panel.Children.Add box |> ignore
            let buttons = StackPanel(Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = Thickness(0.0, 16.0, 0.0, 0.0))
            let mutable result = None
            let ok = styleButton (Button(Content = "OK", Width = 80.0, Margin = Thickness(0.0, 0.0, 8.0, 0.0), IsDefault = true))
            ok.Click.Add(fun _ -> result <- Some box.Text; dlg.DialogResult <- Nullable true)
            let cancel = styleButton (Button(Content = "Cancel", Width = 80.0, IsCancel = true))
            buttons.Children.Add ok |> ignore
            buttons.Children.Add cancel |> ignore
            panel.Children.Add buttons |> ignore
            dlg.Content <- panel
            dlg.ShowDialog() |> ignore
            result

        let showRuleDialog (existing: Config.EmailRule) : Config.EmailRule option =
            let dlg = Window(Title = "Email rule", Width = 440.0, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = window, ResizeMode = ResizeMode.NoResize, Background = brush palette.WindowBg, FontFamily = fontFamily, Foreground = brush palette.Text)
            let panel = StackPanel(Margin = Thickness 16.0)
            let labelled (label: string) (value: string) =
                panel.Children.Add(text label 13.0 FontWeights.Normal palette.Text) |> ignore
                let box = TextBox(Text = value, Margin = Thickness(0.0, 4.0, 0.0, 12.0), FontSize = 13.0, Padding = Thickness(6.0, 4.0, 6.0, 4.0))
                panel.Children.Add box |> ignore
                box
            let nameBox = labelled "Name" existing.Name
            let fromBox = labelled "From contains (sender address or name)" existing.From
            let recipBox = labelled "To/Cc contains (recipient address or name)" existing.RecipientContains
            let subjBox = labelled "Subject contains" existing.SubjectContains
            let buttons = StackPanel(Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right)
            let mutable result = None
            let ok = styleButton (Button(Content = "Save", Width = 80.0, Margin = Thickness(0.0, 0.0, 8.0, 0.0), IsDefault = true))
            ok.Click.Add(fun _ ->
                let r : Config.EmailRule = { Name = nameBox.Text.Trim(); From = fromBox.Text.Trim(); RecipientContains = recipBox.Text.Trim(); SubjectContains = subjBox.Text.Trim() }
                result <- Some r
                dlg.DialogResult <- Nullable true)
            let cancel = styleButton (Button(Content = "Cancel", Width = 80.0, IsCancel = true))
            buttons.Children.Add ok |> ignore
            buttons.Children.Add cancel |> ignore
            panel.Children.Add buttons |> ignore
            dlg.Content <- panel
            dlg.ShowDialog() |> ignore
            result

        let mkPanel () =
            let sp = StackPanel(Orientation = Orientation.Vertical, Margin = Thickness(28.0, 2.0, 28.0, 24.0))
            let sv = ScrollViewer(VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp)
            sv, sp

        // ----- pages -----

        let generalPanel () =
            let sv, p = mkPanel ()
            p.Children.Add(pageTitle "General") |> ignore
            p.Children.Add(
                card "Keep alert windows on top" "Reminder pop-ups stay above other apps until dismissed"
                    (makeToggle cfg.KeepOnTop (fun v -> change (fun c -> { c with KeepOnTop = v }))) false) |> ignore
            p.Children.Add(sectionHeader "Checking") |> ignore
            let pollItems = [ "30 seconds", 30; "60 seconds", 60; "2 minutes", 120; "5 minutes", 300 ]
            p.Children.Add(
                card "Check for new reminders every" "How often your Outlook calendar and mailbox are polled"
                    (combo pollItems cfg.PollIntervalSeconds (fun v -> change (fun c -> { c with PollIntervalSeconds = v }))) false) |> ignore
            p.Children.Add(
                card "Check now" "Poll immediately instead of waiting for the next tick"
                    (button "Check now" callbacks.OnCheckNow) false) |> ignore
            p.Children.Add(sectionHeader "Tray icon") |> ignore
            let trayItems = [ "Open Settings", true; "Show menu only", false ]
            p.Children.Add(
                card "When you click the tray icon" "What a single left-click of the notification-area icon does"
                    (combo trayItems cfg.TrayClickOpensSettings (fun v -> change (fun c -> { c with TrayClickOpensSettings = v }))) false) |> ignore
            p.Children.Add(note "Changes are saved and applied as you make them. A new check interval takes effect on the next tick.") |> ignore
            sv :> UIElement

        let meetingPanel () =
            let sv, p = mkPanel ()
            p.Children.Add(pageTitle "Meeting reminders") |> ignore
            p.Children.Add(
                card "Final reminder" "Minutes before start for the always-on final alert"
                    (stepper cfg.FinalReminderMinutes 1 60 "min" (fun v -> change (fun c -> { c with FinalReminderMinutes = v }))) false) |> ignore
            p.Children.Add(note "The first reminder for each meeting uses its own Outlook reminder time. This final reminder always fires, even when the meeting's Outlook reminder is off.") |> ignore
            p.Children.Add(sectionHeader "Sound") |> ignore
            p.Children.Add(
                card "Play a sound for meeting reminders" "Chime when a meeting reminder appears"
                    (makeToggle cfg.SoundEnabled (fun v -> change (fun c -> { c with SoundEnabled = v }))) false) |> ignore
            p.Children.Add(
                card "Only chime on the final reminder" "Earlier meeting reminders appear silently"
                    (makeToggle cfg.SoundOnFinalOnly (fun v -> change (fun c -> { c with SoundOnFinalOnly = v }))) true) |> ignore
            p.Children.Add(
                card "Volume" "Loudness of meeting reminder chimes"
                    (volumeSlider cfg.Volume (fun v -> change (fun c -> { c with Volume = v }))) false) |> ignore
            let deviceItems = ("System default", "") :: (callbacks.DeviceNames () |> List.map (fun d -> d, d))
            p.Children.Add(
                card "Output device" "Where reminder sounds play"
                    (combo deviceItems cfg.AudioDeviceName (fun v -> change (fun c -> { c with AudioDeviceName = v }))) false) |> ignore
            sv :> UIElement

        let emailPanel () =
            let sv, p = mkPanel ()
            p.Children.Add(pageTitle "Email reminders") |> ignore
            p.Children.Add(note "Surface matching emails as check-off reminders. Changes to rules and folders take effect after a restart.") |> ignore

            let rulesHeader = Grid(Margin = Thickness(2.0, 18.0, 2.0, 6.0))
            rulesHeader.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
            rulesHeader.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
            let rh = text "Rules" 13.0 FontWeights.SemiBold palette.Subtle
            Grid.SetColumn(rh, 0)
            rulesHeader.Children.Add rh |> ignore
            let rulesPanel = StackPanel(Orientation = Orientation.Vertical)

            let rec renderRules () =
                rulesPanel.Children.Clear()
                if Array.isEmpty cfg.EmailRules then
                    rulesPanel.Children.Add(note "No rules yet — add one to start matching emails.") |> ignore
                else
                    cfg.EmailRules
                    |> Array.iteri (fun i r ->
                        let subtitle =
                            [ if not (String.IsNullOrWhiteSpace r.From) then sprintf "From %s" r.From
                              if not (String.IsNullOrWhiteSpace r.RecipientContains) then sprintf "To/Cc %s" r.RecipientContains
                              if not (String.IsNullOrWhiteSpace r.SubjectContains) then sprintf "Subject contains %s" r.SubjectContains ]
                            |> String.concat "   ·   "
                        let actions = StackPanel(Orientation = Orientation.Horizontal)
                        let edit = styleButton (Button(Content = "Edit", Margin = Thickness(0.0, 0.0, 6.0, 0.0)))
                        let del = styleButton (Button(Content = "Delete"))
                        edit.Click.Add(fun _ -> editRule (Some i))
                        del.Click.Add(fun _ ->
                            change (fun c -> { c with EmailRules = c.EmailRules |> Array.indexed |> Array.filter (fun (j, _) -> j <> i) |> Array.map snd })
                            renderRules ())
                        actions.Children.Add edit |> ignore
                        actions.Children.Add del |> ignore
                        let name = if String.IsNullOrWhiteSpace r.Name then "(unnamed rule)" else r.Name
                        rulesPanel.Children.Add(card name subtitle (actions :> UIElement) false) |> ignore)

            and editRule (index: int option) =
                let existing =
                    match index with
                    | Some i when i < Array.length cfg.EmailRules -> cfg.EmailRules.[i]
                    | _ -> { Name = ""; From = ""; RecipientContains = ""; SubjectContains = "" }
                match showRuleDialog existing with
                | Some r ->
                    change (fun c ->
                        match index with
                        | Some i -> { c with EmailRules = c.EmailRules |> Array.mapi (fun j x -> if j = i then r else x) }
                        | None -> { c with EmailRules = Array.append c.EmailRules [| r |] })
                    renderRules ()
                | None -> ()

            let addRule = styleButton (Button(Content = "Add rule"))
            addRule.Background <- brush palette.Accent
            addRule.Foreground <- Brushes.White
            addRule.BorderBrush <- brush palette.Accent
            addRule.Click.Add(fun _ -> editRule None)
            Grid.SetColumn(addRule, 1)
            rulesHeader.Children.Add addRule |> ignore

            p.Children.Add rulesHeader |> ignore
            p.Children.Add rulesPanel |> ignore

            p.Children.Add(sectionHeader "Scanning") |> ignore
            p.Children.Add(
                card "Look back on startup" "Catch reminders that arrived just before the app started"
                    (stepper cfg.EmailLookbackMinutes 0 1440 "min" (fun v -> change (fun c -> { c with EmailLookbackMinutes = v }))) false) |> ignore

            let foldersCard = Border(Background = brush palette.CardBg, BorderBrush = brush palette.CardBorder, BorderThickness = Thickness 1.0, CornerRadius = CornerRadius 6.0, Padding = Thickness(16.0, 12.0, 16.0, 12.0), Margin = Thickness(0.0, 0.0, 0.0, 3.0))
            let foldersStack = StackPanel(Orientation = Orientation.Vertical)
            foldersStack.Children.Add(text "Folders to scan" 14.0 FontWeights.Normal palette.Text) |> ignore
            foldersStack.Children.Add(text "Scanned in addition to the Inbox" 12.0 FontWeights.Normal palette.Subtle) |> ignore
            let foldersPanel = WrapPanel(Orientation = Orientation.Horizontal, Margin = Thickness(0.0, 8.0, 0.0, 0.0))
            foldersStack.Children.Add foldersPanel |> ignore
            foldersCard.Child <- foldersStack

            let chip (label: string) (onRemove: (unit -> unit) option) : UIElement =
                let b = Border(Background = brush palette.ControlBg, BorderBrush = brush palette.CardBorder, BorderThickness = Thickness 1.0, CornerRadius = CornerRadius 14.0, Padding = Thickness(12.0, 4.0, 8.0, 4.0), Margin = Thickness(0.0, 0.0, 8.0, 8.0))
                let sp = StackPanel(Orientation = Orientation.Horizontal)
                sp.Children.Add(text label 13.0 FontWeights.Normal palette.Text) |> ignore
                match onRemove with
                | Some f ->
                    let x = Button(Content = "✕", FontSize = 11.0, Foreground = brush palette.Subtle, Background = Brushes.Transparent, BorderThickness = Thickness 0.0, Margin = Thickness(6.0, 0.0, 0.0, 0.0), Cursor = Cursors.Hand, Padding = Thickness 0.0)
                    x.Click.Add(fun _ -> f ())
                    sp.Children.Add x |> ignore
                | None -> ()
                b.Child <- sp
                b :> UIElement

            let rec renderFolders () =
                foldersPanel.Children.Clear()
                foldersPanel.Children.Add(chip "Inbox" None) |> ignore
                cfg.EmailIncludedFolders
                |> Array.iteri (fun i name ->
                    foldersPanel.Children.Add(
                        chip name (Some (fun () ->
                            change (fun c -> { c with EmailIncludedFolders = c.EmailIncludedFolders |> Array.indexed |> Array.filter (fun (j, _) -> j <> i) |> Array.map snd })
                            renderFolders ()))) |> ignore)
                let add = styleButton (Button(Content = "+ Add folder"))
                add.Foreground <- brush palette.Accent
                add.Click.Add(fun _ ->
                    match showInputDialog "Add folder" "Folder display name (as it appears in Outlook):" with
                    | Some name when not (String.IsNullOrWhiteSpace name) ->
                        change (fun c -> { c with EmailIncludedFolders = Array.append c.EmailIncludedFolders [| name.Trim() |] })
                        renderFolders ()
                    | _ -> ())
                foldersPanel.Children.Add add |> ignore

            p.Children.Add foldersCard |> ignore

            p.Children.Add(sectionHeader "Sound") |> ignore
            p.Children.Add(
                card "Play a sound for email reminders" "Off by default — email reminders are to-dos you check off at your own pace"
                    (makeToggle cfg.EmailSoundEnabled (fun v -> change (fun c -> { c with EmailSoundEnabled = v }))) false) |> ignore
            p.Children.Add(
                card "Volume" "Loudness of the email reminder chime"
                    (volumeSlider cfg.EmailVolume (fun v -> change (fun c -> { c with EmailVolume = v }))) true) |> ignore

            renderRules ()
            renderFolders ()
            sv :> UIElement

        let accountPanel () =
            let sv, p = mkPanel ()
            p.Children.Add(pageTitle "Account") |> ignore
            let statusRow = StackPanel(Orientation = Orientation.Horizontal)
            statusRow.Children.Add(button "Sign out" callbacks.OnSignOut) |> ignore
            p.Children.Add(card "Microsoft 365" (callbacks.StatusText ()) (statusRow :> UIElement) false) |> ignore

            p.Children.Add(sectionHeader "Azure AD app registration") |> ignore
            let field (label: string) (value: string) (onChange: string -> unit) : UIElement =
                let border = Border(Background = brush palette.CardBg, BorderBrush = brush palette.CardBorder, BorderThickness = Thickness 1.0, CornerRadius = CornerRadius 6.0, Padding = Thickness(16.0, 12.0, 16.0, 12.0), Margin = Thickness(0.0, 0.0, 0.0, 3.0))
                let stack = StackPanel(Orientation = Orientation.Vertical)
                let lbl = text label 13.0 FontWeights.Normal palette.Text
                lbl.Margin <- Thickness(0.0, 0.0, 0.0, 7.0)
                let box = TextBox(Text = value, FontFamily = fontFamily, FontSize = 13.0, Foreground = brush palette.Text, Background = brush palette.ControlBg, BorderBrush = brush palette.CardBorder, BorderThickness = Thickness 1.0, Padding = Thickness(8.0, 5.0, 8.0, 5.0))
                box.TextChanged.Add(fun _ -> onChange box.Text)
                stack.Children.Add lbl |> ignore
                stack.Children.Add box |> ignore
                border.Child <- stack
                border :> UIElement
            p.Children.Add(field "Application (client) ID" cfg.ClientId (fun v -> change (fun c -> { c with ClientId = v.Trim() }))) |> ignore
            p.Children.Add(field "Directory (tenant) ID" cfg.TenantId (fun v -> change (fun c -> { c with TenantId = v.Trim() }))) |> ignore

            let help = TextBlock(Margin = Thickness(4.0, 7.0, 0.0, 0.0), FontFamily = fontFamily, FontSize = 12.0, Foreground = brush palette.Subtle, TextWrapping = TextWrapping.Wrap)
            help.Inlines.Add(Run "These come from your Azure AD app registration. ")
            let link = Hyperlink(Run "How do I get these?", NavigateUri = Uri "https://github.com/daniellittledev/meeting-notifier#first-run-setup")
            link.RequestNavigate.Add(fun e ->
                (try Process.Start(ProcessStartInfo(e.Uri.AbsoluteUri, UseShellExecute = true)) |> ignore with _ -> ())
                e.Handled <- true)
            help.Inlines.Add link
            p.Children.Add help |> ignore

            let actions = StackPanel(Orientation = Orientation.Horizontal, Margin = Thickness(0.0, 16.0, 0.0, 0.0))
            actions.Children.Add(button "Reconnect" callbacks.OnReconnect) |> ignore
            p.Children.Add actions |> ignore
            p.Children.Add(note "Settings apply as you change them. New IDs take effect when you reconnect.") |> ignore
            sv :> UIElement

        let aboutPanel () =
            let sv, p = mkPanel ()
            p.Children.Add(pageTitle "About") |> ignore
            let version =
                match Assembly.GetExecutingAssembly().GetName().Version with
                | null -> "0.0.0"
                | v -> v.ToString()
            p.Children.Add(card "Outlook Notifier" (sprintf "Version %s" version) (TextBlock() :> UIElement) false) |> ignore
            sv :> UIElement

        // ----- nav + layout -----

        let pages =
            [ "General", generalPanel
              "Meeting reminders", meetingPanel
              "Email reminders", emailPanel
              "Account", accountPanel
              "About", aboutPanel ]

        let contentHost = Grid()
        let builtPages =
            pages
            |> List.map (fun (_, build) ->
                let el = build ()
                el.Visibility <- Visibility.Collapsed
                contentHost.Children.Add el |> ignore
                el)

        let nav = ListBox(BorderThickness = Thickness 0.0, Background = Brushes.Transparent, FontFamily = fontFamily, FontSize = 14.0, Padding = Thickness(8.0, 8.0, 8.0, 8.0))
        do
            for (label, _) in pages do
                let item = ListBoxItem(Content = label, Padding = Thickness(12.0, 8.0, 12.0, 8.0), Foreground = brush palette.Text)
                nav.Items.Add item |> ignore
            nav.SelectionChanged.Add(fun _ ->
                builtPages |> List.iteri (fun i el ->
                    el.Visibility <- if i = nav.SelectedIndex then Visibility.Visible else Visibility.Collapsed))
            nav.SelectedIndex <- 0

        let root = Grid()
        do
            root.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength 220.0))
            root.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
            let navBorder = Border(Background = brush palette.WindowBg, Child = nav)
            Grid.SetColumn(navBorder, 0)
            Grid.SetColumn(contentHost, 1)
            root.Children.Add navBorder |> ignore
            root.Children.Add contentHost |> ignore

        do
            window.Title <- "Settings — Outlook Notifier"
            window.Width <- 780.0
            window.Height <- 640.0
            window.MinWidth <- 640.0
            window.MinHeight <- 480.0
            window.WindowStartupLocation <- WindowStartupLocation.CenterScreen
            window.Background <- brush palette.WindowBg
            window.FontFamily <- fontFamily
            window.Foreground <- brush palette.Text
            window.Resources.["Accent"] <- brush palette.Accent
            window.Resources.["Subtle"] <- brush palette.Subtle
            window.Resources.["ToggleBorder"] <- brush palette.ToggleBorder
            window.Content <- root
            window.SourceInitialized.Add(fun _ ->
                hwnd <- WindowInteropHelper(window).Handle
                setDarkTitleBar hwnd palette.IsDark)

        /// Show the window, bringing it to the front if already open.
        member _.Show() =
            if window.IsVisible then
                if window.WindowState = WindowState.Minimized then window.WindowState <- WindowState.Normal
                window.Activate() |> ignore
            else
                window.Show()
                window.Activate() |> ignore

        /// True while the window is on screen.
        member _.IsOpen = window.IsVisible

        member _.Close() =
            try window.Close() with _ -> ()
