using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DynamicIsland;

/// <summary>
/// Notices new screenshots (Win+Shift+S / Print Screen save them to Pictures\Screenshots) and finished downloads
/// (in the Downloads folder), and tells the island so it can pop out and put them on the Shelf.
/// </summary>
public sealed class CaptureWatcher : IDisposable
{
    public enum Kind { Screenshot, Download }

    /// <summary>A file is ready (fully written). Raised on a background thread.</summary>
    public event Action<string, Kind>? FileReady;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // Files browsers and download managers use while still downloading.
    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".aria2", ".temp",
    };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    public void Start()
    {
        Watch(KnownFolder("b7bede81-df94-4682-a7d8-57a52620b86f") // Screenshots
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"), Kind.Screenshot);
        Watch(KnownFolder("374de290-123f-4565-9164-39c4925e467b") // Downloads
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Kind.Download);
    }

    private void Watch(string folder, Kind kind)
    {
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 32 * 1024,
            };
            watcher.Created += (_, e) => Consider(e.FullPath, kind);
            watcher.Renamed += (_, e) => Consider(e.FullPath, kind); // downloads are renamed from .crdownload/.part when done
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            App.LogNote($"couldn't watch {folder}: {ex.Message}");
        }
    }

    private void Consider(string path, Kind kind)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path);
        if (name.StartsWith('.') || name.StartsWith('~') || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
        if (PartialExtensions.Contains(ext)) return;
        if (kind == Kind.Screenshot && !ImageExtensions.Contains(ext)) return;

        lock (_lock)
        {
            // The same file often produces several events; only act once.
            if (_recent.TryGetValue(path, out var seen) && DateTime.Now - seen < TimeSpan.FromSeconds(30)) return;
            _recent[path] = DateTime.Now;
            if (_recent.Count > 200) _recent.Clear();
        }
        _ = WaitUntilReadyAsync(path, kind);
    }

    /// <summary>Waits until the file has stopped growing and nothing is writing to it, then reports it.</summary>
    private async Task WaitUntilReadyAsync(string path, Kind kind)
    {
        long lastSize = -1;
        var timeout = kind == Kind.Download ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(15);
        var started = DateTime.Now;
        while (DateTime.Now - started < timeout)
        {
            await Task.Delay(kind == Kind.Download ? 1500 : 400);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return; // it was a temporary file after all
                if (info.Length > 0 && info.Length == lastSize && !IsLocked(path))
                {
                    FileReady?.Invoke(path, kind);
                    return;
                }
                lastSize = info.Length;
            }
            catch { }
        }
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? KnownFolder(string id)
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid(id), 0, IntPtr.Zero, out var pointer) != 0) return null;
            string? path = Marshal.PtrToStringUni(pointer);
            Marshal.FreeCoTaskMem(pointer);
            return path;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }
}
