using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DynamicIsland;

/// <summary>
/// Everything the island remembers between restarts: your settings, starred and recent apps,
/// pinned clipboard text and the files on your shelf.
/// Saved to %AppData%\DynamicIsland\settings.json.
/// </summary>
public sealed class IslandSettings
{
    private const int MaxRecent = 5;

    // ---- Apps ----
    /// <summary>Launch paths of starred apps.</summary>
    public List<string> Starred { get; set; } = new();
    /// <summary>Launch paths of apps opened from the island, newest first.</summary>
    public List<string> Recent { get; set; } = new();
    /// <summary>Whether we've already copied the apps pinned to the old taskbar into Starred (done once).</summary>
    public bool ImportedTaskbarPins { get; set; }
    /// <summary>Launch paths of apps pinned to the hover tray, in the order they're shown.</summary>
    public List<string> TrayPins { get; set; } = new();
    /// <summary>Names you've given pins (folders, files, websites), by what they open.</summary>
    public Dictionary<string, string> PinNames { get; set; } = new();

    // ---- Appearance and behaviour ----
    public string AccentColor { get; set; } = "#D6B46A";
    public bool ShowMusicBars { get; set; } = true;
    /// <summary>"Black", "Light" (frosted), "Glow", "Outline" or "Auto" (Black or Light, following Windows).</summary>
    public string IslandStyle { get; set; } = "Black";
    public bool ShowBatteryInPill { get; set; } = true;
    /// <summary>Light style only: real frosted blur behind the island (Windows can only blur a plain rounded panel,
    /// so the island loses its curved shoulders).</summary>
    public bool LightFrosted { get; set; }
    /// <summary>Light style: how solid the island is, 50-100 (%). Lower lets more of the wallpaper show through.</summary>
    public int LightOpacity { get; set; } = 100;
    /// <summary>When the island hasn't been used for a while: "Stay" as it is, or "Fade" to a faint ghost.</summary>
    public string IdleMode { get; set; } = "Stay";
    /// <summary>How long without the mouse near it before it fades.</summary>
    public int IdleSeconds { get; set; } = 30;
    /// <summary>Line-style app icons (Tabler) instead of each app's own icon.</summary>
    public bool UseLineIcons { get; set; } = true;
    public bool PeekOnHover { get; set; } = true;
    public int PeekDelayMs { get; set; } = 450;
    public bool HideOverMaximized { get; set; } = true;
    public bool DevicePopOuts { get; set; } = true;

    /// <summary>Ctrl+Space opens the island straight to app search, from anywhere.</summary>
    public bool SearchHotkey { get; set; } = true;

    /// <summary>Download and install new versions from GitHub by itself (otherwise it just tells you).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Pop out when a screenshot is taken (Win+Shift+S / Print Screen).</summary>
    public bool ScreenshotPopups { get; set; } = true;

    /// <summary>Pop out when a download finishes.</summary>
    public bool DownloadPopups { get; set; } = true;

    /// <summary>Put new screenshots and downloads on the Shelf, ready to drag somewhere.</summary>
    public bool CapturesToShelf { get; set; } = true;

    /// <summary>A moving glow on the pill (and a row in the hover tray) while something is downloading.</summary>
    public bool DownloadProgress { get; set; } = true;

    /// <summary>Show the icons apps put in the taskbar corner (OneDrive, Discord…) on the Controls tab.</summary>
    public bool TrayAppIcons { get; set; } = true;

    /// <summary>Pop out when Caps Lock is turned on or off.</summary>
    public bool CapsLockPopups { get; set; } = true;

    /// <summary>Pop out with the song (and play/skip buttons) when the track changes.</summary>
    public bool SongPopups { get; set; } = true;

    /// <summary>How long pop-outs stay up: "Short", "Normal" or "Long".</summary>
    public string PopupLength { get; set; } = "Normal";

