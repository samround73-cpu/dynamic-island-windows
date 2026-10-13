using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;

namespace DynamicIsland;

/// <summary>A row in the Stats tab's "heaviest open apps" list.</summary>
public sealed record AppUsage(string Name, System.Windows.Media.ImageSource? Icon, LineIcon? Line, double CpuPercent, long Bytes, double Fraction)
{
    public bool HasLine => Line is not null;
    public string CpuText => CpuPercent < 0.05 ? "0%" : CpuPercent < 10 ? $"{CpuPercent:0.0}%" : $"{CpuPercent:0}%";
    public string MemoryText => SystemStats.FormatBytes(Bytes);
}

/// <summary>
/// Performance numbers for the Stats tab: CPU, memory, network speed and disk space.
/// Sample() is called once a second and keeps the last minute of history for the little graphs.
/// </summary>
public sealed class SystemStats
{
    public const int HistoryLength = 60;

    private long _lastIdle, _lastKernel, _lastUser;
    private long _lastReceived, _lastSent;
    private DateTime _lastNetworkSample;
    private bool _primed;

    public double CpuPercent { get; private set; }
    public double MemoryUsedGb { get; private set; }
    public double MemoryTotalGb { get; private set; }
    public double MemoryPercent { get; private set; }
    public double DownloadBytesPerSecond { get; private set; }
    public double UploadBytesPerSecond { get; private set; }

    public List<double> CpuHistory { get; } = new();
    public List<double> MemoryHistory { get; } = new();
    public List<double> DownloadHistory { get; } = new();

    public void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleNetwork();

        if (_primed)
        {
            Push(CpuHistory, CpuPercent);
            Push(MemoryHistory, MemoryPercent);
            Push(DownloadHistory, DownloadBytesPerSecond);
        }
        _primed = true;
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    private void SampleCpu()
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return;
        long idleDelta = idle - _lastIdle;
        long totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle time
        if (_lastKernel != 0 && totalDelta > 0)
            CpuPercent = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
    }

    private void SampleMemory()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) return;
        const double gb = 1024.0 * 1024 * 1024;
        MemoryTotalGb = status.ullTotalPhys / gb;
        MemoryUsedGb = (status.ullTotalPhys - status.ullAvailPhys) / gb;
        MemoryPercent = MemoryTotalGb > 0 ? 100 * MemoryUsedGb / MemoryTotalGb : 0;
    }

    private void SampleNetwork()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = adapter.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch { return; }

        var now = DateTime.UtcNow;
        double seconds = (now - _lastNetworkSample).TotalSeconds;
        if (_lastNetworkSample != default && seconds > 0)
        {
            DownloadBytesPerSecond = Math.Max(0, (received - _lastReceived) / seconds);
            UploadBytesPerSecond = Math.Max(0, (sent - _lastSent) / seconds);
        }
        _lastReceived = received;
        _lastSent = sent;
        _lastNetworkSample = now;
    }

    /// <summary>Free and total space on the drive Windows is installed on.</summary>
    public static (string Name, double FreeGb, double TotalGb)? SystemDrive()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            const double gb = 1024.0 * 1024 * 1024;
            return (root.TrimEnd('\\'), drive.AvailableFreeSpace / gb, drive.TotalSize / gb);
        }
        catch { return null; }
    }

    // Per-process CPU time from the last measurement, to work out how busy each app has been since.
    private readonly object _appLock = new();
    private Dictionary<int, TimeSpan> _cpuByProcess = new();
    private DateTime _lastAppMeasure;

    /// <summary>
    /// CPU (% of the whole PC since the last call) and memory used by each open app. Each app is given as its
    /// windows; every process with the same name as a window's process is counted (e.g. all of Chrome's).
    /// The first call can't know CPU yet, so it reports 0% until the next one.
    /// </summary>
    public List<(double Cpu, long Bytes)> MeasureApps(IReadOnlyList<IntPtr[]> windowsPerApp)
    {
        lock (_appLock)
        {
            var now = DateTime.UtcNow;
            double elapsed = _lastAppMeasure == default ? 0 : (now - _lastAppMeasure).TotalSeconds;
            _lastAppMeasure = now;

            var processes = Process.GetProcesses();
            try
            {
                var byId = new Dictionary<int, Process>();
                foreach (var p in processes) byId[p.Id] = p;
                var byName = processes
                    .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                var seen = new Dictionary<int, TimeSpan>();
                var results = new List<(double Cpu, long Bytes)>(windowsPerApp.Count);
                foreach (var windows in windowsPerApp)
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var hWnd in windows)
                    {
                        Native.GetWindowThreadProcessId(hWnd, out uint pid);
                        if (byId.TryGetValue((int)pid, out var owner)) names.Add(owner.ProcessName);
                    }

                    double cpuSeconds = 0;
                    long bytes = 0;
                    foreach (var name in names)
                    {
                        if (!byName.TryGetValue(name, out var group)) continue;
                        foreach (var p in group)
                        {
                            bytes += SafeWorkingSet(p);
                            try
                            {
                                var total = p.TotalProcessorTime;
                                seen[p.Id] = total;
                                if (_cpuByProcess.TryGetValue(p.Id, out var before) && total >= before)
                                    cpuSeconds += (total - before).TotalSeconds;
                            }
                            catch { } // e.g. apps running as administrator
                        }
                    }

                    double cpu = elapsed > 0 ? Math.Clamp(100 * cpuSeconds / elapsed / Environment.ProcessorCount, 0, 100) : 0;
                    results.Add((cpu, bytes));
                }

                _cpuByProcess = seen;
                return results;
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit <= 1 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.0} {units[unit]}";
    }

    public static string FormatSpeed(double bytesPerSecond) => FormatBytes(bytesPerSecond) + "/s";
}
