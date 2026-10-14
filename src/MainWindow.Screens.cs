using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// More than one screen: the island lives on one screen at a time and, unless Settings says "Main screen", moves to
/// whichever screen your mouse rests on. It tucks up into the top edge of the old screen, then drops out of the top
/// of the new one as a small pill and springs open (the same bounce the island uses everywhere).
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _screenTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _screenTimerWired;
    private IntPtr _screenMonitor;          // the screen the island is on
    private Native.RECT _screenBounds;      // …and its edges, in screen pixels
    private IntPtr _screenCandidate;        // the screen the mouse is resting on, if different
    private DateTime _candidateSince;
    private Native.POINT _lastScreenCursor;
    private bool _movingScreens;
    private DateTime _screenMovedAt = DateTime.MinValue;
    private bool _forcePresence;            // apply the presence afresh after a move, even if it's unchanged

    /// <summary>How long the mouse has to rest on another screen before the island follows it.</summary>
    private static readonly TimeSpan ScreenRest = TimeSpan.FromMilliseconds(650);
    private const double DotWidth = 34;

    /// <summary>The island's screen, in screen pixels (the main screen until it has moved).</summary>
    private Native.RECT ScreenBounds
    {
        get
        {
            if (_screenBounds.Right - _screenBounds.Left > 0) return _screenBounds;
            var info = MonitorInfo(PrimaryMonitor());
            return info?.rcMonitor ?? new Native.RECT
            {
                Right = Native.GetSystemMetrics(Native.SM_CXSCREEN),
                Bottom = 1080,
            };
        }
    }

    private static IntPtr PrimaryMonitor() => Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, Native.MONITOR_DEFAULTTOPRIMARY);

    private static Native.MONITORINFO? MonitorInfo(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        return Native.GetMonitorInfo(monitor, ref info) ? info : null;
    }

    private static double MonitorScale(IntPtr monitor)
    {
        try { return Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0; }
        catch { return 1.0; }
    }

    private static int ScreenCount => Math.Max(1, Native.GetSystemMetrics(Native.SM_CMONITORS));

    // ---- Placing the window ----

    /// <summary>Top-centre of the island's screen (the main screen, or the one it moved to if that's still there).</summary>
    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
            return;
        }
        var monitor = _screenMonitor;
        if (monitor == IntPtr.Zero || MonitorInfo(monitor) is null || (_settings.ScreenMode == "Main" && monitor != PrimaryMonitor()))
            monitor = PrimaryMonitor();
        PlaceOn(monitor);
    }

    private void PlaceOn(IntPtr monitor)
    {
        var info = MonitorInfo(monitor);
        if (info is null) return;
        _screenMonitor = monitor;
        _screenBounds = info.Value.rcMonitor;
        WindowTracker.IslandMonitor = monitor;
        CentreOnScreen();
        if (_dim is not null) ApplyDim(); // the dimming follows the island to its screen
        // Moving to a screen with different scaling makes Windows resize the window; centre it again once it has.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(CentreOnScreen));
    }

    private void CentreOnScreen()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect)) return;
        var screen = ScreenBounds;
        int width = rect.Right - rect.Left;
        int x = (screen.Left + screen.Right) / 2 - width / 2;
        if (rect.Left == x && rect.Top == screen.Top) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, screen.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Takes the glow and frost windows along to the new screen (they line themselves up again afterwards).</summary>
    private void MoveBackdropsAlong()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect)) return;
        int spread = (int)Math.Round(GlowBackdrop.Spread * MonitorScale(_screenMonitor));
        const uint flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
        if (_glow.Handle != IntPtr.Zero) Native.SetWindowPos(_glow.Handle, IntPtr.Zero, rect.Left - spread, rect.Top, 0, 0, flags);
        if (_glass.Handle != IntPtr.Zero) Native.SetWindowPos(_glass.Handle, IntPtr.Zero, rect.Left, rect.Top, 0, 0, flags);
    }

    // ---- Following the mouse ----

    /// <summary>Starts or stops following the mouse, to match Settings and how many screens there are.</summary>
    private void ApplyScreenMode()
    {
        if (!_screenTimerWired)
        {
            _screenTimer.Tick += (_, _) => OnScreenTick();
            _screenTimerWired = true;
        }
        bool follow = _settings.ScreenMode != "Main" && ScreenCount > 1;
        if (follow)
        {
            if (!_screenTimer.IsEnabled) _screenTimer.Start();
            return;
        }
        _screenTimer.Stop();
        _screenCandidate = IntPtr.Zero;
        var primary = PrimaryMonitor();
        if (_screenMonitor != IntPtr.Zero && _screenMonitor != primary && !_movingScreens) MoveToScreen(primary);
    }

    private void OnScreenTick()
    {
        if (_movingScreens || _hwnd == IntPtr.Zero || !Native.GetCursorPos(out var cursor)) return;

        var monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        double scale = DpiScale;
        bool still = Math.Abs(cursor.X - _lastScreenCursor.X) <= 30 * scale && Math.Abs(cursor.Y - _lastScreenCursor.Y) <= 30 * scale;
        _lastScreenCursor = cursor;

        if (monitor == IntPtr.Zero || monitor == _screenMonitor)
        {
            _screenCandidate = IntPtr.Zero;
            return;
        }
        // Only a rest counts: sweeping across a screen, or dragging something, doesn't.
        if (monitor != _screenCandidate || !still || Native.AnyMouseButtonDown())
        {
            _screenCandidate = monitor;
            _candidateSince = DateTime.Now;
            return;
        }
        if (DateTime.Now - _candidateSince < ScreenRest) return;

        // Not while you're using the island, or something is showing on it; it follows once that's done.
        if (_expanded || _peeking || _menuOpen || _dropZoneShowing || _activityShowing || _preview.IsVisible) return;
        if (FullScreenAppOn(monitor)) return; // never drop in over a game or a full-screen video

        _screenCandidate = IntPtr.Zero;
        MoveToScreen(monitor);
    }

    /// <summary>Is a full-screen game or video in front on that screen?</summary>
    private static bool FullScreenAppOn(IntPtr monitor)
    {
        IntPtr focused = Native.GetForegroundWindow();
        if (focused == IntPtr.Zero || WindowTracker.IsOwnWindow(focused)) return false;
        IntPtr root = Native.GetAncestor(focused, Native.GA_ROOTOWNER);
        if (root == IntPtr.Zero || WindowTracker.IsDesktopOrTaskbar(root) || Native.IsZoomed(root)) return false;
        if (Native.MonitorFromWindow(root, Native.MONITOR_DEFAULTTONEAREST) != monitor) return false;
        if (MonitorInfo(monitor) is not Native.MONITORINFO info || !Native.GetWindowRect(root, out var rect)) return false;
        var screen = info.rcMonitor;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    // ---- The move: tuck away, hop across, drop in ----

    private void MoveToScreen(IntPtr monitor)
    {
        if (_movingScreens || MonitorInfo(monitor) is null) return;
        _movingScreens = true;
        HidePreview();
        App.LogNote($"moving to the screen at {MonitorInfo(monitor)!.Value.rcMonitor.Left},{MonitorInfo(monitor)!.Value.rcMonitor.Top}");

        bool onShow = IslandHost.Visibility == Visibility.Visible && _presence != Presence.FullScreen
                      && (_presence == Presence.Shown || _revealed);
        if (!onShow)
        {
            HopTo(monitor);
            return;
        }

        // 1) Tuck: shrink to a small pill and slide up under the top edge.
        FadeOut(CompactContent, 100);
        AnimateSize(DotWidth, CompactHeight, 200, bouncy: false);
        var up = new DoubleAnimation(-(CompactHeight + 12), TimeSpan.FromMilliseconds(200))
        {
            BeginTime = TimeSpan.FromMilliseconds(70),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        up.Completed += (_, _) => HopTo(monitor);
        IslandSlide.BeginAnimation(TranslateTransform.YProperty, up);
    }

    /// <summary>2) Out of sight: jump the window to the new screen.</summary>
    private void HopTo(IntPtr monitor)
    {
        IslandHost.Visibility = Visibility.Hidden;
        IslandSlide.BeginAnimation(TranslateTransform.YProperty, null);
        IslandSlide.Y = -(CompactHeight + 12);

        _screenMovedAt = DateTime.Now;
        PlaceOn(monitor);
        MoveBackdropsAlong();

        // Give Windows a moment to finish any change of scaling before coming back in.
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            DropIn();
        };
        settle.Start();
    }

    /// <summary>3) Drop out of the top of the new screen as a small pill, then spring open.</summary>
    private void DropIn()
    {
        CentreOnScreen();
        PositionBackdrops();

        IslandHost.BeginAnimation(WidthProperty, null);
        IslandHost.BeginAnimation(HeightProperty, null);
        IslandHost.Width = DotWidth + Shoulder * 2;
        IslandHost.Height = CompactHeight;
        CompactContent.BeginAnimation(OpacityProperty, null);
        CompactContent.Opacity = 0;
        CompactContent.Visibility = Visibility.Visible;

        _movingScreens = false;
        _forcePresence = true;
        UpdatePresence(); // shown here → it slides down; over a maximised app → stays tucked; full-screen → hidden
        _forcePresence = false;

        if (_presence == Presence.Shown)
        {
            var grow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
            grow.Tick += (_, _) =>
            {
                grow.Stop();
                if (_expanded || _peeking || _activityShowing) return; // something else took over meanwhile
                AnimateSize(CompactWidth, CompactHeight, 480, bouncy: true);
                FadeIn(CompactContent, 200, delay: 140);
            };
            grow.Start();
        }
        else
        {
            AnimateSize(CompactWidth, CompactHeight, 1, bouncy: false);
            CompactContent.Opacity = 1;
        }
        SyncBackdrops();
    }
}
