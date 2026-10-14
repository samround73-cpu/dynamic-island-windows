using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland;

/// <summary>A place sound can play: speakers, headphones, a TV…</summary>
public sealed record AudioOutput(string Id, string Name, string Detail, bool IsDefault);

/// <summary>One app's sound (all of its audio streams together, e.g. every Chrome tab).</summary>
public sealed class AppVolume
{
    public required string Key { get; init; }
    /// <summary>The app's .exe ("" for system sounds or when Windows won't say).</summary>
    public string ExePath { get; init; } = "";
    /// <summary>Filled in on the UI thread (looking up names and icons needs it).</summary>
    public string Name { get; set; } = "App";
    public ImageSource? Icon { get; set; }
    public bool IsSystemSounds { get; init; }
    public bool Playing { get; set; }
    internal List<SimpleAudioVolume> Streams { get; } = new();

    public float Volume
    {
        get { try { return Streams.Count == 0 ? 0 : Streams.Max(s => s.Volume); } catch { return 0; } }
        set { foreach (var s in Streams) try { s.Volume = Math.Clamp(value, 0f, 1f); } catch { } }
    }

    public bool Muted
    {
        get { try { return Streams.Count > 0 && Streams.All(s => s.Mute); } catch { return false; } }
        set { foreach (var s in Streams) try { s.Mute = value; } catch { } }
    }
}

/// <summary>
/// Lists your speakers and headphones (and switches between them), and the apps making sound on the current
/// one, each with its own volume, like Windows' volume mixer. Outputs() and Apps() run on a background thread
/// (Windows' audio objects are happiest there); one at a time.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    private MMDeviceEnumerator? _created;
    private MMDeviceEnumerator _enumerator => _created ??= new MMDeviceEnumerator(); // made on the background thread
    private MMDevice? _device; // the output whose apps we last listed (kept alive so their volumes can be set)

    public List<AudioOutput> Outputs()
    {
        var list = new List<AudioOutput>();
        try
        {
            string defaultId = "";
            try
            {
                var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = current.ID;
                Release(current);
            }
            catch { }

            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    // Windows names them "Headphones (Sony WH-1000XM5)" or "Speakers (Realtek(R) Audio)".
                    string full = device.FriendlyName;
                    int bracket = full.IndexOf(" (", StringComparison.Ordinal);
                    string name = bracket > 0 ? full[..bracket] : full;
                    string detail = bracket > 0 ? full[(bracket + 2)..].TrimEnd(')') : "";
                    list.Add(new AudioOutput(device.ID, name, detail, device.ID == defaultId));
                }
                catch { }
                finally { Release(device); }
            }
        }
        catch { }
        return list.OrderByDescending(o => o.IsDefault).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Makes this the output for everything (music, games, calls). Windows has no public way to do this, so it uses
    /// the same hidden setting the Sound control panel uses (as EarTrumpet and similar tools do).
    /// </summary>
    public static bool SetDefault(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            bool ok = true;
            for (int role = 0; role <= 2; role++) // console, multimedia, communications
                ok &= policy.SetDefaultEndpoint(deviceId, role) >= 0;
            Marshal.ReleaseComObject(policy);
            return ok;
        }
        catch (Exception ex)
        {
            App.LogNote($"couldn't switch sound output: {ex.Message}");
            return false;
        }
    }

    /// <summary>The apps with sound on the current output: playing ones first, then by name.</summary>
    public List<AppVolume> Apps()
    {
        var apps = new Dictionary<string, AppVolume>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Release(_device);
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                    bool system = session.IsSystemSoundsSession;
                    uint pid = session.GetProcessID;
                    string exe = system ? "" : ExePath(pid);
                    if (!system && pid == (uint)Environment.ProcessId) continue;
                    string key = system ? "system" : exe.Length > 0 ? exe : "pid:" + pid;

                    if (!apps.TryGetValue(key, out var app))
                    {
                        string display = "";
                        try { display = session.DisplayName ?? ""; } catch { }
                        app = new AppVolume
                        {
                            Key = key,
                            ExePath = exe,
                            Name = system ? "System sounds"
                                : !string.IsNullOrWhiteSpace(display) && !display.StartsWith('@') ? display
                                : exe.Length > 0 ? Path.GetFileNameWithoutExtension(exe)
                                : "App",
                            IsSystemSounds = system,
                        };
                        apps[key] = app;
                    }
                    app.Streams.Add(session.SimpleAudioVolume);
                    if (session.State == AudioSessionState.AudioSessionStateActive) app.Playing = true;
                }
                catch { }
            }
        }
        catch { }

        return apps.Values
            .OrderBy(a => a.IsSystemSounds)
            .ThenByDescending(a => a.Playing)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void Release(object? thing)
    {
        try { (thing as IDisposable)?.Dispose(); } catch { }
    }

    private static readonly Dictionary<string, (string Name, ImageSource? Icon)> Looks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gives each app its friendly name and icon. Call on the UI thread.</summary>
    public static void Dress(IEnumerable<AppVolume> apps)
    {
        foreach (var app in apps)
        {
            if (app.ExePath.Length == 0) continue;
            if (!Looks.TryGetValue(app.ExePath, out var look))
            {
                string name = WindowTracker.GetAppName(app.ExePath);
                ImageSource? icon = null;
                try { icon = AppCatalog.GetShellImage(app.ExePath, 32); } catch { }
                look = (name, icon);
                Looks[app.ExePath] = look;
            }
            app.Name = look.Name;
            app.Icon = look.Icon;
        }
    }

    private static string ExePath(uint pid)
    {
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

    public void Dispose()
    {
        Release(_device);
        _device = null;
        Release(_created);
        _created = null;
    }

    // ---- Windows' hidden "set default device" (IPolicyConfig, Windows 7 to 11) ----

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is used; the others are listed so it sits in the right place.
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }
}
