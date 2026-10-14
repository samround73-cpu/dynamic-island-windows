using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>A file or folder parked on the shelf.</summary>
public sealed class ShelfItem : INotifyPropertyChanged
{
    private bool _isSelected;
    /// <summary>Ticked on the Shelf, for doing something to several files at once.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Path;
    public ImageSource? Thumbnail { get; init; }
    public bool IsFolder => Directory.Exists(Path);
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    /// <summary>"PDF · 2.3 MB" or "Folder".</summary>
    public string Details
    {
        get
        {
            try
            {
                if (IsFolder) return "Folder";
                var info = new FileInfo(Path);
                string ext = info.Extension.TrimStart('.').ToUpperInvariant();
                return $"{(ext.Length > 0 ? ext : "File")} · {SystemStats.FormatBytes(info.Length)}";
            }
            catch { return "Missing"; }
        }
    }
}

/// <summary>
/// Files you've dropped onto the island to keep handy. The shelf only remembers where they are;
/// nothing is copied or moved unless you drag them somewhere yourself.
/// </summary>
public sealed class Shelf
{
    private readonly IslandSettings _settings;
    public List<ShelfItem> Items { get; } = new();
    public event Action? Changed;

    public Shelf(IslandSettings settings)
    {
        _settings = settings;
        foreach (var path in settings.ShelfFiles.Where(p => File.Exists(p) || Directory.Exists(p)))
            Items.Add(Create(path));
    }

    private static ShelfItem Create(string path) => new() { Path = path, Thumbnail = AppCatalog.GetShellImage(path, 96) };

    /// <summary>Adds files (skipping ones already there). Returns how many were new.</summary>
    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            Items.Insert(0, Create(path));
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        Save();
    }

    public void Remove(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items.ToList()) Items.Remove(item);
        Save();
    }

    /// <summary>After a rename: the same spot on the shelf, now pointing at the new name.</summary>
    public void Replace(ShelfItem item, string newPath)
    {
        int index = Items.IndexOf(item);
        if (index < 0) return;
        Items[index] = Create(newPath);
        Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    /// <summary>Drops anything that's been moved or deleted since it was shelved.</summary>
    public void Prune()
    {
        if (Items.RemoveAll(i => !i.Exists) > 0) Save();
    }

    private void Save()
    {
        _settings.ShelfFiles = Items.Select(i => i.Path).ToList();
        _settings.Save();
        Changed?.Invoke();
    }
}
