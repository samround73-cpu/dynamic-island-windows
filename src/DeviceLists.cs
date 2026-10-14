using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace DynamicIsland;

/// <summary>A Wi-Fi network in range.</summary>
public sealed record WifiNetwork(string Ssid, int Bars, bool Secured, bool Connected, WiFiAvailableNetwork Network);

/// <summary>A paired Bluetooth device.</summary>
public sealed record BluetoothItem(string Name, string Kind, bool Connected, int? Battery, bool CanConnect);

/// <summary>The Wi-Fi networks around you, and connecting to one.</summary>
public static class WifiLister
{
    private static WiFiAdapter? _adapter;

    /// <summary>Null result means there's no Wi-Fi adapter (or Windows won't let apps use it); see Problem.</summary>
    public static string? Problem { get; private set; }

    private static async Task<WiFiAdapter?> AdapterAsync()
    {
        if (_adapter is not null) return _adapter;
        var access = await WiFiAdapter.RequestAccessAsync();
        if (access != WiFiAccessStatus.Allowed)
        {
            Problem = "Windows isn't letting apps see Wi-Fi networks. Turn on Location for desktop apps in Settings › Privacy › Location.";
            return null;
        }
        var adapters = await WiFiAdapter.FindAllAdaptersAsync();
        _adapter = adapters.FirstOrDefault();
        if (_adapter is null) Problem = "This PC doesn't have Wi-Fi.";
        return _adapter;
    }

    public static async Task<List<WifiNetwork>?> ScanAsync()
    {
        Problem = null;
        try
        {
            var adapter = await AdapterAsync();
            if (adapter is null) return null;
            await adapter.ScanAsync();

            string? connected = null;
            try
            {
                var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
                connected = profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
            }
            catch { }

            var networks = adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .Select(n => new WifiNetwork(n.Ssid, n.SignalBars,
                    n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None),
                    string.Equals(n.Ssid, connected, StringComparison.Ordinal), n))
                .OrderByDescending(n => n.Connected)
                .ThenByDescending(n => n.Bars)
                .ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase)
                .Take(20)
                .ToList();
            if (networks.Count == 0)
                Problem ??= "No networks found. If there should be some, Windows may need Location turned on for desktop apps (Settings › Privacy › Location).";
            return networks;
        }
        catch (Exception ex)
        {
            Problem = "Couldn't look for networks.";
            App.LogNote($"wifi scan failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Connects; without a password it uses one Windows already has saved. Returns a short result.</summary>
    public static async Task<(bool Ok, bool NeedsPassword)> ConnectAsync(WifiNetwork network, string? password, bool automatic)
    {
        try
        {
            var adapter = await AdapterAsync();
            if (adapter is null) return (false, false);
            var kind = automatic ? WiFiReconnectionKind.Automatic : WiFiReconnectionKind.Manual;
            WiFiConnectionResult result = password is null
                ? await adapter.ConnectAsync(network.Network, kind)
                : await adapter.ConnectAsync(network.Network, kind, new PasswordCredential { Password = password });
            return result.ConnectionStatus switch
            {
                WiFiConnectionStatus.Success => (true, false),
                WiFiConnectionStatus.InvalidCredential => (false, true),
                _ => (false, network.Secured && password is null),
            };
        }
        catch (Exception ex)
        {
            App.LogNote($"wifi connect failed: {ex.Message}");
            return (false, network.Secured && password is null);
        }
    }

    public static void Disconnect()
    {
        try { _adapter?.Disconnect(); } catch { }
    }
}

/// <summary>Your paired Bluetooth devices, and connecting or disconnecting headphones and speakers.</summary>
public static class BluetoothLister
{
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // reported by many headphones and mice

    public static async Task<List<BluetoothItem>> ListAsync()
    {
        var items = new List<BluetoothItem>();
        var audioNames = AudioEndpointNames();
        foreach (var selector in new[]
                 {
                     BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            try
            {
                var found = await DeviceInformation.FindAllAsync(selector, new[] { IsConnectedKey, BatteryKey });
                foreach (var info in found)
                {
                    if (string.IsNullOrWhiteSpace(info.Name) || items.Any(i => i.Name == info.Name)) continue;
                    bool connected = info.Properties.TryGetValue(IsConnectedKey, out var c) && c is bool b && b;
                    int? battery = info.Properties.TryGetValue(BatteryKey, out var level) && level is byte v ? v : null;
                    bool audio = audioNames.Any(n => n.Contains(info.Name, StringComparison.OrdinalIgnoreCase));
                    items.Add(new BluetoothItem(info.Name, audio ? "audio" : GuessKind(info.Name), connected, battery, audio));
                }
            }
            catch (Exception ex)
            {
                App.LogNote($"bluetooth list failed: {ex.Message}");
            }
        }
        return items.OrderByDescending(i => i.Connected).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("mx ")) return "mouse";
        if (n.Contains("keyboard") || n.Contains("keys")) return "keyboard";
        if (n.Contains("phone") || n.Contains("galaxy") || n.Contains("pixel")) return "phone";
        if (n.Contains("controller") || n.Contains("xbox") || n.Contains("dualsense")) return "gamepad";
        if (n.Contains("watch")) return "watch";
        return "device";
    }

    /// <summary>Names of the sound devices Windows knows (Bluetooth headphones show up as e.g. "Headphones (Sony WH-1000XM5)").</summary>
    private static List<string> AudioEndpointNames()
    {
        var names = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled))
            {
                try { names.Add(device.FriendlyName); } catch { }
            }
        }
        catch { }
        return names;
    }

    /// <summary>
    /// Connects or disconnects Bluetooth headphones or speakers by name. Windows has no simple switch for this, so it
    /// asks the device's sound driver directly (the same way the "Connect" button in Windows' own sound settings does).
    /// </summary>
    public static Task<bool> SetAudioConnectedAsync(string deviceName, bool connect) => Task.Run(() =>
    {
        bool any = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled))
                {
                    string name;
                    try { name = device.FriendlyName; } catch { continue; }
                    if (!name.Contains(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (BluetoothAudioControl.Send(device.ID, connect)) any = true;
                }
            }
        }
        catch (Exception ex)
        {
            App.LogNote($"bluetooth {(connect ? "connect" : "disconnect")} failed: {ex.Message}");
        }
        return any;
    });
}

