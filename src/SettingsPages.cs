using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicIsland;

/// <summary>A page of the Windows Settings app that can be found from the island's search.</summary>
public sealed record SettingsPage(string Name, string Uri, string Icon, string Keywords);

/// <summary>Common Windows Settings pages, so typing "bluetooth" or "display" in search can jump straight there.</summary>
public static class SettingsPages
{
    public static readonly IReadOnlyList<SettingsPage> All = new SettingsPage[]
    {
        new("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth", "bluetooth devices pair headphones mouse keyboard"),
        new("Display", "ms-settings:display", "device-desktop", "display screen resolution scale scaling monitor brightness hdr"),
        new("Night light", "ms-settings:nightlight", "moon", "night light blue light warm"),
        new("Sound", "ms-settings:sound", "volume", "sound audio speakers headphones microphone output input volume"),
        new("Volume mixer", "ms-settings:apps-volume", "adjustments-horizontal", "volume mixer app volume"),
        new("Notifications", "ms-settings:notifications", "bell", "notifications do not disturb focus alerts"),
        new("Focus", "ms-settings:focus", "target", "focus assist do not disturb"),
        new("Power & battery", "ms-settings:powersleep", "battery-charging", "power battery sleep screen timeout energy saver"),
        new("Storage", "ms-settings:storagesense", "database", "storage disk space cleanup storage sense"),
        new("Wi-Fi", "ms-settings:network-wifi", "wifi", "wifi wireless network internet"),
        new("Network & internet", "ms-settings:network", "world", "network internet ethernet proxy vpn"),
        new("VPN", "ms-settings:network-vpn", "shield-lock", "vpn network"),
        new("Personalisation", "ms-settings:personalization", "palette", "personalisation personalization theme colours colors"),
        new("Background", "ms-settings:personalization-background", "photo", "background wallpaper desktop picture"),
        new("Colours", "ms-settings:colors", "color-swatch", "colours colors accent dark mode light mode theme"),
        new("Lock screen", "ms-settings:lockscreen", "lock", "lock screen"),
        new("Themes", "ms-settings:themes", "brush", "themes"),
        new("Fonts", "ms-settings:fonts", "typography", "fonts"),
        new("Start", "ms-settings:personalization-start", "layout-grid", "start menu"),
        new("Taskbar", "ms-settings:taskbar", "layout-bottombar", "taskbar"),
        new("Installed apps", "ms-settings:appsfeatures", "apps", "installed apps uninstall programs features"),
        new("Default apps", "ms-settings:defaultapps", "app-window", "default apps browser"),
        new("Startup apps", "ms-settings:startupapps", "rocket", "startup apps start with windows"),
        new("Accounts", "ms-settings:yourinfo", "user", "account your info profile"),
        new("Sign-in options", "ms-settings:signinoptions", "fingerprint", "sign in password pin hello fingerprint face"),
        new("Date & time", "ms-settings:dateandtime", "clock", "date time clock timezone time zone"),
        new("Language & region", "ms-settings:regionlanguage", "language", "language region keyboard layout"),
        new("Gaming", "ms-settings:gaming-gamebar", "device-gamepad-2", "gaming game bar game mode xbox"),
        new("Accessibility", "ms-settings:easeofaccess", "accessible", "accessibility ease of access narrator magnifier"),
        new("Mouse", "ms-settings:mousetouchpad", "mouse", "mouse pointer cursor speed"),
        new("Touchpad", "ms-settings:devices-touchpad", "hand-finger", "touchpad trackpad gestures"),
        new("Printers & scanners", "ms-settings:printers", "printer", "printers scanners print"),
        new("Privacy & security", "ms-settings:privacy", "shield", "privacy security permissions"),
        new("Windows Security", "windowsdefender:", "shield-check", "windows security antivirus defender virus firewall"),
        new("Location", "ms-settings:privacy-location", "map-pin", "location gps privacy"),
        new("Camera", "ms-settings:privacy-webcam", "camera", "camera webcam privacy"),
        new("Windows Update", "ms-settings:windowsupdate", "refresh", "windows update updates upgrade"),
        new("About this PC", "ms-settings:about", "info-circle", "about pc name specs system information processor ram"),
        new("Remote Desktop", "ms-settings:remotedesktop", "device-desktop-share", "remote desktop rdp"),
        new("Clipboard settings", "ms-settings:clipboard", "clipboard", "clipboard history sync"),
        new("Multitasking", "ms-settings:multitasking", "layout-columns", "multitasking snap windows alt tab desktops"),
        new("Recovery", "ms-settings:recovery", "lifebuoy", "recovery reset pc restore"),
        new("Control Panel", "control", "adjustments", "control panel classic"),
        new("Device Manager", "devmgmt.msc", "cpu", "device manager drivers hardware"),
    };

    public static bool IsSettingsTarget(string? target) =>
        target is not null && (target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
                               || All.Any(p => string.Equals(p.Uri, target, StringComparison.OrdinalIgnoreCase)));
}
