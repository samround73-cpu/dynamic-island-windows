using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace DynamicIsland;

/// <summary>A newer version published on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// Keeps the island up to date from its GitHub releases:
/// asks GitHub for the latest release, downloads its DynamicIsland.exe next to the running one, checks it arrived
/// complete (size and, when GitHub provides it, a SHA-256 fingerprint), then swaps it in and restarts.
/// Windows lets a running exe be renamed, so the old one is moved aside and deleted on the next start.
/// </summary>
public static class UpdateService
{
    private const string Repository = "samround73-cpu/dynamic-island-windows";
    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string AssetName = "DynamicIsland.exe";
    public const string UpdatedArgument = "--updated";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The running version, e.g. 0.31.0.</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Asks GitHub for the latest release. Returns it only if it's newer than this one and has an exe.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var response = await Http.GetAsync(LatestReleaseApi);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= Current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
            {
                string d = digest.GetString() ?? "";
                if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = d[7..];
            }
            return new UpdateInfo(
                latest,
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("size").GetInt64(),
                sha,
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "");
        }
        return null; // the release is still being built (no exe attached yet)
    }

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where the app is");
    private static string Folder => Path.GetDirectoryName(ExePath)!;
    private static string NewPath => Path.Combine(Folder, Path.GetFileNameWithoutExtension(ExePath) + ".new.exe");
    private static string OldPath => Path.Combine(Folder, Path.GetFileNameWithoutExtension(ExePath) + ".old.exe");

    /// <summary>False when the app lives somewhere it can't write (e.g. Program Files), so it can't replace itself.</summary>
    public static bool CanUpdateInPlace
    {
        get
        {
            try
            {
                string probe = Path.Combine(Folder, ".update-check.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Downloads the new exe next to the running one and checks it. Returns the downloaded file.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update)
    {
        if (!update.DownloadUrl.StartsWith("https://github.com/" + Repository + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected download address");

        string target = NewPath;
        try
        {
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(target);
                await source.CopyToAsync(file);
            }

            var info = new FileInfo(target);
            if (info.Length != update.Size) throw new InvalidDataException($"Download incomplete ({info.Length} of {update.Size} bytes)");
            if (update.Sha256 is string expected)
            {
                await using var stream = File.OpenRead(target);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Download didn't match GitHub's fingerprint");
            }
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch { }
            throw;
        }
    }

    /// <summary>Swaps the downloaded exe in for the running one and starts it. On any problem, puts things back.</summary>
    public static bool ApplyAndRestart(string downloaded, Version version)
    {
        string exe = ExePath, old = OldPath;
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);          // allowed even while running
            File.Move(downloaded, exe);
        }
        catch (Exception ex)
        {
            App.LogNote($"update: couldn't swap in the new version: {ex.Message}");
            try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
            return false;
        }

        App.LogNote($"update: installed {version}, restarting");
        if (App.StartReplacement(exe, $"{App.RestartArgument} {UpdatedArgument}")) return true;

        // Couldn't start the new one: put the old one back so nothing is lost.
        try
        {
            File.Move(exe, downloaded, overwrite: true);
            File.Move(old, exe);
        }
        catch { }
        return false;
    }

    /// <summary>Deletes the previous version left beside the exe after an update (called a little after starting).</summary>
    public static void CleanUpOldVersion()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
        try { if (File.Exists(NewPath)) File.Delete(NewPath); } catch { }
    }
}
