using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DynamicIsland;

/// <summary>
/// Notices new screenshots (Win+Shift+S / Print Screen save them to Pictures\Screenshots) and finished downloads
/// (in the Downloads folder), and tells the island so it can pop out and put them on the Shelf.
/// It also follows downloads while they're still arriving (the .crdownload / .part files browsers write to),
/// so the island can show a glow and the speed. Browsers don't tell other apps how big a download will be,
/// so there's no percentage: just how much has arrived and how fast.
/// </summary>
public sealed class CaptureWatcher : IDisposable
{
    public enum Kind { Screenshot, Download }

    /// <summary>What's downloading right now: the newest file's name and size so far, the total speed, and how many.</summary>
    public sealed record DownloadProgress(string Name, long Bytes, double BytesPerSecond, int Count);

    /// <summary>Raised about once a second while something is downloading, then once with null when it stops. Background thread.</summary>
    public event Action<DownloadProgress?>? DownloadsChanged;

    /// <summary>The Downloads folder being watched (null if it couldn't be found).</summary>
    public string? DownloadsFolder { get; private set; }

    // The files browsers write to while downloading (renamed to the real name when done).
    private static readonly HashSet<string> InProgressExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".opdownload",
    };

    private sealed class Tracked
    {
        public long Size = -1;
        public DateTime FirstSeen = DateTime.Now;
        public DateTime LastGrew = DateTime.MinValue;
        public double Speed;
    }

    private readonly Dictionary<string, Tracked> _inProgress = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _progressTimer;
    private bool _reportedActive;
    private int _polling;

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
        DownloadsFolder = KnownFolder("374de290-123f-4565-9164-39c4925e467b") // Downloads
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Watch(DownloadsFolder, Kind.Download);

        // Pick up downloads that were already going when the island started.
        try
        {
            foreach (var file in Directory.EnumerateFiles(DownloadsFolder))
                if (InProgressExtensions.Contains(Path.GetExtension(file))) TrackInProgress(file);
        }
        catch { }
        _progressTimer = new Timer(_ => PollDownloads(), null, 1000, 1000);
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
            if (kind == Kind.Download)
            {
                watcher.Created += (_, e) => TrackInProgress(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    lock (_lock)
                    {
                        // Chrome renames "Unconfirmed 123.crdownload" to "name.zip.crdownload" part-way: keep following it.
                        if (_inProgress.Remove(e.OldFullPath, out var tracked) && InProgressExtensions.Contains(Path.GetExtension(e.FullPath)))
                            _inProgress[e.FullPath] = tracked;
                    }
                    TrackInProgress(e.FullPath);
                };
            }
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

    private void TrackInProgress(string path)
    {
        if (!InProgressExtensions.Contains(Path.GetExtension(path))) return;
        lock (_lock)
        {
            if (!_inProgress.ContainsKey(path) && _inProgress.Count < 50) _inProgress[path] = new Tracked();
        }
    }

    /// <summary>Once a second: how big is each download now, how fast is it growing, and is anything still arriving?</summary>
    private void PollDownloads()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // the previous check is still going
        try
        {
            List<KeyValuePair<string, Tracked>> items;
            lock (_lock) items = _inProgress.ToList();

            var now = DateTime.Now;
            foreach (var (path, item) in items)
            {
                long size = CurrentSize(path);
                if (size < 0)
                {
                    lock (_lock) _inProgress.Remove(path); // finished (renamed), cancelled or deleted
                    continue;
                }
                if (item.Size >= 0)
                {
                    long grown = Math.Max(0, size - item.Size);
                    item.Speed = item.Speed * 0.55 + grown * 0.45; // smoothed bytes per second
                    if (grown > 0) item.LastGrew = now;
                }
                item.Size = size;

                // Paused or abandoned for a long time: stop following it.
                if (now - item.FirstSeen > TimeSpan.FromMinutes(2) && now - item.LastGrew > TimeSpan.FromMinutes(10))
                    lock (_lock) _inProgress.Remove(path);
            }

            // Only count ones that are actually arriving, and have been for a moment (tiny files come and go too fast to show).
            var active = items
                .Where(x => now - x.Value.LastGrew < TimeSpan.FromSeconds(6) && now - x.Value.FirstSeen > TimeSpan.FromSeconds(1.5))
                .OrderByDescending(x => x.Value.FirstSeen)
                .ToList();

            if (active.Count == 0)
            {
                if (_reportedActive)
                {
                    _reportedActive = false;
                    DownloadsChanged?.Invoke(null);
                }
                return;
            }

            _reportedActive = true;
            var newest = active[0];
            DownloadsChanged?.Invoke(new DownloadProgress(
                FriendlyName(newest.Key), Math.Max(0, newest.Value.Size), active.Sum(x => x.Value.Speed), active.Count));
        }
        catch { }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    /// <summary>"holiday.zip.crdownload" → "holiday.zip"; Chrome's early "Unconfirmed 123456.crdownload" → "Downloading…".</summary>
    private static string FriendlyName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) && name[12..].All(char.IsDigit)) return "Downloading…";
        return name.Length > 0 ? name : "Downloading…";
    }

    /// <summary>
    /// The file's size right now, or -1 if it's gone. Asked through the file itself (not the folder listing, which
    /// Windows only updates now and then for files that are still being written), without disturbing the browser.
    /// </summary>
    private static long CurrentSize(string path)
    {
        try
        {
            using SafeFileHandle handle = CreateFileW(path, FILE_READ_ATTRIBUTES, 7 /* share read, write and delete */,
                IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (handle.IsInvalid) return File.Exists(path) ? Math.Max(0, new FileInfo(path).Length) : -1;
            return GetFileSizeEx(handle, out long size) ? size : 0;
        }
        catch { return -1; }
    }

    private const uint FILE_READ_ATTRIBUTES = 0x80;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);

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
                // Screenshots: Snipping Tool may keep the file open while its editor is showing, so only check the
                // picture can be read. Downloads: wait until nothing is writing to it any more.
                if (info.Length > 0 && info.Length == lastSize && !IsLocked(path, strict: kind == Kind.Download))
                {
                    FileReady?.Invoke(path, kind);
                    return;
                }
                lastSize = info.Length;
            }
            catch { }
        }

        // A screenshot that's been sitting there, complete, the whole time: report it anyway.
        try
        {
            if (kind == Kind.Screenshot && File.Exists(path) && new FileInfo(path).Length > 0) FileReady?.Invoke(path, kind);
        }
        catch { }
    }

    private static bool IsLocked(string path, bool strict)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                strict ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete);
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
        _progressTimer?.Dispose();
        _progressTimer = null;
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }
}
