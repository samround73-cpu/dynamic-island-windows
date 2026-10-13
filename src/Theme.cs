using System;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>
/// Light or dark: from the island style in Settings, or from Windows' own light/dark mode when the style is
/// Auto. It can change while the island is running (see MainWindow.SwitchTheme).
///
/// "Ink" is the colour of text and fine lines: white on the dark island, near-black on the light one.
/// Ink(0x80) is half-strength ink, and so on.
/// </summary>
public static class Theme
{
    public static bool Light { get; private set; } = Decide(SafeStyle());

    /// <summary>Switches light/dark. Brushes handed out before keep their old colour, so callers redraw afterwards.</summary>
    public static void Set(bool light)
    {
        Light = light;
        InkCache.Clear();
    }

    /// <summary>Near-black used for text on the light island.</summary>
    public static readonly Color DarkInk = Color.FromRgb(0x1C, 0x1C, 0x1E);

    private static readonly Dictionary<byte, SolidColorBrush> InkCache = new();

    public static Color InkColor(byte alpha = 0xFF) =>
        Light ? Color.FromArgb(alpha, DarkInk.R, DarkInk.G, DarkInk.B) : Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);

    public static SolidColorBrush Ink(byte alpha = 0xFF)
    {
        if (InkCache.TryGetValue(alpha, out var brush)) return brush;
        brush = new SolidColorBrush(InkColor(alpha));
        brush.Freeze();
        InkCache[alpha] = brush;
        return brush;
    }

    /// <summary>Whether this island style should be light (Automatic follows Windows).</summary>
    public static bool Decide(string style) => style switch
    {
        "Light" or "Glass" => true,
        "Auto" => WindowsUsesLight(),
        _ => false,
    };

    /// <summary>Windows' own light/dark setting for the taskbar and Start menu ("Choose your mode").</summary>
    public static bool WindowsUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch { return false; }
    }

    private static string SafeStyle()
    {
        try { return IslandSettings.Load().IslandStyle; }
        catch { return "Black"; }
    }
}
