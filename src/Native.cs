using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland;

/// <summary>
/// Direct calls into Windows itself (user32, shell32 and friends).
/// Everything the island knows about windows, the taskbar and the keyboard comes through here.
/// </summary>
internal static class Native
{
    // ---- Window enumeration -------------------------------------------------------------
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    public const uint GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const int DWMWA_CLOAKED = 14;
    public const int GCLP_HICON = -14;
    public const int GCLP_HICONSM = -34;

    // ---- Window actions -----------------------------------------------------------------
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_GETICON = 0x007F;
    public const int ICON_BIG = 1;
    public const int ICON_SMALL2 = 2;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // ---- Processes ----------------------------------------------------------------------
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ---- Icons --------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref SHFILEINFO info, uint infoSize, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;

    // ---- Finding the real taskbar -------------------------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    public const int SM_CXSCREEN = 0;

    // ---- App bars (reserving screen space, taskbar auto-hide) ---------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    public const uint ABM_NEW = 0x0;
    public const uint ABM_REMOVE = 0x1;
    public const uint ABM_QUERYPOS = 0x2;
    public const uint ABM_SETPOS = 0x3;
    public const uint ABM_GETSTATE = 0x4;
    public const uint ABM_SETSTATE = 0xA;
    public const uint ABE_TOP = 1;
    public const int ABS_AUTOHIDE = 0x1;
    public const int ABN_POSCHANGED = 0x1;
    public const int ABN_FULLSCREENAPP = 0x2;

    public static APPBARDATA NewAppBarData(IntPtr hWnd) => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = hWnd,
    };

    // ---- Keyboard (Start, Search, Quick Settings, Notifications) ------------------------
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
    public const byte VK_LWIN = 0x5B;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Presses the Windows key, optionally together with a letter (e.g. Win+A).</summary>
    public static void PressWinKey(char? letter = null)
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        if (letter is char c)
        {
            byte vk = (byte)char.ToUpperInvariant(c);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Presses Ctrl+letter (Ctrl+V pastes into the app in front).</summary>
    public const byte VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    /// <summary>Presses a keyboard media key (play/pause, next, previous) — works with almost any player.</summary>
    public static void PressMediaKey(byte vk)
    {
        const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void PressCtrlKey(char letter)
    {
        const byte VK_CONTROL = 0x11;
        byte vk = (byte)char.ToUpperInvariant(letter);
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Presses Win+Ctrl+letter (Win+Ctrl+V opens the sound output picker on Windows 11).</summary>
    public static void PressWinCtrlKey(char letter)
    {
        const byte VK_CONTROL = 0x11;
        byte vk = (byte)char.ToUpperInvariant(letter);
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---- Listening for the active window changing ----------------------------------------
    public delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const int OBJID_WINDOW = 0;

    // ---- Full-screen detection and staying on top ----------------------------------------
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    // ---- Live window previews (DWM thumbnails) -------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct PSIZE { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out PSIZE size);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    public const int DWM_TNP_RECTDESTINATION = 0x01;
    public const int DWM_TNP_OPACITY = 0x04;
    public const int DWM_TNP_VISIBLE = 0x08;
    public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    // ---- Mouse position and window relationships (auto-hide) -----------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowRect(IntPtr hWnd, int attribute, out RECT value, int size);
    public const uint GA_ROOTOWNER = 3;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>True while the left or right mouse button is held down (e.g. dragging a tab or window).</summary>
    public static bool AnyMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);

    // ---- Frosted glass (acrylic) and shaped windows ------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);
    [DllImport("gdi32.dll")] public static extern IntPtr CreatePolygonRgn(POINT[] points, int count, int fillMode);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    public const int SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOZORDER = 0x0004;
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_DISABLED = 0;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    public const int WINDING = 2;
    public const int DWMWCP_DONOTROUND = 1;
    public const long WS_EX_TRANSPARENT = 0x00000020;

    // ---- Power --------------------------------------------------------------------------
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // ---- Performance stats --------------------------------------------------------------
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // ---- Telling Windows a setting changed (dark mode) ------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    public static extern IntPtr SendMessageTimeoutString(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public const uint WM_SETTINGCHANGE = 0x001A;

    // ---- Clipboard history --------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hWnd);
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    // ---- Turning Windows bitmaps into WPF images (app icons) -------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] public static extern int GetObject(IntPtr hObject, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hBitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    // ---- Battery ------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
