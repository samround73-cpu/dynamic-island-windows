using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

public enum ClipKind { Text, Link, Code, Image }

/// <summary>Something you copied: text (plain, a link or code) or a picture.</summary>
public sealed class ClipItem : INotifyPropertyChanged
{
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public DateTime CopiedAt { get; set; } = DateTime.Now;
    public ClipKind Kind { get; init; }

    private bool _isPinned;
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public static ClipItem FromText(string text) => new() { Text = text, Kind = Classify(text) };
    public static ClipItem FromImage(BitmapSource image) => new() { Image = image, Kind = ClipKind.Image };

    /// <summary>Works out whether some text is a web link, a snippet of code/commands, or ordinary text.</summary>
    private static ClipKind Classify(string text)
    {
        string t = text.Trim();
        if (!t.Contains(' ') && !t.Contains('\n') &&
            (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
             || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)))
            return ClipKind.Link;

        if (Regex.IsMatch(t, @"^(npm|npx|yarn|pnpm|git|cd|dotnet|python|py|pip|node|docker|kubectl|curl|wget|sudo|ls|dir|mkdir|rm|cp|mv|winget|choco|ssh|code)\b")
            || Regex.IsMatch(t, @"[{};]\s*$", RegexOptions.Multiline) && (t.Contains('{') || t.Contains(';'))
            || t.Contains("=>") || t.Contains("</") || t.Contains("function ") || t.Contains("def "))
            return ClipKind.Code;

