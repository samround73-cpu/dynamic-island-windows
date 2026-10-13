using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>A heading in the Apps list ("STARRED", "RECENT", "APPLICATIONS").</summary>
public sealed class AppSectionHeader
{
    public string Title { get; init; } = "";
}

/// <summary>One row in the Apps list: an installed app (or an open window that isn't in the Start menu).</summary>
public sealed class AppRow : INotifyPropertyChanged
{
    /// <summary>The launch path for installed apps, or "window:…" for an open window with no Start-menu entry.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? LaunchPath { get; init; }
    public string? ExePath { get; init; }

    /// <summary>First letter, shown if there's no icon.</summary>
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    /// <summary>The app's open windows, most recently used first.</summary>
    public List<IntPtr> Windows { get; set; } = new();

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    private LineIcon? _line;
    /// <summary>Line-style icon (when the icon set is available and turned on), otherwise null.</summary>
    public LineIcon? Line
    {
        get => _line;
        set
        {
            Set(ref _line, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLine)));
        }
    }
    public bool HasLine => Line is not null;

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; set => Set(ref _isOpen, value); }

    private bool _isStarred;
    public bool IsStarred { get => _isStarred; set => Set(ref _isStarred, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
