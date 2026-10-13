using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>One app from the Start menu's "All apps" list.</summary>
/// <param name="Name">What the Start menu calls it.</param>
/// <param name="LaunchPath">"shell:AppsFolder\…" — opens it, whatever kind of app it is.</param>
/// <param name="ExePath">The program file it runs (lower-case), when known — used to spot it's already open.</param>
/// <param name="IsStoreApp">A Microsoft Store app (Calculator, Settings…).</param>
public sealed record InstalledApp(string Name, string LaunchPath, string? ExePath, bool IsStoreApp, ImageSource? Icon);

/// <summary>
/// Reads every installed app — normal programs and Store apps alike — from the same list that powers
/// the Start menu's "All apps" (the shell:AppsFolder), with their proper icons.
/// </summary>
public static class AppCatalog
{
    private const int IconPixels = 64;

    /// <summary>Loads the list on a background thread (the Windows shell needs a special "STA" thread for this).</summary>
    public static Task<List<InstalledApp>> LoadAsync()
    {
        var result = new TaskCompletionSource<List<InstalledApp>>();
        var thread = new Thread(() =>
        {
            try { result.SetResult(Load()); }
            catch { result.SetResult(new List<InstalledApp>()); }
        })
        {
            IsBackground = true,
            Name = "App catalog loader",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static List<InstalledApp> Load()
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        Guid shellItemId = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref shellItemId, out IShellItem folder);
        try
        {
            Guid enumItems = new("94f60519-2850-4924-aa5a-d15e84868039"); // BHID_EnumItems
            Guid enumId = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, ref enumItems, ref enumId, out object enumerator);
            var items = (IEnumShellItems)enumerator;

            var batch = new IShellItem[1];
            while (items.Next(1, batch, out uint fetched) == 0 && fetched == 1)
            {
                var item = batch[0];
                try
                {
                    string name = DisplayName(item, SIGDN.NORMALDISPLAY);
                    string id = DisplayName(item, SIGDN.PARENTRELATIVEPARSING);
                    if (ShouldSkip(name, id) || !seen.Add(name)) continue;

                    // Store apps are identified like "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App".
                    bool isStoreApp = id.Contains('!');
                    apps.Add(new InstalledApp(name, @"shell:AppsFolder\" + id, FindExe(item, id), isStoreApp, Icon(item, isStoreApp)));
                }
                catch { /* one odd entry shouldn't stop the rest */ }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            Marshal.ReleaseComObject(items);
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }

        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Leaves out uninstallers, readme files and web links that some programs add to the Start menu.</summary>
    private static bool ShouldSkip(string name, string id)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        string[] junk = { ".txt", ".url", ".htm", ".html", ".chm", ".pdf", ".rtf", ".log" };
        return junk.Any(ext => id.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static string DisplayName(IShellItem item, SIGDN kind)
    {
        item.GetDisplayName(kind, out IntPtr text);
        try { return Marshal.PtrToStringUni(text) ?? ""; }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    /// <summary>Works out which .exe an entry runs, so a running window can be matched back to it.</summary>
    private static string? FindExe(IShellItem item, string id)
    {
        // Entries backed by a Start-menu shortcut know their target.
        if (item is IShellItem2 item2)
        {
            var key = new PROPERTYKEY { fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), pid = 2 }; // Link.TargetParsingPath
            if (item2.GetString(ref key, out IntPtr text) == 0 && text != IntPtr.Zero)
            {
                try
                {
                    string? target = Marshal.PtrToStringUni(text);
                    if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return Normalise(target);
                }
                finally { Marshal.FreeCoTaskMem(text); }
            }
        }

        // Others are a path that starts with a special-folder code, e.g. "{6D809377-…}\Microsoft VS Code\Code.exe".
        if (id.StartsWith('{') && id.Contains("}\\") && id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            int end = id.IndexOf('}');
            if (Guid.TryParse(id[1..end], out var folderId) &&
                SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out IntPtr folderPath) == 0)
            {
                try { return Normalise(Path.Combine(Marshal.PtrToStringUni(folderPath) ?? "", id[(end + 2)..])); }
                finally { Marshal.FreeCoTaskMem(folderPath); }
            }
        }

        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(id)) return Normalise(id);
        return null;
    }

    public static string Normalise(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }

    private static ImageSource? Icon(IShellItem item, bool isStoreApp)
    {
        if (item is not IShellItemImageFactory factory) return null;
        if (factory.GetImage(new SIZE { cx = IconPixels, cy = IconPixels }, SIIGBF_ICONONLY, out IntPtr bitmap) != 0 || bitmap == IntPtr.Zero)
            return null;
        try { return BitmapToImage(bitmap, removePlate: isStoreApp); }
        finally { Native.DeleteObject(bitmap); }
    }

    /// <summary>A file's thumbnail (photos, videos, documents) or its icon, as Explorer would show it.
    /// Must be called on the UI thread (or another STA thread).</summary>
    public static ImageSource? GetShellImage(string path, int pixels)
    {
        try
        {
            Guid shellItemId = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out IShellItem item);
            try
            {
                if (item is not IShellItemImageFactory factory) return null;
                if (factory.GetImage(new SIZE { cx = pixels, cy = pixels }, 0, out IntPtr bitmap) != 0 || bitmap == IntPtr.Zero)
                    return null;
                try { return BitmapToImage(bitmap); }
                finally { Native.DeleteObject(bitmap); }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Copies a Windows bitmap into a WPF image, keeping its transparency.</summary>
    /// <param name="removePlate">Store apps come on a solid coloured square with a tiny symbol in the middle;
    /// this removes the square and enlarges the symbol, which looks much better on the island's dark background.</param>
    public static BitmapSource? BitmapToImage(IntPtr hBitmap, bool removePlate = false)
    {
        if (Native.GetObject(hBitmap, Marshal.SizeOf<Native.BITMAP>(), out var info) == 0) return null;
        int width = info.bmWidth, height = Math.Abs(info.bmHeight);
        if (width <= 0 || height <= 0) return null;

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down, so the image isn't upside-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(screen, hBitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        // Some icons come without any transparency information; make those fully opaque rather than invisible.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        if (removePlate && TryRemovePlate(pixels, width, height, out var cropped, out int side))
        {
            pixels = cropped;
            width = height = side;
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static bool TryRemovePlate(byte[] px, int w, int h, out byte[] result, out int side)
    {
        result = px;
        side = w;

        // The plate colour is whatever fills the corner; the whole border must be that colour.
        byte b0 = px[0], g0 = px[1], r0 = px[2], a0 = px[3];
        if (a0 < 250) return false;

        bool Same(int i, int tolerance) =>
            px[i + 3] >= 240 && Math.Abs(px[i] - b0) <= tolerance && Math.Abs(px[i + 1] - g0) <= tolerance && Math.Abs(px[i + 2] - r0) <= tolerance;

        int border = 0, matching = 0;
        for (int x = 0; x < w; x++)
        {
            foreach (int y in new[] { 0, h - 1 })
            {
                border++;
                if (Same((y * w + x) * 4, 10)) matching++;
            }
        }
        for (int y = 1; y < h - 1; y++)
        {
            foreach (int x in new[] { 0, w - 1 })
            {
                border++;
                if (Same((y * w + x) * 4, 10)) matching++;
            }
        }
        if (matching < border * 0.92) return false;

        // Clear the plate and find the symbol in the middle.
        var copy = (byte[])px.Clone();
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (Same(i, 28))
                {
                    copy[i] = copy[i + 1] = copy[i + 2] = copy[i + 3] = 0;
                }
                else if (copy[i + 3] > 24)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }
        if (maxX < 0) return false;

        // Re-centre the symbol on a square with a little breathing room, so it fills the icon space.
        int boxW = maxX - minX + 1, boxH = maxY - minY + 1;
        int size = Math.Max(boxW, boxH);
        int pad = Math.Max(1, size / 10);
        side = size + pad * 2;
        result = new byte[side * side * 4];
        int offsetX = pad + (size - boxW) / 2, offsetY = pad + (size - boxH) / 2;
        for (int y = 0; y < boxH; y++)
            Buffer.BlockCopy(copy, ((minY + y) * w + minX) * 4, result, ((offsetY + y) * side + offsetX) * 4, boxW * 4);
        return true;
    }

    // ---- Windows shell plumbing -----------------------------------------------------------

    private const int SIIGBF_ICONONLY = 0x4;

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItem item);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(SIGDN kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    /// <summary>IShellItem plus property access. Methods are listed in Windows' exact order;
    /// only GetString is actually called, the rest are placeholders that keep the order right.</summary>
    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IntPtr parent);
        void GetDisplayName(SIGDN kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IntPtr other, uint hint, out int order);
        // IShellItem2
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr store);
        void GetPropertyStoreWithCreateObject(int flags, IntPtr createObject, ref Guid riid, out IntPtr store);
        void GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid riid, out IntPtr store);
        void GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr list);
        void Update(IntPtr bindContext);
        void GetProperty(ref PROPERTYKEY key, IntPtr value);
        void GetCLSID(ref PROPERTYKEY key, out Guid clsid);
        void GetFileTime(ref PROPERTYKEY key, out long fileTime);
        void GetInt32(ref PROPERTYKEY key, out int value);
        [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr value);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] items, out uint fetched);
        void Skip(uint count);
        void Reset();
        void Clone(out IEnumShellItems copy);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }
}
