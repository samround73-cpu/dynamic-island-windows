using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>The hover tray of open apps, the Apps tab list with search, and live previews.</summary>
public partial class MainWindow
{
    // All installed apps, and quick ways to match a running window back to one.
    private List<InstalledApp> _installedApps = new();
    private Dictionary<string, List<InstalledApp>> _installedByExe = new();
    private Dictionary<string, InstalledApp> _installedByName = new();
    private DateTime _installedAppsLoadedAt = DateTime.MinValue;
    private bool _installedAppsLoading;

    // What the Apps list is currently showing.
    private readonly List<AppRow> _rows = new();
    private int _selectedRow = -1;

    // Smooth scrolling: a target the list glides towards, frame by frame.
    private double _scrollTarget;
    private double _scrollCurrent;
    private bool _scrollRunning;
    private TimeSpan _lastScrollFrame;
    private bool _cascadeNext; // the next rows to appear should cascade in (a fresh list)

    // Where each row sits in the list (worked out once after layout, so scrolling doesn't have to measure rows).
    private double[] _rowTops = Array.Empty<double>();

    // Rows the hover wave has nudged sideways (so only those need moving back).
    private readonly HashSet<FrameworkElement> _wavedRows = new();

    // Hover previews
    private AppTile? _hoverTile;
    private FrameworkElement? _hoverElement;

    // =====================================================================================
    //  Open apps (used by the hover tray)
    // =====================================================================================

    private void RefreshOpenApps()
    {
        var windows = WindowTracker.GetTaskbarWindows();

        // Group windows of the same app under one icon.
        var groups = new Dictionary<string, AppTile>();
        foreach (var window in windows)
        {
            if (!groups.TryGetValue(window.GroupKey, out var tile))
            {
                bool isStoreApp = window.GroupKey.StartsWith("window:", StringComparison.Ordinal);
                tile = new AppTile
                {
                    Key = window.GroupKey,
                    Name = window.AppName,
                    Icon = WindowTracker.GetIcon(window),
                    ExePath = isStoreApp ? "" : window.ExePath,
                    MostRecent = window.Handle, // the list is newest-first
                    LaunchPath = MatchInstalled(window)?.LaunchPath,
                };
                groups[window.GroupKey] = tile;
            }
            tile.Windows.Add(window.Handle);
        }

        foreach (var tile in groups.Values)
        {
            tile.Windows.Sort((a, b) => a.ToInt64().CompareTo(b.ToInt64()));
            tile.Count = tile.Windows.Count;
            tile.IsActive = tile.Windows.Contains(_lastForeground) && !Native.IsIconic(_lastForeground);
            tile.IsMinimized = tile.Windows.All(Native.IsIconic);
            tile.Line = LineIconFor(tile.Name, tile.ExePath, tile.Icon);
            IntPtr shown = tile.IsActive ? _lastForeground : tile.MostRecent;
            tile.Title = CleanTitle(WindowTracker.GetTitle(shown), tile.Name);
        }

        // Keep icons in the order apps were opened, so they don't jump around.
        _openAppOrder.RemoveAll(key => !groups.ContainsKey(key));
        foreach (var key in groups.Keys)
            if (!_openAppOrder.Contains(key)) _openAppOrder.Add(key);

        SyncTiles(_openApps, _openAppOrder.Select(key => groups[key]).ToList());
        AppsOpenChips.Visibility = _openApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNowUsing();
    }

    private readonly List<string> _openAppOrder = new();

    /// <summary>The line-style icon for an app, or null to show its own icon (setting off, or no icon set).</summary>
    private LineIcon? LineIconFor(string name, string? exePath, ImageSource? realIcon) =>
        _settings.UseLineIcons ? LineIcons.For(name, exePath ?? "", realIcon) : null;

    /// <summary>
    /// "Dynamic island - Work - Microsoft Edge" → "Dynamic island": just what you're looking at.
    /// Returns "" when the title is only the app's own name.
    /// </summary>
    private static string CleanTitle(string title, string appName)
    {
        title = title.Trim();
        if (title.Length == 0) return "";

        // Window titles are usually "What you're looking at - more detail - App name"; keep the first part.
        foreach (var separator in new[] { " - ", " \u2014 ", " \u2013 ", " | " })
        {
            int cut = title.IndexOf(separator, StringComparison.Ordinal);
            if (cut > 0) title = title[..cut].Trim();
        }
        bool justTheApp = title.Equals(appName, StringComparison.CurrentCultureIgnoreCase)
            || appName.EndsWith(title, StringComparison.CurrentCultureIgnoreCase);
        return justTheApp ? "" : title;
    }

    /// <summary>Updates the visible list in place rather than rebuilding it, so nothing flickers.</summary>
    private static void SyncTiles(ObservableCollection<AppTile> target, IList<AppTile> fresh)
    {
        var keys = new HashSet<string>(fresh.Select(t => t.Key));
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(target[i].Key)) target.RemoveAt(i);

