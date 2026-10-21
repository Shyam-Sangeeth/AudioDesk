using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioDesk.Updates;

/// <summary>A newer AudioDesk release on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version,
    string PageUrl,
    string InstallerUrl,
    string InstallerName,
    long InstallerSize,
    string? Sha256);

/// <summary>
/// Checks GitHub Releases for a newer version, downloads its installer (.msi asset) and runs it.
/// The installer closes AudioDesk, upgrades it in place (settings kept) and starts it again.
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Shyam-Sangeeth/AudioDesk/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AudioDesk/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>This copy's version (major.minor.build).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>The latest release if it's newer than this copy and has an installer; otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        using var response = await Http.GetAsync(LatestReleaseUrl, cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var root = json.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
        if (latest <= CurrentVersion) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;

            // GitHub publishes a "sha256:<hex>" digest for each asset; use it to verify the download.
            string? sha = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d &&
                          d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? d["sha256:".Length..]
                : null;

            return new UpdateInfo(
                latest,
                root.GetProperty("html_url").GetString() ?? "https://github.com/Shyam-Sangeeth/AudioDesk/releases",
                asset.GetProperty("browser_download_url").GetString()!,
                name,
                asset.GetProperty("size").GetInt64(),
                sha);
        }
        return null; // a newer release without an installer can't be installed from here
    }

    /// <summary>Downloads the installer to a temp folder, reporting progress (0–1), and verifies it.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken cancel = default)
    {
        string folder = Path.Combine(Path.GetTempPath(), "AudioDesk", "update");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Path.GetFileName(update.InstallerName));

        using (var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                progress.Report(update.InstallerSize > 0 ? (double)done / update.InstallerSize : 0);
            }
        }

        var file = new FileInfo(path);
        if (file.Length != update.InstallerSize)
            throw new InvalidDataException("The download is incomplete. Please try again.");

        if (update.Sha256 != null)
        {
            await using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("The downloaded installer didn't pass verification and was deleted.");
            }
        }
        return path;
    }

    /// <summary>Starts the installer with only a progress bar. It closes AudioDesk itself before upgrading.</summary>
    public static void RunInstaller(string msiPath) =>
        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\" /passive") { UseShellExecute = true });
}