/// <summary>Windows' audio driver interfaces, just enough to ask a Bluetooth sound device to connect or disconnect.</summary>
internal static class BluetoothAudioControl
{
    private static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint OneShotReconnect = 0, OneShotDisconnect = 1, KsPropertyTypeGet = 1;
    private const int ClsCtxAll = 0x17, ClsCtxInprocServer = 0x1;

    public static bool Send(string endpointId, bool connect)
    {
        try
        {
            var enumerator = (IMMDeviceEnumeratorNative)new MMDeviceEnumeratorCom();
            enumerator.GetDevice(endpointId, out var device);
            var topologyId = typeof(IDeviceTopologyNative).GUID;
            device.Activate(ref topologyId, ClsCtxAll, IntPtr.Zero, out var topologyObject);
            var topology = (IDeviceTopologyNative)topologyObject;
            topology.GetConnector(0, out var connector);
            connector.GetConnectedTo(out var connectedTo);
            var part = (IPartNative)connectedTo;
            var ksId = typeof(IKsControlNative).GUID;
            part.Activate(ClsCtxInprocServer, ref ksId, out var ksObject);
            var ks = (IKsControlNative)ksObject;

            var property = new KsPropertyData { Set = KsPropSetIdBtAudio, Id = connect ? OneShotReconnect : OneShotDisconnect, Flags = KsPropertyTypeGet };
            int hr = ks.KsProperty(ref property, (uint)Marshal.SizeOf<KsPropertyData>(), IntPtr.Zero, 0, out _);
            return hr >= 0;
        }
        catch
        {
            return false; // not a Bluetooth device, or the driver doesn't support it
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsPropertyData
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumeratorNative
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceNative device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceNative
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopologyNative
    {
        void GetConnectorCount(out uint count);
        void GetConnector(uint index, out IConnectorNative connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnectorNative
    {
        void GetConnectorType(out int type);
        void GetDataFlow(out int flow);
        void ConnectTo(IConnectorNative other);
        void Disconnect();
        void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        void GetConnectedTo([MarshalAs(UnmanagedType.IUnknown)] out object connectedTo);
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPartNative
    {
        void GetName(out IntPtr name);
        void GetLocalId(out uint id);
        void GetGlobalId(out IntPtr id);
        void GetPartType(out int type);
        void GetSubType(out Guid subType);
        void GetControlInterfaceCount(out uint count);
        void GetControlInterface(uint index, out IntPtr control);
        void EnumPartsIncoming(out IntPtr parts);
        void EnumPartsOutgoing(out IntPtr parts);
        void GetTopologyObject(out IntPtr topology);
        void Activate(int clsContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControlNative
    {
        [PreserveSig] int KsProperty(ref KsPropertyData property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
    }
}
