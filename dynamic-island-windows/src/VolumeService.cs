using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland;

/// <summary>
/// Reads and sets the master volume of your current speakers/headphones, tells the island whenever
/// the volume changes (including from keyboard volume keys), and handles the microphone mute.
/// Events fire on a background thread.
/// </summary>
public sealed class VolumeService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private MMDevice? _mic;

    /// <summary>(level 0..1, muted)</summary>
    public event Action<float, bool>? Changed;

    /// <summary>The microphone was muted or unmuted.</summary>
    public event Action? MicChanged;

    /// <summary>Windows switched where sound plays (e.g. headphones connected).</summary>
    public event Action? OutputDeviceChanged;

    private readonly OutputWatcher _outputWatcher;

    public VolumeService()
    {
        Attach();
        _outputWatcher = new OutputWatcher(this);
        try { _enumerator.RegisterEndpointNotificationCallback(_outputWatcher); } catch { }
    }

    /// <summary>Windows calls this whenever audio devices change; we only care about the default output switching.</summary>
    private sealed class OutputWatcher : IMMNotificationClient
    {
        private readonly VolumeService _owner;
        public OutputWatcher(VolumeService owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) _owner.OutputDeviceChanged?.Invoke();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    public bool Available => _device is not null;
    public bool MicAvailable => _mic is not null;

    /// <summary>"Speakers", "Headphones"… (the part before the brackets in Windows' device name).</summary>
    public string OutputName
    {
        get
        {
            try
            {
                string name = _device?.FriendlyName ?? "";
                int bracket = name.IndexOf(" (", StringComparison.Ordinal);
                return bracket > 0 ? name[..bracket] : name;
            }
            catch { return ""; }
        }
    }

    /// <summary>Connects to the current default speakers and microphone. Called again whenever the island
    /// expands, so plugging in headphones is picked up.</summary>
    public void Attach()
    {
        _device = Reattach(_device, DataFlow.Render, Role.Multimedia, OnVolumeNotification);
        _mic = Reattach(_mic, DataFlow.Capture, Role.Communications, OnMicNotification);
    }

    private MMDevice? Reattach(MMDevice? existing, DataFlow flow, Role role, AudioEndpointVolumeNotificationDelegate handler)
    {
        try
        {
            var current = _enumerator.GetDefaultAudioEndpoint(flow, role);
            if (existing is not null && current.ID == existing.ID)
            {
                (current as IDisposable)?.Dispose();
                return existing;
            }

            Release(existing, handler);
            current.AudioEndpointVolume.OnVolumeNotification += handler;
            return current;
        }
        catch
        {
            Release(existing, handler);
            return null;
        }
    }

    private static void Release(MMDevice? device, AudioEndpointVolumeNotificationDelegate handler)
    {
        if (device is null) return;
        try
        {
            device.AudioEndpointVolume.OnVolumeNotification -= handler;
            (device as IDisposable)?.Dispose();
        }
        catch { }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data) => Changed?.Invoke(data.MasterVolume, data.Muted);
    private void OnMicNotification(AudioVolumeNotificationData data) => MicChanged?.Invoke();

    public float Level
    {
        get { try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; } catch { return 0f; } }
        set { try { if (_device is not null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f); } catch { } }
    }

    public bool Muted
    {
        get { try { return _device?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
        set { try { if (_device is not null) _device.AudioEndpointVolume.Mute = value; } catch { } }
    }

    public bool MicMuted
    {
        get { try { return _mic?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
        set { try { if (_mic is not null) _mic.AudioEndpointVolume.Mute = value; } catch { } }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_outputWatcher); } catch { }
        Release(_device, OnVolumeNotification);
        Release(_mic, OnMicNotification);
        _device = null;
        _mic = null;
        try { (_enumerator as IDisposable)?.Dispose(); } catch { }
    }
}
