using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>Everyday things to do with files: recycle, zip, open with, edit, copy to the clipboard.</summary>
public static class FileActions
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>Moves a file or folder to the Recycle Bin (so it can be got back). Returns true if it's gone.</summary>
    public static bool Recycle(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0", // the list must end with two nulls; marshalling adds the second
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            SHFileOperation(ref op);
        }
        catch { }
        return !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>Puts the files on the clipboard, ready to paste in Explorer, an email or a chat.</summary>
    public static void CopyFiles(IEnumerable<string> paths)
    {
        var list = new StringCollection();
        foreach (var path in paths) list.Add(path);
        if (list.Count > 0) Clipboard.SetFileDropList(list);
    }

    /// <summary>Puts the picture itself on the clipboard (to paste into a chat or document).</summary>
    public static bool CopyImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Clipboard.SetImage(image);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Windows' "Open with" chooser.</summary>
    public static void OpenWith(string path)
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path) { UseShellExecute = false }); } catch { }
    }

    /// <summary>Opens a picture for editing (Paint or whatever Windows uses to edit that kind of file).</summary>
    public static void Edit(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("mspaint.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInFolder(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    /// <summary>
    /// Zips files and folders into one .zip next to the first one ("name.zip", or "Shelf items.zip" for several),
    /// never overwriting anything. Returns the new zip's path.
    /// </summary>
    public static Task<string> ZipAsync(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        if (paths.Count == 0) throw new ArgumentException("Nothing to zip");
        string folder = Path.GetDirectoryName(paths[0].TrimEnd('\\')) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string baseName = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('\\')) : "Shelf items";
        string target = Path.Combine(folder, baseName + ".zip");
        for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{baseName} ({n}).zip");

        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                zip.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
            }
            else if (Directory.Exists(path))
            {
                string root = Path.GetFileName(path.TrimEnd('\\'));
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(path, file);
                    zip.CreateEntryFromFile(file, Path.Combine(root, relative).Replace('\\', '/'), CompressionLevel.Optimal);
                }
            }
        }
        return target;
    });

    /// <summary>Renames a file or folder in place. Returns the new path, or null if it couldn't.</summary>
    public static string? Rename(string path, string newName)
    {
        try
        {
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            string folder = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
            string target = Path.Combine(folder, newName);
            if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return path;
            if (File.Exists(target) || Directory.Exists(target)) return null;
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            return target;
        }
        catch { return null; }
    }

    // ---- Windows' file operation (for the Recycle Bin) ----

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);
}
