using System;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// The big slider on the Controls tab does volume or brightness, switched with the little toggle beside it.
/// Laptops and most monitors change their real brightness; screens that can't (Shadow, Remote Desktop, some monitors)
/// are dimmed with a see-through layer instead (see DimOverlay).
/// </summary>
public partial class MainWindow
{
    private readonly BrightnessService _brightness = new();
    private DimOverlay? _dim;
    private bool _sliderShowsBrightness;
    private bool _realBrightness; // false: this screen is dimmed with the overlay instead

    private IntPtr BrightnessMonitor => _screenMonitor != IntPtr.Zero ? _screenMonitor : PrimaryMonitor();

    /// <summary>Called when the Controls tab opens: can this screen's real brightness be changed, and what is it?</summary>
    private async void RefreshBrightness()
    {
        int? level = await _brightness.ReadAsync(BrightnessMonitor);
        _realBrightness = level is not null;
        SliderModeToggle.Visibility = Visibility.Visible;
        SliderBrightnessMode.ToolTip = _realBrightness
            ? "Brightness"
            : "Brightness (this screen can't change its real brightness, so the picture is dimmed instead)";
        if (_sliderShowsBrightness) ShowBrightnessOnSlider(level ?? _settings.DimLevel);
        UpdateSliderModeButtons();
    }

    /// <summary>Puts the dimming back after a restart, and keeps it on the island's screen when that changes.</summary>
    private void ApplyDim()
    {
        if (_settings.DimLevel >= 100 && _dim is null) return;
        _dim ??= new DimOverlay();
        _dim.Apply(ScreenBounds, _settings.DimLevel);
    }

    private void SetBrightness(int percent)
    {
        if (_realBrightness)
        {
            _brightness.Set(percent);
            return;
        }
        _settings.DimLevel = percent;
        ApplyDim();
        // Remember it (once you've stopped dragging), so it's still dimmed after a restart.
        if (_dimSave is null)
        {
            _dimSave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _dimSave.Tick += (_, _) => { _dimSave.Stop(); _settings.Save(); };
        }
        _dimSave.Stop();
        _dimSave.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _dimSave;

    private void SliderMode_Click(object sender, RoutedEventArgs e) => SetSliderMode(sender == SliderBrightnessMode);

    private async void SetSliderMode(bool brightness)
    {
        _sliderShowsBrightness = brightness;
        UpdateSliderModeButtons();
        if (!_sliderShowsBrightness)
        {
            _settings.Save(); // remembers the dimming level
            RefreshVolume();
            return;
        }
        ShowBrightnessOnSlider(_realBrightness ? (int)ControlsVolume.Value : _settings.DimLevel);
        if (!_realBrightness) return;
        int? level = await _brightness.ReadAsync(BrightnessMonitor);
        if (_sliderShowsBrightness && level is int percent) ShowBrightnessOnSlider(percent);
    }

    private void ShowBrightnessOnSlider(int percent)
    {
        _updatingVolume = true;
        ControlsVolume.Value = percent;
        _updatingVolume = false;
        ControlsVolumeGlyph.Text = "";
        ControlsVolumeText.Text = $"{percent}%";
    }

    private void UpdateSliderModeButtons()
    {
        foreach (var (button, on) in new[] { (SliderVolumeMode, !_sliderShowsBrightness), (SliderBrightnessMode, _sliderShowsBrightness) })
        {
            if (on)
            {
                button.Background = (Brush)FindResource("AccentFaint");
                button.Foreground = (Brush)FindResource("Accent");
            }
            else
            {
                button.ClearValue(BackgroundProperty);
                button.ClearValue(ForegroundProperty);
            }
        }
    }
}
