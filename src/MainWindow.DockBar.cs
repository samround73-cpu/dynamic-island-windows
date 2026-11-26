using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// Dock mode (Settings › Behaviour › Dock at the bottom): the old taskbar's jobs in three pills along the bottom,
/// styled like the island. Left: Start, search, Task View. Middle: pinned and open apps (they leave the island's
/// hover tray). Right: hidden tray icons, quick settings (Wi-Fi and Bluetooth lists, volume, brightness, battery)
/// and notifications. The pills stay tucked below the screen until the mouse touches the bottom edge.
/// </summary>
public partial class MainWindow
{
    private DockWindow? _dockBar;
    private readonly DispatcherTimer _dockTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _dockTimerWired;
    private bool _dockShown;
    private DateTime _dockLeftAt = DateTime.MaxValue;

    private Grid? _dockRoot;
    private StackPanel? _dockRow;
    private Border? _dockLeft, _dockMiddle, _dockRight, _dockPanel;
    private readonly TranslateTransform _dockLeftMove = new(), _dockMiddleMove = new(), _dockRightMove = new();
    private StackPanel? _dockHandles;
    private string _dockPanelKind = ""; // "", "qs" (quick settings), "hidden" (hidden icons), "list" (Wi-Fi / Bluetooth)
    private TextBlock? _dockNetGlyph, _dockSoundGlyph, _dockBatteryGlyph;

    // Quick settings: its own copies of the Controls tab's switches.
    private ToggleTile? _dockWifiTile, _dockBluetoothTile, _dockSoundTile, _dockMicTile;
    private Slider? _dockVolume, _dockBrightness;
    private TextBlock? _dockVolumeText, _dockBrightnessText, _dockBatteryText;
    private bool _dockUpdating;

    // The Wi-Fi / Bluetooth list shown in the dock (filled by the same code as the Controls tab's list).
    private StackPanel? _dockListRows;
    private TextBlock? _dockListTitle;
    private bool _deviceListInDock;

    private const double DockWindowHeight = 520; // room for the pills and a panel above them
    private const double PillHeight = 48;
    private const double PillGap = 12;
    private const double DockHiddenOffset = 74;  // how far the pills sink when tucked away

    /// <summary>Where the device list rows go: the dock's panel when it's showing the list, else the Controls tab.</summary>
    private StackPanel ListRows => _deviceListInDock && _dockListRows is not null ? _dockListRows : DeviceListRows;
    private TextBlock ListTitle => _deviceListInDock && _dockListTitle is not null ? _dockListTitle : DeviceListTitle;

    // =====================================================================================
    //  On and off
    // =====================================================================================

    private void ApplyDockMode()
    {
        if (!_dockTimerWired)
        {
            _dockTimer.Tick += (_, _) => OnDockTick();
            _dockTimerWired = true;
        }

        if (_settings.DockMode)
        {
            if (_dockBar is null) CreateDockBar();
            _dockTimer.Start();
        }
        else
        {
            _dockTimer.Stop();
            CloseDockPanel();
            try { _dockBar?.Close(); } catch { }
            _dockBar = null;
            _dockShown = false;
        }
        UpdateDock(); // the apps move between the island's hover tray and the dock
    }

