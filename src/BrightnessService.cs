using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicIsland;

/// <summary>
/// Screen brightness. Laptop and tablet screens are set through Windows (WMI); separate monitors through the monitor's
/// own controls over the cable (DDC/CI), which most monitors allow. Streamed screens (Shadow, Remote Desktop) and
/// monitors with DDC/CI turned off have neither, so the island hides the brightness option there.
/// Everything here runs on a background thread: monitors can take a moment to answer.
/// </summary>
public sealed class BrightnessService
{
    private enum Method { Unknown, Windows, Monitor, None }

    private Method _method = Method.Unknown;
    private IntPtr _checkedMonitor;
    private int _pending = -1;
    private int _applying;

    /// <summary>Finds out whether the brightness of this screen can be set, and what it is now (0-100). Null if it can't.</summary>
    public Task<int?> ReadAsync(IntPtr monitor) => Task.Run(() =>
    {
        lock (this)
        {
            if (monitor != _checkedMonitor) _method = Method.Unknown; // moved to another screen: ask again
            _checkedMonitor = monitor;

            if (_method is Method.Unknown or Method.Windows && ReadWindows() is int viaWindows)
            {
                _method = Method.Windows;
                return (int?)viaWindows;
            }
            if (_method is Method.Unknown or Method.Monitor && ReadMonitor(monitor) is int viaMonitor)
            {
                _method = Method.Monitor;
                return (int?)viaMonitor;
            }
            if (_method == Method.Unknown) App.LogNote("brightness: this screen doesn't let apps change its brightness");
            _method = Method.None;
            return null;
        }
    });

    /// <summary>Sets the brightness (0-100). While a slider is dragged only the latest value is sent.</summary>
    public void Set(int percent)
    {
        Interlocked.Exchange(ref _pending, Math.Clamp(percent, 0, 100));
        if (Interlocked.Exchange(ref _applying, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                int value;
                while ((value = Interlocked.Exchange(ref _pending, -1)) >= 0)
                {
                    lock (this)
                    {
                        if (_method == Method.Windows) WriteWindows(value);
                        else if (_method == Method.Monitor) WriteMonitor(_checkedMonitor, value);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _applying, 0);
            }
        });
    }

    // ---- Laptop screens: Windows' own brightness ----

    private static int? ReadWindows()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementObject item in searcher.Get())
                using (item)
                    return Convert.ToInt32(item["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    private static void WriteWindows(int percent)
    {
        try
        {
            using var methods = new ManagementClass(@"root\WMI", "WmiMonitorBrightnessMethods", null);
            foreach (ManagementObject item in methods.GetInstances())
                using (item)
                    item.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
        }
        catch (Exception ex)
        {
            App.LogNote($"brightness: couldn't set it through Windows: {ex.Message}");
        }
    }

    // ---- Separate monitors: DDC/CI ----

    private static int? ReadMonitor(IntPtr monitor)
    {
        int? result = null;
        WithPhysicalMonitor(monitor, handle =>
        {
            if (GetMonitorBrightness(handle, out uint min, out uint current, out uint max) && max > min)
                result = (int)Math.Round((current - min) * 100.0 / (max - min));
        });
        return result;
    }

    private static void WriteMonitor(IntPtr monitor, int percent)
    {
        WithPhysicalMonitor(monitor, handle =>
        {
            if (GetMonitorBrightness(handle, out uint min, out _, out uint max) && max > min)
                SetMonitorBrightness(handle, (uint)(min + Math.Round((max - min) * percent / 100.0)));
        });
    }

    private static void WithPhysicalMonitor(IntPtr monitor, Action<IntPtr> use)
    {
        try
        {
            if (monitor == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0) return;
            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return;
            try { use(monitors[0].hPhysicalMonitor); }
            finally { DestroyPhysicalMonitors(count, monitors); }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr handle, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr handle, uint brightness);
}
