using System;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// The big slider on the Controls tab does volume or brightness, switched with the little toggle beside it.
/// The toggle only appears on screens whose brightness can be changed (laptops, and monitors that allow it).
/// </summary>
public partial class MainWindow
{
    private readonly BrightnessService _brightness = new();
    private bool _sliderShowsBrightness;
    private bool _brightnessAvailable;

    /// <summary>Called when the Controls tab opens: can this screen's brightness be changed, and what is it?</summary>
    private async void RefreshBrightness()
    {
        int? level = await _brightness.ReadAsync(_screenMonitor != IntPtr.Zero ? _screenMonitor : PrimaryMonitor());
        _brightnessAvailable = level is not null;
        SliderModeToggle.Visibility = _brightnessAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!_brightnessAvailable && _sliderShowsBrightness) SetSliderMode(brightness: false);
        if (_sliderShowsBrightness && level is int percent) ShowBrightnessOnSlider(percent);
        UpdateSliderModeButtons();
    }

    private void SliderMode_Click(object sender, RoutedEventArgs e) => SetSliderMode(sender == SliderBrightnessMode);

    private async void SetSliderMode(bool brightness)
    {
        _sliderShowsBrightness = brightness && _brightnessAvailable;
        UpdateSliderModeButtons();
        if (!_sliderShowsBrightness)
        {
            RefreshVolume();
            return;
        }
        ControlsVolumeGlyph.Text = "";
        int? level = await _brightness.ReadAsync(_screenMonitor != IntPtr.Zero ? _screenMonitor : PrimaryMonitor());
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
