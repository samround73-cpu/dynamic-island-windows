using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

public enum DeviceListKind { None, Wifi, Bluetooth }

/// <summary>
/// The Wi-Fi and Bluetooth lists on the Controls tab: the arrow on the Wi-Fi or Bluetooth row slides a list in
/// over the switches, where you can pick a network (typing its password if needed) or connect your headphones.
/// </summary>
public partial class MainWindow
{
    private DeviceListKind _deviceList = DeviceListKind.None;
    private string? _passwordFor;   // the Wi-Fi network whose password box is open
    private bool _deviceListBusy;

    private void ShowDeviceList(DeviceListKind kind)
    {
        // Without the radio (e.g. a Shadow PC or a desktop without Wi-Fi), Windows' own page is more useful.
        if (kind == DeviceListKind.Wifi && !_connectivity.HasWifi) { OpenAndCollapse("ms-settings:network"); return; }
        if (kind == DeviceListKind.Bluetooth && !_connectivity.HasBluetooth) { OpenAndCollapse("ms-settings:bluetooth"); return; }

        _deviceList = kind;
        _passwordFor = null;
        DeviceListTitle.Text = kind == DeviceListKind.Wifi ? "Wi-Fi" : "Bluetooth";
        DeviceListMoreText.Text = kind == DeviceListKind.Wifi ? "More Wi-Fi settings" : "Add a device or more settings";
        DeviceListSwitch.IsChecked = kind == DeviceListKind.Wifi ? _connectivity.WifiOn : _connectivity.BluetoothOn;
        SwitchList.Visibility = Visibility.Hidden; // keeps its size, so the card doesn't jump
        DeviceListPanel.Visibility = Visibility.Visible;
        LoadDeviceList();
    }

    private void HideDeviceList()
    {
        if (_deviceList == DeviceListKind.None) return;
        _deviceList = DeviceListKind.None;
        _passwordFor = null;
        DeviceListPanel.Visibility = Visibility.Collapsed;
        SwitchList.Visibility = Visibility.Visible;
    }

    private void DeviceListBack_Click(object sender, RoutedEventArgs e) => HideDeviceList();

    private void DeviceListRefresh_Click(object sender, RoutedEventArgs e) => LoadDeviceList();

    private void DeviceListMore_Click(object sender, RoutedEventArgs e) =>
        OpenAndCollapse(_deviceList == DeviceListKind.Wifi ? "ms-settings:network-wifi" : "ms-settings:bluetooth");