        for (int i = 0; i < fresh.Count; i++)
        {
            var incoming = fresh[i];
            int current = -1;
            for (int j = 0; j < target.Count; j++)
                if (target[j].Key == incoming.Key) { current = j; break; }

            if (current < 0)
            {
                target.Insert(i, incoming);
                continue;
            }

            var existing = target[current];
            existing.Name = incoming.Name;
            if (incoming.Icon is not null) existing.Icon = incoming.Icon;
            existing.Windows = incoming.Windows;
            existing.MostRecent = incoming.MostRecent;
            existing.Count = incoming.Count;
            existing.IsActive = incoming.IsActive;
            existing.IsMinimized = incoming.IsMinimized;
            existing.Title = incoming.Title;
            existing.Line = incoming.Line;
            existing.LaunchPath = incoming.LaunchPath;
            existing.IsClosedPin = incoming.IsClosedPin;
            if (current != i) target.Move(current, i);
        }
    }

    /// <summary>
    /// Click an open app (hover tray or Apps tab): bring it back — from minimised too — or minimise it if it's
    /// already in front. With several windows, each click moves on to the next one.
    /// </summary>
    private void AppTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppTile tile) return;
        var windows = tile.Windows.Where(Native.IsWindow).ToList();
        if (windows.Count == 0) return;
        int activeIndex = windows.IndexOf(_lastForeground);

        if (_expanded) Collapse();
        else ExitPeek();

        if (activeIndex >= 0 && windows.Count == 1 && !Native.IsIconic(windows[0]))
        {
            WindowTracker.Minimize(windows[0]);
            return;
        }
        IntPtr target = activeIndex >= 0 && !Native.IsIconic(windows[activeIndex])
            ? windows[(activeIndex + 1) % windows.Count]
            : (windows.Contains(tile.MostRecent) ? tile.MostRecent : windows[0]);
        WindowTracker.Activate(target);
    }

    private void TileNewWindow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppTile tile || string.IsNullOrEmpty(tile.ExePath)) return;
        ExitPeek();
        SystemActions.Open(tile.ExePath);
    }

    private void TileClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppTile tile) return;
        CloseWindows(tile.Windows, tile.Name);
    }

    /// <summary>Closes an app's windows; if Windows won't allow it (an administrator app), says so instead of doing nothing.</summary>
    private void CloseWindows(IEnumerable<IntPtr> windows, string appName)
    {
        bool refused = false;
        foreach (var window in windows.ToList())
            if (!WindowTracker.Close(window)) refused = true;
        if (refused) ShowCloseRefused(appName);
    }

    private async void ShowCloseRefused(string appName)
    {
        string name = appName.Length > 0 ? appName : "This app";
        // The pop-out only shows on the plain pill, so close the tray / panel first.
        if (_peeking) ExitPeek();
        if (_expanded) Collapse();
        await System.Threading.Tasks.Task.Delay(450);
        ShowActivity("\uE7BA", null, $"Can't close {name}", "It runs as administrator; close it from its own window", null);
    }

    // ---- Pinned apps: a dock row in the hover tray ----

    /// <summary>Pinned apps (open ones in colour, closed ones faded), a divider, then any other open apps.</summary>
    private readonly ObservableCollection<AppTile> _dock = new();
    private static readonly AppTile DockDivider = new() { Key = "|divider", IsDivider = true };

    /// <summary>
    /// The hover tray's row of apps: pinned apps first (faded if they aren't running), a thin line, then the other
    /// open apps. The app in front has a ring in the accent colour.
    /// </summary>
    private void UpdateDock()
    {
        var pinned = new List<AppTile>();
        var shown = new HashSet<AppTile>();
        foreach (var launch in _settings.TrayPins)
        {
            var open = _openApps.FirstOrDefault(t => string.Equals(t.LaunchPath, launch, StringComparison.OrdinalIgnoreCase));
            if (open is not null)
            {
                if (shown.Add(open)) pinned.Add(open);
                continue;
            }
            var app = _installedApps.FirstOrDefault(a => string.Equals(a.LaunchPath, launch, StringComparison.OrdinalIgnoreCase));
            if (app is null) continue; // still loading the apps list, or it was uninstalled
            pinned.Add(new AppTile
            {
                Key = "pin:" + launch,
                Name = app.Name,
                Icon = app.Icon,
                ExePath = app.ExePath ?? "",
                LaunchPath = launch,
                Line = LineIconFor(app.Name, app.ExePath, app.Icon),
                IsClosedPin = true,
            });
        }

        var others = _openApps.Where(t => !shown.Contains(t)).ToList();
        var all = new List<AppTile>(pinned);
        if (pinned.Count > 0 && others.Count > 0) all.Add(DockDivider);
        all.AddRange(others);
        SyncTiles(_dock, all);

        PeekDock.Visibility = _dock.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Click in the dock row: a closed pinned app starts; an open one behaves like any open app.</summary>
    private void DockTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppTile tile || tile.IsDivider) return;
        if (!tile.IsClosedPin)
        {
            AppTile_Click(sender, e);
            return;
        }
        if (string.IsNullOrEmpty(tile.LaunchPath)) return;
        ExitPeek();
        _settings.AddRecent(tile.LaunchPath);
        SystemActions.Open(tile.LaunchPath);
    }

    /// <summary>Right-click an app in the hover tray or the Apps tab's open-app chips. The menu is made on the spot.</summary>
    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppTile tile || tile.IsDivider) return;
        e.Handled = true;

        MenuItem Item(string header, RoutedEventHandler click)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            return item;
        }

        var menu = new ContextMenu { DataContext = tile };
        menu.Opened += Menu_Opened;
        menu.Closed += Menu_Closed;

        if (tile.IsClosedPin) menu.Items.Add(Item("Open", (_, _) => DockTile_Click(element, new RoutedEventArgs())));
        else menu.Items.Add(Item("New window", TileNewWindow_Click));

        if (tile.LaunchPath is { } launch && !SettingsPages.IsSettingsTarget(launch))
        {
            bool pinned = _settings.IsTrayPinned(launch);
            menu.Items.Add(Item(pinned ? "Unpin from tray" : "Pin to tray", (_, _) => ToggleTrayPin(launch)));
            if (pinned)
            {
                int index = _settings.TrayPins.FindIndex(s => string.Equals(s, launch, StringComparison.OrdinalIgnoreCase));
                if (index > 0) menu.Items.Add(Item("Move left", (_, _) => MoveTrayPin(launch, -1)));
                if (index < _settings.TrayPins.Count - 1) menu.Items.Add(Item("Move right", (_, _) => MoveTrayPin(launch, +1)));
            }
        }

        if (!tile.IsClosedPin)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Close", TileClose_Click));
        }

        element.ContextMenu = menu; // attached to the icon, so it picks up the island's menu styling
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void ToggleTrayPin(string launchPath)
    {
        _settings.ToggleTrayPin(launchPath);
        _settings.Save();
        AfterTrayPinsChanged();
    }

    private void MoveTrayPin(string launchPath, int delta)
    {
        _settings.MoveTrayPin(launchPath, delta);
        _settings.Save();
        AfterTrayPinsChanged();
    }

    private void AfterTrayPinsChanged()
    {
        RefreshOpenApps();
        RemeasurePeek();
    }

    /// <summary>Resizes the hover tray to fit what's in it now (if it's open).</summary>
    private void RemeasurePeek()
    {
        if (!_peeking) return;
        var size = MeasurePeek();
        AnimateSize(size.Width, size.Height, 300, bouncy: false);
    }

    // ---- Live previews when hovering an app in the tray ----

    /// <summary>
    /// The hover tray's "now using" line: the app in front (icon, name, window title) on the left, the other
    /// open apps as small icons on the right. With nothing in front, it shows how many apps are open.
    /// </summary>
    private void UpdateNowUsing()
    {
        var active = _openApps.FirstOrDefault(t => t.IsActive);
        SyncTiles(_otherOpenApps, _openApps.Where(t => t != active).ToList());
        UpdateDock();

        PeekActiveButton.DataContext = active;
        PeekActiveButton.IsHitTestVisible = active is not null;
        if (active is null)
        {
            PeekActiveImage.Source = null;
            PeekActiveImage.Visibility = Visibility.Collapsed;
            PeekActiveGlyph.Text = "\uE8A9"; // a simple "windows" symbol from Segoe icons
            PeekActiveGlyph.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            PeekActiveGlyph.Foreground = Theme.Ink(0x99);
            PeekActiveGlyph.FontSize = 15;
            PeekActiveGlyph.Visibility = Visibility.Visible;
            PeekActiveName.Text = _openApps.Count switch { 0 => "No apps open", 1 => "1 app open", _ => $"{_openApps.Count} apps open" };
            PeekActiveTitle.Text = "";
            return;
        }

        PeekActiveName.Text = active.ShortName;
        PeekActiveTitle.Text = active.Title;
        if (active.Line is { } line)
        {
            PeekActiveGlyph.FontFamily = LineIcons.FontOrDefault;
            PeekActiveGlyph.FontSize = 20;
            PeekActiveGlyph.Text = line.Glyph;
            PeekActiveGlyph.Foreground = line.Brush;
            PeekActiveGlyph.Visibility = Visibility.Visible;
            PeekActiveImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            PeekActiveImage.Source = active.Icon;
            PeekActiveImage.Visibility = Visibility.Visible;
            PeekActiveGlyph.Visibility = Visibility.Collapsed;
        }
    }

    private void Tile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppTile tile || _menuOpen) return;
        if (tile.Windows.Count == 0) // a pinned app that isn't open: nothing to preview
        {
            _previewShowTimer.Stop();
            if (_preview.IsVisible) RestartTimer(_previewHideTimer);
            return;
        }
        _hoverTile = tile;
        _hoverElement = element;
        _previewHideTimer.Stop();

        // Once a preview is showing, moving to the next icon switches instantly.
        if (_preview.IsVisible) ShowPreview();
        else RestartTimer(_previewShowTimer);
    }

    private void Tile_MouseLeave(object sender, MouseEventArgs e)
    {
        _previewShowTimer.Stop();
        RestartTimer(_previewHideTimer);
    }

    private void ShowPreview()
    {
        bool inTrayOrApps = _peeking || (_expanded && _currentTab == "Apps");
        if (!inTrayOrApps || _menuOpen || _hoverTile is null || _hoverElement is null || !_hoverElement.IsMouseOver) return;

        var items = _hoverTile.Windows
            .Where(Native.IsWindow)
            .Select(h => new PreviewItem(h, WindowTracker.GetTitle(h), _hoverTile.Icon))
            .ToList();

        var topLeft = _hoverElement.TranslatePoint(new Point(0, 0), this);
        var anchor = new Rect(Left + topLeft.X, Top + topLeft.Y, _hoverElement.ActualWidth, _hoverElement.ActualHeight);
        _preview.ShowFor(items, anchor);
    }

    /// <summary>The ✕ on a preview: close that window, then refresh the previews (or hide them if none are left).</summary>
    private async void OnPreviewCloseRequested(IntPtr hWnd)
    {
        if (!WindowTracker.Close(hWnd))
        {
            HidePreview();
            ShowCloseRefused(WindowTracker.GetTitle(hWnd));
            return;
        }
        await System.Threading.Tasks.Task.Delay(350); // give the app a moment to close (it may ask "save changes?")
        RefreshOpenApps();
        if (_openApps.Count == 0)
        {
            HidePreview();
            ExitPeek();
            return;
        }
        if (_hoverTile is not null && _hoverTile.Windows.Any(Native.IsWindow))
        {
            _hoverTile = _openApps.FirstOrDefault(t => t.Key == _hoverTile.Key) ?? _hoverTile;
            var items = _hoverTile.Windows.Where(Native.IsWindow)
                .Select(h => new PreviewItem(h, WindowTracker.GetTitle(h), _hoverTile.Icon)).ToList();
            if (_hoverElement is not null && _preview.IsVisible)
            {
                var topLeft = _hoverElement.TranslatePoint(new Point(0, 0), this);
                _preview.ShowFor(items, new Rect(Left + topLeft.X, Top + topLeft.Y, _hoverElement.ActualWidth, _hoverElement.ActualHeight));
            }
        }
        else
        {
            HidePreview();
        }
        if (_peeking)
        {
            var size = MeasurePeek();
            AnimateSize(size.Width, size.Height, 300, bouncy: false);
        }
    }

    private void HidePreview()
    {
        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _preview.HidePreview();
    }

    // =====================================================================================
    //  Installed apps
    // =====================================================================================

    /// <summary>Reads every installed app in the background (takes a second or two the first time).</summary>
    private async void LoadInstalledApps()
    {
        if (_installedAppsLoading) return;
        _installedAppsLoading = true;
        try
        {
            var apps = await AppCatalog.LoadAsync();
            if (apps.Count == 0 && _installedApps.Count > 0) return; // keep what we had if a reload fails

            _installedApps = apps;
            _installedByExe = apps.Where(a => a.ExePath is not null)
                .GroupBy(a => a.ExePath!)
                .ToDictionary(g => g.Key, g => g.ToList());
            _installedByName = apps
                .GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.CurrentCultureIgnoreCase);
            _installedAppsLoadedAt = DateTime.Now;

            ImportTaskbarPinsOnce();
            if (_settings.TrayPins.Count > 0) RefreshOpenApps(); // pinned apps that aren't open can be shown now
            if (_currentTab == "Apps") RebuildAppList();
        }
        finally
        {
            _installedAppsLoading = false;
        }
    }

    /// <summary>The first time, star whatever was pinned to the old taskbar so the list feels familiar.</summary>
    private void ImportTaskbarPinsOnce()
    {
        if (_settings.ImportedTaskbarPins || _installedApps.Count == 0) return;
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var shortcut in Directory.GetFiles(folder, "*.lnk"))
                {
                    string name = Path.GetFileNameWithoutExtension(shortcut);
                    if (_installedByName.TryGetValue(name, out var app) && !_settings.IsStarred(app.LaunchPath))
                        _settings.Starred.Add(app.LaunchPath);
                }
            }
        }
        catch { }
        _settings.ImportedTaskbarPins = true;
        _settings.Save();
    }

    /// <summary>Which installed app a running window belongs to (or null if it isn't in the Start menu).</summary>
    private InstalledApp? MatchInstalled(WindowInfo window)
    {
        // Store apps (Calculator, Settings…) are matched by their window title.
        if (window.GroupKey.StartsWith("window:", StringComparison.Ordinal))
        {
            string title = window.Title;
            return _installedApps.FirstOrDefault(a => a.IsStoreApp &&
                (title.Equals(a.Name, StringComparison.CurrentCultureIgnoreCase)
                 || title.EndsWith(" - " + a.Name, StringComparison.CurrentCultureIgnoreCase)
                 || title.EndsWith(" – " + a.Name, StringComparison.CurrentCultureIgnoreCase)
                 || title.EndsWith(" — " + a.Name, StringComparison.CurrentCultureIgnoreCase)));
        }

        string exe = window.ExePath.ToLowerInvariant();
        if (Path.GetFileName(exe) == "explorer.exe")
            return _installedApps.FirstOrDefault(a => a.LaunchPath.EndsWith("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase));

        if (_installedByExe.TryGetValue(exe, out var sameExe))
            return sameExe.FirstOrDefault(a => a.Name.Equals(window.AppName, StringComparison.CurrentCultureIgnoreCase)) ?? sameExe[0];

        return _installedByName.TryGetValue(window.AppName, out var byName) ? byName : null;
    }

    /// <summary>Every open window, grouped under the installed app it belongs to (or on its own if none).</summary>
    private (Dictionary<string, List<IntPtr>> ByApp, List<AppRow> Others) ScanOpenWindows()
    {
        var byApp = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
        var others = new Dictionary<string, AppRow>();

        foreach (var window in WindowTracker.GetTaskbarWindows()) // newest first
        {
            var app = MatchInstalled(window);
            if (app is not null)
            {
                if (!byApp.TryGetValue(app.LaunchPath, out var list)) byApp[app.LaunchPath] = list = new List<IntPtr>();
                list.Add(window.Handle);
            }
            else
            {
                if (!others.TryGetValue(window.GroupKey, out var row))
                {
                    others[window.GroupKey] = row = new AppRow
                    {
                        Key = window.GroupKey,
                        Name = window.AppName,
                        ExePath = window.GroupKey.StartsWith("window:", StringComparison.Ordinal) ? null : window.ExePath,
                        Icon = WindowTracker.GetIcon(window),
                        Line = LineIconFor(window.AppName, window.ExePath, WindowTracker.GetIcon(window)),
                        IsOpen = true,
                    };
                }
                row.Windows.Add(window.Handle);
            }
        }
        return (byApp, others.Values.ToList());
    }

    // =====================================================================================
    //  The Apps list
    // =====================================================================================

    private string SearchText => SearchBox.Text.Trim();

    /// <summary>How well an app name matches the search (0 = not at all).</summary>
    private static int SearchScore(string name, string query)
    {
        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 100;
        var words = name.Split(new[] { ' ', '-', '.', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))) return 80;
        var initials = new string(words.Select(w => w[0]).ToArray());
        if (initials.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 60; // "vsc" → Visual Studio Code
        if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return 40;
        return 0;
    }

    /// <summary>Matches a Settings page by its extra keywords (only whole-word starts, and at least 3 letters typed).</summary>
    private static int KeywordScore(string keywords, string query)
    {
        if (query.Length < 3) return 0;
        return keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(k => k.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) ? 50 : 0;
    }

    // ---- Opening the Apps tab quickly ----

    private string _appListStamp = "";
    private bool _listShowsSearch;

    /// <summary>Everything the full list depends on; if none of it has changed, the list can simply be reused.</summary>
    private string AppListStamp(List<AppRow> otherOpen) => string.Join("|",
        _installedApps.Count,
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_installedApps),
        _settings.UseLineIcons && LineIcons.Available,
        string.Join(",", _settings.Starred),
        string.Join(",", _settings.Recent),
        string.Join(",", otherOpen.Select(o => o.Key)));

    /// <summary>
    /// Called when the Apps tab opens. Reuses the list from last time when nothing has changed (just refreshing which
    /// apps are open); otherwise rebuilds it just after the island has started opening, so the animation stays smooth.
    /// </summary>
    private void OpenAppsTab()
    {
        if (SearchText.Length == 0 && !_listShowsSearch && _rows.Count > 0)
        {
            var (_, otherOpen) = ScanOpenWindows();
            if (AppListStamp(otherOpen) == _appListStamp)
            {
                UpdateAppListOpenState();
                foreach (var row in _rows.Where(r => r.IsSelected)) row.IsSelected = false;
                _selectedRow = -1;
                StopSmoothScroll();
                _scrollTarget = _scrollCurrent = 0;
                AppsScroller.ScrollToTop();
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RevealVisibleRows(cascade: false)));
                ResizeExpandedIfNeeded();
                return;
            }
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_currentTab == "Apps") RebuildAppList();
        }));
    }

    private void RebuildAppList()
    {
        string query = SearchText;
        bool searching = query.Length > 0;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var (openByApp, otherOpen) = ScanOpenWindows();
        var items = new List<object>();
        _rows.Clear();

        AppRow Row(InstalledApp app) => new()
        {
            Key = app.LaunchPath,
            Name = app.Name,
            LaunchPath = app.LaunchPath,
            ExePath = app.ExePath,
            Icon = app.Icon,
            Line = LineIconFor(app.Name, app.ExePath, app.Icon),
            IsStarred = _settings.IsStarred(app.LaunchPath),
            IsOpen = openByApp.ContainsKey(app.LaunchPath),
            Windows = openByApp.TryGetValue(app.LaunchPath, out var w) ? w : new List<IntPtr>(),
        };

        void Section(string title, IEnumerable<AppRow> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            items.Add(new AppSectionHeader { Title = title });
            foreach (var row in list)
            {
                items.Add(row);
                _rows.Add(row);
            }
        }

        if (searching)
        {
            var results = _installedApps
                .Select(a => (App: a, Score: SearchScore(a.Name, query)))
                .Where(x => x.Score > 0)
                .Select(x => (Row: Row(x.App), x.Score))
                .Concat(otherOpen.Select(r => (Row: r, Score: SearchScore(r.Name, query))).Where(x => x.Score > 0))
                // Open and starred apps get a small boost, as you're more likely to want them.
                .OrderByDescending(x => x.Score + (x.Row.IsOpen ? 5 : 0) + (x.Row.IsStarred ? 3 : 0))
                .ThenBy(x => x.Row.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Row);
            Section("RESULTS", results);

            // Windows Settings pages ("bluetooth", "display", "startup apps"...).
            var pages = SettingsPages.All
                .Select(page => (Page: page, Score: Math.Max(SearchScore(page.Name, query), KeywordScore(page.Keywords, query))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(4)
                .Select(x => new AppRow
                {
                    Key = "settings:" + x.Page.Uri,
                    Name = x.Page.Name,
                    LaunchPath = x.Page.Uri,
                    Line = LineIcons.Named(x.Page.Icon, "#9DB4CC"),
                });
            Section("SETTINGS", pages);
        }
        else
        {
            Section("STARRED", _installedApps.Where(a => _settings.IsStarred(a.LaunchPath)).Select(Row));
            Section("RECENT", _settings.Recent
                .Select(path => _installedApps.FirstOrDefault(a => a.LaunchPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                .Where(a => a is not null)
                .Select(a => Row(a!)));
            Section("OTHER OPEN WINDOWS", otherOpen);
            Section("APPLICATIONS", _installedApps.Select(Row));
        }

        // Searching: the top result is highlighted, ready for Enter.
        _selectedRow = searching && _rows.Count > 0 ? 0 : -1;
        if (_selectedRow == 0) _rows[0].IsSelected = true;
        _listShowsSearch = searching;
        _appListStamp = searching ? "" : AppListStamp(otherOpen);

        AppsList.ItemsSource = items;
        AppsStatus.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsStatus.Text = _installedApps.Count == 0
            ? (_installedAppsLoading ? "Loading your apps…" : "Couldn't read your apps list")
            : $"No apps match \"{query}\"";

        // Back to the top, then let the rows glide in once they've been laid out.
        StopSmoothScroll();
        _scrollTarget = _scrollCurrent = 0;
        AppsScroller.ScrollToTop();
        _wavedRows.Clear();
        _rowTops = Array.Empty<double>();
        _cascadeNext = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            MeasureRowTops();
            RevealVisibleRows(cascade: true);
        }));
        if (_currentTab == "Apps") ResizeExpandedIfNeeded();
    }

    /// <summary>Refreshes the "Open" dots without rebuilding (so nothing jumps while you're looking at it).</summary>
    private void UpdateAppListOpenState()
    {
        if (_rows.Count == 0) return;
        var (openByApp, otherOpen) = ScanOpenWindows();
        foreach (var row in _rows)
        {
            if (row.LaunchPath is not null)
            {
                bool open = openByApp.TryGetValue(row.LaunchPath, out var windows);
                row.IsOpen = open;
                row.Windows = open ? windows! : new List<IntPtr>();
            }
            else
            {
                var match = otherOpen.FirstOrDefault(o => o.Key == row.Key);
                row.IsOpen = match is not null;
                row.Windows = match?.Windows ?? new List<IntPtr>();
            }
        }
    }

    // ---- Scrolling and the glide-in animation ----

    /// <summary>Notes where every row sits, once, so scrolling never has to measure rows.</summary>
    private void MeasureRowTops()
    {
        int count = AppsList.Items.Count;
        var tops = new double[count];
        for (int i = 0; i < count; i++)
        {
            tops[i] = AppsList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row && row.IsArrangeValid
                ? row.TranslatePoint(new Point(0, 0), AppsList).Y - ((row.RenderTransform as TranslateTransform)?.Y ?? 0)
                : (i > 0 ? tops[i - 1] + 42 : 0);
        }
        _rowTops = tops;
    }

    /// <summary>Fades and slides in rows as they come into view. On a fresh list they cascade in one after another.</summary>
    private void RevealVisibleRows(bool cascade)
    {
        int count = AppsList.Items.Count;
        if (count == 0) return;
        if (_rowTops.Length != count) MeasureRowTops();
        cascade |= _cascadeNext;

        double top = AppsScroller.VerticalOffset - 40;
        double bottom = AppsScroller.VerticalOffset + AppsScroller.ViewportHeight + 80;

        // Jump straight to the first row near the top of the view (rows are in order, so a quick search works).
        int first = Array.BinarySearch(_rowTops, top);
        if (first < 0) first = Math.Max(0, ~first - 1);

        int shown = 0;
        for (int i = first; i < count && _rowTops[i] <= bottom; i++)
        {
            if (AppsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || row.Tag is "revealed") continue;
            row.Tag = "revealed";

            int delay = cascade ? Math.Min(shown * 22, 330) : Math.Min(shown * 18, 120);
            shown++;
            var slide = row.RenderTransform as TranslateTransform;
            if (slide is null || slide.IsFrozen)
            {
                slide = new TranslateTransform();
                row.RenderTransform = slide;
            }
            slide.Y = cascade ? 18 : 10;
            var begin = TimeSpan.FromMilliseconds(delay);
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = begin });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380))
            {
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        if (shown > 0) _cascadeNext = false;
    }

    private void AppsScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The top fade only appears once you've scrolled, so the first heading is never dimmed.
        AppsFadeTop.Visibility = AppsScroller.VerticalOffset > 2 ? Visibility.Visible : Visibility.Collapsed;
        AppsFadeBottom.Visibility = AppsScroller.VerticalOffset < AppsScroller.ScrollableHeight - 2 ? Visibility.Visible : Visibility.Collapsed;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) _rowTops = Array.Empty<double>();
        RevealVisibleRows(cascade: false);
    }

    /// <summary>Each wheel notch moves the target; the list glides towards it every frame (smooth, never stalls).</summary>
    private void AppsScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_scrollRunning) _scrollTarget = _scrollCurrent = AppsScroller.VerticalOffset;
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta * 1.15, 0, AppsScroller.ScrollableHeight);
        StartSmoothScroll();
    }

    private void ScrollAppsTo(double offset)
    {
        if (!_scrollRunning) _scrollCurrent = AppsScroller.VerticalOffset;
        _scrollTarget = Math.Clamp(offset, 0, AppsScroller.ScrollableHeight);
        StartSmoothScroll();
    }

    private void StartSmoothScroll()
    {
        if (_scrollRunning) return;
        _scrollRunning = true;
        _lastScrollFrame = TimeSpan.Zero;
        ResetWave(); // rows glide back into line while the list moves
        CompositionTarget.Rendering += OnScrollFrame;
    }

    private void StopSmoothScroll()
    {
        if (!_scrollRunning) return;
        _scrollRunning = false;
        CompositionTarget.Rendering -= OnScrollFrame;
    }

    private void OnScrollFrame(object? sender, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        double seconds = _lastScrollFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastScrollFrame).TotalSeconds, 0, 0.05);
        if (now == _lastScrollFrame) return; // same frame reported twice
        _lastScrollFrame = now;

        // Close a fixed fraction of the remaining distance each moment: quick to respond, soft to settle.
        double follow = 1 - Math.Exp(-seconds * 16);
        _scrollCurrent += (_scrollTarget - _scrollCurrent) * follow;
        if (Math.Abs(_scrollTarget - _scrollCurrent) < 0.5)
        {
            _scrollCurrent = _scrollTarget;
            StopSmoothScroll();
        }
        AppsScroller.ScrollToVerticalOffset(_scrollCurrent);
    }

    // ---- The hover "wave": the row under the mouse glides right and its neighbours follow a little ----

    private static readonly double[] WaveOffsets = { 14, 6, 2.5 };

    private void AppRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_scrollRunning) return; // rows passing under the mouse while scrolling shouldn't wave
        if ((sender as FrameworkElement)?.DataContext is AppRow row)
            WaveAround(AppsList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement);
    }

    private void AppsList_MouseLeave(object sender, MouseEventArgs e) => ResetWave();

    private void ResetWave() => WaveAround(null);

    /// <summary>Only the hovered row, its close neighbours, and rows still nudged from before are touched.</summary>
    private void WaveAround(FrameworkElement? center)
    {
        var targets = new Dictionary<FrameworkElement, (double Offset, int Distance)>();
        foreach (var row in _wavedRows) targets[row] = (0, int.MaxValue);

        int centerIndex = center is null ? -1 : AppsList.ItemContainerGenerator.IndexFromContainer(center);
        if (centerIndex >= 0)
        {
            for (int d = -(WaveOffsets.Length - 1); d < WaveOffsets.Length; d++)
            {
                int index = centerIndex + d;
                if (index < 0 || index >= AppsList.Items.Count) continue; // no row above the first or below the last
                if (AppsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row)
                    targets[row] = (WaveOffsets[Math.Abs(d)], Math.Abs(d));
            }
        }

        foreach (var (row, (offset, distance)) in targets)
        {
            if (row.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
            {
                if (offset == 0) continue;
                shift = new TranslateTransform();
                row.RenderTransform = shift;
            }
            if (offset == 0) _wavedRows.Remove(row);
            else _wavedRows.Add(row);
            if (Math.Abs(shift.X - offset) < 0.1) continue;

            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset, TimeSpan.FromMilliseconds(distance == 0 ? 340 : 280))
            {
                EasingFunction = distance == 0
                    ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
                    : new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    // ---- Clicking rows ----

    private void AppRow_Click(object sender, MouseButtonEventArgs e)
    {
        // Handled, so the click doesn't carry on to the island underneath (which would re-open it).
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is AppRow row) OpenRow(row, newWindow: false);
    }

    /// <summary>Middle-click opens a new window, like the normal taskbar.</summary>
    private void AppRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || (sender as FrameworkElement)?.DataContext is not AppRow row) return;
        e.Handled = true;
        OpenRow(row, newWindow: true);
    }

    /// <summary>An open app is brought back to the front (even if minimised); otherwise it's started.</summary>
    private void OpenRow(AppRow row, bool newWindow)
    {
        var windows = row.Windows.Where(Native.IsWindow).ToList();
        if (!newWindow && windows.Count > 0)
        {
            Collapse();
            WindowTracker.Activate(windows[0]); // the most recently used one
            return;
        }

        string? target = row.LaunchPath ?? row.ExePath;
        if (string.IsNullOrEmpty(target)) return;
        if (row.LaunchPath is not null && !SettingsPages.IsSettingsTarget(row.LaunchPath)) _settings.AddRecent(row.LaunchPath);
        SearchBox.Clear();
        OpenAndCollapse(target);
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppRow row) ToggleStar(row);
    }

    private void ToggleStar(AppRow row)
    {
        if (row.LaunchPath is null || SettingsPages.IsSettingsTarget(row.LaunchPath)) return;
        _settings.ToggleStar(row.LaunchPath);
        bool starred = _settings.IsStarred(row.LaunchPath);
        // The same app can appear in several sections; update them all.
        foreach (var other in _rows.Where(r => r.LaunchPath == row.LaunchPath)) other.IsStarred = starred;
    }

    /// <summary>Right-click on an app in the list: its menu is built now rather than for every row up front.</summary>
    private void AppRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppRow row) return;
        e.Handled = true;

        MenuItem Item(string header, RoutedEventHandler click)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            return item;
        }

        var menu = new ContextMenu { DataContext = row };
        menu.Opened += Menu_Opened;
        menu.Closed += Menu_Closed;
        menu.Items.Add(Item("Open new window", AppRowNewWindow_Click));
        menu.Items.Add(Item("Star / unstar", AppRowStar_Click));
        if (row.LaunchPath is { } launch && !SettingsPages.IsSettingsTarget(launch))
            menu.Items.Add(Item(_settings.IsTrayPinned(launch) ? "Unpin from tray" : "Pin to tray", (_, _) => ToggleTrayPin(launch)));
        if (row.IsOpen) menu.Items.Add(Item("Close all its windows", AppRowClose_Click));

        element.ContextMenu = menu; // attached to the row, so it picks up the island's menu styling
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void AppRowNewWindow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppRow row) OpenRow(row, newWindow: true);
    }

    private void AppRowStar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppRow row) ToggleStar(row);
    }

    private void AppRowClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRow row) return;
        CloseWindows(row.Windows, row.Name);
    }

    // ---- Search ----

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => RebuildAppList();

    /// <summary>↑/↓ move the highlight, Enter opens it.</summary>
    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                e.Handled = true;
                if (_rows.Count == 0) return;
                if (_selectedRow >= 0) _rows[_selectedRow].IsSelected = false;
                _selectedRow = e.Key == Key.Down
                    ? Math.Min(_rows.Count - 1, _selectedRow + 1)
                    : Math.Max(0, _selectedRow - 1);
                _rows[_selectedRow].IsSelected = true;
                ScrollRowIntoView(_rows[_selectedRow]);
                WaveAround(AppsList.ItemContainerGenerator.ContainerFromItem(_rows[_selectedRow]) as FrameworkElement);
                break;

            case Key.Enter:
                e.Handled = true;
                if (_rows.Count > 0) OpenRow(_rows[Math.Max(0, _selectedRow)], newWindow: false);
                break;
        }
    }

    private void ScrollRowIntoView(AppRow row)
    {
        if (AppsList.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container) return;
        double y = container.TranslatePoint(new Point(0, 0), AppsList).Y;
        double viewTop = AppsScroller.VerticalOffset, viewHeight = AppsScroller.ViewportHeight;
        double target = viewTop;
        if (y < viewTop + 24) target = y - 24;
        else if (y + container.ActualHeight > viewTop + viewHeight - 24) target = y + container.ActualHeight - viewHeight + 24;
        else return;
        _scrollTarget = Math.Clamp(target, 0, AppsScroller.ScrollableHeight);
        ScrollAppsTo(_scrollTarget);
    }

    /// <summary>Start typing anywhere in the open island to search: the clipboard on the Clipboard tab,
    /// your apps everywhere else (like the Start menu).</summary>
    private void OnWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_expanded || Keyboard.FocusedElement is TextBox || string.IsNullOrWhiteSpace(e.Text) || char.IsControl(e.Text[0])) return;
        if (_currentTab == "Settings") return;
        e.Handled = true;

        TextBox target = _currentTab == "Clipboard" ? ClipSearchBox : SearchBox;
        if (target == SearchBox) AppsTabButton.IsChecked = true;
        target.Text = e.Text;
        target.CaretIndex = target.Text.Length;
        Keyboard.Focus(target);
    }

    /// <summary>The magnifying glass jumps straight to app search.</summary>
    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (AppsTabButton.IsChecked == true) Keyboard.Focus(SearchBox);
        else AppsTabButton.IsChecked = true; // focusing the search box happens when the tab opens
    }
}
