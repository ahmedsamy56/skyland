using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DynamicIsland.Services;

public enum UpdateStatus { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <param name="Sha256">Expected SHA-256 of the installer, from the release's .sha256 asset.</param>
public sealed record UpdateInfo(Version Version, string MsiName, string MsiUrl, string Sha256, string Notes);

/// <summary>
/// Looks for a newer SkyLand release on GitHub and installs it.
///
/// Source: the latest release of <see cref="Repo"/>. A release must contain <c>SkyLand-x.y.z.msi</c> and
/// <c>SkyLand-x.y.z.msi.sha256</c>. The installer is only run if its SHA-256 matches, and only downloaded
/// from GitHub hosts over HTTPS. While releases are unsigned this guards against corrupted or swapped
/// downloads; it does not protect against a compromised GitHub account (a signed installer would).
/// The MSI is per-user, so installing needs no admin prompt.
/// </summary>
public sealed class UpdateService
{
    public const string Repo = "cbsajan/skyland";

    // Test seams (never set in normal use): SKYLAND_UPDATE_URL replaces the GitHub API URL and lifts the host
    // allow-list, so the whole update flow can be exercised against a local server.
    private static string? OverrideUrl => Environment.GetEnvironmentVariable("SKYLAND_UPDATE_URL");

    private static readonly string[] AllowedHosts =
    [
        "github.com", "api.github.com", "objects.githubusercontent.com",
        "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"
    ];

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SkyLand-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
    public string StatusText { get; private set; } = "";
    public double Progress { get; private set; }            // 0..1 while downloading
    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised on any thread when the status, text or progress changes.</summary>
    public event Action? Changed;

    /// <summary>The installer was started; the app should exit now so the installer can replace its files.</summary>
    public event Action? ExitRequested;

    private int _busy;

    // ----- Check ---------------------------------------------------------

    /// <summary>Returns true when a newer, installable release exists.</summary>
    public async Task<bool> CheckAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Available is not null;

        try
        {
            Set(UpdateStatus.Checking, "Checking for updates...");
            string url = OverrideUrl ?? $"https://api.github.com/repos/{Repo}/releases/latest";

            string json = await Http.GetStringAsync(url);
            var release = JsonNode.Parse(json) as JsonObject;
            string tag = release?["tag_name"]?.GetValue<string>() ?? "";

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return Fail("The latest release has no readable version.");

            SettingsService.Current.LastUpdateCheckUtc = DateTime.UtcNow;
            SettingsService.Save();

            if (Normalize(latest) <= Normalize(CurrentVersion))
            {
                Available = null;
                Set(UpdateStatus.UpToDate, $"You are up to date (version {Format(CurrentVersion)}).");
                return false;
            }

            var assets = release?["assets"] as JsonArray ?? [];
            string? msiName = null, msiUrl = null, shaUrl = null;
            foreach (var asset in assets)
            {
                string name = asset?["name"]?.GetValue<string>() ?? "";
                string link = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) { msiName = name; msiUrl = link; }
            }
            foreach (var asset in assets)
            {
                string name = asset?["name"]?.GetValue<string>() ?? "";
                if (msiName is not null && name.Equals(msiName + ".sha256", StringComparison.OrdinalIgnoreCase))
                    shaUrl = asset?["browser_download_url"]?.GetValue<string>();
            }

            if (msiName is null || msiUrl is null || shaUrl is null)
                return Fail($"Version {Format(latest)} exists but has no installer with a checksum, so it was not offered.");

            string hashText = (await Http.GetStringAsync(RequireAllowed(shaUrl))).Trim();
            string hash = hashText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                return Fail("The release checksum file is not a valid SHA-256 hash.");

            RequireAllowed(msiUrl);
            Available = new UpdateInfo(latest, msiName, msiUrl, hash, release?["body"]?.GetValue<string>() ?? "");
            Set(UpdateStatus.Available, $"Version {Format(latest)} is available.");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline or GitHub unreachable: not an error worth interrupting anyone for.
            return Fail("Could not reach the update server. Check your internet connection.");
        }
        catch (Exception ex)
        {
            return Fail("Update check failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // ----- Download and install ------------------------------------------

    public async Task InstallAsync()
    {
        var update = Available;
        if (update is null) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "SkyLand-update");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(update.MsiName)); // file name only: no path tricks
            Progress = 0;
            Set(UpdateStatus.Downloading, "Downloading...");

            using (var response = await Http.GetAsync(RequireAllowed(update.MsiUrl), HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;

                using var source = await response.Content.ReadAsStreamAsync();
                using var file = File.Create(target);
                using var sha = SHA256.Create();

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (total > 0)
                    {
                        Progress = (double)done / total;
                        Set(UpdateStatus.Downloading, $"Downloading... {(int)(Progress * 100)}%");
                    }
                }
                sha.TransformFinalBlock([], 0, 0);

                string actual = Convert.ToHexString(sha.Hash!);
                if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    file.Close();
                    File.Delete(target);
                    Fail("The download did not match its checksum, so it was discarded and not installed.");
                    return;
                }
            }

            Set(UpdateStatus.Installing, "Installing...");

            // Per-user package: no admin prompt. /passive shows a small progress window. The installer closes
            // SkyLand, replaces the files and starts the new version itself.
            Process.Start(new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{target}\" /passive /norestart",
                UseShellExecute = false
            });

            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Fail("Update failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // ----- Helpers -------------------------------------------------------

    private static string RequireAllowed(string url)
    {
        if (OverrideUrl is not null) return url; // test seam only

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Update download blocked: not an https GitHub address.");

        return url;
    }

    // 1.0 and 1.0.0.0 are the same version.
    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    private bool Fail(string message)
    {
        Set(UpdateStatus.Failed, message);
        return false;
    }

    private void Set(UpdateStatus status, string text)
    {
        Status = status;
        StatusText = text;
        Changed?.Invoke();
    }
}
