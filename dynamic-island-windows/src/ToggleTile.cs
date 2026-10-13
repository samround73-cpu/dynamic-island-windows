using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicIsland;

/// <summary>
/// One row of the Controls tab, laid out like a settings switch:
/// [icon]  Title / subtitle  ........  [switch]  [›]
/// Click anywhere on the row to flip the switch; click the arrow on the right for more options.
/// </summary>
public sealed class ToggleTile : Border
{
    /// <summary>The current accent colour (changed from Settings).</summary>
    public static Brush Accent { get; private set; } = Frozen(Color.FromRgb(0xD6, 0xB4, 0x6A));
    public static readonly Brush AccentText = Frozen(Color.FromRgb(0x1F, 0x1A, 0x0E));

    private static readonly List<ToggleTile> AllTiles = new();

    /// <summary>Switches every row to a new accent colour.</summary>
    public static void SetAccent(Color color)
    {
        Accent = Frozen(color);
        foreach (var tile in AllTiles) tile.Apply(animate: false);
    }

    private static Brush OffBackground = Frozen(Color.FromRgb(0x26, 0x26, 0x26));

    /// <summary>The colour of the icon circle when a row is off (translucent in the Glass style).</summary>
    public static void SetOffBackground(Color color)
    {
        OffBackground = Frozen(color);
        foreach (var tile in AllTiles) tile.Apply(animate: false);
    }

    private static readonly Brush Subtle = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TrackOff = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Hover = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MoreHover = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private const double TrackWidth = 36, TrackHeight = 20, KnobSize = 14, KnobInset = 3;
    private const double KnobTravel = TrackWidth - KnobSize - KnobInset * 2;

    private readonly TextBlock _glyph;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly Border _circle;
    private readonly Border _track;
    private readonly TranslateTransform _knobMove = new();
    private bool _isOn;
    private bool _applied;

    /// <summary>The row was clicked.</summary>
    public event Action? Toggled;

    /// <summary>The arrow on the right was clicked.</summary>
    public event Action? MoreRequested;

    public ToggleTile(string glyph, string title)
    {
        Height = 46;
        CornerRadius = new CornerRadius(12);
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        Padding = new Thickness(7, 0, 2, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ---- Icon ----
        _glyph = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _circle = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = _glyph, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(_circle);

        // ---- Title and subtitle ----
        var texts = new StackPanel { Margin = new Thickness(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _title = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        _subtitle = new TextBlock { FontSize = 10.5, Foreground = Subtle, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        texts.Children.Add(_title);
        texts.Children.Add(_subtitle);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        // ---- The switch ----
        var knob = new Ellipse
        {
            Width = KnobSize,
            Height = KnobSize,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(KnobInset, 0, 0, 0),
            RenderTransform = _knobMove,
        };
        _track = new Border
        {
            Width = TrackWidth,
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid { Children = { knob } },
        };
        Grid.SetColumn(_track, 2);
        grid.Children.Add(_track);

        // ---- "More" arrow ----
        var moreBg = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Transparent };
        var chevron = new TextBlock
        {
            Text = "", // chevron right
            FontFamily = IconFont,
            FontSize = 9,
            Foreground = Subtle,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var more = new Grid { Background = Brushes.Transparent, Width = 30, ToolTip = "More options", Margin = new Thickness(4, 0, 0, 0) };
        more.Children.Add(moreBg);
        more.Children.Add(chevron);
        more.MouseEnter += (_, _) => { moreBg.Background = MoreHover; chevron.Foreground = Brushes.White; };
        more.MouseLeave += (_, _) => { moreBg.Background = Brushes.Transparent; chevron.Foreground = Subtle; };
        more.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            MoreRequested?.Invoke();
        };
        Grid.SetColumn(more, 3);
        grid.Children.Add(more);

        MouseEnter += (_, _) => Background = Hover;
        MouseLeave += (_, _) => Background = Brushes.Transparent;
        MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            Toggled?.Invoke();
        };

        Child = grid;
        AllTiles.Add(this);
        Apply(animate: false);
    }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public string Subtitle
    {
        get => _subtitle.Text;
        set => _subtitle.Text = value;
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_applied && _isOn == value) return;
            _isOn = value;
            Apply(animate: IsLoaded);
        }
    }

    private void Apply(bool animate)
    {
        _applied = true;
        _circle.Background = _isOn ? Accent : OffBackground;
        _glyph.Foreground = _isOn ? AccentText : Brushes.White;
        _track.Background = _isOn ? Accent : TrackOff;

        double x = _isOn ? KnobTravel : 0;
        if (animate)
        {
            _knobMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            _knobMove.BeginAnimation(TranslateTransform.XProperty, null);
            _knobMove.X = x;
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
