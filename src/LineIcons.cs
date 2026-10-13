using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>A line-style icon: the character in the icon font, and the colour to draw it in.</summary>
public sealed record LineIcon(string Glyph, Brush Brush);

/// <summary>
/// Line-style app icons from the free Tabler Icons font (MIT licence), which Build.cmd downloads.
/// Well-known apps get their own logo in their brand colour; everything else gets a tidy generic app icon
/// tinted with the main colour of the app's real icon, so the whole row looks consistent.
/// If the font isn't there, Available is false and the island shows normal app icons.
/// </summary>
public static class LineIcons
{
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LineIcon> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static FontFamily? Font { get; private set; }

    /// <summary>For XAML: the icon font, or an ordinary font if it isn't available (nothing is drawn then).</summary>
    public static FontFamily FontOrDefault => Font ?? new FontFamily("Segoe UI");
    public static bool Available => Font is not null && Glyphs.Count > 0;

    /// <summary>(words to look for in the app's name or .exe, icon names to try in order, brand colour)</summary>
    private static readonly (string[] Match, string[] Icons, string Color)[] Known =
    {
        (new[] { "msedge", "microsoft edge" }, new[] { "brand-edge" }, "#3BA7F0"),
        (new[] { "chrome", "google chrome" }, new[] { "brand-chrome" }, "#4C8BF5"),
        (new[] { "firefox" }, new[] { "brand-firefox" }, "#FF7139"),
        (new[] { "opera" }, new[] { "brand-opera" }, "#FF1B2D"),
        (new[] { "brave" }, new[] { "brand-brave" }, "#FB542B"),
        (new[] { "explorer", "file explorer" }, new[] { "folder" }, "#F2C14E"),
        (new[] { "spotify" }, new[] { "brand-spotify" }, "#1DB954"),
        (new[] { "discord" }, new[] { "brand-discord" }, "#7984F5"),
        (new[] { "steam" }, new[] { "brand-steam" }, "#C7D5E0"),
        (new[] { "battle.net", "battlenet" }, new[] { "brand-battlenet", "device-gamepad-2" }, "#2DA6FF"),
        (new[] { "epicgameslauncher", "epic games" }, new[] { "brand-epic-games", "device-gamepad-2" }, "#E8E8E8"),
        (new[] { "xbox" }, new[] { "brand-xbox" }, "#3DC93D"),
        (new[] { "code", "visual studio code" }, new[] { "brand-vscode" }, "#3FA9F5"),
        (new[] { "devenv", "visual studio" }, new[] { "brand-visual-studio", "code" }, "#A97BFF"),
        (new[] { "slack" }, new[] { "brand-slack" }, "#E8467C"),
        (new[] { "teams", "ms-teams" }, new[] { "brand-teams", "users-group" }, "#7B83EB"),
        (new[] { "zoom" }, new[] { "brand-zoom" }, "#2D8CFF"),
        (new[] { "whatsapp" }, new[] { "brand-whatsapp" }, "#25D366"),
        (new[] { "telegram" }, new[] { "brand-telegram" }, "#2AABEE"),
        (new[] { "outlook", "olk" }, new[] { "mail" }, "#2F8FE0"),
        (new[] { "winword", "word" }, new[] { "file-word", "file-text" }, "#3C8CE7"),
        (new[] { "excel" }, new[] { "file-spreadsheet", "table" }, "#2EB872"),
        (new[] { "powerpnt", "powerpoint" }, new[] { "presentation" }, "#E8693F"),
        (new[] { "onenote" }, new[] { "notebook" }, "#B57BE0"),
        (new[] { "notepad" }, new[] { "notes" }, "#A9D6FF"),
        (new[] { "calculator", "calc" }, new[] { "calculator" }, "#B8BDC4"),
        (new[] { "windowsterminal", "terminal", "powershell", "cmd", "command prompt" }, new[] { "terminal-2", "terminal" }, "#C9CED6"),
        (new[] { "systemsettings", "settings" }, new[] { "settings" }, "#B8BDC4"),
        (new[] { "taskmgr", "task manager" }, new[] { "activity-heartbeat", "activity" }, "#6CCB8C"),
        (new[] { "mspaint", "paint" }, new[] { "brush" }, "#F0997B"),
        (new[] { "photos" }, new[] { "photo" }, "#5AA9F0"),
        (new[] { "media player", "zunemusic", "wmplayer" }, new[] { "player-play" }, "#F08DB4"),
        (new[] { "vlc" }, new[] { "brand-vlc", "player-play" }, "#FF8800"),
        (new[] { "obs64", "obs studio" }, new[] { "brand-obs", "video" }, "#D8D8D8"),
        (new[] { "figma" }, new[] { "brand-figma" }, "#A97BFF"),
        (new[] { "notion" }, new[] { "brand-notion" }, "#EDEDED"),
        (new[] { "githubdesktop", "github desktop" }, new[] { "brand-github" }, "#EDEDED"),
        (new[] { "photoshop" }, new[] { "brand-adobe-photoshop", "photo-edit" }, "#31A8FF"),
        (new[] { "illustrator" }, new[] { "brand-adobe-illustrator", "vector" }, "#FF9A00"),
        (new[] { "premiere" }, new[] { "brand-adobe-premier", "movie" }, "#9999FF"),
        (new[] { "blender" }, new[] { "brand-blender" }, "#F5792A"),
        (new[] { "unity" }, new[] { "brand-unity" }, "#EDEDED"),
        (new[] { "netflix" }, new[] { "brand-netflix" }, "#E50914"),
        (new[] { "claude" }, new[] { "sparkles" }, "#D97757"),
        (new[] { "snippingtool", "snipping tool" }, new[] { "cut" }, "#5AA9F0"),
        (new[] { "store", "winstore" }, new[] { "building-store" }, "#5AA9F0"),
        (new[] { "mail" }, new[] { "mail" }, "#2F8FE0"),
        (new[] { "calendar" }, new[] { "calendar" }, "#5AA9F0"),
        (new[] { "camera" }, new[] { "camera" }, "#B8BDC4"),
        (new[] { "clock", "alarms" }, new[] { "clock" }, "#B8BDC4"),
        (new[] { "control panel", "control" }, new[] { "adjustments" }, "#B8BDC4"),
    };

