using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// For screens whose real brightness can't be changed (Shadow, Remote Desktop, some monitors): a see-through black
/// layer over the whole screen that dims the picture instead. Clicks go straight through it to whatever is underneath.
/// </summary>
internal sealed class DimOverlay : Window
{
    private IntPtr _hwnd;

    /// <summary>At 0% brightness the screen is this dark, so it's never completely black.</summary>
    private const double Darkest = 0.75;

    public DimOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Black;
        Opacity = 0;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Left = -32000; // placed properly once it has a handle
        Width = 10;
        Height = 10;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
                new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT));
        };
    }

    /// <summary>Dims the given screen (screen pixels) to this brightness (0-100); 100 takes the layer away.</summary>
    public void Apply(Native.RECT screen, int brightness)
    {
        if (brightness >= 100)
        {
            if (IsVisible) Hide();
            return;
        }
        Opacity = (100 - Math.Clamp(brightness, 0, 100)) / 100.0 * Darkest;
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, screen.Left, screen.Top,
            screen.Right - screen.Left, screen.Bottom - screen.Top, Native.SWP_NOACTIVATE);
    }
}
