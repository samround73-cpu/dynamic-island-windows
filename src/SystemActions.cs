using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>Dark mode, power (lock, sleep, sign out, restart, shut down) and quick launches.</summary>
public static class SystemActions
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when Windows apps are set to dark mode.</summary>
    public static bool IsDarkMode
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Switches both Windows and apps between light and dark, exactly like the Settings page does.</summary>
    public static void SetDarkMode(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            int value = dark ? 0 : 1;
            key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
            // Tell open apps so they switch straight away.
            Native.SendMessageTimeoutString(Native.HWND_BROADCAST, Native.WM_SETTINGCHANGE, IntPtr.Zero,
                "ImmersiveColorSet", Native.SMTO_ABORTIFHUNG, 200, out _);
        }
        catch { }
    }

    public static void Lock() => Native.LockWorkStation();
    public static void Sleep() => Native.SetSuspendState(false, false, false);
    public static void SignOut() => RunShutdown("/l");
    public static void Restart() => Restart(installUpdates: true);
    public static void ShutDown() => ShutDown(installUpdates: true);

    /// <summary>Restarts; if Windows has updates ready, they're installed on the way (like "Update and restart").</summary>
    public static void Restart(bool installUpdates)
    {
        if (!Initiate(SHUTDOWN_RESTART | (installUpdates ? SHUTDOWN_INSTALL_UPDATES : 0)))
            RunShutdown("/r /t 0");
    }

    /// <summary>Shuts down; if Windows has updates ready, they're installed first (like "Update and shut down").</summary>
    public static void ShutDown(bool installUpdates)
    {
        if (!Initiate(SHUTDOWN_POWEROFF | (installUpdates ? SHUTDOWN_INSTALL_UPDATES : 0)))
            RunShutdown("/s /t 0");
    }

    // ---- InitiateShutdown, the same call the Start menu uses (it can install pending updates) ----

    private const uint SHUTDOWN_RESTART = 0x4, SHUTDOWN_POWEROFF = 0x8,
                       SHUTDOWN_INSTALL_UPDATES = 0x40;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    private static bool Initiate(uint flags)
    {
        try
        {
            EnableShutdownPrivilege();
            uint result = InitiateShutdownW(null, null, 0, flags, SHTDN_REASON_FLAG_PLANNED);
            return result == 0;
        }
        catch { return false; }
    }

    private static void EnableShutdownPrivilege()
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return;
        try
        {
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Attributes = SE_PRIVILEGE_ENABLED };
            if (LookupPrivilegeValueW(null, "SeShutdownPrivilege", out privileges.Luid))
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint InitiateShutdownW(string? machineName, string? message, uint gracePeriod, uint flags, uint reason);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint length, IntPtr previous, IntPtr returnLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static void RunShutdown(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
    }

    /// <summary>Opens a program, file, folder or settings link (e.g. "ms-settings:bluetooth").</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