    private void CreateDockBar()
    {
        var window = new DockWindow();
        _dockBar = window;
        CopyLookTo(window);

        _dockRoot = new Grid(); // no background: the empty parts are see-through to clicks

        // The three pills, side by side at the bottom centre.
        _dockLeft = Pill(LeftPillContent(), new Thickness(8, 6, 8, 6), _dockLeftMove);
        _dockMiddle = Pill(MiddlePillContent(), new Thickness(10, 7, 4, 1), _dockMiddleMove);
        _dockRight = Pill(RightPillContent(), new Thickness(6, 6, 6, 6), _dockRightMove);
        _dockRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10),
        };
        _dockRow.Children.Add(_dockLeft);
        _dockRow.Children.Add(new Border { Width = PillGap });
        _dockRow.Children.Add(_dockMiddle);
        _dockRow.Children.Add(new Border { Width = PillGap });
        _dockRow.Children.Add(_dockRight);

        // Three faint lines showing where the pills are while they're tucked away.
        _dockHandles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
            IsHitTestVisible = false,
        };
        foreach (var pill in new[] { _dockLeft, _dockMiddle, _dockRight })
        {
            var slot = new Grid();
            var line = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x80, 0x80)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            slot.Children.Add(line);
            pill.SizeChanged += (_, _) =>
            {
                slot.Width = pill.ActualWidth;
                line.Width = Math.Max(30, pill.ActualWidth * 0.45);
            };
            if (_dockHandles.Children.Count > 0) _dockHandles.Children.Add(new Border { Width = PillGap });
            _dockHandles.Children.Add(slot);
        }

        // The panel that opens above the right-hand pill.
        _dockPanel = new Border
        {
            Width = 320,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.45, Direction = 270 },
        };

        _dockRoot.Children.Add(_dockHandles);
        _dockRoot.Children.Add(_dockRow);
        _dockRoot.Children.Add(_dockPanel);
        window.Content = _dockRoot;
        PaintDock();

        // Start tucked away.
        foreach (var move in new[] { _dockLeftMove, _dockMiddleMove, _dockRightMove }) move.Y = DockHiddenOffset;
        _dockShown = false;

        window.Show();
        PlaceDockBar();
        RefreshDockStatus();
    }

    /// <summary>Copies the island's colours, styles and templates so the dock looks the same and follows changes.</summary>
    private void CopyLookTo(Window window)
    {
        foreach (var key in Resources.Keys.Cast<object>().ToList()) window.Resources[key] = Resources[key];
        window.FontFamily = FontFamily;
        TextElement.SetForeground(window, Theme.Ink());
    }

    /// <summary>After a change of style, theme or accent colour.</summary>
    private void SyncDockLook()
    {
        if (_dockBar is null) return;
        CopyLookTo(_dockBar);
        PaintDock();
        RefreshDockStatus();
    }

    private Brush DockFill()
    {
        if (!Theme.Light) return Brushes.Black;
        var wash = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        wash.GradientStops.Add(new GradientStop(Color.FromRgb(0xEE, 0xEE, 0xF1), 0));
        wash.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0xE8, 0xEC), 1));
        wash.Freeze();
        return wash;
    }

    private void PaintDock()
    {
        var fill = DockFill();
        foreach (var pill in new[] { _dockLeft, _dockMiddle, _dockRight, _dockPanel })
            if (pill is not null) pill.Background = fill;
    }

    private void PlaceDockBar()
    {
        if (_dockBar is null) return;
        IntPtr monitor = _screenMonitor != IntPtr.Zero ? _screenMonitor : PrimaryMonitor();
        _dockBar.PlaceAtBottom(ScreenBounds, MonitorScale(monitor), DockWindowHeight);
    }

    private Border Pill(UIElement content, Thickness padding, TranslateTransform move) => new()
    {
        Child = content,
        Padding = padding,
        MinHeight = PillHeight,
        CornerRadius = new CornerRadius(18),
        VerticalAlignment = VerticalAlignment.Bottom,
        RenderTransform = move,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35, Direction = 270 },
    };

    private Button DockButton(object content, string tip, Action click, double width = 36)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IslandButton"),
            Width = width,
            Height = 36,
            Content = content,
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private TextBlock DockGlyph(string glyph, double size = 14) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)FindResource("IconFont"),
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // =====================================================================================
    //  The three pills
    // =====================================================================================

    private UIElement LeftPillContent()
    {
        // The Windows logo, drawn as four little squares.
        var logo = new UniformGrid { Rows = 2, Columns = 2, Width = 16, Height = 16 };
        for (int i = 0; i < 4; i++)
            logo.Children.Add(new Border { Margin = new Thickness(0.8), CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)) });

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(DockButton(logo, "Start", () => { CloseDockPanel(); Native.PressWinKey(); }));
        panel.Children.Add(DockButton(DockGlyph("", 15), "Search", () => { CloseDockPanel(); Native.PressWinKey('S'); }));
        panel.Children.Add(DockButton(DockGlyph("", 15), "Task View", () => { CloseDockPanel(); Native.PressWinKey('\t'); }));
        return panel;
    }

    private UIElement MiddlePillContent()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        return new ItemsControl
        {
            ItemsSource = _dock, // the same pinned + open apps the hover tray used
            ItemTemplate = (DataTemplate)FindResource("DockIconTemplate"),
            ItemsPanel = new ItemsPanelTemplate(row),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private UIElement RightPillContent()
    {
        _dockNetGlyph = DockGlyph("");
        _dockSoundGlyph = DockGlyph("");
        _dockBatteryGlyph = DockGlyph("", 15);
        var cluster = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var glyph in new[] { _dockNetGlyph, _dockSoundGlyph, _dockBatteryGlyph })
        {
            glyph.Margin = new Thickness(5, 0, 5, 0);
            cluster.Children.Add(glyph);
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(DockButton(DockGlyph("", 12), "Show hidden icons", () => ToggleDockPanel("hidden"), 30));
        panel.Children.Add(DockButton(cluster, "Quick settings", () => ToggleDockPanel("qs"), double.NaN));
        panel.Children.Add(DockButton(DockGlyph("", 14), "Notifications", () => { CloseDockPanel(); Native.PressWinKey('N'); }));
        return panel;
    }

    /// <summary>Keeps the little Wi-Fi / sound / battery icons, and the quick settings, up to date.</summary>
    private void RefreshDockStatus()
    {
        if (_dockBar is null || _dockNetGlyph is null || _dockSoundGlyph is null || _dockBatteryGlyph is null) return;

        var network = ConnectivityService.CurrentNetwork();
        _dockNetGlyph.Text = network.HasValue && !network.Value.IsWifi ? "\uE839" : "\uE701"; // cable or Wi-Fi
        _dockNetGlyph.Opacity = network.HasValue ? 1 : 0.4;                       // faint when offline
        _dockSoundGlyph.Text = !_volume.Available || _volume.Muted ? "" : "";
        var battery = ReadBattery();
        _dockBatteryGlyph.Visibility = battery is null ? Visibility.Collapsed : Visibility.Visible;
        if (battery is { } b)
        {
            // Segoe's battery glyphs go from empty (E850) to full (E83F) in tenths; charging ones start at E85A.
            int tenth = Math.Clamp(b.Percent / 10, 0, 10);
            _dockBatteryGlyph.Text = tenth == 10
                ? (b.Charging ? "\uE83E" : "\uE83F")
                : ((char)((b.Charging ? 0xE85A : 0xE850) + tenth)).ToString();
        }

        if (_dockWifiTile is null) return;
        foreach (var (mine, island) in new[] { (_dockWifiTile, _wifiTile), (_dockBluetoothTile!, _bluetoothTile), (_dockSoundTile!, _audioTile), (_dockMicTile!, _micTile) })
        {
            mine.Title = island.Title;
            mine.Subtitle = island.Subtitle;
            mine.IsOn = island.IsOn;
        }
        _dockUpdating = true;
        if (_dockVolume is not null && !_dockVolume.IsMouseCaptureWithin)
        {
            double level = _volume.Available && !_volume.Muted ? Math.Round(_volume.Level * 100) : 0;
            _dockVolume.Value = level;
            if (_dockVolumeText is not null) _dockVolumeText.Text = _volume.Muted ? "Muted" : $"{level:0}%";
        }
        _dockUpdating = false;
        if (_dockBatteryText is not null)
            _dockBatteryText.Text = battery is { } bb ? $"Battery {bb.Percent}%{(bb.Charging ? ", charging" : "")}" : "";
    }

    // =====================================================================================
    //  Panels above the right-hand pill
    // =====================================================================================

    private void ToggleDockPanel(string kind)
    {
        if (_dockPanelKind == kind || (kind == "qs" && _dockPanelKind == "list"))
        {
            CloseDockPanel();
            return;
        }
        OpenDockPanel(kind);
    }

    private void OpenDockPanel(string kind)
    {
        if (_dockPanel is null) return;
        if (_deviceListInDock)
        {
            _deviceListInDock = false; // leaving the list: stop it filling in
            _deviceList = DeviceListKind.None;
            _passwordFor = null;
        }
        _dockPanelKind = kind;
        _dockPanel.Child = kind switch
        {
            "hidden" => HiddenIconsPanel(),
            "list" => DeviceListPanelForDock(),
            _ => QuickSettingsPanel(),
        };
        _dockPanel.Width = kind == "hidden" ? double.NaN : 320;
        _dockPanel.MaxWidth = 420;
        _dockPanel.Visibility = Visibility.Visible;
        _dockPanel.Opacity = 0;
        RefreshDockStatus();
        // Line it up above the right-hand pill once it knows its size.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_dockPanel is null || _dockRight is null || _dockRoot is null) return;
            var corner = _dockRight.TranslatePoint(new Point(0, 0), _dockRoot);
            double right = corner.X + _dockRight.ActualWidth;
            double left = Math.Max(8, Math.Min(right - _dockPanel.ActualWidth, _dockRoot.ActualWidth - _dockPanel.ActualWidth - 8));
            _dockPanel.Margin = new Thickness(left, 0, 0, 10 + _dockRight.ActualHeight + 10);
            _dockPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        }));
    }

    private void CloseDockPanel()
    {
        if (_deviceListInDock)
        {
            _deviceListInDock = false;
            _deviceList = DeviceListKind.None;
            _passwordFor = null;
        }
        _dockPanelKind = "";
        if (_dockPanel is null) return;
        _dockPanel.Visibility = Visibility.Collapsed;
        _dockPanel.Child = null;
    }

    private UIElement HiddenIconsPanel()
    {
        var chips = _trayHost?.Chips();
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "IN THE BACKGROUND", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink(0x80), Margin = new Thickness(2, 0, 0, 8) });
        if (chips is not { Count: > 0 })
        {
            panel.Children.Add(Note(_settings.TrayAppIcons
                ? "No background apps have icons right now."
                : "Turn on Settings › Behaviour › Background app icons to see them here."));
            return panel;
        }
        var wrap = new FrameworkElementFactory(typeof(WrapPanel));
        panel.Children.Add(new ItemsControl
        {
            ItemsSource = chips,
            ItemTemplate = (DataTemplate)FindResource("TrayChipTemplate"),
            ItemsPanel = new ItemsPanelTemplate(wrap),
            MaxWidth = 396,
        });
        panel.Children.Add(new TextBlock { Text = "Click to open · right-click for the app's menu", FontSize = 10, Foreground = Theme.Ink(0x80), Margin = new Thickness(2, 2, 0, 0) });
        return panel;
    }

    private UIElement QuickSettingsPanel()
    {
        if (_dockWifiTile is null) BuildDockTiles();

        var panel = new StackPanel();
        foreach (var tile in new[] { _dockWifiTile!, _dockBluetoothTile!, _dockSoundTile!, _dockMicTile! })
        {
            if (tile.Parent is Panel old) old.Children.Remove(tile);
            panel.Children.Add(tile);
        }

        _dockVolume = DockSlider("", out _dockVolumeText, (value) =>
        {
            _volume.Level = (float)(value / 100.0);
            if (_volume.Muted && value > 0) _volume.Muted = false;
            RefreshVolume();
            RefreshControls();
        });
        panel.Children.Add(((FrameworkElement)_dockVolume.Parent).Parent is FrameworkElement v ? v : _dockVolume);

        _dockBrightness = DockSlider("", out _dockBrightnessText, (value) => SetBrightness((int)Math.Round(value)));
        panel.Children.Add(((FrameworkElement)_dockBrightness.Parent).Parent is FrameworkElement br ? br : _dockBrightness);
        LoadDockBrightness();

        var foot = new DockPanel { Margin = new Thickness(6, 10, 2, 0) };
        var settings = DockButton(DockGlyph("", 13), "All settings", () => { CloseDockPanel(); SystemActions.Open("ms-settings:"); }, 30);
        settings.Height = 28;
        DockPanel.SetDock(settings, Dock.Right);
        foot.Children.Add(settings);
        _dockBatteryText = new TextBlock { FontSize = 11, Foreground = Theme.Ink(0x99), VerticalAlignment = VerticalAlignment.Center };
        foot.Children.Add(_dockBatteryText);
        panel.Children.Add(foot);
        return panel;
    }

    private void BuildDockTiles()
    {
        _dockWifiTile = new ToggleTile("", "Wi-Fi");
        _dockBluetoothTile = new ToggleTile("", "Bluetooth");
        _dockSoundTile = new ToggleTile("", "Sound");
        _dockMicTile = new ToggleTile("", "Microphone");

        _dockWifiTile.Toggled += () =>
        {
            if (_connectivity.HasWifi) _connectivity.SetWifi(!_connectivity.WifiOn);
            else { CloseDockPanel(); SystemActions.Open("ms-settings:network"); }
        };
        _dockWifiTile.MoreRequested += () => ShowDockList(DeviceListKind.Wifi);
        _dockBluetoothTile.Toggled += () =>
        {
            if (_connectivity.HasBluetooth) _connectivity.SetBluetooth(!_connectivity.BluetoothOn);
            else { CloseDockPanel(); SystemActions.Open("ms-settings:bluetooth"); }
        };
        _dockBluetoothTile.MoreRequested += () => ShowDockList(DeviceListKind.Bluetooth);
        _dockSoundTile.Toggled += () =>
        {
            _volume.Muted = !_volume.Muted;
            RefreshVolume();
            RefreshControls();
        };
        _dockSoundTile.MoreRequested += () => { CloseDockPanel(); SystemActions.Open("ms-settings:sound"); };
        _dockMicTile.Toggled += () =>
        {
            if (!_volume.MicAvailable) return;
            _volume.MicMuted = !_volume.MicMuted;
            RefreshControls();
        };
        _dockMicTile.MoreRequested += () => { CloseDockPanel(); SystemActions.Open("ms-settings:sound"); };
    }

    /// <summary>A big rounded slider with its icon and value on it, like the Controls tab's volume slider.</summary>
    private Slider DockSlider(string glyph, out TextBlock valueText, Action<double> changed)
    {
        var slider = new Slider
        {
            Style = (Style)FindResource("BigSlider"),
            Minimum = 0,
            Maximum = 100,
            SmallChange = 2,
            LargeChange = 10,
        };
        var icon = DockGlyph(glyph, 13);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(12, 0, 0, 0);
        icon.Foreground = (Brush)FindResource("AccentText");
        icon.IsHitTestVisible = false;
        var text = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            IsHitTestVisible = false,
        };
        valueText = text;
        var grid = new Grid();
        grid.Children.Add(slider);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        var holder = new Border { Margin = new Thickness(0, 8, 0, 0), Child = grid };
        slider.ValueChanged += (_, e) =>
        {
            text.Text = $"{e.NewValue:0}%";
            if (_dockUpdating) return;
            changed(e.NewValue);
        };
        return slider;
    }

    private async void LoadDockBrightness()
    {
        if (_dockBrightness is null) return;
        int? level = await _brightness.ReadAsync(BrightnessMonitor);
        _realBrightness = level is not null;
        if (_dockBrightness is null) return;
        _dockUpdating = true;
        _dockBrightness.Value = level ?? _settings.DimLevel;
        _dockUpdating = false;
        if (_dockBrightnessText is not null) _dockBrightnessText.Text = $"{_dockBrightness.Value:0}%";
    }

    /// <summary>The Wi-Fi or Bluetooth list, in the dock's panel.</summary>
    private void ShowDockList(DeviceListKind kind)
    {
        _deviceList = kind;
        _passwordFor = null;
        OpenDockPanel("list");
        _deviceListInDock = true;
        _dockListTitle!.Text = kind == DeviceListKind.Wifi ? "Wi-Fi" : "Bluetooth";
        LoadDeviceList();
    }

    private UIElement DeviceListPanelForDock()
    {
        var kind = _deviceList;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var back = DockButton(DockGlyph("", 12), "Back", () => OpenDockPanel("qs"), 30);
        back.Height = 28;
        DockPanel.SetDock(back, Dock.Left);
        head.Children.Add(back);
        var more = DockButton(DockGlyph("", 12), kind == DeviceListKind.Wifi ? "More Wi-Fi settings" : "Add a device or more settings",
            () => { CloseDockPanel(); SystemActions.Open(kind == DeviceListKind.Wifi ? "ms-settings:network-wifi" : "ms-settings:bluetooth"); }, 30);
        more.Height = 28;
        DockPanel.SetDock(more, Dock.Right);
        head.Children.Add(more);
        var refresh = DockButton(DockGlyph("", 12), "Look again", LoadDeviceList, 30);
        refresh.Height = 28;
        DockPanel.SetDock(refresh, Dock.Right);
        head.Children.Add(refresh);
        _dockListTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        head.Children.Add(_dockListTitle);

        _dockListRows = new StackPanel();
        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(new ScrollViewer { Content = _dockListRows, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        return panel;
    }

    // =====================================================================================
    //  Tucked away until the mouse touches the bottom edge
    // =====================================================================================

    private void OnDockTick()
    {
        if (_dockBar is null || !Native.GetCursorPos(out var cursor)) return;
        var screen = ScreenBounds;
        bool fullScreen = _presence == Presence.FullScreen;
        if (_dockHandles is not null) _dockHandles.Visibility = fullScreen ? Visibility.Hidden : Visibility.Visible;

        bool onScreen = cursor.X >= screen.Left && cursor.X < screen.Right && cursor.Y >= screen.Top && cursor.Y < screen.Bottom;
        if (!_dockShown)
        {
            // Never over a full-screen game or video, and not while dragging something across the bottom.
            if (!fullScreen && onScreen && cursor.Y >= screen.Bottom - 2 && !Native.AnyMouseButtonDown()) ShowDockBar();
            return;
        }

        if (DockInUse(cursor, screen))
        {
            _dockLeftAt = DateTime.MaxValue;
            return;
        }
        if (_dockLeftAt == DateTime.MaxValue) _dockLeftAt = DateTime.Now;
        else if (DateTime.Now - _dockLeftAt > TimeSpan.FromMilliseconds(700)) HideDockBar();
    }

    /// <summary>The mouse is on (or just below) the pills, a menu or preview from them is open, or you're typing in a panel.</summary>
    private bool DockInUse(Native.POINT cursor, Native.RECT screen)
    {
        if (_dockBar is null || _dockRow is null) return false;
        if (_dockBar.IsMouseOver || _menuOpen || _preview.IsVisible) return true;
        if (_dockPanelKind.Length > 0 && _dockBar.IsKeyboardFocusWithin) return true;
        double scale = MonitorScale(_screenMonitor != IntPtr.Zero ? _screenMonitor : PrimaryMonitor());
        int centre = (screen.Left + screen.Right) / 2;
        int half = (int)(_dockRow.ActualWidth * scale / 2) + (int)(24 * scale);
        int top = screen.Bottom - (int)((PillHeight + 34) * scale);
        return cursor.X >= centre - half && cursor.X <= centre + half && cursor.Y >= top && cursor.Y <= screen.Bottom;
    }

    private void ShowDockBar()
    {
        if (_dockBar is null) return;
        _dockShown = true;
        _dockLeftAt = DateTime.MaxValue;
        if (_dockBar.Handle != IntPtr.Zero)
            Native.SetWindowPos(_dockBar.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        RefreshDockStatus();

        // Middle first, then the sides, each with the island's little bounce.
        foreach (var (move, delay) in new[] { (_dockMiddleMove, 0), (_dockLeftMove, 45), (_dockRightMove, 85) })
        {
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut },
            });
        }
        _dockHandles?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
    }

    private void HideDockBar()
    {
        _dockShown = false;
        _dockLeftAt = DateTime.MaxValue;
        CloseDockPanel();
        foreach (var (move, delay) in new[] { (_dockLeftMove, 0), (_dockRightMove, 0), (_dockMiddleMove, 40) })
        {
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(DockHiddenOffset, TimeSpan.FromMilliseconds(220))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
        }
        _dockHandles?.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(180) });
    }
}
