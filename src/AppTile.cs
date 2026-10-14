using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>One icon in the expanded island: either an open app (maybe several windows) or a pinned shortcut.</summary>
public sealed class AppTile : INotifyPropertyChanged
{
    public string Key { get; init; } = "";
    public bool IsPinned { get; init; }
    public string? ShortcutPath { get; init; }

    /// <summary>Not an app: the thin line between pinned apps and other open apps in the hover tray.</summary>
    public bool IsDivider { get; init; }

    /// <summary>Not an app: the "+" at the end of the hover tray's row, for adding a pin.</summary>
    public bool IsAddButton { get; init; }

    /// <summary>A pinned folder, file, website or Settings page (not an app): what it opens.</summary>
    public string? PinTarget { get; set; }

    private bool _isShortcutPin;
    public bool IsShortcutPin { get => _isShortcutPin; set => Set(ref _isShortcutPin, value); }

    private bool _hasBadge;
    /// <summary>A small dot on the icon (a pinned Downloads folder with something new in it).</summary>
    public bool HasBadge { get => _hasBadge; set => Set(ref _hasBadge, value); }

    /// <summary>How the installed app is started (from the Start menu list); null if it isn't in the Start menu.</summary>
    public string? LaunchPath { get; set; }

    private bool _isClosedPin;
    /// <summary>A pinned app that isn't running: drawn faded, and clicking it starts the app.</summary>
    public bool IsClosedPin { get => _isClosedPin; set { if (Set(ref _isClosedPin, value)) OnPropertyChanged(nameof(DockTip)); } }

    /// <summary>Tooltip in the hover tray's dock row.</summary>
    public string DockTip => IsClosedPin ? $"{Name} · not open, click to start" : Name;

    /// <summary>The app's .exe, used to open a new window on middle-click (empty for Store apps).</summary>
    public string ExePath { get; init; } = "";

    /// <summary>The app's windows in a stable order, so clicking repeatedly cycles through all of them.</summary>
    public List<IntPtr> Windows { get; set; } = new();

    /// <summary>The window of this app that was used most recently.</summary>
    public IntPtr MostRecent { get; set; }

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            OnPropertyChanged(nameof(ShortName));
            OnPropertyChanged(nameof(DockTip));
        }
    }

    /// <summary>A shorter name for chips: "Microsoft Edge" → "Edge", "File Explorer" → "Explorer".</summary>
    public string ShortName
    {
        get
        {
            string n = Name;
            foreach (var prefix in new[] { "Microsoft ", "Windows ", "Google " })
                if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Length > prefix.Length + 2) n = n[prefix.Length..];
            if (n.Equals("File Explorer", StringComparison.OrdinalIgnoreCase)) n = "Explorer";
            return n;
        }
    }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    private LineIcon? _line;
    /// <summary>Line-style icon (when the icon set is available and turned on), otherwise null.</summary>
    public LineIcon? Line { get => _line; set { if (Set(ref _line, value)) OnPropertyChanged(nameof(HasLine)); } }
    public bool HasLine => Line is not null;

    private string _title = "";
    /// <summary>The current window's title, tidied (shown in the gold capsule when this app is in front).</summary>
    public string Title { get => _title; set { if (Set(ref _title, value)) OnPropertyChanged(nameof(HasTitle)); } }
    public bool HasTitle => Title.Length > 0;

    private bool _isMinimized;
    /// <summary>Every window of this app is minimised (shown dimmed in the hover tray).</summary>
    public bool IsMinimized { get => _isMinimized; set => Set(ref _isMinimized, value); }

    private int _count;
    public int Count
    {
        get => _count;
        set { if (Set(ref _count, value)) OnPropertyChanged(nameof(HasMany)); }
    }

    public bool HasMany => Count > 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