    private const string GenericIcon = "app-window";

    /// <summary>Loads the downloaded font and its character map (once, at start-up).</summary>
    public static void Load()
    {
        try
        {
            var css = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/tabler-icons.css"));
            if (css is null) return;
            using (var reader = new StreamReader(css.Stream))
            {
                foreach (Match m in Regex.Matches(reader.ReadToEnd(), @"\.ti-([a-z0-9-]+):before\s*\{\s*content:\s*""\\([0-9a-fA-F]+)"""))
                {
                    int code = Convert.ToInt32(m.Groups[2].Value, 16);
                    Glyphs[m.Groups[1].Value] = char.ConvertFromUtf32(code);
                }
            }
            Font = Fonts.GetFontFamilies(new Uri("pack://application:,,,/Assets/")).FirstOrDefault();
        }
        catch
        {
            Font = null; // no icon font: normal app icons are used
        }
    }

    /// <summary>A specific icon from the set by name (e.g. "bluetooth"), in the given colour; null if unavailable.</summary>
    public static LineIcon? Named(string iconName, string color)
    {
        if (!Available) return null;
        string key = "named|" + iconName + "|" + color;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (!Glyphs.TryGetValue(iconName, out var glyph) && !Glyphs.TryGetValue("settings", out glyph)) return null;
        var icon = new LineIcon(glyph, Solid((Color)ColorConverter.ConvertFromString(color)));
        Cache[key] = icon;
        return icon;
    }

    /// <summary>The line icon for an app (by its name and .exe), or null if the icon font isn't available.</summary>
    public static LineIcon? For(string appName, string exePath, ImageSource? realIcon)
    {
        if (!Available) return null;
        string exe = Path.GetFileNameWithoutExtension(exePath ?? "");
        string key = appName + "|" + exe;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        LineIcon? icon = null;
        foreach (var (match, icons, color) in Known)
        {
            bool hit = match.Any(word =>
                exe.Equals(word.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
                || ContainsWord(appName, word));
            if (!hit) continue;
            string? glyph = icons.Select(n => Glyphs.TryGetValue(n, out var g) ? g : null).FirstOrDefault(g => g is not null);
            if (glyph is null) continue;
            icon = new LineIcon(glyph, Solid((Color)ColorConverter.ConvertFromString(color)));
            break;
        }

        if (icon is null && Glyphs.TryGetValue(GenericIcon, out var generic))
            icon = new LineIcon(generic, Solid(MainColour(realIcon)));

        if (icon is not null) Cache[key] = icon;
        return icon;
    }

    /// <summary>"Visual Studio Code" contains the word "code"; "Codex" doesn't.</summary>
    private static bool ContainsWord(string text, string word)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            int end = at + word.Length;
            bool endOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (startOk && endOk) return true;
            at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>The most vivid colour in an app's real icon, brightened so it reads on the dark island.</summary>
    private static Color MainColour(ImageSource? image)
    {
        var fallback = Color.FromRgb(0xB8, 0xBD, 0xC4);
        if (image is not BitmapSource source) return fallback;
        try
        {
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] < 160) continue;
                double pb = px[i], pg = px[i + 1], pr = px[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double saturation = max <= 0 ? 0 : (max - min) / max;
                double wgt = 0.05 + saturation * saturation;
                r += pr * wgt; g += pg * wgt; b += pb * wgt; weight += wgt;
            }
            if (weight <= 0) return fallback;
            r /= weight; g /= weight; b /= weight;

            // Lift dark colours so they stay visible on black.
            double brightest = Math.Max(r, Math.Max(g, b));
            if (brightest < 190)
            {
                double lift = 190 / Math.Max(1, brightest);
                r = Math.Min(255, r * lift); g = Math.Min(255, g * lift); b = Math.Min(255, b * lift);
            }
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush Solid(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