        return ClipKind.Text;
    }

    /// <summary>Icon for the list (Segoe Fluent Icons).</summary>
    public string Glyph => Kind switch
    {
        ClipKind.Link => "",
        ClipKind.Code => "",
        ClipKind.Image => "",
        _ => "",
    };

    public string TypeLabel => Kind switch
    {
        ClipKind.Link => "Link",
        ClipKind.Code => "Code",
        ClipKind.Image => "Image",
        _ => "Text",
    };

    /// <summary>One tidy line for the list.</summary>
    public string Preview => Kind == ClipKind.Image
        ? $"Image · {Image!.PixelWidth}×{Image.PixelHeight}"
        : string.Join(" ", Shorten(Text ?? "", 300).Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())).Trim();

    /// <summary>The text shown in the detail pane (very long text is cut short).</summary>
    public string DetailText => Shorten(Text ?? "", 4000);

    /// <summary>"108 characters • 22 words • 1 line", or "1920 × 1080" for a picture.</summary>
    public string SizeText
    {
        get
        {
            if (Image is not null) return $"{Image.PixelWidth} × {Image.PixelHeight} pixels";
            string text = Text ?? "";
            int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            int lines = text.Split('\n').Length;
            return $"{text.Length:N0} characters • {words:N0} {(words == 1 ? "word" : "words")} • {lines:N0} {(lines == 1 ? "line" : "lines")}";
        }
    }

    /// <summary>"Today, 9:41 PM"</summary>
    public string CopiedText
    {
        get
        {
            string day = CopiedAt.Date == DateTime.Today ? "Today"
                : CopiedAt.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
                : CopiedAt.ToString("d MMM");
            return $"{day}, {CopiedAt:t}";
        }
    }

    /// <summary>"now", "4m", "1h", "2d"</summary>
    public string Ago
    {
        get
        {
            var age = DateTime.Now - CopiedAt;
            return age.TotalMinutes < 1 ? "now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
                : $"{(int)age.TotalDays}d";
        }
    }

    /// <summary>Which heading it appears under.</summary>
    public string Group => IsPinned ? "PINNED"
        : CopiedAt.Date == DateTime.Today ? "TODAY"
        : CopiedAt.Date == DateTime.Today.AddDays(-1) ? "YESTERDAY"
        : "EARLIER";

    private static string Shorten(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Remembers the last few things you copied. Ordinary history is kept in memory only (forgotten when the
/// island closes); text you pin is saved so it's still there next time. Copies that password managers
/// mark as private are skipped.
/// </summary>
public sealed class ClipboardHistory
{
    private const int MaxItems = 40;
    private const int MaxImages = 8;

    private readonly IslandSettings _settings;
    private DateTime _ignoreUntil;

    public List<ClipItem> Items { get; } = new();

    public event Action? Changed;

    public ClipboardHistory(IslandSettings settings)
    {
        _settings = settings;
        foreach (var text in settings.PinnedClips)
        {
            var item = ClipItem.FromText(text);
            item.IsPinned = true;
            Items.Add(item);
        }
    }

    /// <summary>Called when Windows says the clipboard changed. Returns false if the clipboard was busy (try again shortly).</summary>
    public bool Capture()
    {
        // Our own "copy back" shouldn't add a duplicate entry.
        if (DateTime.Now < _ignoreUntil) return true;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || IsPrivate(data)) return true;

            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text)) Add(ClipItem.FromText(text));
            }
            else if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is not null)
                {
                    if (image.CanFreeze) image.Freeze();
                    Add(ClipItem.FromImage(image));
                }
            }
            return true;
        }
        catch
        {
            return false; // another app has the clipboard open right now
        }
    }

    /// <summary>Password managers tag their copies so clipboard tools leave them alone; we respect that.</summary>
    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore"))
            return true;

        foreach (var format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (!data.GetDataPresent(format)) continue;
            if (data.GetData(format) is MemoryStream stream && stream.Length >= 4)
            {
                var bytes = new byte[4];
                stream.Position = 0;
                stream.Read(bytes, 0, 4);
                if (BitConverter.ToInt32(bytes, 0) == 0) return true;
            }
        }
        return false;
    }

    private void Add(ClipItem item)
    {
        // Copying the same text again just moves it back to the top (keeping its pin).
        if (item.Text is not null)
        {
            var existing = Items.FirstOrDefault(i => i.Text == item.Text);
            if (existing is not null)
            {
                existing.CopiedAt = DateTime.Now;
                Items.Remove(existing);
                Items.Insert(0, existing);
                Changed?.Invoke();
                return;
            }
        }
        Items.Insert(0, item);

        while (Items.Count(i => i.IsImage() && !i.IsPinned) > MaxImages)
            Items.RemoveAt(Items.FindLastIndex(i => i.IsImage() && !i.IsPinned));
        while (Items.Count(i => !i.IsPinned) > MaxItems)
            Items.RemoveAt(Items.FindLastIndex(i => !i.IsPinned));

        Changed?.Invoke();
    }

    /// <summary>Puts an item back on the clipboard, ready to paste.</summary>
    public bool CopyBack(ClipItem item)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                _ignoreUntil = DateTime.Now.AddMilliseconds(500);
                if (item.Text is not null) Clipboard.SetText(item.Text);
                else if (item.Image is not null) Clipboard.SetImage(item.Image);
                item.CopiedAt = DateTime.Now;
                Items.Remove(item);
                Items.Insert(0, item);
                Changed?.Invoke();
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(40); // clipboard briefly busy
            }
        }
        return false;
    }

    public void TogglePin(ClipItem item)
    {
        item.IsPinned = !item.IsPinned;
        // Only text pins are saved; pinned pictures stay pinned until the island closes.
        if (item.Text is not null)
        {
            _settings.PinnedClips.Remove(item.Text);
            if (item.IsPinned) _settings.PinnedClips.Insert(0, item.Text);
            _settings.Save();
        }
        Changed?.Invoke();
    }

    public void Remove(ClipItem item)
    {
        Items.Remove(item);
        if (item.IsPinned && item.Text is not null && _settings.PinnedClips.Remove(item.Text)) _settings.Save();
        Changed?.Invoke();
    }

    /// <summary>Clears everything except pinned items.</summary>
    public void Clear()
    {
        Items.RemoveAll(i => !i.IsPinned);
        Changed?.Invoke();
    }
}

internal static class ClipItemExtensions
{
    public static bool IsImage(this ClipItem item) => item.Kind == ClipKind.Image;
}
