namespace OutlookNotifier

open System
open System.Diagnostics
open System.Reflection
open System.Runtime.InteropServices
open System.Windows
open System.Windows.Automation
open System.Windows.Controls
open System.Windows.Controls.Primitives
open System.Windows.Documents
open System.Windows.Interop
open System.Windows.Markup
open System.Windows.Media
open Microsoft.Win32

/// The Windows 11-styled Settings window. Layout is built in code (like
/// ReminderWindow) so the wiring is compiler-checked; the Fluent look comes
/// from control templates parsed once from XAML. Every change is applied live
/// via `OnChanged`, which the tray uses to persist and push the new config
/// into the running app.
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

    // ----- Theme (light/dark from the Windows personalization setting) -----

    type private Palette =
        { IsDark: bool
          WindowBg: Color
          CardBg: Color
          CardBorder: Color
          Text: Color
          Subtle: Color
          Accent: Color
          AccentHover: Color
          AccentText: Color
          ControlBg: Color
          ControlHover: Color
          ControlPressed: Color
          ControlBorder: Color
          NavHover: Color
          NavSelected: Color
          TrackBg: Color
          ThumbOuter: Color
          PopupBg: Color
          ToggleBorder: Color
          ScrollThumb: Color }

    let private rgb (r, g, b) = Color.FromRgb(byte r, byte g, byte b)

    let private darkPalette =
        { IsDark = true
          WindowBg = rgb (32, 32, 32)
          CardBg = rgb (43, 43, 43)
          CardBorder = rgb (29, 29, 29)
          Text = rgb (255, 255, 255)
          Subtle = rgb (200, 200, 200)
          Accent = rgb (96, 205, 255)
          AccentHover = rgb (88, 185, 230)
          AccentText = rgb (0, 0, 0)
          ControlBg = rgb (55, 55, 55)
          ControlHover = rgb (62, 62, 62)
          ControlPressed = rgb (48, 48, 48)
          ControlBorder = rgb (72, 72, 72)
          NavHover = rgb (41, 41, 41)
          NavSelected = rgb (47, 47, 47)
          TrackBg = rgb (150, 150, 150)
          ThumbOuter = rgb (69, 69, 69)
          PopupBg = rgb (44, 44, 44)
          ToggleBorder = rgb (200, 200, 200)
          ScrollThumb = rgb (159, 159, 159) }

    let private lightPalette =
        { IsDark = false
          WindowBg = rgb (243, 243, 243)
          CardBg = rgb (251, 251, 251)
          CardBorder = rgb (229, 229, 229)
          Text = rgb (26, 26, 26)
          Subtle = rgb (96, 96, 96)
          Accent = rgb (0, 95, 184)
          AccentHover = rgb (26, 110, 196)
          AccentText = rgb (255, 255, 255)
          ControlBg = rgb (254, 254, 254)
          ControlHover = rgb (246, 246, 246)
          ControlPressed = rgb (240, 240, 240)
          ControlBorder = rgb (214, 214, 214)
          NavHover = rgb (234, 234, 234)
          NavSelected = rgb (230, 230, 230)
          TrackBg = rgb (138, 138, 138)
          ThumbOuter = rgb (255, 255, 255)
          PopupBg = rgb (249, 249, 249)
          ToggleBorder = rgb (120, 120, 120)
          ScrollThumb = rgb (138, 138, 138) }

    /// The DynamicResource keys every template below reads.
    let private themeKeys (p: Palette) =
        [ "Text", p.Text
          "Subtle", p.Subtle
          "Accent", p.Accent
          "AccentHover", p.AccentHover
          "AccentText", p.AccentText
          "ControlBg", p.ControlBg
          "ControlHover", p.ControlHover
          "ControlPressed", p.ControlPressed
          "ControlBorder", p.ControlBorder
          "NavHover", p.NavHover
          "NavSelected", p.NavSelected
          "TrackBg", p.TrackBg
          "ThumbOuter", p.ThumbOuter
          "PopupBg", p.PopupBg
          "ToggleBorder", p.ToggleBorder
          "ScrollThumb", p.ScrollThumb ]

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
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+/Win11)
            let mutable v = if dark then 1 else 0
            DwmSetWindowAttribute(hwnd, 20, &v, 4) |> ignore

    /// Segoe Fluent Icons (Windows 11), falling back to Segoe MDL2 Assets.
    module private Glyph =
        let font = FontFamily "Segoe Fluent Icons, Segoe MDL2 Assets"
        let settings = ""
        let calendar = ""
        let mail = ""
        let contact = ""
        let info = ""
        let pin = ""
        let sync = ""
        let refresh = ""
        let monitor = ""
        let clock = ""
        let volume = ""
        let speakers = ""
        let history = ""
        let folder = ""
        let lock = ""
        let add = ""
        let remove = ""
        let close = ""

    // Fluent-style control templates. Colours are DynamicResource keys (see
    // `themeKeys`) set on each window, so one dictionary serves light and dark.
    let private stylesXaml =
        """<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <Style x:Key="ToggleSwitch" TargetType="ToggleButton">
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToggleButton">
          <Border x:Name="track" Width="40" Height="20" CornerRadius="10" Background="Transparent"
                  BorderThickness="1" BorderBrush="{DynamicResource ToggleBorder}" SnapsToDevicePixels="True">
            <Ellipse x:Name="thumb" Width="12" Height="12" HorizontalAlignment="Left" VerticalAlignment="Center"
                     Margin="4,0,0,0" Fill="{DynamicResource Subtle}"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="track" Property="Background" Value="{DynamicResource ControlHover}"/>
            </Trigger>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="track" Property="Background" Value="{DynamicResource Accent}"/>
              <Setter TargetName="track" Property="BorderBrush" Value="{DynamicResource Accent}"/>
              <Setter TargetName="thumb" Property="HorizontalAlignment" Value="Right"/>
              <Setter TargetName="thumb" Property="Margin" Value="0,0,4,0"/>
              <Setter TargetName="thumb" Property="Fill" Value="{DynamicResource AccentText}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="track" Property="BorderThickness" Value="2"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Btn" TargetType="Button">
    <Setter Property="Foreground" Value="{DynamicResource Text}"/>
    <Setter Property="Background" Value="{DynamicResource ControlBg}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource ControlBorder}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="12,0"/>
    <Setter Property="MinHeight" Value="32"/>
    <Setter Property="MinWidth" Value="72"/>
    <Setter Property="FontSize" Value="14"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="HorizontalContentAlignment" Value="Center"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4"
                  Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="Background" Value="{DynamicResource ControlHover}"/>
      </Trigger>
      <Trigger Property="IsPressed" Value="True">
        <Setter Property="Background" Value="{DynamicResource ControlPressed}"/>
      </Trigger>
      <Trigger Property="IsKeyboardFocused" Value="True">
        <Setter Property="BorderBrush" Value="{DynamicResource Text}"/>
      </Trigger>
      <Trigger Property="IsEnabled" Value="False">
        <Setter Property="Opacity" Value="0.5"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style x:Key="AccentBtn" TargetType="Button" BasedOn="{StaticResource Btn}">
    <Setter Property="Background" Value="{DynamicResource Accent}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource Accent}"/>
    <Setter Property="Foreground" Value="{DynamicResource AccentText}"/>
    <Style.Triggers>
      <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="Background" Value="{DynamicResource AccentHover}"/>
      </Trigger>
      <Trigger Property="IsPressed" Value="True">
        <Setter Property="Background" Value="{DynamicResource AccentHover}"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style x:Key="IconBtn" TargetType="Button" BasedOn="{StaticResource Btn}">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="MinWidth" Value="32"/>
    <Setter Property="MinHeight" Value="28"/>
    <Setter Property="Padding" Value="0"/>
    <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets"/>
    <Setter Property="FontSize" Value="12"/>
  </Style>

  <Style x:Key="PillBtn" TargetType="Button" BasedOn="{StaticResource Btn}">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Foreground" Value="{DynamicResource Accent}"/>
    <Setter Property="MinHeight" Value="30"/>
    <Setter Property="Height" Value="30"/>
    <Setter Property="MinWidth" Value="0"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="15"
                  Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Field" TargetType="TextBox">
    <Setter Property="Foreground" Value="{DynamicResource Text}"/>
    <Setter Property="CaretBrush" Value="{DynamicResource Text}"/>
    <Setter Property="Background" Value="{DynamicResource ControlBg}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource ControlBorder}"/>
    <Setter Property="FontSize" Value="14"/>
    <Setter Property="MinHeight" Value="32"/>
    <Setter Property="Padding" Value="8,4"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="1" CornerRadius="4" SnapsToDevicePixels="True">
            <Grid>
              <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" VerticalAlignment="Center"/>
              <Border x:Name="focusLine" Height="2" VerticalAlignment="Bottom" CornerRadius="0,0,4,4"
                      Background="{DynamicResource Accent}" Visibility="Collapsed"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{DynamicResource ControlHover}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="focusLine" Property="Visibility" Value="Visible"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="ComboItem" TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="{DynamicResource Text}"/>
    <Setter Property="Padding" Value="10,6"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="bd" Background="Transparent" CornerRadius="4" Margin="4,1" Padding="{TemplateBinding Padding}">
            <ContentPresenter VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsSelected" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{DynamicResource NavSelected}"/>
            </Trigger>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{DynamicResource ControlHover}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="Combo" TargetType="ComboBox">
    <Setter Property="Foreground" Value="{DynamicResource Text}"/>
    <Setter Property="FontSize" Value="14"/>
    <Setter Property="MinHeight" Value="32"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="ItemContainerStyle" Value="{StaticResource ComboItem}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton Focusable="False" ClickMode="Press" Cursor="Hand"
                          IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="bd" Background="{DynamicResource ControlBg}" BorderBrush="{DynamicResource ControlBorder}"
                          BorderThickness="1" CornerRadius="4" SnapsToDevicePixels="True">
                    <TextBlock Text="&#xE70D;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="10"
                               Foreground="{DynamicResource Subtle}" HorizontalAlignment="Right"
                               VerticalAlignment="Center" Margin="0,0,12,0"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="bd" Property="Background" Value="{DynamicResource ControlHover}"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible="False" Margin="12,0,32,0" VerticalAlignment="Center"
                              HorizontalAlignment="Left" Content="{TemplateBinding SelectionBoxItem}"
                              ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                   PopupAnimation="Fade" IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}">
              <Border Background="{DynamicResource PopupBg}" BorderBrush="{DynamicResource ControlBorder}"
                      BorderThickness="1" CornerRadius="6" Padding="0,4" Margin="0,2,0,0"
                      MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                      MaxHeight="{TemplateBinding MaxDropDownHeight}">
                <ScrollViewer>
                  <ItemsPresenter/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="SliderRepeat" TargetType="RepeatButton">
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="IsTabStop" Value="False"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="RepeatButton">
          <Border Background="Transparent">
            <Border Height="4" CornerRadius="2" VerticalAlignment="Center" Background="{TemplateBinding Background}"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="SliderThumb" TargetType="Thumb">
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Thumb">
          <Grid Width="20" Height="20">
            <Ellipse Fill="{DynamicResource ThumbOuter}" Stroke="{DynamicResource ControlBorder}" StrokeThickness="1"/>
            <Ellipse x:Name="dot" Width="10" Height="10" Fill="{DynamicResource Accent}"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="dot" Property="Width" Value="12"/>
              <Setter TargetName="dot" Property="Height" Value="12"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="FluentSlider" TargetType="Slider">
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Slider">
          <Grid MinHeight="24" Background="Transparent">
            <Track x:Name="PART_Track">
              <Track.DecreaseRepeatButton>
                <RepeatButton Style="{StaticResource SliderRepeat}" Background="{DynamicResource Accent}"
                              Command="{x:Static Slider.DecreaseLarge}"/>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style="{StaticResource SliderRepeat}" Background="{DynamicResource TrackBg}"
                              Command="{x:Static Slider.IncreaseLarge}"/>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style="{StaticResource SliderThumb}"/>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="NavItem" TargetType="ListBoxItem">
    <Setter Property="Foreground" Value="{DynamicResource Text}"/>
    <Setter Property="FontSize" Value="14"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ListBoxItem">
          <Border x:Name="bd" Background="Transparent" CornerRadius="4" Height="36" Margin="0,2">
            <Grid>
              <Border x:Name="pill" Width="3" Height="16" CornerRadius="1.5" Background="{DynamicResource Accent}"
                      HorizontalAlignment="Left" VerticalAlignment="Center" Visibility="Collapsed"/>
              <ContentPresenter Margin="14,0,12,0" VerticalAlignment="Center"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{DynamicResource NavHover}"/>
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{DynamicResource NavSelected}"/>
              <Setter TargetName="pill" Property="Visibility" Value="Visible"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ScrollBar">
    <Style.Triggers>
      <Trigger Property="Orientation" Value="Vertical">
        <Setter Property="Width" Value="12"/>
        <Setter Property="MinWidth" Value="12"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="ScrollBar">
              <Grid Background="Transparent">
                <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True">
                  <Track.Thumb>
                    <Thumb>
                      <Thumb.Template>
                        <ControlTemplate TargetType="Thumb">
                          <Border x:Name="bar" Width="4" CornerRadius="2" HorizontalAlignment="Center"
                                  Background="{DynamicResource ScrollThumb}"/>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                              <Setter TargetName="bar" Property="Width" Value="8"/>
                              <Setter TargetName="bar" Property="CornerRadius" Value="4"/>
                            </Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                </Track>
              </Grid>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>

</ResourceDictionary>"""

    let private hasText (s: string) = not (String.IsNullOrWhiteSpace s)

    /// Single reusable Settings window. All members run on the UI (STA) thread.
    type SettingsView(initial: Config.AppConfig, callbacks: Callbacks) =

        let palette = currentPalette ()
        let brush (c: Color) = SolidColorBrush c :> Brush
        let fontFamily = FontFamily "Segoe UI Variable Text, Segoe UI"
        let styles = XamlReader.Parse stylesXaml :?> ResourceDictionary
        let style (key: string) = styles.[key] :?> Style
        let ui (x: #UIElement) = x :> UIElement

        let mutable cfg = initial
        let window = Window()

        // Push the latest config out to the host (persist + live-apply).
        let change (f: Config.AppConfig -> Config.AppConfig) =
            cfg <- f cfg
            callbacks.OnChanged cfg

        // The brushes every template reads, plus chrome; dialogs share it.
        let applyTheme (w: Window) =
            for (key, c) in themeKeys palette do
                w.Resources.[key] <- brush c
            w.Background <- brush palette.WindowBg
            w.Foreground <- brush palette.Text
            w.FontFamily <- fontFamily
            w.FontSize <- 14.0
            w.SourceInitialized.Add(fun _ ->
                setDarkTitleBar (WindowInteropHelper(w).Handle) palette.IsDark)

        // ----- text -----

        let text (s: string) size weight color =
            TextBlock(
                Text = s,
                FontSize = size,
                FontWeight = weight,
                FontFamily = fontFamily,
                Foreground = brush color,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center)

        let pageTitle (s: string) =
            let t = text s 28.0 FontWeights.SemiBold palette.Text
            t.Margin <- Thickness(0.0, 0.0, 0.0, 16.0)
            ui t

        let sectionHeader (s: string) =
            let t = text s 14.0 FontWeights.SemiBold palette.Text
            t.Margin <- Thickness(2.0, 20.0, 2.0, 8.0)
            ui t

        let note (s: string) =
            let t = text s 12.0 FontWeights.Normal palette.Subtle
            t.Margin <- Thickness(2.0, 4.0, 2.0, 4.0)
            ui t

        let glyphText (glyph: string) size =
            TextBlock(
                Text = glyph,
                FontFamily = Glyph.font,
                FontSize = size,
                VerticalAlignment = VerticalAlignment.Center)

        // ----- cards -----

        // The bordered surface every setting sits on. A sub-setting keeps the
        // same edges and indents its content to line up with its parent's
        // title (16 padding + 24 icon + 16 gap). Disabled cards dim.
        let cardShell (sub: bool) =
            let b =
                Border(
                    Background = brush palette.CardBg,
                    BorderBrush = brush palette.CardBorder,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius 6.0,
                    MinHeight = 68.0,
                    Margin = Thickness(0.0, 0.0, 0.0, 4.0),
                    Padding = Thickness((if sub then 56.0 else 16.0), 12.0, 16.0, 12.0))
            b.IsEnabledChanged.Add(fun _ -> b.Opacity <- (if b.IsEnabled then 1.0 else 0.5))
            b

        let cardIcon (glyph: string) =
            let t = glyphText glyph 18.0
            t.Foreground <- brush palette.Text
            t.Width <- 24.0
            t.Margin <- Thickness(0.0, 0.0, 16.0, 0.0)
            t

        let titleBlock (title: string) (subtitle: string) =
            let texts = StackPanel(VerticalAlignment = VerticalAlignment.Center)
            texts.Children.Add(text title 14.0 FontWeights.Normal palette.Text) |> ignore
            if hasText subtitle then
                let s = text subtitle 12.0 FontWeights.Normal palette.Subtle
                s.Margin <- Thickness(0.0, 2.0, 0.0, 0.0)
                texts.Children.Add s |> ignore
            texts

        /// One setting: optional icon, title and subtitle on the left, its
        /// control right-aligned and vertically centred.
        let card (icon: string) (title: string) (subtitle: string) (control: UIElement) (sub: bool) =
            let border = cardShell sub
            let grid = Grid()
            grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
            grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
            grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
            if hasText icon then
                let i = cardIcon icon
                Grid.SetColumn(i, 0)
                grid.Children.Add i |> ignore
            let texts = titleBlock title subtitle
            Grid.SetColumn(texts, 1)
            grid.Children.Add texts |> ignore
            let host =
                ContentControl(
                    Content = control,
                    Focusable = false,
                    IsTabStop = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = Thickness(16.0, 0.0, 0.0, 0.0))
            Grid.SetColumn(host, 2)
            grid.Children.Add host |> ignore
            border.Child <- grid
            border

        // ----- controls -----

        let button (label: string) (onClick: unit -> unit) =
            let b = Button(Content = label, Style = style "Btn")
            b.Click.Add(fun _ -> onClick ())
            b

        let accentButton (label: string) (onClick: unit -> unit) =
            let b = button label onClick
            b.Style <- style "AccentBtn"
            b

        let iconButton (glyph: string) (name: string) (onClick: unit -> unit) =
            let b = Button(Content = glyph, Style = style "IconBtn", ToolTip = name)
            AutomationProperties.SetName(b, name)
            b.Click.Add(fun _ -> onClick ())
            b

        /// A Win11 toggle switch with its On/Off state label to the left.
        let toggle (name: string) (isOn: bool) (onChange: bool -> unit) =
            let state = text (if isOn then "On" else "Off") 14.0 FontWeights.Normal palette.Text
            state.Margin <- Thickness(0.0, 0.0, 12.0, 0.0)
            let tb = ToggleButton(IsChecked = Nullable isOn, Style = style "ToggleSwitch")
            AutomationProperties.SetName(tb, name)
            tb.Checked.Add(fun _ ->
                state.Text <- "On"
                onChange true)
            tb.Unchecked.Add(fun _ ->
                state.Text <- "Off"
                onChange false)
            let row = StackPanel(Orientation = Orientation.Horizontal)
            row.Children.Add state |> ignore
            row.Children.Add tb |> ignore
            ui row

        /// A combo over (label, value) pairs, calling back with the value.
        let combo (name: string) (items: (string * 'a) list) (selected: 'a) (onChange: 'a -> unit) =
            let cb = ComboBox(Style = style "Combo", Width = 200.0)
            AutomationProperties.SetName(cb, name)
            for (label, _) in items do
                cb.Items.Add label |> ignore
            cb.SelectedIndex <-
                items |> List.tryFindIndex (fun (_, v) -> v = selected) |> Option.defaultValue 0
            cb.SelectionChanged.Add(fun _ ->
                let i = cb.SelectedIndex
                if i >= 0 && i < List.length items then onChange (snd (List.item i items)))
            ui cb

        /// A number box: value with − / + buttons, clamped to [lo, hi].
        let stepper (name: string) (value: int) (lo: int) (hi: int) (unitLabel: string) (onChange: int -> unit) =
            let valueText = text "" 14.0 FontWeights.Normal palette.Text
            valueText.MinWidth <- 64.0
            valueText.TextAlignment <- TextAlignment.Center
            valueText.Margin <- Thickness(8.0, 0.0, 4.0, 0.0)
            let mutable v = value
            let render () = valueText.Text <- sprintf "%d %s" v unitLabel
            let set nv =
                let nv = max lo (min hi nv)
                if nv <> v then
                    v <- nv
                    render ()
                    onChange v
            let row = StackPanel(Orientation = Orientation.Horizontal)
            row.Children.Add valueText |> ignore
            row.Children.Add(iconButton Glyph.remove (sprintf "Decrease %s" name) (fun () -> set (v - 1))) |> ignore
            row.Children.Add(iconButton Glyph.add (sprintf "Increase %s" name) (fun () -> set (v + 1))) |> ignore
            render ()
            let box =
                Border(
                    Child = row,
                    Background = brush palette.ControlBg,
                    BorderBrush = brush palette.ControlBorder,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius 4.0,
                    Height = 32.0,
                    Padding = Thickness(0.0, 1.0, 1.0, 1.0))
            ui box

        /// A Fluent slider with a live percentage readout.
        let volumeSlider (name: string) (minimum: float) (value: int) (onChange: int -> unit) =
            let slider =
                Slider(
                    Style = style "FluentSlider",
                    Minimum = minimum,
                    Maximum = 100.0,
                    Value = float value,
                    Width = 180.0,
                    SmallChange = 1.0,
                    LargeChange = 10.0,
                    IsMoveToPointEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center)
            AutomationProperties.SetName(slider, name)
            let readout = text (sprintf "%d%%" (int slider.Value)) 14.0 FontWeights.Normal palette.Text
            readout.MinWidth <- 44.0
            readout.TextAlignment <- TextAlignment.Right
            readout.Margin <- Thickness(12.0, 0.0, 0.0, 0.0)
            slider.ValueChanged.Add(fun _ ->
                let v = int (Math.Round slider.Value)
                readout.Text <- sprintf "%d%%" v
                onChange v)
            let row = StackPanel(Orientation = Orientation.Horizontal)
            row.Children.Add slider |> ignore
            row.Children.Add readout |> ignore
            ui row

        let input (name: string) (value: string) =
            let box = TextBox(Text = value, Style = style "Field")
            AutomationProperties.SetName(box, name)
            box

        /// Keep `items` as is when `current` is already one of them, otherwise
        /// add it so the combo shows the stored value instead of a wrong one.
        let withCurrent (label: string) (current: 'a) (items: (string * 'a) list) =
            if items |> List.exists (fun (_, v) -> v = current) then items
            else items @ [ label, current ]

        // ----- dialogs -----

        let dialog (title: string) =
            let dlg =
                Window(
                    Title = title,
                    Width = 440.0,
                    SizeToContent = SizeToContent.Height,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner)
            // Owner may only be a window that has been shown.
            if window.IsVisible then dlg.Owner <- window
            applyTheme dlg
            dlg

        let dialogButtons (primaryLabel: string) (onPrimary: unit -> unit) =
            let row =
                StackPanel(
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = Thickness(0.0, 8.0, 0.0, 0.0))
            let primary = accentButton primaryLabel onPrimary
            primary.IsDefault <- true
            primary.Margin <- Thickness(0.0, 0.0, 8.0, 0.0)
            let cancel = button "Cancel" ignore
            cancel.IsCancel <- true
            row.Children.Add primary |> ignore
            row.Children.Add cancel |> ignore
            row, primary

        let labelled (panel: StackPanel) (label: string) (box: TextBox) =
            let l = text label 14.0 FontWeights.Normal palette.Text
            l.Margin <- Thickness(0.0, 0.0, 0.0, 6.0)
            box.Margin <- Thickness(0.0, 0.0, 0.0, 16.0)
            panel.Children.Add l |> ignore
            panel.Children.Add box |> ignore

        let showInputDialog (title: string) (prompt: string) : string option =
            let dlg = dialog title
            let panel = StackPanel(Margin = Thickness 24.0)
            let box = input prompt ""
            labelled panel prompt box
            let mutable result = None
            let buttons, ok =
                dialogButtons "Add" (fun () ->
                    result <- Some(box.Text.Trim())
                    dlg.DialogResult <- Nullable true)
            ok.IsEnabled <- false
            box.TextChanged.Add(fun _ -> ok.IsEnabled <- hasText box.Text)
            panel.Children.Add buttons |> ignore
            dlg.Content <- panel
            box.Focus() |> ignore
            dlg.ShowDialog() |> ignore
            result

        let showRuleDialog (existing: Config.EmailRule) : Config.EmailRule option =
            let dlg = dialog "Email rule"
            let panel = StackPanel(Margin = Thickness 24.0)
            let nameBox = input "Name" existing.Name
            let fromBox = input "From contains" existing.From
            let recipBox = input "To or Cc contains" existing.RecipientContains
            let subjBox = input "Subject contains" existing.SubjectContains
            labelled panel "Name" nameBox
            labelled panel "From contains (sender address or name)" fromBox
            labelled panel "To or Cc contains (recipient address or name)" recipBox
            labelled panel "Subject contains" subjBox
            let current () : Config.EmailRule =
                { Name = nameBox.Text.Trim()
                  From = fromBox.Text.Trim()
                  RecipientContains = recipBox.Text.Trim()
                  SubjectContains = subjBox.Text.Trim() }
            let hint = note "Set at least one of From, To/Cc or Subject; every one you set must match."
            panel.Children.Add hint |> ignore
            let mutable result = None
            let buttons, save =
                dialogButtons "Save" (fun () ->
                    result <- Some(current ())
                    dlg.DialogResult <- Nullable true)
            // A rule with no criteria would match nothing (it's ignored), so
            // don't let one be saved.
            let validate () = save.IsEnabled <- EmailRules.isActive (current ())
            for b in [ fromBox; recipBox; subjBox ] do
                b.TextChanged.Add(fun _ -> validate ())
            validate ()
            panel.Children.Add buttons |> ignore
            dlg.Content <- panel
            nameBox.Focus() |> ignore
            dlg.ShowDialog() |> ignore
            result

        // ----- pages -----

        let mkPage (title: string) =
            let p = StackPanel(Margin = Thickness(28.0, 20.0, 28.0, 28.0))
            p.Children.Add(pageTitle title) |> ignore
            let sv =
                ScrollViewer(
                    Content = p,
                    Focusable = false,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
            sv, p

        let generalPage () =
            let sv, p = mkPage "General"
            p.Children.Add(
                card Glyph.pin "Keep alert windows on top"
                    "Reminder pop-ups stay above other apps until dismissed"
                    (toggle "Keep alert windows on top" cfg.KeepOnTop (fun v ->
                        change (fun c -> { c with KeepOnTop = v })))
                    false) |> ignore
            p.Children.Add(sectionHeader "Checking") |> ignore
            let pollItems =
                [ "30 seconds", 30; "1 minute", 60; "2 minutes", 120; "5 minutes", 300 ]
                |> withCurrent (sprintf "%d seconds" cfg.PollIntervalSeconds) cfg.PollIntervalSeconds
                |> List.sortBy snd
            p.Children.Add(
                card Glyph.sync "Check for new reminders every"
                    "How often Outlook is checked"
                    (combo "Check interval" pollItems cfg.PollIntervalSeconds (fun v ->
                        change (fun c -> { c with PollIntervalSeconds = v })))
                    false) |> ignore
            p.Children.Add(
                card Glyph.refresh "Check now"
                    "Poll immediately instead of waiting for the next check"
                    (ui (button "Check now" callbacks.OnCheckNow))
                    false) |> ignore
            p.Children.Add(sectionHeader "Tray icon") |> ignore
            p.Children.Add(
                card Glyph.monitor "When you click the tray icon"
                    "What a left-click on the tray icon does"
                    (combo "Tray icon click" [ "Open Settings", true; "Show menu only", false ]
                        cfg.TrayClickOpensSettings (fun v ->
                            change (fun c -> { c with TrayClickOpensSettings = v })))
                    false) |> ignore
            p.Children.Add(note "Changes are saved and applied as you make them.") |> ignore
            ui sv

        let meetingPage () =
            let sv, p = mkPage "Meeting reminders"
            p.Children.Add(
                card Glyph.clock "Final reminder"
                    "Minutes before start for the always-on final alert"
                    (stepper "final reminder" cfg.FinalReminderMinutes 1 60 "min" (fun v ->
                        change (fun c -> { c with FinalReminderMinutes = v })))
                    false) |> ignore
            p.Children.Add(
                note "The first reminder for each meeting uses its own Outlook reminder time. The final reminder always fires, even when the meeting's Outlook reminder is off.") |> ignore
            p.Children.Add(sectionHeader "Sound") |> ignore
            let finalOnly =
                card "" "Only chime on the final reminder" "Earlier meeting reminders appear silently"
                    (toggle "Only chime on the final reminder" cfg.SoundOnFinalOnly (fun v ->
                        change (fun c -> { c with SoundOnFinalOnly = v })))
                    true
            let volume =
                card "" "Volume" "Loudness of meeting reminder chimes"
                    (volumeSlider "Meeting reminder volume" 0.0 cfg.Volume (fun v ->
                        change (fun c -> { c with Volume = v })))
                    true
            // The sub-settings only mean something while the chime is on.
            let enableDependents on =
                finalOnly.IsEnabled <- on
                volume.IsEnabled <- on
            let sound =
                card Glyph.volume "Play a sound for meeting reminders" "Chime when a meeting reminder appears"
                    (toggle "Play a sound for meeting reminders" cfg.SoundEnabled (fun v ->
                        change (fun c -> { c with SoundEnabled = v })
                        enableDependents v))
                    false
            enableDependents cfg.SoundEnabled
            p.Children.Add sound |> ignore
            p.Children.Add finalOnly |> ignore
            p.Children.Add volume |> ignore
            let deviceItems =
                ("System default", "") :: (callbacks.DeviceNames () |> List.map (fun d -> d, d))
                |> withCurrent cfg.AudioDeviceName cfg.AudioDeviceName
            p.Children.Add(
                card Glyph.speakers "Output device" "Where meeting and email reminder sounds play"
                    (combo "Output device" deviceItems cfg.AudioDeviceName (fun v ->
                        change (fun c -> { c with AudioDeviceName = v })))
                    false) |> ignore
            ui sv

        let emailPage () =
            let sv, p = mkPage "Email reminders"
            p.Children.Add(
                note "Turn matching emails into check-off reminders. Changes to rules and folders take effect after a restart.") |> ignore

            // Rules: header row with the add button, then one card per rule.
            let header = Grid(Margin = Thickness(2.0, 20.0, 0.0, 8.0))
            header.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
            header.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
            let headerText = text "Rules" 14.0 FontWeights.SemiBold palette.Text
            Grid.SetColumn(headerText, 0)
            header.Children.Add headerText |> ignore
            let rulesPanel = StackPanel()

            let rec renderRules () =
                rulesPanel.Children.Clear()
                if Array.isEmpty cfg.EmailRules then
                    rulesPanel.Children.Add(note "No rules yet. Add one to start matching emails.") |> ignore
                else
                    cfg.EmailRules
                    |> Array.iteri (fun i r ->
                        let criteria =
                            [ if hasText r.From then "From: " + r.From.Trim()
                              if hasText r.RecipientContains then "To/Cc: " + r.RecipientContains.Trim()
                              if hasText r.SubjectContains then "Subject: " + r.SubjectContains.Trim() ]
                            |> String.concat "\n"
                        let name = EmailRules.label r
                        let actions = StackPanel(Orientation = Orientation.Horizontal)
                        let edit = button "Edit" (fun () -> editRule (Some i))
                        edit.Margin <- Thickness(0.0, 0.0, 8.0, 0.0)
                        AutomationProperties.SetName(edit, sprintf "Edit %s" name)
                        let delete =
                            button "Delete" (fun () ->
                                change (fun c ->
                                    { c with EmailRules = c.EmailRules |> Array.removeAt i })
                                renderRules ())
                        AutomationProperties.SetName(delete, sprintf "Delete %s" name)
                        actions.Children.Add edit |> ignore
                        actions.Children.Add delete |> ignore
                        rulesPanel.Children.Add(card Glyph.mail name criteria (ui actions) false) |> ignore)

            and editRule (index: int option) =
                let existing =
                    match index with
                    | Some i when i < Array.length cfg.EmailRules -> cfg.EmailRules.[i]
                    | _ -> { Name = ""; From = ""; RecipientContains = ""; SubjectContains = "" }
                match showRuleDialog existing with
                | Some r ->
                    change (fun c ->
                        match index with
                        | Some i -> { c with EmailRules = c.EmailRules |> Array.updateAt i r }
                        | None -> { c with EmailRules = Array.append c.EmailRules [| r |] })
                    renderRules ()
                | None -> ()

            let addRule = accentButton "Add rule" (fun () -> editRule None)
            Grid.SetColumn(addRule, 1)
            header.Children.Add addRule |> ignore
            p.Children.Add header |> ignore
            p.Children.Add rulesPanel |> ignore

            p.Children.Add(sectionHeader "Scanning") |> ignore
            p.Children.Add(
                card Glyph.history "Look back on startup"
                    "Catch reminders that arrived just before the app started"
                    (stepper "look-back minutes" cfg.EmailLookbackMinutes 1 1440 "min" (fun v ->
                        change (fun c -> { c with EmailLookbackMinutes = v })))
                    false) |> ignore

            // Folders: title above a wrap of chips (Inbox is always scanned).
            let foldersPanel = WrapPanel(Margin = Thickness(0.0, 12.0, 0.0, 0.0))
            let chip (label: string) (glyph: string option) (onRemove: (unit -> unit) option) =
                let b =
                    Border(
                        Background = brush palette.ControlBg,
                        BorderBrush = brush palette.ControlBorder,
                        BorderThickness = Thickness 1.0,
                        CornerRadius = CornerRadius 15.0,
                        Height = 30.0,
                        Margin = Thickness(0.0, 0.0, 8.0, 8.0),
                        Padding = Thickness(12.0, 0.0, (if Option.isSome onRemove then 3.0 else 12.0), 0.0))
                let row = StackPanel(Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center)
                glyph
                |> Option.iter (fun g ->
                    let t = glyphText g 12.0
                    t.Foreground <- brush palette.Subtle
                    t.Margin <- Thickness(0.0, 0.0, 6.0, 0.0)
                    row.Children.Add t |> ignore)
                row.Children.Add(text label 14.0 FontWeights.Normal palette.Text) |> ignore
                onRemove
                |> Option.iter (fun f ->
                    let x = iconButton Glyph.close (sprintf "Remove %s" label) f
                    x.FontSize <- 10.0
                    x.MinWidth <- 24.0
                    x.MinHeight <- 24.0
                    x.Width <- 24.0
                    x.Height <- 24.0
                    x.Margin <- Thickness(4.0, 0.0, 0.0, 0.0)
                    row.Children.Add x |> ignore)
                b.Child <- row
                ui b

            let rec renderFolders () =
                foldersPanel.Children.Clear()
                foldersPanel.Children.Add(chip "Inbox" (Some Glyph.lock) None) |> ignore
                cfg.EmailIncludedFolders
                |> Array.iteri (fun i name ->
                    foldersPanel.Children.Add(
                        chip name None (Some (fun () ->
                            change (fun c ->
                                { c with EmailIncludedFolders = c.EmailIncludedFolders |> Array.removeAt i })
                            renderFolders ()))) |> ignore)
                let add = Button(Style = style "PillBtn")
                let content = StackPanel(Orientation = Orientation.Horizontal)
                let plus = glyphText Glyph.add 11.0
                plus.Margin <- Thickness(0.0, 0.0, 6.0, 0.0)
                content.Children.Add plus |> ignore
                content.Children.Add(TextBlock(Text = "Add folder", VerticalAlignment = VerticalAlignment.Center)) |> ignore
                add.Content <- content
                add.Margin <- Thickness(0.0, 0.0, 0.0, 8.0)
                AutomationProperties.SetName(add, "Add folder")
                add.Click.Add(fun _ ->
                    match showInputDialog "Add folder" "Folder name, as it appears in Outlook" with
                    | Some name when hasText name ->
                        change (fun c ->
                            { c with EmailIncludedFolders = Array.append c.EmailIncludedFolders [| name |] })
                        renderFolders ()
                    | _ -> ())
                foldersPanel.Children.Add add |> ignore

            let folders = cardShell false
            let foldersGrid = Grid()
            foldersGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
            foldersGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
            let foldersIcon = cardIcon Glyph.folder
            foldersIcon.VerticalAlignment <- VerticalAlignment.Top
            foldersIcon.Margin <- Thickness(0.0, 2.0, 16.0, 0.0)
            Grid.SetColumn(foldersIcon, 0)
            let foldersStack = titleBlock "Folders to scan" "Scanned in addition to the Inbox"
            foldersStack.Children.Add foldersPanel |> ignore
            Grid.SetColumn(foldersStack, 1)
            foldersGrid.Children.Add foldersIcon |> ignore
            foldersGrid.Children.Add foldersStack |> ignore
            folders.Child <- foldersGrid
            p.Children.Add folders |> ignore

            p.Children.Add(sectionHeader "Sound") |> ignore
            let emailVolume =
                card "" "Volume" "Loudness of the email reminder chime"
                    (volumeSlider "Email reminder volume" 1.0 cfg.EmailVolume (fun v ->
                        change (fun c -> { c with EmailVolume = v })))
                    true
            let emailSound =
                card Glyph.volume "Play a sound for email reminders"
                    "Email reminders are to-dos, so they're silent by default"
                    (toggle "Play a sound for email reminders" cfg.EmailSoundEnabled (fun v ->
                        change (fun c -> { c with EmailSoundEnabled = v })
                        emailVolume.IsEnabled <- v))
                    false
            emailVolume.IsEnabled <- cfg.EmailSoundEnabled
            p.Children.Add emailSound |> ignore
            p.Children.Add emailVolume |> ignore

            renderRules ()
            renderFolders ()
            ui sv

        let accountPage () =
            let sv, p = mkPage "Account"
            p.Children.Add(
                card Glyph.contact "Microsoft 365" (callbacks.StatusText ())
                    (ui (button "Sign out" callbacks.OnSignOut))
                    false) |> ignore
            p.Children.Add(sectionHeader "Azure AD app registration") |> ignore
            let field (label: string) (value: string) (onChange: string -> unit) =
                // Indented like a sub-setting so the label lines up with the
                // titles of the icon cards around it.
                let border = cardShell true
                border.MinHeight <- 0.0
                let stack = StackPanel()
                let box = input label value
                box.Margin <- Thickness(0.0, 8.0, 0.0, 0.0)
                box.TextChanged.Add(fun _ -> onChange box.Text)
                stack.Children.Add(text label 14.0 FontWeights.Normal palette.Text) |> ignore
                stack.Children.Add box |> ignore
                border.Child <- stack
                border
            p.Children.Add(
                field "Application (client) ID" cfg.ClientId (fun v ->
                    change (fun c -> { c with ClientId = v.Trim() }))) |> ignore
            p.Children.Add(
                field "Directory (tenant) ID" cfg.TenantId (fun v ->
                    change (fun c -> { c with TenantId = v.Trim() }))) |> ignore
            let help = text "" 12.0 FontWeights.Normal palette.Subtle
            help.Margin <- Thickness(2.0, 4.0, 2.0, 0.0)
            help.Inlines.Add(Run "These come from your Azure AD app registration. ")
            let link =
                Hyperlink(
                    Run "How do I get these?",
                    NavigateUri = Uri "https://github.com/daniellittledev/meeting-notifier#first-run-setup",
                    Foreground = brush palette.Accent)
            link.RequestNavigate.Add(fun e ->
                (try Process.Start(ProcessStartInfo(e.Uri.AbsoluteUri, UseShellExecute = true)) |> ignore
                 with _ -> ())
                e.Handled <- true)
            help.Inlines.Add link
            p.Children.Add help |> ignore
            let reconnect =
                card Glyph.refresh "Sign in again" "Applies new IDs and renews consent"
                    (ui (button "Reconnect" callbacks.OnReconnect))
                    false
            reconnect.Margin <- Thickness(0.0, 16.0, 0.0, 4.0)
            p.Children.Add reconnect |> ignore
            ui sv

        let aboutPage () =
            let sv, p = mkPage "About"
            let version =
                match Assembly.GetExecutingAssembly().GetName().Version with
                | null -> "unknown"
                | v -> sprintf "%d.%d.%d" v.Major v.Minor v.Build
            p.Children.Add(
                card Glyph.info "Outlook Notifier" (sprintf "Version %s" version)
                    (ui (button "Release notes" (fun () ->
                        try
                            Process.Start(
                                ProcessStartInfo(
                                    "https://github.com/daniellittledev/meeting-notifier/releases",
                                    UseShellExecute = true))
                            |> ignore
                        with _ -> ())))
                    false) |> ignore
            ui sv

        // ----- nav + layout -----

        let pages =
            [ Glyph.settings, "General", generalPage
              Glyph.calendar, "Meeting reminders", meetingPage
              Glyph.mail, "Email reminders", emailPage
              Glyph.contact, "Account", accountPage
              Glyph.info, "About", aboutPage ]

        let contentHost = Grid()
        // Build every page up front and toggle visibility (cheap; keeps scroll).
        let builtPages =
            pages
            |> List.map (fun (_, _, build) ->
                let el = build ()
                el.Visibility <- Visibility.Collapsed
                contentHost.Children.Add el |> ignore
                el)

        let nav =
            ListBox(
                BorderThickness = Thickness 0.0,
                Background = Brushes.Transparent,
                FocusVisualStyle = null,
                ItemContainerStyle = style "NavItem",
                Margin = Thickness(8.0, 16.0, 8.0, 8.0))
        do
            for (glyph, label, _) in pages do
                let row = StackPanel(Orientation = Orientation.Horizontal)
                let icon = glyphText glyph 16.0
                icon.Margin <- Thickness(0.0, 0.0, 14.0, 0.0)
                row.Children.Add icon |> ignore
                row.Children.Add(TextBlock(Text = label, VerticalAlignment = VerticalAlignment.Center)) |> ignore
                let item = ListBoxItem(Content = row)
                AutomationProperties.SetName(item, label)
                nav.Items.Add item |> ignore
            nav.SelectionChanged.Add(fun _ ->
                builtPages
                |> List.iteri (fun i el ->
                    el.Visibility <- (if i = nav.SelectedIndex then Visibility.Visible else Visibility.Collapsed)))
            nav.SelectedIndex <- 0

        let root = Grid()
        do
            root.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength 240.0))
            root.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
            Grid.SetColumn(nav, 0)
            Grid.SetColumn(contentHost, 1)
            root.Children.Add nav |> ignore
            root.Children.Add contentHost |> ignore

        do
            applyTheme window
            // Merged so the implicit ScrollBar style applies inside the window.
            window.Resources.MergedDictionaries.Add styles
            window.Title <- "Settings — Outlook Notifier"
            window.Width <- 860.0
            window.Height <- 680.0
            window.MinWidth <- 680.0
            window.MinHeight <- 480.0
            window.WindowStartupLocation <- WindowStartupLocation.CenterScreen
            window.Content <- root

        /// Show the window, bringing it to the front if already open.
        member _.Show() =
            if window.IsVisible then
                if window.WindowState = WindowState.Minimized then
                    window.WindowState <- WindowState.Normal
                window.Activate() |> ignore
            else
                window.Show()
                window.Activate() |> ignore

        /// True while the window is on screen.
        member _.IsOpen = window.IsVisible

        member _.Close() =
            try window.Close() with _ -> ()
