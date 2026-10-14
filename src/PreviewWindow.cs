using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIsland;

public sealed record PreviewItem(IntPtr Handle, string Title, ImageSource? Icon);

/// <summary>
/// The little live preview that appears when you hover over an open app, like on the normal taskbar.
/// The pictures are drawn by Windows itself (DWM thumbnails), so they show the real window, live.
/// Apps with several windows get one preview per window (up to four); click one to switch to it.
/// </summary>
public sealed class PreviewWindow : Window
{
    private const double SlotWidth = 240, TitleHeight = 34, ThumbHeight = 152, Pad = 12, Gap = 8;
    private const int MaxWindows = 4;

    private readonly StackPanel _slots = new() { Orientation = Orientation.Horizontal };
    private readonly List<IntPtr> _thumbnails = new();
    private IntPtr _hwnd;

    /// <summary>Fired with the window the user clicked.</summary>
    public event Action<IntPtr>? WindowChosen;

    /// <summary>Fired when the ✕ on a preview is clicked.</summary>
    public event Action<IntPtr>? WindowCloseRequested;

    private static readonly Brush CloseHover = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));

    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = new Border { Padding = new Thickness(Pad), Child = _slots };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            // Never steal focus from the island, and stay out of Alt+Tab.
            long exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
                new IntPtr(exStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

            // Windows 11 rounded corners and dark frame.
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int dark = 1;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };
    }

    /// <param name="anchor">The hovered icon's position on screen, in screen pixels.</param>
    public void ShowFor(IReadOnlyList<PreviewItem> items, Rect anchor)
    {
        RemoveThumbnails();
        _slots.Children.Clear();

        int count = Math.Min(items.Count, MaxWindows);
        if (count == 0)
        {
            HidePreview();
            return;
        }

        Width = Pad * 2 + count * SlotWidth + (count - 1) * Gap;
        Height = Pad * 2 + TitleHeight + ThumbHeight;
        for (int i = 0; i < count; i++) _slots.Children.Add(BuildSlot(items[i], gapAfter: i < count - 1));

        // Below the icon, kept inside the screen it's on (which may not be the main screen).
        var below = new Native.POINT { X = (int)(anchor.Left + anchor.Width / 2), Y = (int)anchor.Bottom };
        IntPtr monitor = Native.MonitorFromPoint(below, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        double screenScale = 1.0;
        try { if (Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0) screenScale = dpi / 96.0; } catch { }
        int widthPx = (int)Math.Round(Width * screenScale);
        int margin = (int)Math.Round(6 * screenScale);
        int x = below.X - widthPx / 2;
        if (Native.GetMonitorInfo(monitor, ref info))
            x = Math.Clamp(x, info.rcMonitor.Left + margin, Math.Max(info.rcMonitor.Left + margin, info.rcMonitor.Right - widthPx - margin));
        int y = below.Y + (int)Math.Round(8 * screenScale);

        if (!IsVisible)
        {
            Left = x / screenScale; // roughly there already, so it doesn't flash somewhere else first
            Top = y / screenScale;
            Show();
        }
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        UpdateLayout();

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = 0; i < count; i++)
            AddThumbnail(items[i].Handle, Pad + i * (SlotWidth + Gap), Pad + TitleHeight, scale);
    }

    public void HidePreview()
    {
        RemoveThumbnails();
        if (IsVisible) Hide();
    }

    private UIElement BuildSlot(PreviewItem item, bool gapAfter)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ThumbHeight) });

        var header = new DockPanel { Margin = new Thickness(8, 0, 4, 4), LastChildFill = true };

        // ✕ to close this window (appears when you hover the preview, turns red like Windows' own).
        var closeGlyph = new TextBlock
        {
            Text = "\uE711",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Child = closeGlyph,
            Visibility = Visibility.Hidden,
            ToolTip = "Close window",
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.MouseEnter += (_, _) => close.Background = CloseHover;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WindowCloseRequested?.Invoke(item.Handle);
        };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        if (item.Icon is not null)
        {
            var icon = new Image { Source = item.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(icon);
        }
        header.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(header);

        // Shown underneath the live picture; visible if Windows has nothing to draw (e.g. minimised windows).
        var fallback = new Image { Source = item.Icon, Width = 32, Height = 32, Opacity = 0.6 };
        Grid.SetRow(fallback, 1);
        grid.Children.Add(fallback);

        var hover = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        var slot = new Border
        {
            Width = SlotWidth,
            Height = TitleHeight + ThumbHeight,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, gapAfter ? Gap : 0, 0),
            Cursor = Cursors.Hand,
            Child = grid,
        };
        slot.MouseEnter += (_, _) =>
        {
            slot.Background = hover;
            close.Visibility = Visibility.Visible;
        };
        slot.MouseLeave += (_, _) =>
        {
            slot.Background = Brushes.Transparent;
            close.Visibility = Visibility.Hidden;
        };
        slot.MouseLeftButtonUp += (_, _) => WindowChosen?.Invoke(item.Handle);
        return slot;
    }

    private void AddThumbnail(IntPtr source, double areaLeft, double areaTop, double scale)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Native.DwmRegisterThumbnail(_hwnd, source, out IntPtr thumbnail) != 0) return;
        _thumbnails.Add(thumbnail);

        if (Native.DwmQueryThumbnailSourceSize(thumbnail, out var size) != 0 || size.X <= 0 || size.Y <= 0) return;

        // Fit the window's picture inside its slot, keeping its shape, never enlarging it.
        const double inset = 6;
        double areaWidth = (SlotWidth - inset * 2) * scale;
        double areaHeight = (ThumbHeight - inset) * scale;
        double k = Math.Min(1.0, Math.Min(areaWidth / size.X, areaHeight / size.Y));
        double w = size.X * k, h = size.Y * k;
        double left = (areaLeft + inset) * scale + (areaWidth - w) / 2;
        double top = areaTop * scale + (areaHeight - h) / 2;

        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new Native.RECT { Left = (int)left, Top = (int)top, Right = (int)(left + w), Bottom = (int)(top + h) },
            opacity = 255,
            fVisible = 1,
            fSourceClientAreaOnly = 0,
        };
        Native.DwmUpdateThumbnailProperties(thumbnail, ref props);
    }

    private void RemoveThumbnails()
    {
        foreach (var thumbnail in _thumbnails) Native.DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
    }
}
