using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>
/// The island itself. This file holds the core: start-up, the island's shapes and animations,
/// and auto-hiding. The Apps tab and hover tray are in MainWindow.Apps.cs; the other tabs are in
/// MainWindow.Panels.cs.
/// </summary>
public partial class MainWindow : Window
{
    // ---- Sizes (in Windows "display units", which scale with your display settings) ----
    private const double CompactHeight = 38;

    /// <summary>The pill is a little wider when it's showing the battery.</summary>
    private double CompactWidth => _pillWidth; // grows to fit what the pill shows (UpdatePillWidth)
    private const double ActivityWidth = 340, ActivityHeight = 64;
    private double _activityWidthNow = ActivityWidth;
    private double _activityHeightNow = ActivityHeight;
    private const double ExpandedWidth = 660;
    private const double MinPeekWidth = 250;

    /// <summary>Width of each curved shoulder that blends the island into the top edge (minus 1px overlap).</summary>
    private const double Shoulder = 14;

    /// <summary>How round the bottom corners get when the island is large.</summary>
    private const double MaxCornerRadius = 30;

    /// <summary>Corner radius of Windows' own rounded window corners, which its blur follows (Glass style).</summary>
    private const double GlassCornerRadius = 8;

    /// <summary>Glass style with a working blur: the island takes the shape Windows can blur behind.</summary>
    /// <summary>Light style with the frosted blur turned on (the island is then a plain panel over Windows' blur).</summary>
    private bool IsGlass => Theme.Light && _settings.LightFrosted && _glass.Supported;

    /// <summary>How long the mouse has to rest at the top edge to pull the island down over a maximised app.</summary>
    private static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(300);
    private Native.POINT _lastEdgeCursor;

    /// <summary>How the island behaves right now, based on the app in front.</summary>
    private enum Presence
    {
        /// <summary>Normal: always visible.</summary>
        Shown,
        /// <summary>A maximised (or top-snapped) app is in front: tucked away until the mouse visits the top edge.</summary>
        AutoHidden,
        /// <summary>A full-screen game or video: completely out of the way.</summary>
        FullScreen,
    }

    // ---- State ----
    private bool _expanded;
    private bool _peeking;
    private bool _activityShowing;
    private bool _menuOpen;
    private double _targetHeight = CompactHeight;
    private IntPtr _lastForeground;
    private string _currentTab = "Today";

    private Presence _presence = Presence.Shown;
    private bool _revealed = true;
    private DateTime? _atTopSince;

    private readonly IslandSettings _settings = IslandSettings.Load();
    private readonly ObservableCollection<AppTile> _openApps = new();
    /// <summary>Open apps other than the one in front (the small icons in the hover tray).</summary>
    private readonly ObservableCollection<AppTile> _otherOpenApps = new();

