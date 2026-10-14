using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.WindowsTray;

namespace DynamicIsland;

/// <summary>One background app's icon (OneDrive, Discord…), with a short name for its chip.</summary>
public sealed class TrayChip
{
    public TrayChip(NotifyIcon source, string name)
    {
        Source = source;
        Name = name;
    }

    /// <summary>The icon as the app gave it (its picture and tooltip update as the app changes them).</summary>
    public NotifyIcon Source { get; }
    public string Name { get; }
}

/// <summary>
/// Collects the icons apps put in the taskbar's corner, so the island can show them.
///
/// Windows 11 doesn't let other programs read those icons, so the island receives them itself, the way other taskbar
/// replacements (RetroBar, Cairo) do, using their open-source ManagedShell library: it sits in front of the hidden
/// taskbar so apps send their icons here, and asks every running app to send them again when it starts.
/// When the island closes (or this is turned off), apps are told to send them back to the normal taskbar.
/// Clicks are passed straight to the app, so you get its own menus.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    // Windows' own icons (volume, network, battery…): the island already has its own controls for these.
    private static readonly HashSet<Guid> SystemIcons = new[]
    {
        NotificationArea.HARDWARE_GUID, NotificationArea.UPDATE_GUID, NotificationArea.MICROPHONE_GUID,
        NotificationArea.LOCATION_GUID, NotificationArea.HEALTH_GUID, NotificationArea.MEETNOW_GUID,
        NotificationArea.NETWORK_GUID, NotificationArea.POWER_GUID, NotificationArea.VOLUME_GUID,
    }.Select(g => new Guid(g)).ToHashSet();

    /// <summary>The running one, so the app can hand the icons back to Windows even if it has to close suddenly.</summary>
    public static TrayIconHost? Current { get; set; }

    private TrayService? _trayService;
    private NotificationArea? _area;
    private readonly Dictionary<NotifyIcon, string> _names = new();

    /// <summary>Raised (on the UI thread) when icons come, go, or are hidden/shown by their apps.</summary>
    public event Action? Changed;

    public bool IsRunning => _area is not null;

    public bool Start()
    {
        if (_area is not null) return true;
        try
        {
            _trayService = new TrayService();
            var area = new NotificationArea(Array.Empty<string>(), _trayService, new ExplorerTrayService());
            area.Initialize();
            if (area.IsFailed)
            {
                App.LogNote("tray icons: couldn't start");
                _trayService.Dispose();
                _trayService = null;
                return false;
            }
            _area = area;
            area.TrayIcons.CollectionChanged += OnIconsChanged;
            foreach (var icon in area.TrayIcons) Watch(icon);
            return true;
        }
        catch (Exception ex)
        {
            App.LogNote($"tray icons: couldn't start: {ex.Message}");
            _area = null;
            try { _trayService?.Dispose(); } catch { }
            _trayService = null;
            return false;
        }
    }

    private void OnIconsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (NotifyIcon icon in e.NewItems) Watch(icon);
        Changed?.Invoke();
    }

    private void Watch(NotifyIcon icon) => icon.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName is nameof(NotifyIcon.IsHidden)) Changed?.Invoke();
    };

    /// <summary>The icons to show: apps' own, not hidden by the app, not Windows' built-in ones.</summary>
    public List<TrayChip> Chips()
    {
        if (_area is null) return new List<TrayChip>();
        var chips = new List<TrayChip>();
        foreach (var icon in _area.TrayIcons.ToList())
        {
            if (icon.IsHidden || icon.Icon is null) continue;
            if (icon.GUID != Guid.Empty && SystemIcons.Contains(icon.GUID)) continue;
            if (!_names.TryGetValue(icon, out var name)) _names[icon] = name = ShortName(icon);
            chips.Add(new TrayChip(icon, name));
        }
        return chips;
    }

    /// <summary>"Microsoft OneDrive" → "OneDrive"; falls back to the first part of the tooltip, or the program's name.</summary>
    private static string ShortName(NotifyIcon icon)
    {
        string? name = null;
        try
        {
            if (!string.IsNullOrEmpty(icon.Path) && File.Exists(icon.Path))
            {
                var info = FileVersionInfo.GetVersionInfo(icon.Path);
                name = new[] { info.FileDescription, info.ProductName }
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && n!.Length <= 26);
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(icon.Title))
        {
            name = icon.Title.Split('\n')[0];
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) name = name[..dash];
        }
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(icon.Path ?? "") ?? "App";
        name = name!.Trim();
        foreach (var prefix in new[] { "Microsoft ", "Windows " })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length + 2) name = name[prefix.Length..];
        return name.Length > 18 ? name[..17] + "…" : name;
    }

    // ---- Passing the mouse to the app ----

    public static void MouseEnter(NotifyIcon icon, int left, int top, int width, int height)
    {
        try
        {
            // Tells the app where its icon is, so its tooltip or pop-up appears next to it.
            icon.Placement = new ManagedShell.Interop.NativeMethods.Rect { Left = left, Top = top, Right = width, Bottom = height };
            icon.IconMouseEnter(CursorParam());
        }
        catch { }
    }

    public static void MouseLeave(NotifyIcon icon)
    {
        try { icon.IconMouseLeave(CursorParam()); } catch { }
    }

    public static void MouseMove(NotifyIcon icon)
    {
        try { icon.IconMouseMove(CursorParam()); } catch { }
    }

    public static void MouseDown(NotifyIcon icon, MouseButton button)
    {
        if (button == MouseButton.Left) return; // a left click is sent all at once on release, like the taskbar
        try { icon.IconMouseDown(button, CursorParam(), GetDoubleClickTime()); } catch { }
    }

    public static void MouseUp(NotifyIcon icon, MouseButton button)
    {
        try
        {
            if (button == MouseButton.Left) icon.IconMouseDown(button, CursorParam(), GetDoubleClickTime());
            icon.IconMouseUp(button, CursorParam(), GetDoubleClickTime());
        }
        catch { }
    }

    private static uint CursorParam()
    {
        GetCursorPos(out var point);
        return (uint)((point.Y << 16) | (point.X & 0xFFFF));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern int GetDoubleClickTime();

    public void Dispose()
    {
        var area = _area;
        _area = null;
        if (area is null) return;
        try { area.TrayIcons.CollectionChanged -= OnIconsChanged; } catch { }
        try { area.Dispose(); } catch { } // apps are told to send their icons back to the normal taskbar
        _names.Clear();
    }
}
