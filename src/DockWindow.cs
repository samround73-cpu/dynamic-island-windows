using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// The window that holds dock mode's three pills along the bottom of the screen (see MainWindow.DockBar).
/// It spans the bottom of the screen but only its pills and panels can be clicked: everything else in it is
/// see-through, and clicks there go straight to whatever is underneath.
/// </summary>
internal sealed class DockWindow : Window
{
    public IntPtr Handle { get; private set; }

    public DockWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null; // no background at all, so empty parts don't catch clicks
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Dynamic Island dock";
        Left = -32000; // placed properly once it has a handle
        Width = 800;
        Height = 480;

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            long exStyle = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
            // A tool window: never in Alt+Tab or the island's own list of open apps.
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW));
        };
    }

    /// <summary>Covers the bottom of the given screen (screen pixels), this many display units tall.</summary>
    public void PlaceAtBottom(Native.RECT screen, double scale, double heightDips)
    {
        if (Handle == IntPtr.Zero) return;
        int width = screen.Right - screen.Left;
        int height = (int)Math.Round(heightDips * scale);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, screen.Left, screen.Bottom - height, width, height, Native.SWP_NOACTIVATE);
    }
}
