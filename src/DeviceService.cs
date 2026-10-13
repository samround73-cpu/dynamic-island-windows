using System;
using System.Collections.Generic;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicIsland;

/// <summary>
/// Notices Bluetooth devices (headphones, controllers, mice…) connecting and disconnecting.
/// USB drives and sound-output changes are picked up elsewhere (MainWindow and VolumeService).
/// The event fires on a background thread.
/// </summary>
public sealed class DeviceService
{
    private readonly Dictionary<string, string> _names = new();
    private readonly Dictionary<string, DateTime> _lastAnnounced = new();
    private readonly List<DeviceWatcher> _watchers = new();

    /// <summary>(device name, connected?)</summary>
    public event Action<string, bool>? BluetoothChanged;

    public void Start()
    {
        try
        {
            Watch(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
            Watch(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        }
        catch { /* no Bluetooth on this PC */ }
    }

    public void Stop()
    {
        foreach (var watcher in _watchers)
        {
            try { watcher.Stop(); } catch { }
        }
        _watchers.Clear();
    }

    private void Watch(string selector)
    {
        var watcher = DeviceInformation.CreateWatcher(selector);
        bool ready = false; // devices already connected at start-up shouldn't pop up

        watcher.Added += (_, info) =>
        {
            lock (_names) _names[info.Id] = info.Name;
            if (ready) Announce(info.Name, true);
        };
        watcher.Removed += (_, update) =>
        {
            string? name;
            lock (_names)
            {
                _names.TryGetValue(update.Id, out name);
                _names.Remove(update.Id);
            }
            if (ready && !string.IsNullOrWhiteSpace(name)) Announce(name, false);
        };
        watcher.Updated += (_, _) => { }; // must be subscribed for the other events to arrive
        watcher.EnumerationCompleted += (_, _) => ready = true;
        watcher.Start();
        _watchers.Add(watcher);
    }

    /// <summary>Some devices (mice, especially) flicker on and off; only announce each one every so often.</summary>
    private void Announce(string name, bool connected)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = name + (connected ? "+" : "-");
        lock (_lastAnnounced)
        {
            if (_lastAnnounced.TryGetValue(key, out var last) && DateTime.Now - last < TimeSpan.FromSeconds(20)) return;
            _lastAnnounced[key] = DateTime.Now;
        }
        BluetoothChanged?.Invoke(name, connected);
    }
}
