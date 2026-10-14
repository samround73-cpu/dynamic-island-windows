using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// The Sound page on the Controls tab (the arrow on the Sound row): pick speakers, headphones or TV on the left,
/// and set each app's volume on the right.
/// </summary>
public partial class MainWindow
{
    private readonly AudioMixer _mixer = new();
    private DispatcherTimer? _mixerTimer;
    private Dictionary<string, AppVolume> _mixerApps = new();
    private readonly Dictionary<string, MixerRow> _mixerRows = new();
    private string _mixerLayout = "";
    private bool _mixerUpdating;

    private sealed record MixerRow(Slider Slider, TextBlock Percent, TextBlock MuteGlyph);

    // ---- Outputs (left) ----

    private bool _mixerBusy;

    private async void LoadSoundOutputs()
    {
        DeviceListTitle.Text = "Sound";
        DeviceListSwitch.IsChecked = _volume.Available && !_volume.Muted;
        var outputs = await System.Threading.Tasks.Task.Run(() => { lock (_mixer) return _mixer.Outputs(); });
        if (_deviceList != DeviceListKind.Sound) return;
        DeviceListRows.Children.Clear();
        if (outputs.Count == 0)
        {
            DeviceListRows.Children.Add(Note("No speakers or headphones found."));
            return;
        }
        DeviceListRows.Children.Add(new TextBlock
        {
            Text = "PLAY SOUND THROUGH",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Ink(0x80),
            Margin = new Thickness(6, 0, 0, 6),
        });
        foreach (var output in outputs) DeviceListRows.Children.Add(OutputRow(output));
    }

    private static string OutputGlyph(AudioOutput output)
    {
        string text = (output.Name + " " + output.Detail).ToLowerInvariant();
        if (text.Contains("headphone") || text.Contains("headset") || text.Contains("earbud") || text.Contains("airpods") || text.Contains("hands-free"))
            return "";
        if (text.Contains("tv") || text.Contains("hdmi") || text.Contains("display") || text.Contains("monitor"))
            return "";
        return "";
    }