    // ---- Clock ----
    /// <summary>"Windows" (your PC's 12/24-hour setting), "12" or "24".</summary>
    public string ClockFormat { get; set; } = "Windows";
    public bool ClockSeconds { get; set; }
    /// <summary>How the date shows on the pill: "Short" (Tue 13 Oct), "Numbers" (13/10), "Day" (Tuesday) or "Off".</summary>
    public string PillDate { get; set; } = "Short";

    // ---- Screens ----
    /// <summary>With more than one screen: "Follow" (the island moves to the screen your mouse rests on) or "Main".</summary>
    public string ScreenMode { get; set; } = "Follow";

    // ---- Tidying up ----
    /// <summary>Take things off the Shelf after this many days (0 = never). The files themselves stay put.</summary>
    public int ShelfCleanDays { get; set; }
    /// <summary>When each Shelf item was added, by path (for the clean-up above).</summary>
    public Dictionary<string, DateTime> ShelfAdded { get; set; } = new();
    /// <summary>Forget clipboard history when you lock the PC (pinned clips are kept). It's never kept after a restart anyway.</summary>
    public bool ClearClipboardOnLock { get; set; }

    /// <summary>The order of the tabs along the top, and which ones are hidden.</summary>
    public List<string> TabOrder { get; set; } = new();
    public List<string> HiddenTabs { get; set; } = new();

    // ---- Weather ----
    /// <summary>The town you typed in Settings; empty means "use Windows' location".</summary>
    public string WeatherPlace { get; set; } = "";
    public double? WeatherLatitude { get; set; }
    public double? WeatherLongitude { get; set; }
    public bool Fahrenheit { get; set; }

    // ---- Clipboard and shelf ----
    /// <summary>Text you pinned in the Clipboard tab (only pinned items are ever saved).</summary>
    public List<string> PinnedClips { get; set; } = new();
    /// <summary>Paths of files and folders on your shelf.</summary>
    public List<string> ShelfFiles { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "settings.json");

    public static IslandSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(FilePath)) ?? new IslandSettings();
                if (settings.IslandStyle == "Glass") settings.IslandStyle = "Light"; // Glass was replaced by Light (frosted)
                return settings;
            }
        }
        catch { /* a damaged file just means starting fresh */ }
        return new IslandSettings();
    }

    /// <summary>Set when the island is being removed from the PC, so closing doesn't write the settings back.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DoNotSave { get; set; }

    public void Save()
    {
        if (DoNotSave) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public bool IsTrayPinned(string launchPath) => TrayPins.Contains(launchPath, StringComparer.OrdinalIgnoreCase);

    public void ToggleTrayPin(string launchPath)
    {
        if (IsTrayPinned(launchPath)) TrayPins.RemoveAll(s => string.Equals(s, launchPath, StringComparison.OrdinalIgnoreCase));
        else TrayPins.Add(launchPath);
    }

    /// <summary>Moves a pinned app one place left (-1) or right (+1) in the tray.</summary>
    public void MoveTrayPin(string launchPath, int delta)
    {
        int index = TrayPins.FindIndex(s => string.Equals(s, launchPath, StringComparison.OrdinalIgnoreCase));
        int target = index + delta;
        if (index < 0 || target < 0 || target >= TrayPins.Count) return;
        (TrayPins[index], TrayPins[target]) = (TrayPins[target], TrayPins[index]);
    }

    public bool IsStarred(string launchPath) => Starred.Contains(launchPath, StringComparer.OrdinalIgnoreCase);

    public void ToggleStar(string launchPath)
    {
        if (IsStarred(launchPath)) Starred.RemoveAll(s => string.Equals(s, launchPath, StringComparison.OrdinalIgnoreCase));
        else Starred.Add(launchPath);
        Save();
    }

    public void AddRecent(string launchPath)
    {
        Recent.RemoveAll(s => string.Equals(s, launchPath, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, launchPath);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        Save();
    }
}