    private readonly MediaService _media = new();
    private readonly VolumeService _volume = new();
    private readonly ConnectivityService _connectivity = new();
    private readonly SystemStats _stats = new();
    private readonly ClipboardHistory _clipboard;
    private readonly Shelf _shelf;
    private readonly DeviceService _devices = new();
    private readonly PreviewWindow _preview = new();
    private readonly AcrylicBackdrop _glass = new();
    private readonly GlowBackdrop _glow = new();

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _appsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _topProcessesTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly DispatcherTimer _housekeepingTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _batteryTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _previewShowTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _previewHideTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _autoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(1300) };

    /// <summary>After you bring the island down over a maximised app, it stays at least this long.</summary>
    private static readonly TimeSpan MinRevealTime = TimeSpan.FromSeconds(3);
    private DateTime _revealedAt = DateTime.MinValue;
    private readonly DispatcherTimer _powerConfirmTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _peekOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _peekCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // ---- Windows plumbing ----
    private IntPtr _hwnd;
    private IntPtr _foregroundHook;
    private Native.WinEventDelegate? _foregroundCallback; // kept in a field so it isn't garbage-collected
    private uint _taskbarCreatedMessage;

    public MainWindow()
    {
        _clipboard = new ClipboardHistory(_settings);
        _shelf = new Shelf(_settings);

        InitializeComponent();
        ApplyThemeResources();

        PeekAppsList.ItemsSource = _otherOpenApps;
        PeekDock.ItemsSource = _dock;
        AppsOpenChips.ItemsSource = _openApps;
        BuildTiles();
        BuildPowerSliders();
        BuildWeekdayHeader();
        ApplyAccent();
        ApplyIslandStyle();
        ApplyBehaviourSettings();
        LoadSettingsIntoUi();
        ApplyTabLayout();
        _clipboard.Changed += RefreshClipboardList;
        _shelf.Changed += RefreshShelf;
        _dropLeaveTimer.Tick += (_, _) => OnDropLeave();
        RefreshClipboardList();
        RefreshShelf();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnWindowKeyDown;
        PreviewTextInput += OnWindowTextInput;

        IslandHost.MouseEnter += OnIslandMouseEnter;
        IslandHost.MouseLeave += OnIslandMouseLeave;

        _clockTimer.Tick += (_, _) => UpdateClock();
        _appsTimer.Tick += (_, _) =>
        {
            if (_currentTab != "Apps") return;
            UpdateAppListOpenState();
            RefreshOpenApps();
        };
        _progressTimer.Tick += (_, _) => UpdateMediaProgress();
        _statsTimer.Tick += (_, _) => OnStatsTick();
        _topProcessesTimer.Tick += (_, _) => RefreshTopProcesses();
        _activityTimer.Tick += (_, _) => EndActivity();
        _housekeepingTimer.Tick += (_, _) => Housekeeping();
        _batteryTimer.Tick += (_, _) => RefreshBattery(allowPopOut: true);
        _previewShowTimer.Tick += (_, _) => { _previewShowTimer.Stop(); ShowPreview(); };
        _previewHideTimer.Tick += (_, _) => { _previewHideTimer.Stop(); HidePreview(); };
        _edgeTimer.Tick += (_, _) => OnEdgeTick();
        _autoHideTimer.Tick += (_, _) => OnAutoHideTick();
        _powerConfirmTimer.Tick += (_, _) => ResetPowerConfirm();
        _peekOpenTimer.Tick += (_, _) => OnPeekTimer();
        IslandHost.MouseMove += (_, e) => _lastIslandMouseMove = DateTime.Now;
        _peekCloseTimer.Tick += (_, _) => { _peekCloseTimer.Stop(); ExitPeek(); };

        _preview.MouseEnter += (_, _) =>
        {
            _previewHideTimer.Stop();
            _peekCloseTimer.Stop();
        };
        _preview.MouseLeave += (_, _) =>
        {
            RestartTimer(_previewHideTimer);
            if (_peeking && !IslandHost.IsMouseOver) RestartTimer(_peekCloseTimer);
        };
        _preview.WindowCloseRequested += OnPreviewCloseRequested;
        _preview.WindowChosen += hWnd =>
        {
            HidePreview();
            ExitPeek();
            Collapse();
            WindowTracker.Activate(hWnd);
        };

        _media.Changed += () => Dispatcher.BeginInvoke(new Action(OnMediaChanged));
        _volume.Changed += (level, muted) => Dispatcher.BeginInvoke(() => OnVolumeChanged(level, muted));
        _volume.MicChanged += () => Dispatcher.BeginInvoke(new Action(RefreshControls));
        _connectivity.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshControls));

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        DpiChanged += (_, args) =>
        {
            // Windows also sends this when nothing has really changed (e.g. while taking a screenshot); only a real
            // change of scaling needs a fresh start.
            if (Math.Abs(args.OldDpi.PixelsPerInchX - args.NewDpi.PixelsPerInchX) < 0.5) return;
            // Moving to a screen with different scaling: just line everything up again on the new screen.
            if (_movingScreens || DateTime.Now - _screenMovedAt < TimeSpan.FromSeconds(3))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    CentreOnScreen();
                    PositionBackdrops();
                    SyncBackdrops();
                }));
                return;
            }
            ScheduleFreshStart($"screen scaling changed ({args.OldDpi.PixelsPerInchX:0} to {args.NewDpi.PixelsPerInchX:0} dpi)");
        };

        // Selecting the first tab here (rather than in XAML) means everything above is ready when it fires.
        TodayTabButton.IsChecked = true;
    }

    // =====================================================================================
    //  Start-up and shut-down
    // =====================================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Keep the island out of Alt+Tab.
        long exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
        Native.AddClipboardFormatListener(_hwnd); // tells us whenever anything is copied

        PositionWindow();
        ApplyScreenMode();
        UpdateSearchHotkey();
        StartUpdateChecks();
    }

    // ---- Ctrl+Space: open app search from anywhere ----

    private const int SearchHotkeyId = 0x5150;
    private bool _hotkeyRegistered;
    private bool _hotkeyTaken; // another app already uses Ctrl+Space

    /// <summary>Turns the shortcut on or off to match Settings.</summary>
    private void UpdateSearchHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_settings.SearchHotkey && !_hotkeyRegistered)
        {
            _hotkeyRegistered = Native.RegisterHotKey(_hwnd, SearchHotkeyId, Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_SPACE);
            _hotkeyTaken = !_hotkeyRegistered;
            if (_hotkeyTaken) App.LogNote("Ctrl+Space is already used by another app, so the app-search shortcut is off");
        }
        else if (!_settings.SearchHotkey && _hotkeyRegistered)
        {
            Native.UnregisterHotKey(_hwnd, SearchHotkeyId);
            _hotkeyRegistered = false;
            _hotkeyTaken = false;
        }
    }

    /// <summary>Opens the island on the Apps tab, ready to type. Pressing it again closes it.</summary>
    private void OnSearchHotkey()
    {
        if (_expanded && _currentTab == "Apps")
        {
            Collapse();
            return;
        }
        if (_presence == Presence.FullScreen) return; // games and full-screen videos keep the screen
        if (_peeking) ExitPeek();
        if (!_revealed) SlideBack(); // tucked away over a maximised app: bring it down first

        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        if (!_expanded) Expand();
        if (AppsTabButton.IsChecked == true) ShowTab("Apps", animate: true);
        else AppsTabButton.IsChecked = true;

        Native.SetForegroundWindow(_hwnd); // the shortcut lets us take the keyboard
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(SearchBox)));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Taskbar.Hide();
        App.Taskbar.StartWatching();
        StartCaptureWatcher();
        StartCapsLockWatch();
        ApplyTrayIconSetting();

        _preview.Owner = this;

        // Glass and glow layers (empty until those styles are chosen).
        PositionBackdrops();
        _glass.Show();
        _glass.Conceal(); // stays hidden until the Glass style needs it
        _glow.Show();
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(TranslateTransform.YProperty, typeof(TranslateTransform))
            .AddValueChanged(IslandSlide, (_, _) => SyncBackdrops());
        IslandHost.IsVisibleChanged += (_, _) => SyncBackdrops();
        ApplyIslandStyle();

        // Hear about the active window changing straight away, rather than checking constantly.
        _foregroundCallback = OnForegroundChanged;
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundCallback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        OnForegroundChanged(IntPtr.Zero, 0, Native.GetForegroundWindow(), 0, 0, 0, 0);

        UpdateClock();
        RefreshBattery(allowPopOut: false);
        RefreshVolume();
        RefreshControls();

        _clockTimer.Start();
        _housekeepingTimer.Start();
        _batteryTimer.Start();
        _statsTimer.Start();

        _ = _media.InitAsync();
        _ = _connectivity.InitAsync();
        LoadInstalledApps();
        RefreshWeather();

        _devices.BluetoothChanged += (name, connected) => Dispatcher.BeginInvoke(() => OnBluetoothChanged(name, connected));
        _volume.OutputDeviceChanged += () => Dispatcher.BeginInvoke(new Action(OnOutputDeviceChanged));
        _devices.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        foreach (var timer in new[] { _clockTimer, _appsTimer, _progressTimer, _statsTimer, _topProcessesTimer,
                                      _housekeepingTimer, _batteryTimer, _activityTimer, _previewShowTimer,
                                      _previewHideTimer, _edgeTimer, _autoHideTimer, _powerConfirmTimer,
                                      _peekOpenTimer, _peekCloseTimer, _dropLeaveTimer })
            timer.Stop();
        _devices.Stop();
        try
        {
            _glass.Close();
            _glow.Close();
        }
        catch { }

        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _captures.Dispose();
        StopTrayIcons(); // apps send their icons back to the normal taskbar

        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        if (_hwnd != IntPtr.Zero) Native.RemoveClipboardFormatListener(_hwnd);
        App.Taskbar.StopWatching();
        App.Taskbar.Restore();
        try
        {
            _preview.HidePreview();
            _preview.Close();
        }
        catch { /* already closed along with the main window */ }
        _volume.Dispose();
        try { lock (_mixer) _mixer.Dispose(); } catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Runs a few times a second: keeps the real taskbar hidden, the island on top,
    /// and works out whether it should be showing, tucked away, or hidden for a full-screen app.</summary>
    private DateTime _lastShelfClean = DateTime.MinValue;

    private void Housekeeping()
    {
        App.Taskbar.EnsureHidden();
        UpdatePresence();
        if (DateTime.Now - _lastShelfClean > TimeSpan.FromHours(1))
        {
            _lastShelfClean = DateTime.Now;
            if (_shelf.CleanUp(_settings.ShelfCleanDays) > 0) RefreshShelf();
        }
        if (_presence != Presence.FullScreen && !_menuOpen && _hwnd != IntPtr.Zero)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            KeepBackdropsBelow();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            HandleDeviceChange(wParam, lParam);
        }
        else if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            _clipboardRetries = 0;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CaptureClipboard));
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == SearchHotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(OnSearchHotkey));
        }
        else if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // Explorer restarted, which brings a fresh taskbar with it: hide that one too.
            if (App.Taskbar.IsHidden) App.Taskbar.Hide();
            App.Taskbar.StartWatching();
        }
        return IntPtr.Zero;
    }

    // ---- Starting fresh after a reconnect or a scaling change ----

    private readonly DateTime _startedAt = DateTime.Now;
    private DispatcherTimer? _freshStartTimer;
    private string _freshStartReason = "";

    /// <summary>
    /// Reconnecting to the PC (e.g. reopening a Shadow or Remote Desktop session) can leave the island's see-through
    /// window drawn as a black box until it's restarted, so it restarts itself, once the display has settled.
    /// </summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        App.LogNote($"session change: {e.Reason}");
        if (e.Reason == SessionSwitchReason.SessionLock && _settings.ClearClipboardOnLock)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _clipboard.Clear();
                RefreshClipboardList();
            }));
        if (e.Reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
            Dispatcher.BeginInvoke(new Action(() => ScheduleFreshStart($"reconnected to the PC ({e.Reason})")));
    }

    private void ScheduleFreshStart(string reason)
    {
        // Not straight after starting (Windows reports the screen's scaling as the window first appears).
        if (DateTime.Now - _startedAt < TimeSpan.FromSeconds(20)) return;
        _freshStartReason = reason;
        if (_freshStartTimer is null)
        {
            _freshStartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _freshStartTimer.Tick += (_, _) =>
            {
                _freshStartTimer!.Stop();
                // Don't pull the rug out mid-use: if the island is open or a menu is up, try again shortly.
                if (_expanded || _menuOpen) { _freshStartTimer.Start(); return; }
                App.RestartIsland(_freshStartReason);
            };
        }
        _freshStartTimer.Stop();
        _freshStartTimer.Start();
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        PositionWindow();
        ApplyScreenMode(); // a screen was plugged in or unplugged
        PositionBackdrops();
        SyncBackdrops();
    }));

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint thread, uint time)
    {
        UpdatePresence();
        if (hWnd == IntPtr.Zero || WindowTracker.IsOwnWindow(hWnd)) return;

        _lastForeground = hWnd;
        if (_peeking) RefreshOpenApps();
        if (_expanded && _currentTab == "Apps") UpdateAppListOpenState();
    }

    // =====================================================================================
    //  Auto-hide: tuck away over maximised apps, come back when the mouse visits the top edge
    // =====================================================================================

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void UpdatePresence()
    {
        if (_movingScreens) return; // hopping to another screen; worked out afresh when it lands

        // 1) Full-screen games and videos: judged by the window that has focus.
        IntPtr focused = Native.GetForegroundWindow();
        bool focusIsOurs = focused != IntPtr.Zero && (WindowTracker.IsOwnWindow(focused) || WindowTracker.IsTransientShellWindow(focused));
        bool fullScreen;
        if (focusIsOurs)
        {
            fullScreen = _presence == Presence.FullScreen; // using the island or Start doesn't change this
        }
        else
        {
            IntPtr owner = focused == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(focused, Native.GA_ROOTOWNER);
            fullScreen = owner != IntPtr.Zero && !WindowTracker.IsDesktopOrTaskbar(owner) && WindowTracker.IsFullScreenOnPrimary(owner);
        }

        // 2) Otherwise, look at the app that's actually under the island (not just which window has focus):
        //    tuck away only if that app is maximised. Desktop showing / nothing maximised there = always visible.
        Presence next;
        if (fullScreen) next = Presence.FullScreen;
        else
        {
            IntPtr under = WindowTracker.TopAppWindowUnder(IslandArea());
            next = under != IntPtr.Zero && _settings.HideOverMaximized && WindowTracker.IsMaximizedOnPrimary(under)
                ? Presence.AutoHidden
                : Presence.Shown;
        }

        if (next == _presence && !_forcePresence) return;
        var previous = _presence;
        _presence = next;

        switch (next)
        {
            case Presence.FullScreen:
                ExitPeek();
                Collapse();
                EndActivity();
                _edgeTimer.Stop();
                _autoHideTimer.Stop();
                IslandHost.Visibility = Visibility.Hidden;
                break;

            case Presence.AutoHidden:
                IslandHost.Visibility = Visibility.Visible;
                _edgeTimer.Start();
                if (!_expanded && !_peeking && !_activityShowing) SlideAway(instant: previous == Presence.FullScreen);
                break;

            case Presence.Shown:
                _edgeTimer.Stop();
                _autoHideTimer.Stop();
                SlideBack();
                break;
        }
    }

    /// <summary>Where the pill sits on screen (screen pixels): top-centre of the island's screen.</summary>
    private Native.RECT IslandArea()
    {
        double scale = DpiScale;
        var screen = ScreenBounds;
        int centre = (screen.Left + screen.Right) / 2;
        int half = (int)Math.Ceiling((CompactWidth / 2 + Shoulder) * scale);
        return new Native.RECT
        {
            Left = centre - half,
            Right = centre + half,
            Top = screen.Top,
            Bottom = screen.Top + (int)Math.Ceiling(CompactHeight * scale),
        };
    }

    private void SlideAway(bool instant = false)
    {
        _revealed = false;
        _autoHideTimer.Stop();
        HidePreview();

        double hiddenY = -(IslandHost.ActualHeight + 8);
        if (instant)
        {
            IslandSlide.BeginAnimation(TranslateTransform.YProperty, null);
            IslandSlide.Y = hiddenY;
            IslandHost.Visibility = Visibility.Hidden;
            return;
        }

        var animation = new DoubleAnimation(hiddenY, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        animation.Completed += (_, _) =>
        {
            if (!_revealed) IslandHost.Visibility = Visibility.Hidden;
        };
        IslandSlide.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void SlideBack()
    {
        _revealed = true;
        _revealedAt = DateTime.Now;
        IslandHost.Visibility = Visibility.Visible;
        IslandSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
        });
    }

    /// <summary>Checks the mouse 20 times a second, only while a maximised app is in front.</summary>
    private void OnEdgeTick()
    {
        if (_presence != Presence.AutoHidden || !Native.GetCursorPos(out var cursor)) return;

        if (!_revealed)
        {
            // Only a deliberate pause counts: the mouse right at the top edge, over where the island lives,
            // nearly still, with no button held (so reaching for or dragging a browser tab doesn't trigger it).
            // A thin band along the top edge rather than the exact top pixel (on streamed or scaled displays the
            // cursor often stops a pixel or two short), a bit wider than the island, and "resting" means not
            // sweeping across quickly (small wobbles are fine).
            var area = IslandArea();
            double scale = DpiScale;
            int band = (int)Math.Ceiling(4 * scale);
            int extra = (int)Math.Ceiling(70 * scale);
            bool atTop = cursor.Y <= area.Top + band && cursor.Y >= area.Top - 2 && cursor.X >= area.Left - extra && cursor.X <= area.Right + extra;
            bool still = Math.Abs(cursor.X - _lastEdgeCursor.X) <= 25 * scale && Math.Abs(cursor.Y - _lastEdgeCursor.Y) <= 25 * scale;
            _lastEdgeCursor = cursor;

            if (!atTop || !still)
            {
                _atTopSince = null;
                return;
            }
            // Holding a button (e.g. dragging files to the shelf) needs a slightly longer rest,
            // so dragging a browser tab past the top doesn't trigger it.
            var needed = Native.AnyMouseButtonDown() ? TimeSpan.FromMilliseconds(900) : RevealDelay;
            _atTopSince ??= DateTime.Now;
            if (DateTime.Now - _atTopSince.Value >= needed)
            {
                _atTopSince = null;
                SlideBack();
            }
            return;
        }

        // Revealed: stay for a few seconds at least, and while you're using it; then tuck away
        // once the mouse has been away from it for a little over a second.
        bool recentlyRevealed = DateTime.Now - _revealedAt < MinRevealTime;
        if (recentlyRevealed || _expanded || _peeking || _menuOpen || _activityShowing || _dropZoneShowing
            || _preview.IsVisible || IsCursorNearIsland(cursor))
            _autoHideTimer.Stop();
        else if (!_autoHideTimer.IsEnabled)
            _autoHideTimer.Start();
    }

    private void OnAutoHideTick()
    {
        _autoHideTimer.Stop();
        if (_presence != Presence.AutoHidden || !_revealed || _expanded || _peeking || _menuOpen || _activityShowing) return;
        if (DateTime.Now - _revealedAt < MinRevealTime) return;
        if (Native.GetCursorPos(out var cursor) && IsCursorNearIsland(cursor)) return;
        SlideAway();
    }

    private bool IsCursorNearIsland(Native.POINT cursor)
    {
        if (IslandHost.ActualWidth <= 0) return false;
        var topLeft = IslandHost.PointToScreen(new Point(0, 0));
        double scale = DpiScale;
        double slack = 40 * scale;
        return cursor.X >= topLeft.X - slack && cursor.X <= topLeft.X + IslandHost.ActualWidth * scale + slack
            && cursor.Y <= topLeft.Y + IslandHost.ActualHeight * scale + slack;
    }

    // =====================================================================================
    //  Shape: the bottom corners grow rounder as the island grows
    // =====================================================================================

    private void IslandHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateIslandShape();
        SyncBackdrops();
    }

    /// <summary>
    /// Draws the island as one shape: a flat top along the screen edge, a concave curve (shoulder) on each side
    /// where it meets the edge, and rounded bottom corners that get rounder as the island grows.
    /// </summary>
    private void UpdateIslandShape()
    {
        double w = IslandHost.ActualWidth, h = IslandHost.ActualHeight;
        if (w <= Shoulder * 2 + 2 || h <= Shoulder + 2) return;

        if (IsGlass)
        {
            // A frosted panel hanging from the top edge: flat top, small rounded bottom corners (matches the blur).
            double g = GlassCornerRadius;
            var panel = new StreamGeometry();
            using (var ctx = panel.Open())
            {
                ctx.BeginFigure(new Point(Shoulder, 0), true, true);
                ctx.LineTo(new Point(w - Shoulder, 0), true, true);
                ctx.LineTo(new Point(w - Shoulder, h - g), true, true);
                ctx.ArcTo(new Point(w - Shoulder - g, h), new Size(g, g), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(new Point(Shoulder + g, h), true, true);
                ctx.ArcTo(new Point(Shoulder, h - g), new Size(g, g), 0, false, SweepDirection.Clockwise, true, true);
            }
            panel.Freeze();
            IslandShape.Data = panel;
            return;
        }

        double r = Math.Min(MaxCornerRadius, Math.Min(h / 2, (w - Shoulder * 2) / 2));
        r = Math.Max(1, Math.Min(r, h - Shoulder));

        var shape = new StreamGeometry();
        using (var ctx = shape.Open())
        {
            var shoulder = new Size(Shoulder, Shoulder);
            var corner = new Size(r, r);
            ctx.BeginFigure(new Point(0, 0), true, true);
            ctx.LineTo(new Point(w, 0), true, true);
            ctx.ArcTo(new Point(w - Shoulder, Shoulder), shoulder, 0, false, SweepDirection.Counterclockwise, true, true);
            ctx.LineTo(new Point(w - Shoulder, h - r), true, true);
            ctx.ArcTo(new Point(w - Shoulder - r, h), corner, 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(Shoulder + r, h), true, true);
            ctx.ArcTo(new Point(Shoulder, h - r), corner, 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(Shoulder, Shoulder), true, true);
            ctx.ArcTo(new Point(0, 0), shoulder, 0, false, SweepDirection.Counterclockwise, true, true);
        }
        shape.Freeze();
        IslandShape.Data = shape;
    }

    private void Island_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateContentClip();

    /// <summary>Keeps the island's contents inside its rounded corners.</summary>
    private void UpdateContentClip()
    {
        double width = Island.ActualWidth, height = Island.ActualHeight;
        if (width <= 0 || height <= 0) return;
        double radius = IsGlass ? GlassCornerRadius : Math.Min(MaxCornerRadius, height / 2);
        Island.CornerRadius = new CornerRadius(0, 0, radius, radius);
        IslandContent.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    // =====================================================================================
    //  The island's shapes: compact, activity pop-out, peek (hover tray), expanded
    // =====================================================================================

    private void Island_Click(object sender, MouseButtonEventArgs e)
    {
        // The click that just closed the island (e.g. opening an app) mustn't open it again.
        if (DateTime.Now - _lastCollapsedAt < TimeSpan.FromMilliseconds(450)) return;
        // Clicking a pop-out that goes somewhere (e.g. "Screenshot · on your Shelf") opens that tab.
        string? openTab = _activityShowing ? _activityOpensTab : null;
        if (!_expanded) Expand();
        if (openTab is not null && _expanded)
        {
            var button = TabButtonFor(openTab);
            if (button.IsChecked == true) ShowTab(openTab, animate: true);
            else button.IsChecked = true;
        }
    }

    private DateTime _lastCollapsedAt = DateTime.MinValue;

    /// <summary>Scroll over the pill to change the volume.</summary>
    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_expanded || _peeking || !_volume.Available) return;
        float step = e.Delta > 0 ? 0.04f : -0.04f;
        if (_volume.Muted && step > 0) _volume.Muted = false;
        _volume.Level = _volume.Level + step; // the volume pop-out appears by itself
        e.Handled = true;
    }

    private DateTime _lastIslandMouseMove = DateTime.MinValue;
    private DateTime _hoverStarted = DateTime.MinValue;

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        _peekCloseTimer.Stop();
        _hoverStarted = DateTime.Now;
        if (!_expanded && !_peeking && !_menuOpen && _settings.PeekOnHover) RestartTimer(_peekOpenTimer);
    }

    /// <summary>
    /// The hover delay has passed. Only open the tray if the mouse has actually come to rest on the island
    /// (not just passing over it on the way to something else) and no button is held down.
    /// </summary>
    private void OnPeekTimer()
    {
        _peekOpenTimer.Stop();
        if (!IslandHost.IsMouseOver || _expanded || _peeking) return;

        bool stillMoving = DateTime.Now - _lastIslandMouseMove < TimeSpan.FromMilliseconds(160);
        if (stillMoving || Native.AnyMouseButtonDown())
        {
            // Keep checking for a little while in case it settles; give up if it never does.
            if (DateTime.Now - _hoverStarted < TimeSpan.FromSeconds(2.5))
            {
                _peekOpenTimer.Interval = TimeSpan.FromMilliseconds(120);
                _peekOpenTimer.Start();
            }
            return;
        }
        EnterPeek();
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        _peekOpenTimer.Stop();
        ApplyBehaviourSettings(); // resets the hover delay after any quick re-checks
        if (_peeking && !_menuOpen) RestartTimer(_peekCloseTimer);
    }

    /// <summary>Hovering the pill: grow into a small tray of open apps.</summary>
    private void EnterPeek()
    {
        if (_expanded || _peeking || _menuOpen || _presence == Presence.FullScreen || !_revealed) return;
        if (!IslandHost.IsMouseOver) return;
        if (ScreenshotActionsShowing) return; // you're heading for the screenshot's buttons, not the tray

        RefreshOpenApps();
        if (_openApps.Count == 0 && _dock.Count == 0 && !DownloadShowing) return; // nothing to show

        _peeking = true;
        _activityTimer.Stop();
        _activityShowing = false;
        IslandHost.Cursor = Cursors.Arrow;

        FadeOut(CompactContent, 90);
        FadeOut(ActivityContent, 90);
        PeekContent.Visibility = Visibility.Visible;
        UpdateMediaProgress();
        _progressTimer.Start();
        var size = MeasurePeek();
        AnimateSize(size.Width, size.Height, 460, bouncy: true);
        FadeIn(PeekContent, 200, delay: 110);
    }

    private void ExitPeek()
    {
        CloseFolderPeek();
        _peekOpenTimer.Stop();
        _peekCloseTimer.Stop();
        if (!_peeking) return;
        _peeking = false;
        HidePreview();
        if (_expanded) return; // Expand() takes it from here
        _progressTimer.Stop();

        IslandHost.Cursor = Cursors.Hand;
        FadeOut(PeekContent, 100);
        AnimateSize(CompactWidth, CompactHeight, 400, bouncy: false);
        FadeIn(CompactContent, 180, delay: 150);
    }

    private Size MeasurePeek()
    {
        PeekContent.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
        var desired = PeekContent.DesiredSize; // includes its margins
        double minWidth = PeekMediaRow.Visibility == Visibility.Visible ? 306 : MinPeekWidth;
        return new Size(Math.Clamp(desired.Width, minWidth, ExpandedWidth), Math.Min(desired.Height, Height - 8));
    }

    private void Expand()
    {
        if (_expanded || _presence == Presence.FullScreen) return;
        bool wasPeeking = _peeking;
        _expanded = true;
        _peeking = false;
        _peekOpenTimer.Stop();
        _peekCloseTimer.Stop();
        _activityTimer.Stop();
        _activityShowing = false;
        HidePreview();

        Activate(); // so clicking anywhere else tells us to shrink back
        IslandHost.Cursor = Cursors.Arrow;

        _volume.Attach();
        UpdateClock();
        RefreshBattery(allowPopOut: false);
        RefreshVolume();
        RefreshControls();
        UpdateTrayButton();

        FadeOut(CompactContent);
        FadeOut(ActivityContent);
        if (wasPeeking) FadeOut(PeekContent, 90);
        ExpandedContent.Visibility = Visibility.Visible;
        // If the last tab used has since been hidden, open on the first one that's showing.
        if (_currentTab != "Settings" && IsTabHidden(_currentTab))
        {
            _currentTab = FirstVisibleTab();
            TabButtonFor(_currentTab).IsChecked = true;
        }
        ShowTab(_currentTab, animate: false);
        UpdateMediaProgress();
        AnimateSize(ExpandedWidth, MeasureExpandedHeight(), 520, bouncy: true);
        FadeIn(ExpandedContent, 220, delay: 130);

        _appsTimer.Start();
        _progressTimer.Start();
    }

    private void Collapse()
    {
        HidePreview();
        if (!_expanded) return;
        _expanded = false;
        _lastCollapsedAt = DateTime.Now;
        _appsTimer.Stop();
        _progressTimer.Stop();
        _topProcessesTimer.Stop();
        ResetPowerConfirm();
        IslandHost.Cursor = Cursors.Hand;

        FadeOut(ExpandedContent, 110);
        AnimateSize(CompactWidth, CompactHeight, 420, bouncy: false);
        FadeIn(CompactContent, 180, delay: 180);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // A right-click menu briefly takes focus; don't shrink while one is open.
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(120);
            if (!IsActive && !_menuOpen) Collapse();
        });
    }

    /// <summary>Esc clears the search first (if there is one), then closes the island.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0) SearchBox.Clear();
        else if (ClipSearchBox.IsKeyboardFocusWithin && ClipSearchBox.Text.Length > 0) ClipSearchBox.Clear();
        else Collapse();
    }

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        HidePreview();
        _peekCloseTimer.Stop();
        StartWithWindowsMenuItem.IsChecked = _startupEnabled;
        ShowTaskbarMenuItem.IsChecked = !App.Taskbar.IsHidden;
    }

    private void Menu_Closed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        if (_peeking && !IslandHost.IsMouseOver) RestartTimer(_peekCloseTimer);
    }

    /// <summary>Briefly widens the pill to show something (like the iPhone does for a timer or AirPods).</summary>
    private void ShowActivity(string? glyph, ImageSource? art, string title, string? subtitle, double? level, double seconds = 2.4, double? width = null, double? height = null)
    {
        _activityOpensTab = null;
        _activityFile = null;
        if (_expanded || _peeking || _presence == Presence.FullScreen) return;

        // Over a maximised app, peek out for the pop-out, then tuck away again afterwards.
        if (_presence == Presence.AutoHidden && !_revealed) SlideBack();

        ActivityGlyph.Text = glyph ?? "";
        ActivityGlyph.ClearValue(System.Windows.Controls.TextBlock.FontFamilyProperty); // back to the normal symbol font after a line-style icon
        ActivityGlyph.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        ActivityGlyph.FontSize = 20;
        ActivityGlyph.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;
        ActivityArt.Visibility = art is null ? Visibility.Collapsed : Visibility.Visible;
        ActivityArt.Background = art is null ? null : new ImageBrush(art) { Stretch = Stretch.UniformToFill };

        ActivityTitle.Text = title;
        ActivitySub.Text = subtitle ?? "";
        ActivitySub.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        ActivityBar.Visibility = level is null ? Visibility.Collapsed : Visibility.Visible;
        if (level is double l)
            ActivityBarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(Math.Clamp(l, 0, 1), TimeSpan.FromMilliseconds(150)));

        ActivityActions.Visibility = Visibility.Collapsed;
        ActivityMedia.Visibility = Visibility.Collapsed;
        ActivityKicker.Visibility = Visibility.Collapsed;
        ActivityPicture.Width = ActivityPicture.Height = 36;
        double newHeight = height ?? ActivityHeight;
        if (!_activityShowing)
        {
            _activityShowing = true;
            FadeOut(CompactContent, 90);
            FadeIn(ActivityContent, 180, delay: 100);
            _activityWidthNow = width ?? ActivityWidth;
            _activityHeightNow = newHeight;
            AnimateSize(_activityWidthNow, _activityHeightNow, 480, bouncy: true);
        }
        else if ((width ?? ActivityWidth) != _activityWidthNow || newHeight != _activityHeightNow)
        {
            _activityWidthNow = width ?? ActivityWidth;
            _activityHeightNow = newHeight;
            AnimateSize(_activityWidthNow, _activityHeightNow, 300, bouncy: false);
        }

        _activityTimer.Interval = TimeSpan.FromSeconds(seconds * PopupLengthFactor);
        RestartTimer(_activityTimer);
    }

    /// <summary>Settings › Pop-ups: how long pop-outs stay up.</summary>
    private double PopupLengthFactor => _settings.PopupLength switch { "Short" => 0.65, "Long" => 1.7, _ => 1.0 };

    /// <summary>A pop-out with buttons on it (a screenshot's Copy/Edit/Delete, a song's play controls) waits while you use it.</summary>
    private bool ScreenshotActionsShowing => _activityShowing
        && (ActivityActions.Visibility == Visibility.Visible || ActivityMedia.Visibility == Visibility.Visible);

    private void EndActivity()
    {
        _activityTimer.Stop();
        if (!_activityShowing) return;
        if (ScreenshotActionsShowing && IslandHost.IsMouseOver)
        {
            // Stay while the mouse is on the screenshot pop-out; go a moment after it leaves.
            _activityTimer.Interval = TimeSpan.FromSeconds(1.5);
            RestartTimer(_activityTimer);
            return;
        }
        _activityShowing = false;
        if (_expanded || _peeking) return;

        FadeOut(ActivityContent, 100);
        FadeIn(CompactContent, 180, delay: 140);
        AnimateSize(CompactWidth, CompactHeight, 420, bouncy: false);
    }

    // ---- Animation helpers ----

    private void AnimateSize(double width, double height, int milliseconds, bool bouncy)
    {
        IEasingFunction ease = bouncy
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);

        // The host also holds the two shoulders, one each side.
        IslandHost.BeginAnimation(WidthProperty, new DoubleAnimation(width + Shoulder * 2, duration) { EasingFunction = ease });
        IslandHost.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = ease });
        _targetHeight = height;
    }

    private static void FadeIn(UIElement element, int milliseconds = 180, int delay = 0)
    {
        element.Visibility = Visibility.Visible;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
        });
    }

    private static void FadeOut(UIElement element, int milliseconds = 120)
    {
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(milliseconds));
        animation.Completed += (_, _) =>
        {
            if (element.Opacity < 0.01) element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(OpacityProperty, animation);
    }

    private static void RestartTimer(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private double MeasureExpandedHeight()
    {
        // Let WPF finish laying out anything that just changed (a new tab, a Settings section, late-arriving content)
        // first; otherwise it hands back the size of what was showing before, and the island ends up too short.
        ExpandedContent.UpdateLayout();
        ExpandedContent.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
        double height = ExpandedContent.DesiredSize.Height; // includes its margins
        return Math.Min(height, Height - 8);
    }

    private void ResizeExpandedIfNeeded()
    {
        if (!_expanded) return;
        double height = MeasureExpandedHeight();
        if (Math.Abs(height - _targetHeight) > 1) AnimateSize(ExpandedWidth, height, 320, bouncy: false);
    }

    // =====================================================================================
    //  Tabs
    // =====================================================================================

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tab) ShowTab(tab, animate: true);
    }

    private void ShowTab(string tab, bool animate)
    {
        _currentTab = tab;
        // The gear lights up in the accent colour while Settings is open.
        if (tab == "Settings") SettingsGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Accent");
        else SettingsGlyph.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        HidePreview();
        ResetPowerConfirm();

        var panels = new (string Name, FrameworkElement Panel)[]
        {
            ("Today", TodayTab), ("Controls", ControlsTab), ("Apps", AppsTab), ("Clipboard", ClipboardTab),
            ("Shelf", ShelfTab), ("Media", MediaTab), ("Stats", StatsTab), ("Power", PowerTab), ("Settings", SettingsTab),
        };
        foreach (var (name, panel) in panels)
        {
            if (name == tab)
            {
                panel.Visibility = Visibility.Visible;
                panel.BeginAnimation(OpacityProperty, animate
                    ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                    : null);
            }
            else
            {
                panel.BeginAnimation(OpacityProperty, null);
                panel.Visibility = Visibility.Collapsed;
            }
        }

        _topProcessesTimer.Stop();
        switch (tab)
        {
            case "Today":
                UpdateToday();
                RenderCalendar();
                RefreshWeather();
                break;
            case "Shelf":
                RefreshShelf();
                break;
            case "Settings":
                LoadSettingsIntoUi();
                break;
            case "Power":
                RefreshOpenApps();
                RefreshPowerInfo();
                break;
            case "Controls":
                HideDeviceList();
                RefreshControls();
                RefreshTrayChips();
                break;
            case "Apps":
                // Pick up newly installed apps every so often.
                if (DateTime.Now - _installedAppsLoadedAt > TimeSpan.FromMinutes(10)) LoadInstalledApps();
                RefreshOpenApps();
                OpenAppsTab();
                if (_expanded) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(SearchBox)));
                break;
            case "Clipboard":
                RefreshClipboardList();
                if (_expanded) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(ClipSearchBox)));
                break;
            case "Stats":
                // Draw the graphs once the tab has been laid out and has a size.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateStatsView));
                RefreshTopProcesses();
                if (_expanded) _topProcessesTimer.Start();
                break;
        }

        if (animate)
        {
            ResizeExpandedIfNeeded();
            // And once more after everything has been drawn, for anything that settles a moment later.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ResizeExpandedIfNeeded));
        }
    }

    // =====================================================================================
    //  Start with Windows, the real taskbar, header buttons
    // =====================================================================================

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        SetStartup(!_startupEnabled);
    }

    private void ShowTaskbarMenu_Click(object sender, RoutedEventArgs e) => ToggleRealTaskbar();

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        ToggleRealTaskbar();
        Collapse();
    }

    /// <summary>Brings the normal taskbar back (e.g. to reach a tray icon), or hides it again.</summary>
    private void ToggleRealTaskbar()
    {
        if (App.Taskbar.IsHidden) App.Taskbar.Restore();
        else App.Taskbar.Hide();
        UpdateTrayButton();
    }

    private void UpdateTrayButton()
    {
        bool realTaskbarShowing = !App.Taskbar.IsHidden;
        ShowTaskbarMenuItem.IsChecked = realTaskbarShowing;
        TrayGlyph.Text = realTaskbarShowing ? "" : ""; // chevron down / up
        TrayChipText.Text = realTaskbarShowing ? "Hide taskbar" : "Tray icons";
    }

    private void QuickSettings_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        Native.PressWinKey('A');
    }

    private void Notifications_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        Native.PressWinKey('N');
    }

    /// <summary>With the Auto style, follow Windows when it switches between light and dark mode.</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_settings.IslandStyle == "Auto") SwitchTheme(Theme.WindowsUsesLight());
        }));
    }

    private void OpenAndCollapse(string target)
    {
        Collapse();
        SystemActions.Open(target);
    }
}