    private UIElement OutputRow(AudioOutput output)
    {
        var circle = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = output.IsDefault ? (Brush)FindResource("Accent") : (Brush)FindResource("TileOffBrush"),
            Child = new TextBlock
            {
                Text = OutputGlyph(output),
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 13,
                Foreground = output.IsDefault ? (Brush)FindResource("AccentText") : Theme.Ink(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var row = new DockPanel();
        row.Children.Add(Docked(circle, Dock.Left, new Thickness(0, 0, 10, 0)));
        if (output.IsDefault)
        {
            row.Children.Add(Docked(new TextBlock
            {
                Text = "", // tick
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Foreground = (Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
            }, Dock.Right, new Thickness(6, 0, 4, 0)));
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = output.Name, FontSize = 12, FontWeight = output.IsDefault ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Theme.Ink(), TextTrimming = TextTrimming.CharacterEllipsis });
        if (output.Detail.Length > 0)
            texts.Children.Add(new TextBlock { Text = output.Detail, FontSize = 10.5, Foreground = Theme.Ink(0x99), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(texts);

        var card = RowCard(row, output.IsDefault);
        card.ToolTip = output.IsDefault ? "Sound plays here" : "Play sound here";
        if (!output.IsDefault) card.MouseLeftButtonUp += (_, _) => SwitchOutput(output);
        return card;
    }

    private async void SwitchOutput(AudioOutput output)
    {
        DeviceListTitle.Text = $"Switching to {output.Name}…";
        bool ok = await System.Threading.Tasks.Task.Run(() => AudioMixer.SetDefault(output.Id));
        await System.Threading.Tasks.Task.Delay(350); // let Windows settle on the new device
        if (_deviceList != DeviceListKind.Sound) return;
        _volume.Attach();
        RefreshVolume();
        RefreshControls();
        LoadSoundOutputs();
        if (!ok) DeviceListTitle.Text = "Couldn't switch; try More sound settings";
        _mixerLayout = ""; // the apps are now on a different device
        RefreshAppVolumes();
    }

    // ---- App volumes (right) ----

    private void ShowAppVolumes()
    {
        ControlsRight.Visibility = Visibility.Hidden; // keeps its size, so the tab doesn't jump
        AppVolumesCard.Visibility = Visibility.Visible;
        _mixerLayout = "";
        RefreshAppVolumes();
        if (_mixerTimer is null)
        {
            _mixerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            _mixerTimer.Tick += (_, _) =>
            {
                if (_deviceList != DeviceListKind.Sound || !_expanded) { _mixerTimer.Stop(); return; }
                RefreshAppVolumes();
            };
        }
        _mixerTimer.Start();
    }

    private void HideAppVolumes()
    {
        _mixerTimer?.Stop();
        AppVolumesCard.Visibility = Visibility.Collapsed;
        ControlsRight.Visibility = Visibility.Visible;
        AppVolumeRows.Children.Clear();
        _mixerRows.Clear();
        _mixerApps.Clear();
    }

    private async void RefreshAppVolumes()
    {
        // Don't move things around while a slider is being dragged.
        if (_mixerBusy || (Mouse.LeftButton == MouseButtonState.Pressed && AppVolumeRows.IsMouseOver)) return;

        _mixerBusy = true;
        List<AppVolume> apps;
        try { apps = await System.Threading.Tasks.Task.Run(() => { lock (_mixer) return _mixer.Apps(); }); }
        finally { _mixerBusy = false; }
        if (_deviceList != DeviceListKind.Sound) return;
        AudioMixer.Dress(apps);
        _mixerApps = apps.ToDictionary(a => a.Key);
        string layout = string.Join("|", apps.Select(a => a.Key + (a.Playing ? "+" : "")));

        if (layout != _mixerLayout)
        {
            _mixerLayout = layout;
            AppVolumeRows.Children.Clear();
            _mixerRows.Clear();
            if (apps.Count == 0)
            {
                AppVolumeRows.Children.Add(Note("Nothing is using sound right now. Apps show up here when they play something."));
                return;
            }
            foreach (var app in apps) AppVolumeRows.Children.Add(AppVolumeRow(app));
            return;
        }

        // Same apps: just follow any changes made elsewhere (e.g. in the app itself).
        _mixerUpdating = true;
        try
        {
            foreach (var app in apps)
                if (_mixerRows.TryGetValue(app.Key, out var row)) ShowAppLevel(row, app);
        }
        finally { _mixerUpdating = false; }
    }

    private void ShowAppLevel(MixerRow row, AppVolume app)
    {
        double percent = Math.Round(app.Volume * 100);
        if (Math.Abs(row.Slider.Value - percent) > 0.5) row.Slider.Value = percent;
        bool muted = app.Muted;
        row.Percent.Text = muted ? "Off" : $"{percent:0}%";
        row.MuteGlyph.Text = muted ? "" : "";
        row.Slider.Opacity = muted ? 0.45 : 1;
    }

    private UIElement AppVolumeRow(AppVolume app)
    {
        UIElement picture = app.Icon is not null
            ? new Image { Source = app.Icon, Width = 16, Height = 16 }
            : new TextBlock
            {
                Text = app.IsSystemSounds ? "" : "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 13,
                Foreground = Theme.Ink(0xBB),
            };
        if (picture is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.VerticalAlignment = VerticalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(fe, BitmapScalingMode.HighQuality);
        }
        var iconBox = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("FieldBrush"),
            Child = picture,
        };

        var muteGlyph = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var mute = new Button
        {
            Style = (Style)FindResource("IslandButton"),
            Width = 26,
            Height = 26,
            Content = muteGlyph,
            ToolTip = "Mute or unmute",
            VerticalAlignment = VerticalAlignment.Center,
        };

        var percent = new TextBlock
        {
            Width = 34,
            FontSize = 10.5,
            TextAlignment = TextAlignment.Right,
            Foreground = Theme.Ink(0x99),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var slider = new Slider
        {
            Style = (Style)FindResource("SlimSlider"),
            Minimum = 0,
            Maximum = 100,
            SmallChange = 2,
            LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
        };
        if (app.Playing) slider.Foreground = (Brush)FindResource("Accent");

        var name = new TextBlock
        {
            Text = app.Name,
            Width = 96,
            FontSize = 11.5,
            Foreground = Theme.Ink(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = app.Playing ? $"{app.Name} · playing" : app.Name,
        };

        var row = new DockPanel { Margin = new Thickness(2, 2, 0, 2) };
        row.Children.Add(Docked(iconBox, Dock.Left, new Thickness(0, 0, 9, 0)));
        row.Children.Add(Docked(name, Dock.Left));
        row.Children.Add(Docked(mute, Dock.Right, new Thickness(2, 0, 0, 0)));
        row.Children.Add(Docked(percent, Dock.Right));
        row.Children.Add(slider);

        var parts = new MixerRow(slider, percent, muteGlyph);
        _mixerRows[app.Key] = parts;
        _mixerUpdating = true;
        try { ShowAppLevel(parts, app); }
        finally { _mixerUpdating = false; }

        string key = app.Key;
        slider.ValueChanged += (_, e) =>
        {
            if (_mixerUpdating || !_mixerApps.TryGetValue(key, out var current)) return;
            current.Volume = (float)(e.NewValue / 100.0);
            if (current.Muted && e.NewValue > 0) current.Muted = false;
            _mixerUpdating = true;
            try { ShowAppLevel(parts, current); }
            finally { _mixerUpdating = false; }
        };
        mute.Click += (_, e) =>
        {
            e.Handled = true;
            if (!_mixerApps.TryGetValue(key, out var current)) return;
            current.Muted = !current.Muted;
            _mixerUpdating = true;
            try { ShowAppLevel(parts, current); }
            finally { _mixerUpdating = false; }
        };
        return row;
    }
}
