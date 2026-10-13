using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

/// <summary>
/// An iPhone-style "slide to power off" control: drag the round knob all the way to the right to confirm.
/// Letting go early slides it back, so restart and shut down can't happen by accident.
/// </summary>
public sealed class SlideToConfirm : Border
{
    private const double KnobSize = 42, Inset = 5;

    private readonly Border _knob;
    private readonly Border _trail;
    private readonly TextBlock _label;
    private readonly TextBlock _glyph;
    private readonly TranslateTransform _move = new();
    private double _grabOffset;
    private bool _dragging;

    /// <summary>The knob reached the end.</summary>
    public event Action? Confirmed;

    public SlideToConfirm()
    {
        Height = KnobSize + Inset * 2;
        CornerRadius = new CornerRadius(Height / 2);
        SetResourceReference(BackgroundProperty, "FieldBrush");

        _glyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _knob = new Border
        {
            Width = KnobSize,
            Height = KnobSize,
            CornerRadius = new CornerRadius(KnobSize / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(Inset, 0, 0, 0),
            Cursor = Cursors.SizeWE,
            RenderTransform = _move,
            Child = _glyph,
        };

        // A soft coloured trail that grows behind the knob as you drag.
        _trail = new Border
        {
            Height = KnobSize,
            Width = KnobSize,
            CornerRadius = new CornerRadius(KnobSize / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(Inset, 0, 0, 0),
            Opacity = 0.22,
        };

        _label = new TextBlock
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(KnobSize + Inset, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };

        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(_trail);
        grid.Children.Add(_label);
        grid.Children.Add(_knob);
        Child = grid;

        _knob.MouseLeftButtonDown += OnKnobDown;
        _knob.MouseMove += OnKnobMove;
        _knob.MouseLeftButtonUp += OnKnobUp;
        _knob.LostMouseCapture += (_, _) => { if (_dragging) Release(); };
        // Clicking the track (not the knob) gives a little nudge to show it needs sliding.
        MouseLeftButtonUp += (_, e) => { if (!_dragging && e.OriginalSource != _knob) Nudge(); };
    }

    public string Text { get => _label.Text; set => _label.Text = value; }
    public string Glyph { get => _glyph.Text; set => _glyph.Text = value; }

    /// <summary>The knob's colour, and the dark colour of the icon on it.</summary>
    public void SetColors(Brush knob, Brush icon)
    {
        _knob.Background = knob;
        _trail.Background = knob;
        _glyph.Foreground = icon;
    }

    private double MaxTravel => Math.Max(0, ActualWidth - KnobSize - Inset * 2);

    private void OnKnobDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _grabOffset = e.GetPosition(this).X - _move.X;
        StopAnimations();
        _knob.CaptureMouse();
        e.Handled = true;
    }

    private void OnKnobMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        SetPosition(Math.Clamp(e.GetPosition(this).X - _grabOffset, 0, MaxTravel));
    }

    private void OnKnobUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        bool done = MaxTravel > 0 && _move.X >= MaxTravel * 0.9;
        Release();
        if (done)
        {
            SetPosition(MaxTravel);
            Confirmed?.Invoke();
            // Slide back afterwards, in case the action is cancelled (e.g. an app blocks shutdown).
            var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            reset.Tick += (_, _) => { reset.Stop(); AnimateTo(0); };
            reset.Start();
        }
        else
        {
            AnimateTo(0);
        }
    }

    private void Release()
    {
        _dragging = false;
        if (_knob.IsMouseCaptured) _knob.ReleaseMouseCapture();
    }

    private void SetPosition(double x)
    {
        StopAnimations();
        _move.X = x;
        _trail.Width = KnobSize + x;
        _label.Opacity = MaxTravel > 0 ? Math.Clamp(1 - x / (MaxTravel * 0.6), 0, 1) : 1;
    }

    private void StopAnimations()
    {
        _move.BeginAnimation(TranslateTransform.XProperty, null);
        _trail.BeginAnimation(WidthProperty, null);
        _label.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>Resets straight away (used when the tab is shown).</summary>
    public void Reset() => SetPosition(0);

    private void AnimateTo(double x)
    {
        var duration = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        double from = _move.X;
        StopAnimations();
        _move.X = from;
        _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, x, duration) { EasingFunction = ease });
        _trail.BeginAnimation(WidthProperty, new DoubleAnimation(KnobSize + from, KnobSize + x, duration) { EasingFunction = ease });
        _label.BeginAnimation(OpacityProperty, new DoubleAnimation(x <= 0 ? 1 : 0, duration));
    }

    private void Nudge()
    {
        var bump = new DoubleAnimationUsingKeyFrames();
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(22, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(480)), new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
        _move.BeginAnimation(TranslateTransform.XProperty, bump);
        _trail.BeginAnimation(WidthProperty, null);
        _trail.Width = KnobSize;
    }
}
