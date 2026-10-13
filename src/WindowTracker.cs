using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>One top-level window that would normally have a taskbar button.</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ExePath { get; init; } = "";
    /// <summary>Windows with the same key are grouped under one icon (normally the same .exe).</summary>
    public string GroupKey { get; init; } = "";
    public string AppName { get; init; } = "";
}

/// <summary>
/// Works out which windows are "open apps" (the same rules the real taskbar uses),
/// what they're called, what their icon is, and switches between them.
/// </summary>
public static class WindowTracker
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All taskbar-worthy windows, most recently used first.</summary>
    public static List<WindowInfo> GetTaskbarWindows()
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows((hWnd, _) =>
        {
            if (!IsTaskbarWindow(hWnd)) return true;

            string exe = GetExePath(hWnd);
            string title = GetTitle(hWnd);

            // Store apps (Settings, Calculator…) all run inside "ApplicationFrameHost.exe",
            // so grouping them by .exe would lump them together. Group those by title instead.
            bool isStoreApp = exe.Length == 0 || exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

            result.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ExePath = exe,
                GroupKey = isStoreApp ? "window:" + title : exe,
                AppName = isStoreApp ? title : GetAppName(exe),
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Roughly the same test Windows uses to decide whether a window gets a taskbar button.</summary>
    public static bool IsTaskbarWindow(IntPtr hWnd)
    {
        if (!Native.IsWindowVisible(hWnd)) return false;
        if (IsOwnWindow(hWnd)) return false;

        long exStyle = Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE).ToInt64();
        bool forcedOnTaskbar = (exStyle & Native.WS_EX_APPWINDOW) != 0;
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 && !forcedOnTaskbar) return false;
        if (Native.GetWindow(hWnd, Native.GW_OWNER) != IntPtr.Zero && !forcedOnTaskbar) return false;
        if (Native.GetWindowTextLength(hWnd) == 0) return false;

        // "Cloaked" windows are suspended Store apps and windows on other virtual desktops.
        if (Native.DwmGetWindowAttribute(hWnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        string cls = GetClassName(hWnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        return true;
    }

    public static bool IsOwnWindow(IntPtr hWnd)
    {
        Native.GetWindowThreadProcessId(hWnd, out uint pid);
        return pid == OwnProcessId;
    }

    public static string GetTitle(IntPtr hWnd)
    {
        int length = Native.GetWindowTextLength(hWnd);
        if (length <= 0) return "";
        var sb = new StringBuilder(length + 1);
        Native.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// True only for a genuine full-screen app (a game, a video, F11 in a browser) on the main monitor.
    /// Windows' own "full-screen app" signal also fires when the desktop is in front, which is what made
    /// the island vanish when every app was minimised — so we check for ourselves instead.
    /// </summary>
    /// <summary>Start, search, flyouts and the taskbar itself: briefly in front, but they shouldn't change how the island behaves.</summary>
    public static bool IsTransientShellWindow(IntPtr hWnd)
    {
        string cls = GetClassName(hWnd);
        return cls is "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
            or "#32768"; // context menus
    }

    /// <summary>
    /// The app window that's highest on screen (in front of the others) on the main monitor,
    /// ignoring minimised windows, the island itself, the desktop and the taskbar. Zero if there is none.
    /// </summary>
    public static IntPtr TopAppWindowOnPrimary()
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hWnd, _) =>
        {
            // EnumWindows goes from the top of the stack downwards.
            if (!IsTaskbarWindow(hWnd) || Native.IsIconic(hWnd)) return true;
            var monitor = Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(monitor, ref info) || (info.dwFlags & Native.MONITORINFOF_PRIMARY) == 0) return true;
            found = hWnd;
            return false; // stop: this is the one
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The app window that's directly under the given area of the screen (the island's spot): the first
    /// non-minimised app window, from the top of the stack down, that overlaps it. Zero if only the desktop is there.
    /// </summary>
    internal static IntPtr TopAppWindowUnder(Native.RECT area)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hWnd, _) =>
        {
            // EnumWindows goes from the top of the stack downwards.
            if (!IsTaskbarWindow(hWnd) || Native.IsIconic(hWnd)) return true;
            if (Native.DwmGetWindowRect(hWnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect,
                    System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0 &&
                !Native.GetWindowRect(hWnd, out rect))
                return true;
            bool overlaps = rect.Left < area.Right && rect.Right > area.Left && rect.Top < area.Bottom && rect.Bottom > area.Top;
            if (!overlaps) return true;
            found = hWnd;
            return false; // stop: this is the one
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The desktop (wallpaper) or the taskbar is what's "in front".</summary>
    public static bool IsDesktopOrTaskbar(IntPtr hWnd)
    {
        string cls = GetClassName(hWnd);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>True when the app in front is maximised on the main screen (and not minimised).</summary>
    public static bool IsMaximizedOnPrimary(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || IsOwnWindow(hWnd) || !Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd)) return false;
        if (!Native.IsZoomed(hWnd)) return false;
        var monitor = Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        return Native.GetMonitorInfo(monitor, ref info) && (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
    }

    /// <summary>
    /// True when the app in front is maximised, or snapped so its top edge is at the top of the main screen
    /// right where the island sits. In that case the island tucks away so it doesn't cover the app.
    /// </summary>
    /// <param name="islandLeft">Left edge of the island's area, in screen pixels.</param>
    /// <param name="islandRight">Right edge of the island's area, in screen pixels.</param>
    public static bool IsUnderIsland(IntPtr hWnd, int islandLeft, int islandRight)
    {
        if (hWnd == IntPtr.Zero || IsOwnWindow(hWnd) || !Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd)) return false;

        string cls = GetClassName(hWnd);
        if (cls is "Progman" or "WorkerW") return false;

        var monitor = Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info) || (info.dwFlags & Native.MONITORINFOF_PRIMARY) == 0) return false;

        if (Native.IsZoomed(hWnd)) return true;

        // The visible edges of the window (GetWindowRect includes invisible resize borders).
        if (Native.DwmGetWindowRect(hWnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect,
                System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0 &&
            !Native.GetWindowRect(hWnd, out rect))
            return false;

        int screenTop = info.rcMonitor.Top;
        return rect.Top <= screenTop + 4 && rect.Bottom > screenTop + 60
               && rect.Left < islandRight && rect.Right > islandLeft;
    }

    public static bool IsFullScreenOnPrimary(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || IsOwnWindow(hWnd) || !Native.IsWindowVisible(hWnd)) return false;
        if (Native.IsZoomed(hWnd)) return false; // an ordinary maximised window

        string cls = GetClassName(hWnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow") // desktop, Start, search, flyouts
            return false;

        if (!Native.GetWindowRect(hWnd, out var rect)) return false;

        var monitor = Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return false;
        if ((info.dwFlags & Native.MONITORINFOF_PRIMARY) == 0) return false;

        var screen = info.rcMonitor;
        bool coversScreen = rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
        if (!coversScreen) return false;

        // Screenshot tools cover the whole screen with a see-through overlay while you pick an area;
        // that's not a game or a video, so the island shouldn't vanish for it.
        string exe = System.IO.Path.GetFileNameWithoutExtension(GetExePath(hWnd));
        return !ScreenCaptureTools.Contains(exe);
    }

    private static readonly HashSet<string> ScreenCaptureTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ScreenClippingHost", "SnippingTool", "ScreenSketch", "ShareX", "Greenshot", "Lightshot", "Flameshot",
        "SnagitCapture", "Snagit32", "PicPick", "Screenpresso", "ScreenToGif",
    };

    public static string GetExePath(IntPtr hWnd)
    {
        Native.GetWindowThreadProcessId(hWnd, out uint pid);
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Native.QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// <summary>The friendly name from the .exe ("Google Chrome" rather than "chrome").</summary>
    public static string GetAppName(string exePath)
    {
        if (exePath.Length == 0) return "App";
        if (NameCache.TryGetValue(exePath, out var cached)) return cached;

        string name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
        }
        catch { /* some system files can't be read; the file name is fine */ }

        NameCache[exePath] = name;
        return name;
    }

    /// <summary>Icon for a window: the .exe's icon for normal apps, the window's own icon for Store apps.</summary>
    public static ImageSource? GetIcon(WindowInfo window)
    {
        bool useExeIcon = !window.GroupKey.StartsWith("window:", StringComparison.Ordinal);
        string cacheKey = useExeIcon ? window.ExePath : "hwnd:" + window.Handle.ToInt64();
        if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;

        ImageSource? icon = useExeIcon ? GetFileIcon(window.ExePath) : null;
        icon ??= GetWindowIcon(window.Handle);
        if (icon is null && window.ExePath.Length > 0) icon = GetFileIcon(window.ExePath);

        IconCache[cacheKey] = icon;
        return icon;
    }

    public static ImageSource? GetIcon(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsTaskbarWindow(hWnd)) return null;
        string exe = GetExePath(hWnd);
        bool isStoreApp = exe.Length == 0 || exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        return GetIcon(new WindowInfo { Handle = hWnd, ExePath = exe, GroupKey = isStoreApp ? "window:" : exe });
    }

    private static ImageSource? GetWindowIcon(IntPtr hWnd)
    {
        IntPtr hIcon = IntPtr.Zero;
        if (Native.SendMessageTimeout(hWnd, Native.WM_GETICON, new IntPtr(Native.ICON_BIG), IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 100, out var big) != IntPtr.Zero)
            hIcon = big;
        if (hIcon == IntPtr.Zero &&
            Native.SendMessageTimeout(hWnd, Native.WM_GETICON, new IntPtr(Native.ICON_SMALL2), IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 100, out var small) != IntPtr.Zero)
            hIcon = small;
        if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hWnd, Native.GCLP_HICON);
        if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hWnd, Native.GCLP_HICONSM);
        if (hIcon == IntPtr.Zero) return null;

        // These icons belong to the other app, so we copy them but never destroy them.
        return IconToImage(hIcon);
    }

    /// <summary>Icon for any file, folder or shortcut, as Explorer would show it.</summary>
    public static ImageSource? GetFileIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info),
            Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
        if (info.hIcon == IntPtr.Zero) return null;
        try { return IconToImage(info.hIcon); }
        finally { Native.DestroyIcon(info.hIcon); }
    }

    private static ImageSource? IconToImage(IntPtr hIcon)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    // ---- Actions ------------------------------------------------------------------------

    public static void Activate(IntPtr hWnd)
    {
        if (Native.IsIconic(hWnd)) Native.ShowWindow(hWnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hWnd);
    }

    public static void Minimize(IntPtr hWnd) => Native.ShowWindow(hWnd, Native.SW_MINIMIZE);

    /// <summary>
    /// Asks the window to close, exactly like clicking its X (so apps can still ask "save changes?").
    /// Returns false if Windows refused because the app runs as administrator (e.g. Task Manager):
    /// normal apps aren't allowed to control those.
    /// </summary>
    public static bool Close(IntPtr hWnd)
    {
        if (Native.PostMessage(hWnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) return true;
        const int ERROR_ACCESS_DENIED = 5;
        return System.Runtime.InteropServices.Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED;
    }
}
