using System;
using System.IO;

namespace DynamicIsland;

/// <summary>
/// Puts "Dynamic Island" in the Start menu (your own Start menu folder, no admin needed), pointing at wherever the
/// app is running from. If you move the app, the shortcut is updated the next time it starts.
/// </summary>
public static class StartMenuShortcut
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Dynamic Island.lnk");

    public static void Ensure()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;

            // Don't add a shortcut for a copy running straight out of a download or temporary folder.
            string temp = Path.GetTempPath();
            if (exe.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(ShortcutPath);

            if (File.Exists(ShortcutPath) && string.Equals((string)link.TargetPath, exe, StringComparison.OrdinalIgnoreCase))
                return; // already right

            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            link.Description = "An iPhone-style island that replaces the Windows taskbar";
            link.IconLocation = exe + ",0";
            link.Save();
        }
        catch (Exception ex)
        {
            App.LogNote($"couldn't create the Start menu shortcut: {ex.Message}");
        }
    }
}
