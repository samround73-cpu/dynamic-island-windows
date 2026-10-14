using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>A small "type something" box, used for renaming pins and files and for pasting a link or path.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box;

    private PromptWindow(string title, string message, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var surface = Theme.Light ? Color.FromRgb(0xF4, 0xF4, 0xF6) : Color.FromRgb(0x1C, 0x1C, 0x1E);
        var field = Theme.Light ? Colors.White : Color.FromRgb(0x2A, 0x2A, 0x2D);

        _box = new TextBox
        {
            Text = initial,
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(field),
            Foreground = Theme.Ink(),
            CaretBrush = Theme.Ink(),
            BorderBrush = Theme.Ink(0x26),
            BorderThickness = new Thickness(1),
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };

        Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 84,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 0, 0, 0),
                Background = primary ? new SolidColorBrush(Color.FromRgb(0xD6, 0xB4, 0x6A)) : new SolidColorBrush(field),
                Foreground = primary ? new SolidColorBrush(Color.FromRgb(0x1F, 0x1A, 0x0E)) : Theme.Ink(),
                BorderThickness = new Thickness(0),
                IsDefault = primary,
                IsCancel = !primary,
            };
            button.Click += (_, _) => DialogResult = primary;
            return button;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(MakeButton("Cancel", false));
        buttons.Children.Add(MakeButton("OK", true));

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink() });
        stack.Children.Add(new TextBlock { Text = message, FontSize = 11.5, Foreground = Theme.Ink(0x99), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10) });
        stack.Children.Add(_box);
        stack.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(surface),
            BorderBrush = Theme.Ink(0x22),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 16, 18, 16),
            Child = stack,
        };
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        Loaded += (_, _) =>
        {
            Activate();
            _box.Focus();
            _box.SelectAll();
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) try { DragMove(); } catch { } };
    }

    /// <summary>Shows the box; returns what was typed (trimmed), or null if cancelled or left empty.</summary>
    public static string? Ask(string title, string message, string initial = "")
    {
        var window = new PromptWindow(title, message, initial);
        bool ok = window.ShowDialog() == true;
        string text = window._box.Text.Trim();
        return ok && text.Length > 0 ? text : null;
    }
}
