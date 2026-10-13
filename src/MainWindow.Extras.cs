using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>The Clipboard and Shelf tabs, weather, device pop-ups, Settings and accent colours.</summary>
public partial class MainWindow
{
    // =====================================================================================
    //  Clipboard tab
    // =====================================================================================

    private string _clipFilter = "All";
    private readonly List<ClipItem> _clipRows = new();
    private ClipItem? _clipSelected;
    private int _clipboardRetries;

    private void CaptureClipboard()
    {
        if (_clipboard.Capture()) return;

        // Another app still had the clipboard open; try again in a moment (a few times at most).
        if (++_clipboardRetries > 4) return;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            CaptureClipboard();
        };
        retry.Start();
    }

    private bool ClipMatchesFilter(ClipItem item) => _clipFilter switch
    {
        "Text" => item.Kind is ClipKind.Text or ClipKind.Code,
        "Images" => item.Kind == ClipKind.Image,
        "Links" => item.Kind == ClipKind.Link,
        _ => true,
    };

    /// <summary>Rebuilds the list under its headings (Pinned, Today, Yesterday, Earlier), keeping your selection.</summary>
    private void RefreshClipboardList()
    {
        if (ClipList is null) return; // still starting up
        string query = ClipSearchBox.Text.Trim();
        ClipSearchPlaceholder.Visibility = ClipSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipSearchPlaceholder.Text = $"Search {_clipboard.Items.Count} {(_clipboard.Items.Count == 1 ? "item" : "items")}…";

        var matches = _clipboard.Items
            .Where(ClipMatchesFilter)
            .Where(i => query.Length == 0 || (i.Text ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        var items = new List<object>();
        _clipRows.Clear();
        foreach (var group in new[] { "PINNED", "TODAY", "YESTERDAY", "EARLIER" })
        {
            var inGroup = matches.Where(i => i.Group == group).ToList();
            if (inGroup.Count == 0) continue;
            items.Add(new AppSectionHeader { Title = group });
            items.AddRange(inGroup);
            _clipRows.AddRange(inGroup);
        }

        ClipList.ItemsSource = items;
        ClipEmpty.Visibility = _clipRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipEmptyText.Text = _clipboard.Items.Count == 0 ? "Nothing copied yet" : "Nothing matches";

        SelectClip(_clipSelected is not null && _clipRows.Contains(_clipSelected) ? _clipSelected : _clipRows.FirstOrDefault());
    }

    private void SelectClip(ClipItem? item)
    {
        if (_clipSelected is not null) _clipSelected.IsSelected = false;
        _clipSelected = item;
        if (item is not null)
        {
            item.IsSelected = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                (ClipList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement)?.BringIntoView()));
        }
        UpdateClipDetail();
    }

    private void UpdateClipDetail()
    {
        var item = _clipSelected;
        ClipDetailMeta.Visibility = item is null ? Visibility.Hidden : Visibility.Visible;
        if (item is null)
        {
            ClipDetailImage.Source = null;
            ClipDetailImage.Visibility = Visibility.Collapsed;
            ClipDetailText.Visibility = Visibility.Visible;
            ClipDetailText.Text = _clipboard.Items.Count == 0 ? "Copy something and it'll show up here." : "";
            ClipDetailText.FontWeight = FontWeights.Normal;
            ClipDetailText.TextAlignment = TextAlignment.Center;
            return;
        }

        bool isImage = item.Kind == ClipKind.Image;
        ClipDetailImage.Source = item.Image;
        ClipDetailImage.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        ClipDetailText.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        ClipDetailText.Text = item.DetailText;
        ClipDetailText.FontWeight = item.Kind == ClipKind.Code ? FontWeights.Normal : FontWeights.SemiBold;
        ClipDetailText.FontFamily = item.Kind == ClipKind.Code ? new FontFamily("Cascadia Mono, Consolas") : FontFamily;
        ClipDetailText.TextAlignment = item.DetailText.Length < 220 && item.Kind != ClipKind.Code ? TextAlignment.Center : TextAlignment.Left;

        ClipDetailType.Text = item.TypeLabel;
        ClipDetailSize.Text = item.SizeText;
        ClipDetailCopied.Text = item.IsPinned ? "Pinned" : item.CopiedText;
        ClipPinText.Text = item.IsPinned ? "Unpin" : "Pin";
    }

    private void ClipRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipItem item) return;
        SelectClip(item);
        if (e.ClickCount == 2) PasteClip(item); // double-click pastes
        e.Handled = true;
    }

    private void ClipSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshClipboardList();

    private void ClipFilter_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string filter) _clipFilter = filter;
        RefreshClipboardList();
    }

    /// <summary>↑↓ move, Enter paste, Ctrl+C copy, Ctrl+P pin, Del remove, Tab next filter.</summary>
    private void ClipSearch_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        int index = _clipSelected is null ? -1 : _clipRows.IndexOf(_clipSelected);

        switch (e.Key)
        {
            case Key.Down when _clipRows.Count > 0:
                SelectClip(_clipRows[Math.Min(_clipRows.Count - 1, index + 1)]);
                e.Handled = true;
                break;
            case Key.Up when _clipRows.Count > 0:
                SelectClip(_clipRows[Math.Max(0, index - 1)]);
                e.Handled = true;
                break;
            case Key.Enter when _clipSelected is not null:
                PasteClip(_clipSelected);
                e.Handled = true;
                break;
            case Key.Tab:
                CycleClipFilter();
                e.Handled = true;
                break;
            case Key.Delete when _clipSelected is not null && ClipSearchBox.Text.Length == 0:
                RemoveSelectedClip();
                e.Handled = true;
                break;
            case Key.C when ctrl && _clipSelected is not null && ClipSearchBox.SelectionLength == 0:
                CopyClip(_clipSelected);
                e.Handled = true;
                break;
            case Key.P when ctrl && _clipSelected is not null:
                _clipboard.TogglePin(_clipSelected);
                e.Handled = true;
                break;
        }
    }

    private void CycleClipFilter()
    {
        var chips = new[] { "All", "Text", "Images", "Links" };
        string next = chips[(Array.IndexOf(chips, _clipFilter) + 1) % chips.Length];
        foreach (var chip in FindChips(ClipboardTab))
            if (chip.Tag as string == next) chip.IsChecked = true;
    }

    private static IEnumerable<RadioButton> FindChips(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton radio && radio.GroupName == "ClipFilter") yield return radio;
            foreach (var nested in FindChips(child)) yield return nested;
        }
    }

    /// <summary>Copies the item, goes back to the app you were in, and pastes it there.</summary>
    private void PasteClip(ClipItem item)
    {
        if (!_clipboard.CopyBack(item)) return;
        IntPtr previous = _lastForeground;
        ClipSearchBox.Clear();
        Collapse();
        if (previous == IntPtr.Zero || !Native.IsWindow(previous)) return;
        WindowTracker.Activate(previous);
        var paste = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        paste.Tick += (_, _) =>
        {
            paste.Stop();
            Native.PressCtrlKey('V');
        };
        paste.Start();
    }

    /// <summary>Copies the item and goes back to your app, ready for you to paste where you like.</summary>
    private void CopyClip(ClipItem item)
    {
        if (!_clipboard.CopyBack(item)) return;
        IntPtr previous = _lastForeground;
        Collapse();
        if (previous != IntPtr.Zero && Native.IsWindow(previous)) WindowTracker.Activate(previous);
        ShowActivity("", item.Image, "Copied", "Press Ctrl+V to paste", null);
    }

    private void RemoveSelectedClip()
    {
        if (_clipSelected is null) return;
        int index = _clipRows.IndexOf(_clipSelected);
        var removed = _clipSelected;
        _clipSelected = null;
        _clipboard.Remove(removed); // refreshes the list
        if (_clipRows.Count > 0) SelectClip(_clipRows[Math.Clamp(index, 0, _clipRows.Count - 1)]);
    }

    private void ClipPaste_Click(object sender, RoutedEventArgs e) { if (_clipSelected is not null) PasteClip(_clipSelected); }
    private void ClipCopy_Click(object sender, RoutedEventArgs e) { if (_clipSelected is not null) CopyClip(_clipSelected); }
    private void ClipPin_Click(object sender, RoutedEventArgs e) { if (_clipSelected is not null) _clipboard.TogglePin(_clipSelected); }
    private void ClipRemove_Click(object sender, RoutedEventArgs e) => RemoveSelectedClip();

    private void WindowsClipboard_Click(object sender, RoutedEventArgs e)
    {
        IntPtr previous = _lastForeground;
        Collapse();
        if (previous != IntPtr.Zero && Native.IsWindow(previous)) WindowTracker.Activate(previous);
        Native.PressWinKey('V');
    }

    // =====================================================================================
    //  Shelf: drop files on the island, drag them back out later
    // =====================================================================================

    private Point _dragStart;
    private bool _draggingOut;
    private bool _dropZoneShowing;
    private readonly DispatcherTimer _dropLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private void RefreshShelf()
    {
        _shelf.Prune();
        ShelfList.ItemsSource = _shelf.Items.ToList();
        bool empty = _shelf.Items.Count == 0;
        ShelfEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfDragAllButton.Visibility = _shelf.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShelfTitle.Text = empty ? "SHELF" : $"SHELF  ·  {_shelf.Items.Count} {(_shelf.Items.Count == 1 ? "ITEM" : "ITEMS")}";
        if (_currentTab == "Shelf") ResizeExpandedIfNeeded();
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy; // the files themselves are never moved; the shelf just remembers them
        _dropLeaveTimer.Stop();
        if (_expanded) return;

        if (!_dropZoneShowing)
        {
            _dropZoneShowing = true;
            ExitPeek();
            ShowActivity("", null, "Drop to add to your shelf", "Your files stay where they are", null);
        }
        else
        {
            RestartTimer(_activityTimer); // keep the drop zone open while you're hovering
        }
    }

    private void Island_DragLeave(object sender, DragEventArgs e) => RestartTimer(_dropLeaveTimer);

    private void OnDropLeave()
    {
        _dropLeaveTimer.Stop();
        if (!_dropZoneShowing) return;
        _dropZoneShowing = false;
        EndActivity();
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropZoneShowing = false;
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        int added = _shelf.Add(paths);
        if (_expanded)
        {
            if (ShelfTabButton.IsChecked == true) RefreshShelf();
            else ShelfTabButton.IsChecked = true;
            return;
        }

        var first = _shelf.Items.FirstOrDefault();
        string title = added switch
        {
            0 => "Already on your shelf",
            1 => $"{first?.Name ?? "File"} added to your shelf",
            _ => $"{added} files added to your shelf",
        };
        ShowActivity("", added == 1 ? first?.Thumbnail : null, title, "Open the island › Shelf to use them", null);
    }

    private void ShelfTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is ShelfItem item)
        {
            Collapse();
            SystemActions.Open(item.Path);
        }
    }

    private bool MovedEnoughToDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return false;
        var delta = e.GetPosition(this) - _dragStart;
        return Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void ShelfTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (!MovedEnoughToDrag(e) || sender is not FrameworkElement element || element.DataContext is not ShelfItem item) return;
        DragOut(element, new[] { item.Path });
    }

    private void ShelfDragAll_MouseMove(object sender, MouseEventArgs e)
    {
        if (!MovedEnoughToDrag(e) || _shelf.Items.Count == 0) return;
        DragOut((FrameworkElement)sender, _shelf.Items.Select(i => i.Path).ToArray());
        e.Handled = true;
    }

    /// <summary>Starts a normal Windows drag, so files can be dropped into Explorer, an email, a chat…</summary>
    private void DragOut(FrameworkElement source, string[] paths)
    {
        _draggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch { }
        finally
        {
            _draggingOut = false;
        }
        RefreshShelf(); // anything that was moved away disappears from the shelf
    }

    private void ShelfOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        Collapse();
        SystemActions.Open(item.Path);
    }

    private void ShelfShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        Collapse();
        try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch { }
    }

    private void ShelfCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        try { Clipboard.SetText(item.Path); } catch { }
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShelfItem item) _shelf.Remove(item);
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e) => _shelf.Clear();

    // =====================================================================================
    //  Weather (Today tab)
    // =====================================================================================

    private DateTime _weatherFetchedAt = DateTime.MinValue;
    private bool _weatherLoading;

    private async void RefreshWeather(bool force = false)
    {
        if (_weatherLoading) return;
        if (!force && DateTime.Now - _weatherFetchedAt < TimeSpan.FromMinutes(30)) return;
        _weatherLoading = true;
        try
        {
            double latitude, longitude;
            string place;
            if (_settings.WeatherLatitude is double savedLat && _settings.WeatherLongitude is double savedLon)
            {
                latitude = savedLat;
                longitude = savedLon;
                place = _settings.WeatherPlace;
            }
            else
            {
                var here = await WeatherService.CurrentLocationAsync();
                if (here is null)
                {
                    ShowWeatherMessage("Set your town in Settings to see the weather (or turn on location for desktop apps in Windows).", showButton: true);
                    _weatherFetchedAt = DateTime.Now;
                    return;
                }
                latitude = here.Value.Latitude;
                longitude = here.Value.Longitude;
                place = await WeatherService.NameForAsync(latitude, longitude) ?? "Your location";
            }

            var report = await WeatherService.GetAsync(latitude, longitude, place, _settings.Fahrenheit);
            if (report is null)
            {
                ShowWeatherMessage("Couldn't get the weather just now.", showButton: false);
                _weatherFetchedAt = DateTime.Now - TimeSpan.FromMinutes(25); // try again in about 5 minutes
                return;
            }
            _weatherFetchedAt = DateTime.Now;
            ShowWeather(report);
        }
        finally
        {
            _weatherLoading = false;
        }
    }

    private void ShowWeatherMessage(string message, bool showButton)
    {
        WeatherPanel.Visibility = Visibility.Collapsed;
        WeatherMessagePanel.Visibility = Visibility.Visible;
        WeatherMessage.Text = message;
        WeatherSetupButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowWeather(WeatherReport report)
    {
        WeatherMessagePanel.Visibility = Visibility.Collapsed;
        WeatherPanel.Visibility = Visibility.Visible;

        WeatherSymbol.Text = WeatherService.Symbol(report.Code, report.IsDay);
        WeatherTemp.Text = $"{report.Temperature:0}°";
        WeatherCondition.Text = WeatherService.Describe(report.Code);
        WeatherHighLow.Text = $"H {report.High:0}°  L {report.Low:0}°";
        WeatherPlace.Text = report.Place;
        WeatherPlace.ToolTip = $"{report.Place}  ·  feels like {report.FeelsLike:0}°";

        WeatherHours.Children.Clear();
        var subtle = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        foreach (var hour in report.Hours)
        {
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(new TextBlock { Text = hour.Time.ToString("HH:mm"), FontSize = 10, Foreground = subtle, HorizontalAlignment = HorizontalAlignment.Center });
            var symbol = new TextBlock
            {
                Text = WeatherService.Symbol(hour.Code, hour.IsDay),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 14,
                Margin = new Thickness(0, 2, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            symbol.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            column.Children.Add(symbol);
            column.Children.Add(new TextBlock { Text = $"{hour.Temperature:0}°", FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            WeatherHours.Children.Add(column);
        }
        if (_currentTab == "Today") ResizeExpandedIfNeeded();
    }

    // =====================================================================================
    //  Device pop-ups: Bluetooth, USB drives, sound output
    // =====================================================================================

    private void OnBluetoothChanged(string name, bool connected)
    {
        if (!_settings.DevicePopOuts) return;
        ShowActivity("", null, name, connected ? "Connected" : "Disconnected", null);
    }

    private void OnOutputDeviceChanged()
    {
        _volume.Attach();
        RefreshVolume();
        RefreshControls();
        if (_settings.DevicePopOuts && _volume.OutputName.Length > 0)
            ShowActivity("", null, _volume.OutputName, "Sound now plays here", null);
    }

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 2;
    private const short DBTF_NET = 2;

    /// <summary>Windows tells every window when a drive appears or disappears; pick out USB/removable drives.</summary>
    private void HandleDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        int change = wParam.ToInt32();
        if ((change != DBT_DEVICEARRIVAL && change != DBT_DEVICEREMOVECOMPLETE) || lParam == IntPtr.Zero) return;
        if (Marshal.ReadInt32(lParam, 4) != DBT_DEVTYP_VOLUME) return;
        int unitMask = Marshal.ReadInt32(lParam, 12);
        short flags = Marshal.ReadInt16(lParam, 16);
        if ((flags & DBTF_NET) != 0) return; // network drives aren't interesting here

        for (int i = 0; i < 26; i++)
        {
            if ((unitMask & (1 << i)) == 0) continue;
            char letter = (char)('A' + i);
            bool arrived = change == DBT_DEVICEARRIVAL;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnDriveChanged(letter, arrived)));
            break;
        }
    }

    private void OnDriveChanged(char letter, bool arrived)
    {
        if (!_settings.DevicePopOuts) return;
        if (!arrived)
        {
            ShowActivity("", null, "Drive removed", $"{letter}:", null);
            return;
        }
        string details = $"{letter}:";
        try
        {
            var drive = new DriveInfo($"{letter}:\\");
            if (drive.IsReady)
            {
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : $"  {drive.VolumeLabel}";
                details = $"{letter}:{label}  ·  {SystemStats.FormatBytes(drive.AvailableFreeSpace)} free";
            }
        }
        catch { }
        ShowActivity("", null, "Drive connected", details, null);
    }

    // =====================================================================================
    //  Settings
    // =====================================================================================

    private bool _loadingSettings;

    private static readonly (string Name, string Hex)[] AccentChoices =
    {
        ("Gold", "#D6B46A"), ("Sky", "#5AA9F0"), ("Mint", "#6CCB8C"),
        ("Rose", "#F08DB4"), ("Lilac", "#A98BF0"), ("Silver", "#C8CCD2"),
    };

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_expanded) Expand();
        // No tab is highlighted while Settings is open; clicking any tab goes back to it.
        foreach (var tab in new[] { TodayTabButton, ControlsTabButton, AppsTabButton, ClipboardTabButton, ShelfTabButton,
                                    MediaTabButton, StatsTabButton, PowerTabButton })
            tab.IsChecked = false;
        ShowTab("Settings", animate: true);
    }

    private void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var (name, hex) in AccentChoices)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool selected = string.Equals(hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(selected ? 2.5 : 0),
                Cursor = Cursors.Hand,
                ToolTip = name,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _settings.AccentColor = hex;
                _settings.Save();
                ApplyAccent();
                BuildAccentSwatches();
            };
            AccentSwatches.Children.Add(swatch);
        }
    }

    /// <summary>Recolours everything gold-coloured to the chosen accent.</summary>
    private void ApplyAccent()
    {
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(_settings.AccentColor); }
        catch { accent = Color.FromRgb(0xD6, 0xB4, 0x6A); }

        static Color Blend(Color a, Color b, double amountOfA) => Color.FromRgb(
            (byte)(a.R * amountOfA + b.R * (1 - amountOfA)),
            (byte)(a.G * amountOfA + b.G * (1 - amountOfA)),
            (byte)(a.B * amountOfA + b.B * (1 - amountOfA)));

        static SolidColorBrush Solid(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        Resources["Accent"] = Solid(accent);
        Resources["AccentSoft"] = Solid(Blend(accent, Color.FromRgb(0x12, 0x12, 0x12), 0.22));
        Resources["AccentBright"] = Solid(Blend(accent, Colors.White, 0.85));
        Resources["AccentFaint"] = Solid(Color.FromArgb(0x33, accent.R, accent.G, accent.B));

        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x46, accent.R, accent.G, accent.B), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 0.85));
        gradient.Freeze();
        Resources["AccentHoverGradient"] = gradient;

        ToggleTile.SetAccent(accent);
        ApplyIslandStyle(); // the glow and outline use the accent colour
        RenderCalendar();
        RefreshBattery(allowPopOut: false);
        RefreshMediaTint();
        ApplyPowerSliderAccent();
    }

    /// <summary>Black, Glass (see-through smoked glass), Glow (soft accent halo) or Outline (thin accent edge).</summary>
    private void ApplyIslandStyle()
    {
        IslandShape.Effect = null;
        IslandShape.Stroke = null;
        IslandShape.StrokeThickness = 0;
        IslandShape.Fill = Brushes.Black;

        // Default (solid) surfaces
        Color card = Color.FromRgb(0x17, 0x17, 0x17), field = Color.FromRgb(0x1E, 0x1E, 0x1E), tileOff = Color.FromRgb(0x26, 0x26, 0x26);
        Color cardBorder = Colors.Transparent;
        Color tile = Color.FromRgb(0x1C, 0x1C, 0x1E), tileHover = Color.FromRgb(0x2A, 0x2A, 0x2D);

        Color accent = (Resources["Accent"] as SolidColorBrush)?.Color ?? Color.FromRgb(0xD6, 0xB4, 0x6A);
        switch (_settings.IslandStyle)
        {
            case "Glass" when _glass.Supported:
                // Real frosted glass: Windows blurs whatever is behind (see AcrylicBackdrop); the island adds
                // a light sheen on top, a fine bright edge, and see-through cards so the frost shows everywhere.
                var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF), 0));
                sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), 1));
                sheen.Freeze();
                IslandShape.Fill = sheen;
                IslandShape.Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                IslandShape.StrokeThickness = 1;
                card = Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);
                field = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
                tileOff = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
                cardBorder = Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF);
                tile = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
                tileHover = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
                break;

            case "Glass":
                // Older Windows without acrylic: tinted see-through instead.
                var tint = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                tint.GradientStops.Add(new GradientStop(Color.FromArgb(0xD4, 0x22, 0x22, 0x25), 0));
                tint.GradientStops.Add(new GradientStop(Color.FromArgb(0xC2, 0x0E, 0x0E, 0x11), 1));
                tint.Freeze();
                IslandShape.Fill = tint;
                IslandShape.Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                IslandShape.StrokeThickness = 1;
                break;

            case "Glow":
                // The halo itself is drawn on a click-through layer behind the island (see GlowBackdrop).
                // Inside, the island glows faintly from the bottom edge and the cards pick up the accent.
                var inner = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                inner.GradientStops.Add(new GradientStop(Colors.Black, 0));
                inner.GradientStops.Add(new GradientStop(Colors.Black, 0.55));
                inner.GradientStops.Add(new GradientStop(Blend(accent, Colors.Black, 0.16), 1));
                inner.Freeze();
                IslandShape.Fill = inner;
                cardBorder = Color.FromArgb(0x55, accent.R, accent.G, accent.B);
                break;

            case "Outline":
                IslandShape.Stroke = new SolidColorBrush(accent);
                IslandShape.StrokeThickness = 1.4;
                break;
        }

        Resources["CardBrush"] = Frozen(card);
        Resources["FieldBrush"] = Frozen(field);
        Resources["CardBorder"] = Frozen(cardBorder);
        Resources["TileBrush"] = Frozen(tile);
        Resources["ListFadeTop"] = IsGlass ? Brushes.Transparent : FadeBrush(toBottom: false);
        // In Glow, the island is tinted near the bottom, so the bottom fade blends into that tint instead of black.
        Resources["ListFadeBottom"] = IsGlass ? Brushes.Transparent
            : FadeBrush(toBottom: true, _settings.IslandStyle == "Glow" ? Blend(accent, Colors.Black, 0.12) : Colors.Black);
        Resources["TileHoverBrush"] = Frozen(tileHover);
        ToggleTile.SetOffBackground(tileOff);
        UpdateIslandShape();
        UpdateContentClip();
        SyncBackdrops();
    }

    private static Color Blend(Color a, Color b, double amountOfA) => Color.FromRgb(
        (byte)(a.R * amountOfA + b.R * (1 - amountOfA)),
        (byte)(a.G * amountOfA + b.G * (1 - amountOfA)),
        (byte)(a.B * amountOfA + b.B * (1 - amountOfA)));

    private static Brush FadeBrush(bool toBottom) => FadeBrush(toBottom, Colors.Black);

    private static Brush FadeBrush(bool toBottom, Color edge)
    {
        // Fade to see-through *black*: WPF's "Transparent" is see-through white, which turns grey halfway through a fade.
        var clear = Color.FromArgb(0, edge.R, edge.G, edge.B);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(toBottom ? clear : edge, 0));
        brush.GradientStops.Add(new GradientStop(toBottom ? edge : clear, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    // ---- Keeping the glass and glow layers lined up with the island ----

    /// <summary>Puts the glass and glow layers where the island window is (the glow layer a bit bigger, for the halo).</summary>
    private void PositionBackdrops()
    {
        _glass.Left = Left;   // just a starting point; SyncBackdrops sizes it to the island
        _glass.Top = Top;
        _glass.Width = 1;
        _glass.Height = 1;
        _glow.Left = Left - GlowBackdrop.Spread;
        _glow.Top = Top;
        _glow.Width = Width + GlowBackdrop.Spread * 2;
        _glow.Height = Height + GlowBackdrop.Spread;
    }

    /// <summary>Called whenever the island changes size, slides, shows or hides, or the style changes.</summary>
    private void SyncBackdrops()
    {
        if (!IsLoaded || _glass.Handle == IntPtr.Zero || _glow.Handle == IntPtr.Zero) return;
        bool visible = IslandHost.IsVisible && IslandHost.ActualWidth > 0 && IslandShape.Data is not null;
        Color accent = (Resources["Accent"] as SolidColorBrush)?.Color ?? Color.FromRgb(0xD6, 0xB4, 0x6A);
        Point offset = visible ? IslandHost.TranslatePoint(new Point(0, 0), this) : default; // includes the slide

        // Glass: the frost sits exactly behind the island's glass panel. Its top edge is tucked just above the
        // screen so Windows' rounded top corners are out of sight, leaving a flat top and rounded bottom corners.
        if (visible && IsGlass)
        {
            _glass.SetAcrylic(true);
            double scale = DpiScale;
            var corner = IslandHost.PointToScreen(new Point(Shoulder, 0)); // screen pixels, includes the slide
            int tuck = (int)Math.Ceiling(GlassCornerRadius * scale);
            int width = (int)Math.Round((IslandHost.ActualWidth - Shoulder * 2) * scale);
            int height = (int)Math.Round(IslandHost.ActualHeight * scale) + tuck;
            _glass.Place((int)Math.Round(corner.X), (int)Math.Round(corner.Y) - tuck, width, height);
        }
        else
        {
            _glass.Conceal();
        }

        // Glow: draw the halo around the same outline.
        if (visible && _settings.IslandStyle == "Glow")
            _glow.Update(IslandShape.Data, new Point(offset.X + GlowBackdrop.Spread, offset.Y), accent);
        else
            _glow.Update(null, default, accent);

        KeepBackdropsBelow();
    }

    /// <summary>The layers must always sit directly underneath the island, never on top of it.</summary>
    private void KeepBackdropsBelow()
    {
        foreach (var layer in new[] { _glass.Handle, _glow.Handle })
            if (layer != IntPtr.Zero && _hwnd != IntPtr.Zero)
                Native.SetWindowPos(layer, _hwnd, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }


    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        BuildAccentSwatches();
        MusicBarsSwitch.IsChecked = _settings.ShowMusicBars;
        BatterySwitch.IsChecked = _settings.ShowBatteryInPill;
        LineIconsSwitch.IsChecked = _settings.UseLineIcons && LineIcons.Available;
        LineIconsSwitch.IsEnabled = LineIcons.Available;
        LineIconsStatusText.Text = LineIcons.Available
            ? "Tidy, matching icons for your apps instead of their own icons"
            : "Icon set not downloaded: run Build.cmd while online to add it";
        StyleBlack.IsChecked = _settings.IslandStyle is not ("Glass" or "Glow" or "Outline");
        StyleGlass.IsChecked = _settings.IslandStyle == "Glass";
        StyleGlow.IsChecked = _settings.IslandStyle == "Glow";
        StyleOutline.IsChecked = _settings.IslandStyle == "Outline";
        PeekSwitch.IsChecked = _settings.PeekOnHover;
        DelayQuick.IsChecked = _settings.PeekDelayMs <= 300;
        DelayNormal.IsChecked = _settings.PeekDelayMs is > 300 and <= 600;
        DelayRelaxed.IsChecked = _settings.PeekDelayMs > 600;
        HideMaximizedSwitch.IsChecked = _settings.HideOverMaximized;
        DevicePopOutsSwitch.IsChecked = _settings.DevicePopOuts;
        SearchHotkeySwitch.IsChecked = _settings.SearchHotkey;
        AutoUpdateSwitch.IsChecked = _settings.AutoUpdate;
        ScreenshotPopupsSwitch.IsChecked = _settings.ScreenshotPopups;
        DownloadPopupsSwitch.IsChecked = _settings.DownloadPopups;
        CapturesToShelfSwitch.IsChecked = _settings.CapturesToShelf;
        ShowUpdateState();
        ShowHotkeyStatus();
        StartWithWindowsSwitch.IsChecked = _startupEnabled;
        AboutVersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(2)}";
        CelsiusChip.IsChecked = !_settings.Fahrenheit;
        FahrenheitChip.IsChecked = _settings.Fahrenheit;
        PlaceBox.Text = _settings.WeatherPlace;
        PlaceStatus.Text = _settings.WeatherLatitude is null ? "Using Windows' location." : $"Using {_settings.WeatherPlace}.";
        _loadingSettings = false;
        RefreshStartupStatus();
    }

    /// <summary>Any switch or choice in Settings: save it and apply it straight away.</summary>
    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool unitsChanged = _settings.Fahrenheit != (FahrenheitChip.IsChecked == true);

        _settings.ShowMusicBars = MusicBarsSwitch.IsChecked == true;
        bool batteryChanged = _settings.ShowBatteryInPill != (BatterySwitch.IsChecked == true);
        bool iconsChanged = LineIcons.Available && _settings.UseLineIcons != (LineIconsSwitch.IsChecked == true);
        if (LineIcons.Available) _settings.UseLineIcons = LineIconsSwitch.IsChecked == true;
        _settings.ShowBatteryInPill = BatterySwitch.IsChecked == true;
        _settings.IslandStyle = StyleGlass.IsChecked == true ? "Glass"
            : StyleGlow.IsChecked == true ? "Glow"
            : StyleOutline.IsChecked == true ? "Outline"
            : "Black";
        _settings.PeekOnHover = PeekSwitch.IsChecked == true;
        _settings.PeekDelayMs = DelayQuick.IsChecked == true ? 250 : DelayRelaxed.IsChecked == true ? 750 : 450;
        _settings.HideOverMaximized = HideMaximizedSwitch.IsChecked == true;
        _settings.DevicePopOuts = DevicePopOutsSwitch.IsChecked == true;
        _settings.SearchHotkey = SearchHotkeySwitch.IsChecked == true;
        bool autoUpdateTurnedOn = !_settings.AutoUpdate && AutoUpdateSwitch.IsChecked == true;
        _settings.AutoUpdate = AutoUpdateSwitch.IsChecked == true;
        _settings.ScreenshotPopups = ScreenshotPopupsSwitch.IsChecked == true;
        _settings.DownloadPopups = DownloadPopupsSwitch.IsChecked == true;
        _settings.CapturesToShelf = CapturesToShelfSwitch.IsChecked == true;
        _settings.Fahrenheit = FahrenheitChip.IsChecked == true;
        _settings.Save();

        if (sender == StartWithWindowsSwitch) SetStartup(StartWithWindowsSwitch.IsChecked == true);

        if (autoUpdateTurnedOn && _availableUpdate is not null) InstallUpdate(_availableUpdate, automatic: true);
        ApplyBehaviourSettings();
        UpdateSearchHotkey();
        ShowHotkeyStatus();
        ApplyIslandStyle();
        if (batteryChanged) UpdatePillBatteryVisibility();
        if (iconsChanged)
        {
            RefreshOpenApps();
            _rows.Clear(); // the Apps list rebuilds with the new icons next time it opens
        }
        if (unitsChanged) RefreshWeather(force: true);
    }

    private void ShowHotkeyStatus()
    {
        bool taken = _settings.SearchHotkey && _hotkeyTaken;
        SearchHotkeyStatusText.Text = taken
            ? "Another app is already using Ctrl+Space, so this can't be used right now"
            : "From anywhere: type an app's name and press Enter. Press it again to close";
        SearchHotkeyStatusText.Foreground = taken ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x94)) : (Brush)FindResource("SubtleText");
    }

    // ---- Screenshots and downloads ----

    private readonly CaptureWatcher _captures = new();
    private string? _activityOpensTab; // clicking the current pop-out opens this tab

    private void StartCaptureWatcher()
    {
        _captures.FileReady += (path, kind) => Dispatcher.BeginInvoke(new Action(() => OnCaptureReady(path, kind)));
        _captures.Start();
    }

    private void OnCaptureReady(string path, CaptureWatcher.Kind kind)
    {
        bool screenshot = kind == CaptureWatcher.Kind.Screenshot;
        if (screenshot ? !_settings.ScreenshotPopups : !_settings.DownloadPopups) return;

        bool onShelf = _settings.CapturesToShelf && _shelf.Add(new[] { path }) > 0;
        ImageSource? art = screenshot ? LoadThumbnail(path) : AppCatalog.GetShellImage(path, 96);
        string name = Path.GetFileName(path);

        if (screenshot)
            ShowActivity("\uE722", art, "Screenshot", onShelf ? "On your Shelf · click to open" : "Saved to Screenshots", null);
        else
            ShowActivity("\uE896", art, "Download finished", onShelf ? $"{name} · on your Shelf" : name, null);
        if (onShelf && _activityShowing) _activityOpensTab = "Shelf";
    }

    /// <summary>A small preview of an image file (read once, so the file isn't kept open).</summary>
    private static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    // ---- Tabs: which ones show, and in what order ----

    private static readonly string[] DefaultTabOrder = { "Today", "Controls", "Apps", "Clipboard", "Shelf", "Media", "Stats", "Power" };

    private RadioButton TabButtonFor(string tab) => tab switch
    {
        "Today" => TodayTabButton, "Controls" => ControlsTabButton, "Apps" => AppsTabButton, "Clipboard" => ClipboardTabButton,
        "Shelf" => ShelfTabButton, "Media" => MediaTabButton, "Stats" => StatsTabButton, _ => PowerTabButton,
    };

    /// <summary>The saved order, tidied: unknown names dropped and any missing tabs added at the end.</summary>
    private List<string> TabOrder()
    {
        var order = _settings.TabOrder.Where(t => DefaultTabOrder.Contains(t)).Distinct().ToList();
        order.AddRange(DefaultTabOrder.Where(t => !order.Contains(t)));
        return order;
    }

    private bool IsTabHidden(string tab) => _settings.HiddenTabs.Contains(tab);

    private string FirstVisibleTab() => TabOrder().FirstOrDefault(t => !IsTabHidden(t)) ?? "Today";

    /// <summary>Puts the tab buttons along the top in the chosen order and hides the hidden ones.</summary>
    private void ApplyTabLayout()
    {
        TabBar.Children.Clear();
        foreach (var tab in TabOrder())
        {
            var button = TabButtonFor(tab);
            button.Visibility = IsTabHidden(tab) ? Visibility.Collapsed : Visibility.Visible;
            TabBar.Children.Add(button);
        }
    }

    /// <summary>Settings › Tabs: one row per tab, with up/down arrows and a show switch.</summary>
    private void BuildTabsList()
    {
        TabsList.Children.Clear();
        var order = TabOrder();
        int visibleCount = order.Count(t => !IsTabHidden(t));
        for (int i = 0; i < order.Count; i++)
        {
            string tab = order[i];
            int index = i;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            var show = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                IsChecked = !IsTabHidden(tab),
                VerticalAlignment = VerticalAlignment.Center,
                // The last visible tab can't be hidden.
                IsEnabled = IsTabHidden(tab) || visibleCount > 1,
                ToolTip = "Show this tab",
            };
            show.Click += (_, _) =>
            {
                if (show.IsChecked == true) _settings.HiddenTabs.Remove(tab);
                else if (!_settings.HiddenTabs.Contains(tab)) _settings.HiddenTabs.Add(tab);
                SaveTabLayout();
            };
            DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(show);

            Button Arrow(string glyph, string tip, int move, bool enabled)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("IslandButton"),
                    Width = 28, Height = 28, Margin = new Thickness(0, 0, 2, 0),
                    IsEnabled = enabled, Opacity = enabled ? 1 : 0.25, ToolTip = tip,
                    Content = new TextBlock { Style = (Style)FindResource("Glyph"), FontSize = 11, Text = glyph },
                };
                b.Click += (_, _) =>
                {
                    var list = TabOrder();
                    (list[index], list[index + move]) = (list[index + move], list[index]);
                    _settings.TabOrder = list;
                    SaveTabLayout();
                };
                DockPanel.SetDock(b, Dock.Right);
                return b;
            }
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
            arrows.Children.Add(Arrow("\uE70E", "Move earlier (further left)", -1, i > 0));
            arrows.Children.Add(Arrow("\uE70D", "Move later (further right)", +1, i < order.Count - 1));
            DockPanel.SetDock(arrows, Dock.Right);
            row.Children.Add(arrows);

            row.Children.Add(new TextBlock
            {
                Text = tab,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = IsTabHidden(tab) ? 0.45 : 1,
            });
            TabsList.Children.Add(row);
        }
    }

    private void SaveTabLayout()
    {
        _settings.Save();
        ApplyTabLayout();
        BuildTabsList();
        ResizeExpandedIfNeeded();
    }

    // ---- Updates from GitHub ----

    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private UpdateInfo? _availableUpdate;
    private string? _downloadedUpdate;
    private bool _updateBusy;
    private bool _toldAboutUpdate;
    private string _updateMessage = "Checks GitHub for new versions";

    /// <summary>Starts the regular checks: the first a minute after starting, then every 6 hours.</summary>
    private void StartUpdateChecks()
    {
        // Finish off a previous update: remove the old copy, and say so if we've just been updated.
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(10)); UpdateService.CleanUpOldVersion(); });
        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == UpdateService.UpdatedArgument))
        {
            var justUpdated = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            justUpdated.Tick += (_, _) =>
            {
                justUpdated.Stop();
                ShowActivity("\uE895", null, $"Updated to {UpdateService.Current.ToString(3)}", "Dynamic Island is up to date", null);
            };
            justUpdated.Start();
        }

        _updateTimer.Tick += (_, _) => CheckForUpdate(manual: false);
        _updateTimer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        first.Tick += (_, _) => { first.Stop(); CheckForUpdate(manual: false); };
        first.Start();
    }

    private async void CheckForUpdate(bool manual)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        SetUpdateMessage("Checking for updates…");
        try
        {
            var update = await UpdateService.CheckAsync();
            _availableUpdate = update;
            if (update is null)
            {
                SetUpdateMessage($"You're on the latest version ({UpdateService.Current.ToString(3)}). Last checked {DateTime.Now:t}");
            }
            else if (_settings.AutoUpdate && UpdateService.CanUpdateInPlace)
            {
                _updateBusy = false;
                InstallUpdate(update, automatic: true);
                return;
            }
            else
            {
                SetUpdateMessage(UpdateService.CanUpdateInPlace
                    ? $"Version {update.Version.ToString(3)} is available"
                    : $"Version {update.Version.ToString(3)} is available. This folder can't be written to, so download it from the project page");
                if (!_toldAboutUpdate && !manual)
                {
                    _toldAboutUpdate = true;
                    ShowActivity("\uE896", null, $"Version {update.Version.ToString(3)} is available", "Settings › About to update", null);
                }
            }
        }
        catch (Exception ex)
        {
            SetUpdateMessage(manual ? "Couldn't reach GitHub just now. Try again later" : _updateMessage);
            App.LogNote($"update check failed: {ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            ShowUpdateState();
        }
    }

    /// <summary>Downloads the update, then installs it (restarting the island) as soon as you're not using it.</summary>
    private async void InstallUpdate(UpdateInfo update, bool automatic)
    {
        if (_updateBusy) return;
        if (!UpdateService.CanUpdateInPlace)
        {
            SystemActions.Open(update.PageUrl);
            return;
        }
        _updateBusy = true;
        try
        {
            if (_downloadedUpdate is null || !File.Exists(_downloadedUpdate))
            {
                SetUpdateMessage($"Downloading version {update.Version.ToString(3)}…");
                _downloadedUpdate = await UpdateService.DownloadAsync(update);
            }
            SetUpdateMessage($"Version {update.Version.ToString(3)} is ready; installing when the island is closed");

            // Wait until the island isn't being used (closed, no menu), then swap and restart.
            while (_expanded || _menuOpen || _peeking)
                await Task.Delay(TimeSpan.FromSeconds(automatic ? 5 : 1));
            if (!UpdateService.ApplyAndRestart(_downloadedUpdate, update.Version))
            {
                _downloadedUpdate = null;
                SetUpdateMessage("The update couldn't be installed. Details are in the error log");
            }
        }
        catch (Exception ex)
        {
            _downloadedUpdate = null;
            App.LogNote($"update download failed: {ex.Message}");
            SetUpdateMessage("The update couldn't be downloaded. It'll try again later");
        }
        finally
        {
            _updateBusy = false;
            ShowUpdateState();
        }
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not null)
        {
            Collapse(); // it installs as soon as the island is closed
            InstallUpdate(_availableUpdate, automatic: false);
        }
        else
        {
            CheckForUpdate(manual: true);
        }
    }

    private void SetUpdateMessage(string message)
    {
        _updateMessage = message;
        ShowUpdateState();
    }

    private void ShowUpdateState()
    {
        UpdateStatusText.Text = _updateMessage;
        CheckUpdatesText.Text = _availableUpdate is not null
            ? (UpdateService.CanUpdateInPlace ? "Update now" : "Open download page")
            : "Check now";
        CheckUpdatesButton.IsEnabled = !_updateBusy;
    }

    // ---- Settings sections and the About page ----

    private void SettingsSection_Checked(object sender, RoutedEventArgs e)
    {
        if (SettingsAppearance is null || SettingsTabs is null) return; // still being built
        if (sender == SettingsNavTabs) BuildTabsList();
        foreach (var (nav, panel) in new (RadioButton, FrameworkElement)[]
                 {
                     (SettingsNavAppearance, SettingsAppearance), (SettingsNavBehaviour, SettingsBehaviour),
                     (SettingsNavPopups, SettingsPopups), (SettingsNavTabs, SettingsTabs),
                     (SettingsNavWeather, SettingsWeather), (SettingsNavAbout, SettingsAbout),
                 })
            panel.Visibility = nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_currentTab == "Settings")
        {
            ResizeExpandedIfNeeded();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ResizeExpandedIfNeeded));
        }
    }

    /// <summary>"Set your town" on the Today tab: straight to the Weather settings.</summary>
    private void WeatherSetup_Click(object sender, RoutedEventArgs e)
    {
        SettingsNavWeather.IsChecked = true;
        Settings_Click(sender, e);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.GetDirectoryName(App.CrashLogPath)!;
        Directory.CreateDirectory(folder);
        Collapse();
        SystemActions.Open(folder);
    }

    private void OpenProjectPage_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        SystemActions.Open("https://github.com/samround73-cpu/dynamic-island-windows");
    }

    /// <summary>
    /// Removes everything the island has added to this PC (startup entry, Start menu shortcut, settings and logs,
    /// and the files .NET unpacks to run it), then quits with the normal taskbar back. Only the exe itself is left.
    /// </summary>
    private void RemoveFromPc_Click(object sender, RoutedEventArgs e)
    {
        _menuOpen = true; // keep the island open while the question is up
        var answer = MessageBox.Show(
            "This removes Dynamic Island's startup entry, Start menu shortcut, settings, pinned clips and logs, " +
            "then closes it and brings your normal taskbar back.\n\n" +
            "Afterwards you can delete DynamicIsland.exe itself.\n\nRemove it now?",
            "Remove Dynamic Island", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        _menuOpen = false;
        if (answer != MessageBoxResult.Yes) return;

        try { StartupManager.SetEnabled(false); } catch { }
        StartMenuShortcut.Remove();

        // The settings folder (and the .NET files unpacked to run the exe) can only go once the island has closed,
        // so a tiny hidden command waits a few seconds and then deletes them.
        string data = Path.GetDirectoryName(App.CrashLogPath)!;
        string unpacked = Path.Combine(Path.GetTempPath(), ".net", "DynamicIsland");
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping -n 5 127.0.0.1 >nul & rmdir /s /q \"{data}\" & rmdir /s /q \"{unpacked}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }

        _settings.DoNotSave = true; // don't recreate the settings file while closing
        Close();
    }

    private void StartupFix_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        SystemActions.Open("ms-settings:startupapps");
    }

    // ---- Start with Windows (checking it runs a small Windows tool, so it's done off the UI thread) ----

    private bool _startupEnabled;

    private async void RefreshStartupStatus()
    {
        StartupManager.Method method;
        bool blocked;
        try
        {
            (method, blocked) = await Task.Run(() => (StartupManager.Current, StartupManager.BlockedByWindows));
        }
        catch { return; }

        _startupEnabled = method != StartupManager.Method.Off;
        _loadingSettings = true;
        StartWithWindowsSwitch.IsChecked = _startupEnabled;
        _loadingSettings = false;
        StartWithWindowsMenuItem.IsChecked = _startupEnabled;

        StartupStatusText.Text = method switch
        {
            StartupManager.Method.RunEntry when blocked => "Windows has switched this off in its Startup apps list",
            StartupManager.Method.RunEntry => "Opens when you sign in (Windows didn't allow reopening on reconnect)",
            _ => "Opens when you sign in, and again when you reconnect or unlock if it isn't running",
        };
        StartupStatusText.Foreground = blocked ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x94)) : (Brush)FindResource("SubtleText");
        StartupFixButton.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SetStartup(bool enabled)
    {
        _startupEnabled = enabled;
        StartWithWindowsMenuItem.IsChecked = enabled;
        try { await Task.Run(() => StartupManager.SetEnabled(enabled)); } catch { }
        RefreshStartupStatus();
    }

    private void ApplyBehaviourSettings()
    {
        _peekOpenTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.PeekDelayMs, 100, 1500));
        SetEqualizer(_media.IsPlaying);
    }

    private void PlaceBox_TextChanged(object sender, TextChangedEventArgs e) =>
        PlacePlaceholder.Visibility = PlaceBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void PlaceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SavePlace_Click(sender, e);
    }

    /// <summary>Looks up the town you typed (or switches back to Windows' location if the box is empty).</summary>
    private async void SavePlace_Click(object sender, RoutedEventArgs e)
    {
        string query = PlaceBox.Text.Trim();
        if (query.Length == 0)
        {
            _settings.WeatherPlace = "";
            _settings.WeatherLatitude = null;
            _settings.WeatherLongitude = null;
            _settings.Save();
            PlaceStatus.Text = "Using Windows' location.";
            RefreshWeather(force: true);
            return;
        }

        PlaceStatus.Text = "Looking that up…";
        var found = await WeatherService.FindPlaceAsync(query);
        if (found is null)
        {
            PlaceStatus.Text = $"Couldn't find \"{query}\". Try adding the country, e.g. \"Gravelines, France\".";
            return;
        }
        _settings.WeatherPlace = found.Value.Name;
        _settings.WeatherLatitude = found.Value.Latitude;
        _settings.WeatherLongitude = found.Value.Longitude;
        _settings.Save();
        PlaceBox.Text = found.Value.Name;
        PlaceStatus.Text = $"Using {found.Value.Name}.";
        RefreshWeather(force: true);
    }
}
