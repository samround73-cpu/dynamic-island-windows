using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// Pins that aren't apps: folders, files, websites and Settings pages, shown in the hover tray's row
/// alongside pinned apps. Also the "+" menu, the Downloads dot and the peek at a pinned folder's newest files.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, ImageSource?> _pinIcons = new(StringComparer.OrdinalIgnoreCase);
    private bool _downloadsNew; // something arrived in Downloads since you last looked

    /// <summary>An installed app (from the Start menu list), as opposed to a folder, file, website or Settings page.</summary>
    private static bool IsAppPin(string target) => target.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);

    private static bool IsWebsite(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private bool IsDownloadsFolder(string target) =>
        _captures.DownloadsFolder is { } downloads
        && string.Equals(target.TrimEnd('\\'), downloads.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The tile for a pinned folder, file, website or Settings page.</summary>
    private AppTile ShortcutTile(string target)
    {
        string name = _settings.PinNames.TryGetValue(target, out var custom) ? custom : DefaultPinName(target);
        ImageSource? icon = null;
        LineIcon? line = null;

        if (IsWebsite(target))
        {
            line = LineIcons.Named(WebsiteIcon(target), WebsiteColour(target));
        }
        else if (SettingsPages.All.FirstOrDefault(p => string.Equals(p.Uri, target, StringComparison.OrdinalIgnoreCase)) is { } page)
        {
            line = LineIcons.Named(page.Icon, "#9DB4CC");
        }
        else if (Directory.Exists(target))
        {
            line = _settings.UseLineIcons ? LineIcons.Named(IsDownloadsFolder(target) ? "folder-down" : "folder", "#E8A33D") : null;
            if (line is null) icon = PinIcon(target);
        }
        else if (File.Exists(target))
        {
            icon = PinIcon(target); // the file's own icon (Excel, PDF…) says most about it
        }
        else
        {
            line = LineIcons.Named("alert-triangle", "#E8A33D"); // moved or deleted
        }

        return new AppTile
        {
            Key = "pin:" + target,
            Name = name,
            Icon = icon,
            Line = line,
            PinTarget = target,
            IsShortcutPin = true,
            HasBadge = _downloadsNew && IsDownloadsFolder(target),
        };
    }

    private ImageSource? PinIcon(string path)
    {
        if (!_pinIcons.TryGetValue(path, out var icon))
            _pinIcons[path] = icon = AppCatalog.GetShellImage(path, 64);
        return icon;
    }

    private static string DefaultPinName(string target)
    {
        if (IsWebsite(target) && Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            return host;
        }
        if (SettingsPages.All.FirstOrDefault(p => string.Equals(p.Uri, target, StringComparison.OrdinalIgnoreCase)) is { } page)
            return page.Name;
        string name = Path.GetFileName(target.TrimEnd('\\'));
        return name.Length > 0 ? name : target;
    }

    /// <summary>A brand icon for well-known sites, otherwise a globe.</summary>
    private static string WebsiteIcon(string url)
    {
        string u = url.ToLowerInvariant();
        if (u.Contains("youtube.") || u.Contains("youtu.be")) return "brand-youtube";
        if (u.Contains("whatsapp.")) return "brand-whatsapp";
        if (u.Contains("github.")) return "brand-github";
        if (u.Contains("reddit.")) return "brand-reddit";
        if (u.Contains("x.com") || u.Contains("twitter.")) return "brand-x";
        if (u.Contains("facebook.")) return "brand-facebook";
        if (u.Contains("instagram.")) return "brand-instagram";
        if (u.Contains("netflix.")) return "brand-netflix";
        if (u.Contains("twitch.")) return "brand-twitch";
        if (u.Contains("amazon.")) return "brand-amazon";
        if (u.Contains("google.") || u.Contains("gmail.")) return "brand-google";
        if (u.Contains("outlook.") || u.Contains("office.") || u.Contains("microsoft")) return "brand-windows";
        if (u.Contains("linkedin.")) return "brand-linkedin";
        if (u.Contains("spotify.")) return "brand-spotify";
        if (u.Contains("discord.")) return "brand-discord";
        if (u.Contains("chatgpt.") || u.Contains("openai.")) return "brand-openai";
        return "world";
    }

    private static string WebsiteColour(string url)
    {
        string u = url.ToLowerInvariant();
        if (u.Contains("youtube.") || u.Contains("youtu.be") || u.Contains("netflix.")) return "#FF4040";
        if (u.Contains("whatsapp.") || u.Contains("spotify.")) return "#25D366";
        if (u.Contains("twitch.") || u.Contains("discord.")) return "#9B7BF5";
        if (u.Contains("amazon.")) return "#FF9900";
        if (u.Contains("reddit.")) return "#FF5700";
        return "#5AA9F0";
    }

    private void OpenShortcutPin(string target)
    {
        ExitPeek();
        if (IsDownloadsFolder(target)) MarkDownloadsSeen();
        bool exists = IsWebsite(target) || SettingsPages.IsSettingsTarget(target) || target.Contains(':') && !target.Contains('\\')
                      || File.Exists(target) || Directory.Exists(target);
        if (!exists)
        {
            ShowActivity("", null, "Can't find it", $"{DefaultPinName(target)} has been moved or deleted", null);
            return;
        }
        SystemActions.Open(target);
    }

    private void RenamePin(string target)
    {
        ExitPeek();
        string current = _settings.PinNames.TryGetValue(target, out var custom) ? custom : DefaultPinName(target);
        string? name = PromptWindow.Ask("Rename pin", "The name shown when you hover it in the tray. Leave it as it is to keep it.", current);
        if (name is null) return;
        if (name == DefaultPinName(target)) _settings.PinNames.Remove(target);
        else _settings.PinNames[target] = name;
        _settings.Save();
        AfterTrayPinsChanged();
    }

    /// <summary>Pins something (if it isn't already) and says so.</summary>
    private void AddPin(string target)
    {
        target = target.Trim().Trim('"');
        if (target.Length == 0) return;
        if (!_settings.IsTrayPinned(target))
        {
            _settings.TrayPins.Add(target);
            _settings.Save();
        }
        AfterTrayPinsChanged();
    }

    /// <summary>Turns what someone typed or pasted into something openable: a path, a Settings page or a web address.</summary>
    private static string? NormaliseTarget(string text)
    {
        text = text.Trim().Trim('"');
        if (text.Length == 0) return null;
        if (File.Exists(text) || Directory.Exists(text)) return Path.GetFullPath(text);
        if (IsWebsite(text) || SettingsPages.IsSettingsTarget(text)) return text;
        if (!text.Contains(' ') && text.Contains('.') && !text.Contains('\\')) return "https://" + text; // "bbc.co.uk"
        return null;
    }

    /// <summary>The "+" at the end of the tray's row: pin a folder, file, website or Settings page.</summary>
    private void ShowAddPinMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu();
        menu.Opened += Menu_Opened;
        menu.Closed += Menu_Closed;

        menu.Items.Add(MenuEntry("Folder…", (_, _) =>
        {
            ExitPeek();
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pin a folder to the tray" };
            if (dialog.ShowDialog(this) == true) AddPin(dialog.FolderName);
        }));
        menu.Items.Add(MenuEntry("File…", (_, _) =>
        {
            ExitPeek();
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pin a file to the tray" };
            if (dialog.ShowDialog(this) == true) AddPin(dialog.FileName);
        }));
        menu.Items.Add(MenuEntry("Website or path…", (_, _) =>
        {
            ExitPeek();
            string? text = PromptWindow.Ask("Pin a website or path", "Paste a web address (like bbc.co.uk) or a file or folder path.");
            if (text is null) return;
            if (NormaliseTarget(text) is { } target) AddPin(target);
            else ShowActivity("", null, "Couldn't pin that", "It isn't a web address, file or folder", null);
        }));

        if (_captures.DownloadsFolder is { } downloads && !_settings.IsTrayPinned(downloads))
            menu.Items.Add(MenuEntry("Downloads folder", (_, _) => AddPin(downloads)));

        var settings = new MenuItem { Header = "Settings page" };
        foreach (var page in SettingsPages.All.Take(16))
        {
            string uri = page.Uri;
            settings.Items.Add(MenuEntry(page.Name, (_, _) => AddPin(uri)));
        }
        menu.Items.Add(settings);

        anchor.ContextMenu = menu;
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    // ---- Downloads dot ----

    /// <summary>Called when a download finishes: a pinned Downloads folder gets a dot until you look.</summary>
    private void MarkDownloadsNew()
    {
        if (_downloadsNew) return;
        _downloadsNew = true;
        if (_settings.TrayPins.Any(IsDownloadsFolder)) RefreshOpenApps();
    }

    private void MarkDownloadsSeen()
    {
        if (!_downloadsNew) return;
        _downloadsNew = false;
        RefreshOpenApps();
    }

    // ---- A peek at a pinned folder's newest files ----

    private Popup? _folderPeek;
    private FrameworkElement? _folderPeekAnchor;
    private string? _folderPeekTarget;
    private readonly DispatcherTimer _folderPeekOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _folderPeekCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _folderPeekTimersReady;

    /// <summary>Hovering a pinned folder: after a moment, show its newest files.</summary>
    private void StartFolderPeek(FrameworkElement anchor, string folder)
    {
        if (!_folderPeekTimersReady)
        {
            _folderPeekTimersReady = true;
            _folderPeekOpenTimer.Tick += (_, _) => { _folderPeekOpenTimer.Stop(); OpenFolderPeek(); };
            _folderPeekCloseTimer.Tick += (_, _) =>
            {
                if (_folderPeek?.IsMouseOver == true || _folderPeekAnchor?.IsMouseOver == true) return;
                CloseFolderPeek();
            };
        }
        if (_folderPeek?.IsOpen == true && _folderPeekTarget == folder) { _folderPeekCloseTimer.Stop(); return; }
        CloseFolderPeek();
        _folderPeekAnchor = anchor;
        _folderPeekTarget = folder;
        RestartTimer(_folderPeekOpenTimer);
    }

    private void FolderPeekAnchorLeft()
    {
        _folderPeekOpenTimer.Stop();
        if (_folderPeek?.IsOpen == true) RestartTimer(_folderPeekCloseTimer);
    }

    private void OpenFolderPeek()
    {
        if (!_peeking || _folderPeekAnchor is null || _folderPeekTarget is null || !_folderPeekAnchor.IsMouseOver) return;
        string folder = _folderPeekTarget;

        List<FileSystemInfo> newest;
        try
        {
            newest = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(5)
                .ToList();
        }
        catch { return; }

        bool light = Theme.Light;
        var list = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(6, 2, 6, 6) };
        if (IsDownloadsFolder(folder) && _downloadsNew)
        {
            var fresh = new TextBlock { Text = "new", FontSize = 10.5, Foreground = (Brush)FindResource("Accent") };
            DockPanel.SetDock(fresh, Dock.Right);
            header.Children.Add(fresh);
        }
        header.Children.Add(new TextBlock { Text = DefaultPinName(folder), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink() });
        list.Children.Add(header);

        if (newest.Count == 0)
            list.Children.Add(new TextBlock { Text = "Empty", FontSize = 11.5, Foreground = Theme.Ink(0x80), Margin = new Thickness(8, 2, 8, 6) });

        foreach (var item in newest)
        {
            string path = item.FullName;
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var when = new TextBlock { Text = FriendlyAge(item.LastWriteTime), FontSize = 10.5, Foreground = Theme.Ink(0x80), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            var icon = new Image { Source = PinIcon(path), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = item.Name, FontSize = 11.5, Foreground = Theme.Ink(), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            list.Children.Add(PeekRow(row, () => { CloseFolderPeek(); ExitPeek(); SystemActions.Open(path); }));
        }

        var open = new TextBlock { Text = "Open folder", FontSize = 11, Foreground = (Brush)FindResource("Accent") };
        list.Children.Add(PeekRow(open, () => OpenShortcutPin(folder)));

        _folderPeek = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Placement = PlacementMode.Bottom,
            PlacementTarget = _folderPeekAnchor,
            VerticalOffset = 6,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Width = 270,
                Background = new SolidColorBrush(light ? Color.FromRgb(0xFA, 0xFA, 0xFB) : Color.FromRgb(0x1C, 0x1C, 0x1E)),
                BorderBrush = Theme.Ink(0x1F),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(8),
                Child = list,
            },
        };
        _folderPeek.MouseEnter += (_, _) => _folderPeekCloseTimer.Stop();
        _folderPeek.MouseLeave += (_, _) => RestartTimer(_folderPeekCloseTimer);
        _menuOpen = true; // keeps the hover tray open while you're in the peek
        _folderPeek.IsOpen = true;
        if (IsDownloadsFolder(folder)) MarkDownloadsSeen();
    }

    private Border PeekRow(UIElement content, Action click)
    {
        var row = new Border { Child = content, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = Theme.Ink(0x14);
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }

    private void CloseFolderPeek()
    {
        _folderPeekOpenTimer.Stop();
        _folderPeekCloseTimer.Stop();
        if (_folderPeek is null) return;
        _folderPeek.IsOpen = false;
        _folderPeek = null;
        _menuOpen = false;
        if (_peeking && !IslandHost.IsMouseOver) RestartTimer(_peekCloseTimer);
    }

    private static string FriendlyAge(DateTime when)
    {
        var age = DateTime.Now - when;
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h";
        if (age < TimeSpan.FromDays(2)) return "Yesterday";
        return when.ToString("d MMM");
    }

    // ---- Dragging links and files onto the island ----

    /// <summary>A web address being dragged (from a browser's address bar or a link), if there is one.</summary>
    private static string? DraggedLink(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream stream)
            {
                string url = Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0').Trim();
                if (IsWebsite(url)) return url;
            }
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
            {
                text = text.Trim();
                if (IsWebsite(text) && !text.Contains(' ') && !text.Contains('\n')) return text;
            }
        }
        catch { }
        return null;
    }
}
