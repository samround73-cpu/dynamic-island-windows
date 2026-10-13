using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>The Today, Controls, Clipboard, Media, Stats and Power tabs, plus the clock, battery and volume.</summary>
public partial class MainWindow
{
    private bool _updatingVolume;
    private bool _seeking;
    private bool _equalizerOn;
    private string _lastTrack = "";
    private int? _lastBatteryPercent;
    private bool _hasBattery;

    /// <summary>Battery shows on the pill when this PC has one and it's switched on in Settings.</summary>
    private bool BatteryInPill => _hasBattery && _settings.ShowBatteryInPill;

    /// <summary>
    /// Battery level and charging state, or null on a PC without a battery.
    /// Asks Windows two ways, because some laptops report "no battery" through the older one.
    /// </summary>
    private static (int Percent, bool Charging)? ReadBattery()
    {
        bool pluggedIn = false;
        if (Native.GetSystemPowerStatus(out var status))
        {
            pluggedIn = status.ACLineStatus == 1;
            bool present = status.BatteryFlag != 128 && status.BatteryFlag != 255 && status.BatteryLifePercent <= 100;
            if (present) return (status.BatteryLifePercent, pluggedIn || (status.BatteryFlag & 8) != 0);
        }

        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report.Status == Windows.System.Power.BatteryStatus.NotPresent) return null;
            int? remaining = report.RemainingCapacityInMilliwattHours;
            int? full = report.FullChargeCapacityInMilliwattHours;
            if (remaining is null || full is null || full <= 0) return null;
            int percent = (int)Math.Round(100.0 * remaining.Value / full.Value);
            bool charging = report.Status == Windows.System.Power.BatteryStatus.Charging || pluggedIn;
            return (Math.Clamp(percent, 0, 100), charging);
        }
        catch
        {
            return null;
        }
    }

    private void UpdatePillBatteryVisibility()
    {
        var visibility = BatteryInPill ? Visibility.Visible : Visibility.Collapsed;
        CompactBattery.Visibility = visibility;
        PeekBattery.Visibility = visibility;
        UpdatePillWidth();
    }
    private bool _lastCharging;
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string? _pendingPowerAction;
    private Button? _pendingPowerButton;

    // Controls-tab tiles
    private readonly ToggleTile _wifiTile = new("", "Wi-Fi");
    private readonly ToggleTile _bluetoothTile = new("", "Bluetooth");
    private readonly ToggleTile _audioTile = new("", "Sound");
    private readonly ToggleTile _micTile = new("", "Microphone");
    private readonly ToggleTile _darkModeTile = new("", "Dark mode");

    // =====================================================================================
    //  Clock
    // =====================================================================================

    private void UpdateClock()
    {
        var now = DateTime.Now;
        string time = now.ToString("t"); // follows your Windows 12/24-hour setting
        string date = now.ToString("ddd d MMM");
        CompactClock.Text = time;
        CompactDate.Text = date;
        PeekTime.Text = time;
        PeekDate.Text = date;
        BigClock.Text = time;
        DateText.Text = date;
        if (_expanded && _currentTab == "Today") UpdateToday();
        UpdatePillWidth(); // the time can change width a little (it only resizes for real changes)
    }

    // =====================================================================================
    //  Today tab
    // =====================================================================================

    private void UpdateToday()
    {
        var now = DateTime.Now;
        GreetingText.Text = now.Hour < 12 ? "Good morning" : now.Hour < 18 ? "Good afternoon" : "Good evening";
        TodayTime.Text = now.ToString("t");
        TodayDay.Text = now.ToString("dddd");
        TodayDate.Text = now.ToString("d MMMM yyyy");
        TodayExtra.Text = $"Week {System.Globalization.ISOWeek.GetWeekOfYear(now)}  ·  Day {now.DayOfYear} of {(DateTime.IsLeapYear(now.Year) ? 366 : 365)}";

        // Re-draw the calendar if the date has rolled over at midnight.
        if (CalendarGrid.Tag is DateTime drawnFor && drawnFor != DateTime.Today) RenderCalendar();
    }

    private static DayOfWeek FirstDayOfWeek => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    private void BuildWeekdayHeader()
    {
        var names = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
        for (int i = 0; i < 7; i++)
        {
            WeekdayHeader.Children.Add(new TextBlock
            {
                Text = names[((int)FirstDayOfWeek + i) % 7],
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink(0x80),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
    }

    /// <summary>Draws the six weeks around the chosen month, with today circled in gold.</summary>
    private void RenderCalendar()
    {
        MonthTitle.Text = _calendarMonth.ToString("MMMM yyyy");
        CalendarGrid.Children.Clear();
        CalendarGrid.Tag = DateTime.Today;

        int offset = ((int)_calendarMonth.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        var day = _calendarMonth.AddDays(-offset);
        var dimText = Theme.Ink(0x40);
        var weekendText = Theme.Ink(0xB0);

        for (int i = 0; i < 42; i++, day = day.AddDays(1))
        {
            bool inMonth = day.Month == _calendarMonth.Month;
            bool isToday = day == DateTime.Today;
            bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            CalendarGrid.Children.Add(new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = isToday ? ToggleTile.Accent : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0.5, 0, 0.5),
                Child = new TextBlock
                {
                    Text = day.Day.ToString(),
                    FontSize = 11,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = isToday ? ToggleTile.AccentText : !inMonth ? dimText : weekend ? weekendText : Theme.Ink(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }

    private void ChangeMonth(int months)
    {
        _calendarMonth = _calendarMonth.AddMonths(months);
        RenderCalendar();
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e) => ChangeMonth(-1);
    private void NextMonth_Click(object sender, RoutedEventArgs e) => ChangeMonth(1);

    private void ThisMonth_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RenderCalendar();
    }

    /// <summary>Scroll over the calendar to flip through months.</summary>
    private void Calendar_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeMonth(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    // =====================================================================================
    //  Controls tab
    // =====================================================================================

    private void BuildTiles()
    {
        foreach (var row in new[] { _wifiTile, _bluetoothTile, _audioTile, _micTile, _darkModeTile })
            SwitchList.Children.Add(row);

        _wifiTile.Toggled += () =>
        {
            if (_connectivity.HasWifi) _connectivity.SetWifi(!_connectivity.WifiOn);
            else OpenAndCollapse("ms-settings:network");
        };
        _wifiTile.MoreRequested += () => OpenAndCollapse("ms-settings:network-wifi");

        _bluetoothTile.Toggled += () =>
        {
            if (_connectivity.HasBluetooth) _connectivity.SetBluetooth(!_connectivity.BluetoothOn);
            else OpenAndCollapse("ms-settings:bluetooth");
        };
        _bluetoothTile.MoreRequested += () => OpenAndCollapse("ms-settings:bluetooth");

        _audioTile.Toggled += () =>
        {
            _volume.Muted = !_volume.Muted;
            RefreshVolume();
            RefreshControls();
        };
        _audioTile.MoreRequested += OpenOutputPicker;

        _micTile.Toggled += () =>
        {
            if (!_volume.MicAvailable) return;
            _volume.MicMuted = !_volume.MicMuted;
            RefreshControls();
        };
        _micTile.MoreRequested += () => OpenAndCollapse("ms-settings:sound");

        _darkModeTile.Toggled += () =>
        {
            SystemActions.SetDarkMode(!SystemActions.IsDarkMode);
            RefreshControls();
        };
        _darkModeTile.MoreRequested += () => OpenAndCollapse("ms-settings:colors");
    }

    private void RefreshControls()
    {
        var network = ConnectivityService.CurrentNetwork();
        if (_connectivity.HasWifi)
        {
            _wifiTile.Title = "Wi-Fi";
            _wifiTile.IsOn = _connectivity.WifiOn;
            _wifiTile.Subtitle = !_connectivity.WifiOn ? "Off"
                : !network.HasValue ? "Not connected"
                : network.Value.IsWifi ? network.Value.Name
                : "Wired connection";
        }
        else
        {
            _wifiTile.Title = "Network";
            _wifiTile.IsOn = network.HasValue;
            _wifiTile.Subtitle = network.HasValue ? network.Value.Name : "Not connected";
        }

        _bluetoothTile.IsOn = _connectivity.BluetoothOn;
        _bluetoothTile.Subtitle = !_connectivity.HasBluetooth ? "Open settings" : _connectivity.BluetoothOn ? "On" : "Off";

        _audioTile.IsOn = _volume.Available && !_volume.Muted;
        _audioTile.Subtitle = !_volume.Available ? "No output" : _volume.Muted ? "Muted" : _volume.OutputName;

        _micTile.IsOn = _volume.MicAvailable && !_volume.MicMuted;
        _micTile.Subtitle = !_volume.MicAvailable ? "No microphone" : _volume.MicMuted ? "Muted" : "On";

        bool dark = SystemActions.IsDarkMode;
        _darkModeTile.IsOn = dark;
        _darkModeTile.Subtitle = dark ? "On" : "Off";
    }

    private void OpenOutputPicker()
    {
        Collapse();
        Native.PressWinCtrlKey('V'); // Windows 11's speakers/headphones picker
    }

    private void OutputPicker_Click(object sender, RoutedEventArgs e) => OpenOutputPicker();

    // =====================================================================================
    //  Battery
    // =====================================================================================

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
            Dispatcher.BeginInvoke(() => RefreshBattery(allowPopOut: true));
    }

    private void RefreshBattery(bool allowPopOut)
    {
        var reading = ReadBattery();
        bool hasBattery = reading is not null;
        BatteryPanel.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatteryStatusText.Text = hasBattery
            ? $"Show the battery level next to the date (found: {reading!.Value.Percent}%{(reading.Value.Charging ? ", charging" : "")})"
            : "No battery found on this PC, so there's nothing to show";
        if (hasBattery != _hasBattery)
        {
            _hasBattery = hasBattery;
            UpdatePillBatteryVisibility();
        }
        if (!hasBattery) return;

        int percent = reading!.Value.Percent;
        bool charging = reading.Value.Charging;

        Brush level = charging
            ? ToggleTile.Accent
            : percent <= 20 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57)) : Theme.Ink();
        BatteryText.Text = $"{percent}%";
        BatteryFill.Width = 17 * percent / 100.0;
        BatteryFill.Background = level;

        // The pill and the hover tray show it too.
        foreach (var (fill, text, bolt) in new[] { (CompactBatteryFill, CompactBatteryText, CompactBatteryBolt), (PeekBatteryFill, PeekBatteryText, PeekBatteryBolt) })
        {
            fill.Width = 15 * percent / 100.0;
            fill.Background = level;
            text.Text = $"{percent}%";
            bolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        }

        if (allowPopOut && _lastBatteryPercent is int previous)
        {
            if (charging && !_lastCharging)
                ShowActivity("", null, "Charging", $"{percent}%", percent / 100.0);
            else if (!charging && percent <= 20 && previous > 20)
                ShowActivity("", null, "Low battery", $"{percent}% remaining", percent / 100.0);
        }

        _lastBatteryPercent = percent;
        _lastCharging = charging;
    }

    // =====================================================================================
    //  Stats tab
    // =====================================================================================

    private void OnStatsTick()
    {
        _stats.Sample(); // always, so the graphs already have history when you open the tab
        if (_expanded && _currentTab == "Stats") UpdateStatsView();
    }

    private void UpdateStatsView()
    {
        SetRing(CpuRing, _stats.CpuPercent / 100, warnWhenHigh: true);
        CpuText.Text = $"{_stats.CpuPercent:0}%";
        double average = _stats.CpuHistory.Count > 0 ? _stats.CpuHistory.Average() : _stats.CpuPercent;
        CpuSub.Text = $"{average:0}% avg, last min";

        SetRing(MemRing, _stats.MemoryPercent / 100, warnWhenHigh: true);
        MemText.Text = $"{_stats.MemoryPercent:0}%";
        MemSub.Text = $"{_stats.MemoryUsedGb:0.0} of {_stats.MemoryTotalGb:0} GB";

        var maybeDrive = SystemStats.SystemDrive();
        if (maybeDrive.HasValue && maybeDrive.Value.TotalGb > 0)
        {
            var drive = maybeDrive.Value;
            double used = 1 - drive.FreeGb / drive.TotalGb;
            SetRing(DiskRing, used, warnWhenHigh: true);
            DiskLabel.Text = $"Disk {drive.Name}";
            DiskText.Text = $"{used * 100:0}%";
            DiskSub.Text = $"{drive.FreeGb:0} GB free";
        }

        // The download ring is relative to the busiest moment in the last minute (at least 64 KB/s).
        double down = _stats.DownloadBytesPerSecond;
        double netMax = Math.Max(64 * 1024, _stats.DownloadHistory.Count > 0 ? _stats.DownloadHistory.Max() : 0);
        SetRing(NetDownRing, down / netMax, warnWhenHigh: false);
        string speed = SystemStats.FormatBytes(down);
        int space = speed.IndexOf(' ');
        NetDownText.Text = space > 0 ? speed[..space] : speed;
        NetUnitText.Text = (space > 0 ? speed[(space + 1)..] : "") + "/s";
        NetDownSub.Text = $"Upload {SystemStats.FormatSpeed(_stats.UploadBytesPerSecond)}";

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = uptime.TotalDays >= 1
            ? $"PC up {(int)uptime.TotalDays}d {uptime.Hours}h"
            : $"PC up {uptime.Hours}h {uptime.Minutes}m";
    }

    // ---- Rings: each one glides to its new value instead of jumping ----

    private const double RingSize = 84, RingThickness = 7;
    private static readonly Brush RingWarning = new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57));

    private sealed class RingState
    {
        public double Shown, From, To;
        public DateTime Start;
    }

    private readonly Dictionary<System.Windows.Shapes.Path, RingState> _rings = new();
    private bool _ringsAnimating;

    private void SetRing(System.Windows.Shapes.Path ring, double fraction, bool warnWhenHigh)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        if (!_rings.TryGetValue(ring, out var state)) _rings[ring] = state = new RingState();
        state.From = state.Shown;
        state.To = fraction;
        state.Start = DateTime.Now;

        if (warnWhenHigh && fraction >= 0.9) ring.Stroke = RingWarning;
        else ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");

        if (!_ringsAnimating)
        {
            _ringsAnimating = true;
            CompositionTarget.Rendering += OnRingFrame;
        }
    }

    private void OnRingFrame(object? sender, EventArgs e)
    {
        bool finished = true;
        var now = DateTime.Now;
        foreach (var (ring, state) in _rings)
        {
            double t = Math.Clamp((now - state.Start).TotalMilliseconds / 600, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            state.Shown = state.From + (state.To - state.From) * eased;
            ring.Data = RingGeometry(state.Shown);
            if (t < 1) finished = false;
        }
        if (finished)
        {
            CompositionTarget.Rendering -= OnRingFrame;
            _ringsAnimating = false;
        }
    }

    /// <summary>An arc starting at 12 o'clock and going clockwise round the ring.</summary>
    private static Geometry? RingGeometry(double fraction)
    {
        if (fraction < 0.002) return null;
        double c = RingSize / 2, r = (RingSize - RingThickness) / 2;
        if (fraction > 0.9995) return new EllipseGeometry(new Point(c, c), r, r);

        double angle = fraction * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    // ---- Heaviest open apps ----

    private bool _measuringApps;

    private async void RefreshTopProcesses()
    {
        if (_measuringApps) return;
        _measuringApps = true;
        try
        {
            RefreshOpenApps();
            var tiles = _openApps.ToList();
            var windows = tiles.Select(t => t.Windows.ToArray()).ToList();
            double totalBytes = _stats.MemoryTotalGb * 1024 * 1024 * 1024;

            var usage = await Task.Run(() => _stats.MeasureApps(windows));

            // "Heaviest" weighs CPU and memory equally: 50% CPU counts the same as using half the PC's memory.
            var ranked = tiles
                .Select((tile, i) => (Tile: tile, usage[i].Cpu, usage[i].Bytes,
                                      Score: usage[i].Cpu / 100 + (totalBytes > 0 ? usage[i].Bytes / totalBytes : 0)))
                .Where(x => x.Bytes > 0)
                .OrderByDescending(x => x.Score)
                .Take(5)
                .ToList();
            double top = ranked.Count > 0 ? Math.Max(ranked[0].Score, 1e-9) : 1;

            TopAppsList.ItemsSource = ranked
                .Select(x => new AppUsage(x.Tile.Name, x.Tile.Icon, x.Tile.Line, x.Cpu, x.Bytes, x.Score / top))
                .ToList();
            TopAppsEmpty.Visibility = ranked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_currentTab == "Stats") ResizeExpandedIfNeeded();
        }
        catch { }
        finally
        {
            _measuringApps = false;
        }
    }

    // =====================================================================================
    //  Power tab
    // =====================================================================================

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action) return;

        // Signing out, restarting and shutting down close your apps, so they need a second click.
        bool needsConfirm = action is "signout" or "restart" or "shutdown";
        if (needsConfirm && _pendingPowerAction != action)
        {
            ResetPowerConfirm();
            _pendingPowerAction = action;
            _pendingPowerButton = button;
            button.Background = ToggleTile.Accent;
            button.Foreground = ToggleTile.AccentText;
            string label = action switch { "signout" => "sign out", "restart" => "restart", _ => "shut down" };
            PowerHint.Text = $"Click again to {label}";
            PowerHint.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            RestartTimer(_powerConfirmTimer);
            return;
        }

        ResetPowerConfirm();
        Collapse();
        switch (action)
        {
            case "lock": SystemActions.Lock(); break;
            case "sleep": SystemActions.Sleep(); break;
            case "signout": SystemActions.SignOut(); break;
            case "restart": SystemActions.Restart(); break;
            case "shutdown": SystemActions.ShutDown(); break;
        }
    }

    // ---- Slide to restart / shut down ----

    private readonly SlideToConfirm _restartSlide = new() { Glyph = "\uE72C" };
    private readonly SlideToConfirm _shutDownSlide = new() { Glyph = "\uE7E8" };
    private static readonly Brush ShutDownRed = new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57));
    private static readonly Brush ShutDownIcon = new SolidColorBrush(Color.FromRgb(0x2A, 0x10, 0x10));

    private void BuildPowerSliders()
    {
        RestartSlideHost.Child = _restartSlide;
        ShutDownSlideHost.Child = _shutDownSlide;
        _restartSlide.Confirmed += () =>
        {
            Collapse();
            SystemActions.Restart();
        };
        _shutDownSlide.Confirmed += () =>
        {
            Collapse();
            SystemActions.ShutDown();
        };
        _shutDownSlide.SetColors(ShutDownRed, ShutDownIcon);
        ApplyPowerSliderAccent();
    }

    private void ApplyPowerSliderAccent() => _restartSlide.SetColors(ToggleTile.Accent, ToggleTile.AccentText);

    // ---- The info strip: up time, Windows Update, power and open apps ----

    private (UpdateState State, int Count) _updates = (UpdateState.Unknown, 0);
    private DateTime _updatesCheckedAt;
    private bool _checkingUpdates;

    private void RefreshPowerInfo()
    {
        ApplyPowerSliderAccent();
        _restartSlide.Reset();
        _shutDownSlide.Reset();

        // Up time
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var since = DateTime.Now - uptime;
        PowerUptimeValue.Text = uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}d {uptime.Hours}h"
                              : uptime.TotalHours >= 1 ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                              : $"{uptime.Minutes}m";
        PowerUptimeSub.Text = uptime.TotalDays >= 7 ? "restart soon?"
                            : since.Date == DateTime.Today ? $"since {since:t}"
                            : $"since {since:ddd}";

        // Power
        var battery = ReadBattery();
        if (battery is { } b)
        {
            PowerBatteryGlyph.Text = b.Charging ? "\uE83E"
                                   : b.Percent >= 100 ? "\uE83F"
                                   : ((char)(0xE850 + b.Percent / 10)).ToString(); // battery level icons
            PowerBatteryValue.Text = $"{b.Percent}%";
            string? left = null;
            if (!b.Charging && Native.GetSystemPowerStatus(out var status) && status.BatteryLifeTime > 0)
            {
                var remaining = TimeSpan.FromSeconds(status.BatteryLifeTime);
                left = remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m left" : $"{remaining.Minutes}m left";
            }
            PowerBatterySub.Text = b.Charging ? (b.Percent >= 100 ? "full" : "charging") : left ?? "on battery";
        }
        else
        {
            PowerBatteryGlyph.Text = "\uE7E8";
            PowerBatteryValue.Text = "Plugged in";
            PowerBatterySub.Text = "";
        }

        // Open apps (these close on restart, shut down and sign out)
        int apps = _openApps.Count;
        PowerAppsValue.Text = apps == 0 ? "No apps" : apps == 1 ? "1 app" : $"{apps} apps";
        PowerAppsSub.Text = "open";

        SignOutButton.ToolTip = $"Sign out ({Environment.UserName})";
        ShowUpdateStatus();
        if (DateTime.Now - _updatesCheckedAt > TimeSpan.FromMinutes(10)) CheckForUpdates();
    }

    private async void CheckForUpdates()
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        if (_updates.State == UpdateState.Unknown) PowerUpdateSub.Text = "checking…";
        try
        {
            _updates = await Task.Run(() => UpdateStatus.Read());
            _updatesCheckedAt = DateTime.Now;
        }
        catch { }
        finally
        {
            _checkingUpdates = false;
        }
        ShowUpdateStatus();
    }

    private void ShowUpdateStatus()
    {
        var (state, count) = _updates;
        string updates = count == 1 ? "1 update" : $"{count} updates";
        (PowerUpdateGlyph.Text, PowerUpdateValue.Text, PowerUpdateSub.Text) = state switch
        {
            UpdateState.RestartNeeded => ("\uE7BA", "Restart", "for updates"),
            UpdateState.Available => ("\uE896", updates, "waiting"),
            UpdateState.UpToDate => ("\uE73E", "Up to date", ""),
            _ => ("\uE895", "Updates", _checkingUpdates ? "checking…" : "unknown"),
        };
        PowerUpdateGlyph.Foreground = state == UpdateState.RestartNeeded ? ShutDownRed : (Brush)FindResource("Accent");

        _restartSlide.Text = state == UpdateState.RestartNeeded ? "slide to update and restart" : "slide to restart";
        _shutDownSlide.Text = state == UpdateState.RestartNeeded ? "slide to update and shut down" : "slide to shut down";
        if (_pendingPowerAction is null) PowerHint.Text = DefaultPowerHint();
        TidyFacts();
    }

    /// <summary>Hides empty second halves of the facts, so each one stays exactly centred in its column.</summary>
    private void TidyFacts()
    {
        foreach (var sub in new[] { PowerUptimeSub, PowerUpdateSub, PowerBatterySub, PowerAppsSub })
            sub.Visibility = sub.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string DefaultPowerHint()
    {
        int apps = _openApps.Count;
        return apps == 0 ? "" : $"Restarting or shutting down will close {(apps == 1 ? "your open app" : $"your {apps} open apps")}";
    }

    private void PowerShowApps_Click(object sender, RoutedEventArgs e) => AppsTabButton.IsChecked = true;

    private void ResetPowerConfirm()
    {
        _powerConfirmTimer.Stop();
        if (_pendingPowerButton is not null)
        {
            _pendingPowerButton.ClearValue(BackgroundProperty);
            _pendingPowerButton.ClearValue(ForegroundProperty);
        }
        _pendingPowerButton = null;
        _pendingPowerAction = null;
        PowerHint.Text = DefaultPowerHint();
        PowerHint.Foreground = (Brush)FindResource("SubtleText");
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string target) return;
        Collapse();
        if (target == "desktop") Native.PressWinKey('D');
        else SystemActions.Open(target);
    }

    // =====================================================================================
    //  Media
    // =====================================================================================

    private void OnMediaChanged()
    {
        bool hasMedia = _media.HasSession && _media.Title.Length > 0;
        Brush art = _media.Art is null
            ? Theme.Ink(0x22)
            : new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill };
        string playGlyph = _media.IsPlaying ? "" : "";

        // Controls tab: mini player
        MiniMediaFull.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        MiniMediaEmpty.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
        MiniTitle.Text = _media.Title;
        MiniArtist.Text = _media.Artist;
        MiniArt.Background = art;
        MiniPlayGlyph.Text = playGlyph;
        MiniMediaOutput.Text = string.Join("  ·  ", new[] { _volume.OutputName, _media.SourceApp }.Where(s => s.Length > 0));

        // Media tab: big player
        MediaEmpty.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
        MediaFull.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        BigTitle.Text = _media.Title;
        BigArtist.Text = _media.Artist;
        BigSource.Text = _media.SourceApp.ToUpperInvariant();
        BigArt.Background = art;
        BigPlayGlyph.Text = playGlyph;
        ApplyMediaTint(hasMedia);
        UpdateShuffleRepeat();

        // Hover tray: small now-playing row
        var peekVisibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        bool peekRowChanged = PeekMediaRow.Visibility != peekVisibility;
        PeekMediaRow.Visibility = peekVisibility;
        UpdateLiveCard();
        PeekMediaTitle.Text = _media.Title;
        PeekMediaArtist.Text = _media.Artist.Length > 0 ? "  —  " + _media.Artist : "";
        PeekMediaArt.Background = art;
        PeekPlayGlyph.Text = playGlyph;
        if (_peeking && peekRowChanged)
        {
            var size = MeasurePeek();
            AnimateSize(size.Width, size.Height, 320, bouncy: false);
        }

        UpdateMediaProgress();

        // Compact pill: little dancing bars while playing.
        SetEqualizer(_media.IsPlaying);

        // New song → quick pop-out.
        string track = _media.Title + "\u0001" + _media.Artist;
        if (_media.IsPlaying && _media.Title.Length > 0 && track != _lastTrack)
            ShowActivity("", _media.Art, _media.Title, _media.Artist, null);
        if (_media.Title.Length > 0) _lastTrack = track;

        ResizeExpandedIfNeeded();
    }

    private void UpdateMediaProgress()
    {
        bool show = _media.HasTimeline && _media.HasSession && _media.Title.Length > 0;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        bool changed = MiniProgressRow.Visibility != visibility;
        MiniProgressRow.Visibility = visibility;
        BigProgressRow.Visibility = visibility;
        PeekProgress.Visibility = visibility;
        if (changed) ResizeExpandedIfNeeded();
        if (!show || _seeking) return;

        var position = _media.Position;
        var duration = _media.Duration;
        PeekProgressScale.ScaleX = duration.TotalSeconds > 0 ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) : 0;
        foreach (var (slider, elapsed, total) in new[] { (MiniProgress, MiniElapsed, MiniDuration), (BigProgress, BigElapsed, BigDuration) })
        {
            slider.Maximum = Math.Max(1, duration.TotalSeconds);
            slider.Value = position.TotalSeconds;
            elapsed.Text = FormatTime(position);
            total.Text = FormatTime(duration);
        }
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void MediaProgress_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private async void MediaProgress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking || sender is not Slider slider) return;

        // Read where you let go *now*: a moment later the regular progress refresh could have moved the knob back.
        var target = TimeSpan.FromSeconds(slider.Value);

        // Keep the knob where you dropped it (no refreshes) until the jump has been sent.
        bool accepted = await _media.SeekAsync(target);
        _seeking = false;
        UpdateMediaProgress();
        if (!accepted) ShowSeekRefused();
    }

    /// <summary>The little music bars on the pill and in the hover tray (if turned on in Settings).</summary>
    private void SetEqualizer(bool playing)
    {
        bool on = playing && _settings.ShowMusicBars;
        if (on == _equalizerOn) return;
        _equalizerOn = on;
        UpdatePillItems();
        PeekEqBars.Visibility = on ? Visibility.Visible : Visibility.Hidden;

        var bars = new[] { Eq1, Eq2, Eq3, Eq4, Eq5, Eq6 };
        double[] speeds = { 0.42, 0.58, 0.36 };
        for (int i = 0; i < bars.Length; i++)
        {
            if (on)
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(speeds[i % 3]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                });
            else
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    // ---- Media tab colour: a soft glow taken from the artwork ----

    private SolidColorBrush? _mediaTint;
    private SolidColorBrush? _mediaTintDark;
    private ImageSource? _tintedFor;
    private bool _tintShowing;

    private void ApplyMediaTint(bool hasMedia)
    {
        if (_mediaTint is null)
        {
            _mediaTint = new SolidColorBrush(((SolidColorBrush)ToggleTile.Accent).Color);
            _mediaTintDark = new SolidColorBrush(ArtColour.Darken(_mediaTint.Color));
            BigSource.Foreground = _mediaTint;
            BigProgress.Foreground = _mediaTint;
            BigPlayButton.Background = _mediaTint;
            BigPlayButton.Foreground = _mediaTintDark;
        }

        // Only work out the colour again when the artwork changes.
        if (hasMedia == _tintShowing && ReferenceEquals(_media.Art, _tintedFor)) return;
        _tintShowing = hasMedia;
        _tintedFor = _media.Art;

        var color = ArtColour.Dominant(_media.Art) ?? ((SolidColorBrush)ToggleTile.Accent).Color;
        var duration = TimeSpan.FromMilliseconds(700);
        void Fade(Animatable target, DependencyProperty property, Color to) =>
            target.BeginAnimation(property, new ColorAnimation(to, duration) { EasingFunction = new SineEase() });

        Fade(_mediaTint, SolidColorBrush.ColorProperty, color);
        Fade(_mediaTintDark!, SolidColorBrush.ColorProperty, ArtColour.Darken(color));
        // Strong in the top-left corner behind the artwork, fading to nothing bottom-right.
        Fade(MediaGlowStrong, GradientStop.ColorProperty, ArtColour.WithAlpha(color, (byte)(hasMedia ? 0x5C : 0)));
        Fade(MediaGlowSoft, GradientStop.ColorProperty, ArtColour.WithAlpha(color, (byte)(hasMedia ? 0x1C : 0)));
    }

    /// <summary>The accent colour changed: re-pick the fallback tint for songs without colourful artwork.</summary>
    private void RefreshMediaTint()
    {
        _tintedFor = null;
        _tintShowing = !_tintShowing; // force a refresh
        ApplyMediaTint(_media.HasSession && _media.Title.Length > 0);
    }

    private void UpdateShuffleRepeat()
    {
        var off = Theme.Ink(0x80);
        ShuffleButton.Visibility = _media.CanShuffle ? Visibility.Visible : Visibility.Collapsed;
        RepeatButton.Visibility = _media.CanRepeat ? Visibility.Visible : Visibility.Collapsed;
        ShuffleGlyph.Foreground = _media.ShuffleOn && _mediaTint is not null ? _mediaTint : off;
        RepeatGlyph.Foreground = _media.RepeatMode != 0 && _mediaTint is not null ? _mediaTint : off;
        RepeatGlyph.Text = _media.RepeatMode == 1 ? "\uE8ED" : "\uE8EE"; // repeat one / repeat all
        ShuffleButton.ToolTip = _media.ShuffleOn ? "Shuffle is on" : "Shuffle";
        RepeatButton.ToolTip = _media.RepeatMode switch { 1 => "Repeating this song", 2 => "Repeating", _ => "Repeat" };
    }

    /// <summary>Briefly explains, under the song title, that this app won't let us jump around.</summary>
    private async void ShowSeekRefused()
    {
        string app = _media.SourceApp.Length > 0 ? _media.SourceApp : "This app";
        BigSource.Text = $"{app.ToUpperInvariant()} DOESN'T ALLOW SKIPPING AROUND";
        await Task.Delay(2500);
        BigSource.Text = _media.SourceApp.ToUpperInvariant();
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e) => _media.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => _media.CycleRepeat();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();
    private void Previous_Click(object sender, RoutedEventArgs e) => _media.Previous();

    // =====================================================================================
    //  Volume
    // =====================================================================================

    private void RefreshVolume()
    {
        if (!_volume.Available) return;

        double percent = Math.Round(_volume.Level * 100);
        bool muted = _volume.Muted;
        _updatingVolume = true;
        ControlsVolume.Value = muted ? 0 : percent;
        MediaVolume.Value = muted ? 0 : percent;
        _updatingVolume = false;

        string glyph = muted ? "" : "";
        ControlsVolumeGlyph.Text = glyph;
        MediaVolumeGlyph.Text = glyph;
        ControlsVolumeText.Text = muted ? "Muted" : $"{percent:0}%";
        MediaOutputText.Text = _volume.OutputName;
    }

    private void OnVolumeChanged(float level, bool muted)
    {
        if (_expanded)
        {
            RefreshVolume();
            RefreshControls();
            return;
        }

        int percent = (int)Math.Round(level * 100);
        if (muted) ShowActivity("", null, "Muted", null, 0);
        else ShowActivity("", null, $"Volume  {percent}%", null, level);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume || !IsLoaded) return;
        _volume.Level = (float)(e.NewValue / 100.0);
        if (_volume.Muted && e.NewValue > 0) _volume.Muted = false;
        RefreshVolume();
        RefreshControls();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _volume.Muted = !_volume.Muted;
        RefreshVolume();
        RefreshControls();
    }
}
