using System;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace DynamicIsland;

/// <summary>
/// Wi-Fi and Bluetooth on/off switches (the same ones as in Quick Settings), plus the name of the network you're on.
/// Changed fires on a background thread.
/// </summary>
public sealed class ConnectivityService
{
    private Radio? _wifi;
    private Radio? _bluetooth;

    public event Action? Changed;

    public bool HasWifi => _wifi is not null;
    public bool HasBluetooth => _bluetooth is not null;
    public bool WifiOn => _wifi?.State == RadioState.On;
    public bool BluetoothOn => _bluetooth?.State == RadioState.On;

    public async Task InitAsync()
    {
        try
        {
            NetworkInformation.NetworkStatusChanged += _ => Changed?.Invoke();

            var access = await Radio.RequestAccessAsync();
            if (access == RadioAccessStatus.Allowed)
            {
                foreach (var radio in await Radio.GetRadiosAsync())
                {
                    if (radio.Kind == RadioKind.WiFi && _wifi is null) _wifi = radio;
                    else if (radio.Kind == RadioKind.Bluetooth && _bluetooth is null) _bluetooth = radio;
                    else continue;
                    radio.StateChanged += (_, _) => Changed?.Invoke();
                }
            }
        }
        catch
        {
            // No radios (e.g. a desktop PC without Wi-Fi) - the tiles just open Settings instead.
        }
        Changed?.Invoke();
    }

    public async void SetWifi(bool on)
    {
        try { if (_wifi is not null) await _wifi.SetStateAsync(on ? RadioState.On : RadioState.Off); } catch { }
    }

    public async void SetBluetooth(bool on)
    {
        try { if (_bluetooth is not null) await _bluetooth.SetStateAsync(on ? RadioState.On : RadioState.Off); } catch { }
    }

    /// <summary>The network you're connected to ("Home 5G"), whether it's Wi-Fi, or null when offline.</summary>
    public static (string Name, bool IsWifi)? CurrentNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return null;
            var level = profile.GetNetworkConnectivityLevel();
            if (level == NetworkConnectivityLevel.None) return null;
            return (profile.ProfileName ?? "Connected", profile.IsWlanConnectionProfile);
        }
        catch { return null; }
    }
}
