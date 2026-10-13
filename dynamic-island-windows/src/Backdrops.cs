using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DynamicIsland;

/// <summary>
/// Real frosted glass behind the island. The island window itself is see-through (so clicks pass through
/// around it), and Windows can't blur behind a window like that, so this is a separate plain window sitting
/// just underneath it, the same size as the island and cut to its shape.
///
/// On Windows 11 (22H2 and later) it uses the system "acrylic" backdrop — the same frosted material as
/// Windows 11's own menus — which follows the window's shape. On older Windows it falls back to the
/// older blur effect, which may show slightly square corners.
/// </summary>
internal sealed class AcrylicBackdrop : Window
{
    private const int WM_NCACTIVATE = 0x0086;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    private IntPtr _hwnd;
    private bool _acrylicOn;
    private bool _showing = true;
    private readonly bool _useSystemBackdrop = Environment.OSVersion.Version.Build >= 22621;

    public IntPtr Handle => _hwnd;

    /// <summary>True if Windows accepted a blur effect.</summary>
    public bool Supported { get; private set; } = true;

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public AcrylicBackdrop()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false; // blur only works on ordinary windows
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
                new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT));

            var source = HwndSource.FromHwnd(_hwnd);
            if (source is not null)
            {
                // Let Windows draw the blur where WPF draws nothing.
                if (source.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
                // Always look "active", so Windows never swaps the frost for a flat colour.
                source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg != WM_NCACTIVATE) return IntPtr.Zero;
                    handled = true;
                    return DefWindowProc(hwnd, msg, new IntPtr(1), lParam);
                });
            }

            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            // Windows' blur ignores custom cut-out shapes, but it does follow Windows' own rounded corners,
            // so the island's Glass shape is designed around those (see MainWindow.GlassCornerRadius).
            int corner = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int noBorder = unchecked((int)DWMWA_COLOR_NONE); // no thin grey Windows border around it
            Native.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
            int dark = 1; // dark frost, so white text stays readable
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            SetAcrylic(true);
            Conceal();
        };
    }

    /// <summary>Turns the frosted backdrop on or off.</summary>
    public void SetAcrylic(bool on)
    {
        if (_hwnd == IntPtr.Zero || on == _acrylicOn) return;

        if (_useSystemBackdrop)
        {
            int type = on ? DWMSBT_TRANSIENTWINDOW : 1; // 1 = none
            Supported = Native.DwmSetWindowAttribute(_hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
            if (Supported)
            {
                _acrylicOn = on;
                Native.PostMessage(_hwnd, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
                return;
            }
            // Windows refused it: fall through to the older effect.
        }

        var policy = new Native.ACCENT_POLICY
        {
            AccentState = on ? Native.ACCENT_ENABLE_ACRYLICBLURBEHIND : Native.ACCENT_DISABLED,
            GradientColor = 0x55141414, // about a third opaque, near-black (0xAABBGGRR)
        };
        int size = Marshal.SizeOf(policy);
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new Native.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = Native.WCA_ACCENT_POLICY,
                Data = pointer,
                SizeOfData = size,
            };
            Supported = Native.SetWindowCompositionAttribute(_hwnd, ref data) != 0;
            _acrylicOn = on && Supported;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>Puts the frost exactly behind the island's glass panel (screen pixels).</summary>
    public void Place(int x, int y, int width, int height)
    {
        if (_hwnd == IntPtr.Zero || width <= 0 || height <= 0) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        if (!_showing)
        {
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            _showing = true;
        }
    }

    /// <summary>Hides the frost completely (any style other than Glass, or the island is hidden).</summary>
    public void Conceal()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, Native.SW_HIDE); // always, in case Windows showed it behind our back
        _showing = false;
    }
}

/// <summary>
/// The Glow style's halo, drawn on its own click-through layer just underneath the island.
/// (Drawn on the island window itself, the soft edge would quietly block clicks to whatever is around it.)
/// </summary>
internal sealed class GlowBackdrop : Window
{
    /// <summary>Extra room around the island for the glow to spread into.</summary>
    public const double Spread = 90;

    private readonly Path _wide = new();
    private readonly Path _tight = new();
    private readonly Path _rim = new();
    private readonly DropShadowEffect _wideGlow = new() { ShadowDepth = 0, BlurRadius = 70, Opacity = 0.75 };
    private readonly DropShadowEffect _tightGlow = new() { ShadowDepth = 0, BlurRadius = 24, Opacity = 1.0 };
    private IntPtr _hwnd;

    public IntPtr Handle => _hwnd;

    public GlowBackdrop()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;

        _wide.Effect = _wideGlow;
        _tight.Effect = _tightGlow;
        _rim.StrokeThickness = 1.5;
        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(_wide);
        canvas.Children.Add(_tight);
        canvas.Children.Add(_rim);
        Content = canvas;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            // WS_EX_TRANSPARENT: every click goes straight through to whatever is underneath.
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
                new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT));
        };
    }

    /// <summary>Draws the glow around the island's outline at the given spot (display units, relative to this window).</summary>
    public void Update(Geometry? shape, Point offset, Color accent)
    {
        var fill = new SolidColorBrush(accent);
        fill.Freeze();
        foreach (var path in new[] { _wide, _tight, _rim })
        {
            path.Data = shape;
            Canvas.SetLeft(path, offset.X);
            Canvas.SetTop(path, offset.Y);
        }
        _wide.Fill = fill;
        _tight.Fill = fill;
        _rim.Stroke = fill; // a crisp rim of light right at the island's edge
        _wideGlow.Color = accent;
        _tightGlow.Color = accent;
    }
}