    private async void DeviceListSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = DeviceListSwitch.IsChecked == true;
        if (_deviceList == DeviceListKind.Wifi) _connectivity.SetWifi(on);
        else _connectivity.SetBluetooth(on);
        await System.Threading.Tasks.Task.Delay(on ? 2500 : 600); // give the radio a moment
        LoadDeviceList();
    }

    private async void LoadDeviceList()
    {
        if (_deviceListBusy || _deviceList == DeviceListKind.None) return;
        _deviceListBusy = true;
        var kind = _deviceList;
        DeviceListRows.Children.Clear();
        DeviceListRows.Children.Add(Note(kind == DeviceListKind.Wifi ? "Looking for networks…" : "Finding your devices…"));
        try
        {
            if (kind == DeviceListKind.Wifi)
            {
                bool radioOn = _connectivity.WifiOn;
                var networks = radioOn ? await WifiLister.ScanAsync() : null;
                if (_deviceList != kind) return;
                DeviceListRows.Children.Clear();
                if (!radioOn) { DeviceListRows.Children.Add(Note("Wi-Fi is off.")); return; }
                if (networks is null || networks.Count == 0)
                {
                    DeviceListRows.Children.Add(Note(WifiLister.Problem ?? "No networks found."));
                    return;
                }
                foreach (var network in networks) DeviceListRows.Children.Add(WifiRow(network));
            }
            else
            {
                bool radioOn = _connectivity.BluetoothOn;
                var devices = radioOn ? await BluetoothLister.ListAsync() : new List<BluetoothItem>();
                if (_deviceList != kind) return;
                DeviceListRows.Children.Clear();
                if (!radioOn) { DeviceListRows.Children.Add(Note("Bluetooth is off.")); return; }
                if (devices.Count == 0) { DeviceListRows.Children.Add(Note("No paired devices yet. Use \"Add a device\" below.")); return; }
                foreach (var device in devices) DeviceListRows.Children.Add(BluetoothRow(device));
            }
        }
        finally
        {
            _deviceListBusy = false;
        }
    }

    // ---- Rows ----

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = Theme.Ink(0x99),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 8, 8, 8),
    };

    /// <summary>Signal strength as four little bars.</summary>
    private static StackPanel SignalBars(int bars)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 12 };
        for (int i = 0; i < 4; i++)
        {
            panel.Children.Add(new Border
            {
                Width = 3,
                Height = 4 + i * 2.6,
                Margin = new Thickness(0, 0, 2, 0),
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Theme.Ink(i < bars ? (byte)0xE6 : (byte)0x33),
            });
        }
        return panel;
    }

    private Button SmallButton(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 10.5, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("CardButton"),
        };
        if (primary)
        {
            button.Background = (Brush)FindResource("Accent");
            button.Foreground = (Brush)FindResource("AccentText");
        }
        button.Click += (_, e) => { e.Handled = true; click(); };
        return button;
    }

    private UIElement WifiRow(WifiNetwork network)
    {
        var row = new DockPanel();
        row.Children.Add(Docked(SignalBars(network.Bars), Dock.Left, new Thickness(2, 0, 10, 0)));

        if (network.Connected)
        {
            row.Children.Add(Docked(SmallButton("Disconnect", () =>
            {
                WifiLister.Disconnect();
                DelayedReload(1500);
            }), Dock.Right));
        }
        if (network.Secured)
            row.Children.Add(Docked(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = Theme.Ink(0x80), VerticalAlignment = VerticalAlignment.Center }, Dock.Right, new Thickness(6, 0, 8, 0)));

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = network.Ssid, FontSize = 12, FontWeight = network.Connected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Theme.Ink(), TextTrimming = TextTrimming.CharacterEllipsis });
        if (network.Connected)
            texts.Children.Add(new TextBlock { Text = network.Secured ? "Connected, secured" : "Connected", FontSize = 10.5, Foreground = Theme.Ink(0x99) });
        row.Children.Add(texts);

        var body = new StackPanel();
        body.Children.Add(row);
        if (_passwordFor == network.Ssid) body.Children.Add(WifiPasswordRow(network));

        var card = RowCard(body, network.Connected);
        if (!network.Connected) card.MouseLeftButtonUp += (_, _) => ConnectWifi(network, null);
        return card;
    }

    private UIElement WifiPasswordRow(WifiNetwork network)
    {
        var box = new PasswordBox
        {
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Background = (Brush)FindResource("FieldBrush"),
            Foreground = Theme.Ink(),
            CaretBrush = Theme.Ink(),
            BorderBrush = Theme.Ink(0x26),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Network password",
        };
        var automatic = new CheckBox
        {
            Content = new TextBlock { Text = "Connect automatically", FontSize = 10.5, Foreground = Theme.Ink(0x99) },
            IsChecked = true,
            Margin = new Thickness(0, 6, 0, 0),
        };
        void Go()
        {
            if (box.Password.Length > 0) ConnectWifi(network, box.Password, automatic.IsChecked == true);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Go(); } };

        var line = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        line.Children.Add(Docked(SmallButton("Connect", Go, primary: true), Dock.Right, new Thickness(8, 0, 0, 0)));
        line.Children.Add(box);

        var panel = new StackPanel();
        panel.Children.Add(line);
        panel.Children.Add(automatic);
        panel.MouseLeftButtonUp += (_, e) => e.Handled = true; // typing here shouldn't count as clicking the network
        Dispatcher.BeginInvoke(new Action(() => box.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        return panel;
    }

    private async void ConnectWifi(WifiNetwork network, string? password, bool automatic = true)
    {
        DeviceListTitle.Text = $"Connecting to {network.Ssid}…";
        var (ok, needsPassword) = await WifiLister.ConnectAsync(network, password, automatic);
        DeviceListTitle.Text = "Wi-Fi";
        if (ok)
        {
            _passwordFor = null;
            DelayedReload(800);
            return;
        }
        if (needsPassword)
        {
            _passwordFor = network.Ssid; // open (or keep open) the password box for this one
            LoadDeviceList();
            if (password is not null) DeviceListTitle.Text = "Wrong password? Try again";
            return;
        }
        DeviceListTitle.Text = "Couldn't connect";
    }

    private UIElement BluetoothRow(BluetoothItem device)
    {
        string glyph = device.Kind switch
        {
            "audio" => "",    // headphones
            "mouse" => "",
            "keyboard" => "",
            "phone" => "",
            "gamepad" => "",
            _ => "",          // Bluetooth
        };
        var circle = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = device.Connected ? (Brush)FindResource("Accent") : (Brush)FindResource("TileOffBrush"),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 13,
                Foreground = device.Connected ? (Brush)FindResource("AccentText") : Theme.Ink(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var row = new DockPanel();
        row.Children.Add(Docked(circle, Dock.Left, new Thickness(0, 0, 10, 0)));
        if (device.CanConnect)
        {
            row.Children.Add(Docked(SmallButton(device.Connected ? "Disconnect" : "Connect", async () =>
            {
                DeviceListTitle.Text = device.Connected ? "Disconnecting…" : $"Connecting {device.Name}…";
                bool ok = await BluetoothLister.SetAudioConnectedAsync(device.Name, !device.Connected);
                DeviceListTitle.Text = ok ? "Bluetooth" : "Couldn't do that here; try Windows' settings";
                DelayedReload(ok ? 2500 : 0);
            }), Dock.Right));
        }
        if (device.Battery is int battery)
            row.Children.Add(Docked(new TextBlock { Text = $"{battery}%", FontSize = 10.5, Foreground = Theme.Ink(0x99), VerticalAlignment = VerticalAlignment.Center }, Dock.Right, new Thickness(6, 0, 8, 0)));

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = device.Name, FontSize = 12, Foreground = Theme.Ink(), TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = device.Connected ? "Connected" : "Paired", FontSize = 10.5, Foreground = Theme.Ink(0x99) });
        row.Children.Add(texts);
        return RowCard(row, false);
    }

    private Border RowCard(UIElement content, bool highlighted)
    {
        var card = new Border
        {
            Child = content,
            Padding = new Thickness(8, 6, 6, 6),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand,
            Background = highlighted ? (Brush)FindResource("AccentFaint") : Brushes.Transparent,
            BorderBrush = highlighted ? (Brush)FindResource("Accent") : Brushes.Transparent,
            BorderThickness = new Thickness(highlighted ? 1 : 0),
        };
        if (!highlighted)
        {
            card.MouseEnter += (_, _) => card.Background = Theme.Ink(0x10);
            card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        }
        return card;
    }

    private static T Docked<T>(T element, Dock dock, Thickness? margin = null) where T : FrameworkElement
    {
        DockPanel.SetDock(element, dock);
        if (margin is Thickness m) element.Margin = m;
        return element;
    }

    private async void DelayedReload(int milliseconds)
    {
        if (milliseconds > 0) await System.Threading.Tasks.Task.Delay(milliseconds);
        LoadDeviceList();
    }
}
