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

    // ---- Appearance and behaviour ----
    public string AccentColor { get; set; } = "#D6B46A";
    public bool ShowMusicBars { get; set; } = true;
    /// <summary>"Black", "Glass", "Glow" or "Outline".</summary>
    public string IslandStyle { get; set; } = "Black";
    public bool ShowBatteryInPill { get; set; } = true;
    /// <summary>Line-style app icons (Tabler) instead of each app's own icon.</summary>
    public bool UseLineIcons { get; set; } = true;
    public bool PeekOnHover { get; set; } = true;
    public int PeekDelayMs { get; set; } = 450;
    public bool HideOverMaximized { get; set; } = true;
    public bool DevicePopOuts { get; set; } = true;

    /// <summary>Ctrl+Space opens the island straight to app search, from anywhere.</summary>
    public bool SearchHotkey { get; set; } = true;

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
                return JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(FilePath)) ?? new IslandSettings();
        }
        catch { /* a damaged file just means starting fresh */ }
        return new IslandSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
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
