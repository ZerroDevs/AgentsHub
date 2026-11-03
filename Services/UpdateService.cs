#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using System.Windows.Forms;
#endif

using AgentsHub.Helpers;

namespace AgentsHub.Services;

public enum UpdateCheckResult
{
    UpdateAvailable,
    UpToDate,
    NoReleasesFound,
    CheckFailed
}

public sealed class UpdateInfo
{
    public UpdateCheckResult Result { get; set; } = UpdateCheckResult.UpToDate;
    public bool IsUpdateAvailable { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string ReleaseName { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    /// <summary>SHA-256 published by GitHub for this asset, when the release carries one.</summary>
    public string AssetSha256 { get; set; } = string.Empty;
    public long AssetSizeBytes { get; set; }
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>
    /// True when this build is a higher version than every published release. Worth saying out
    /// loud: a build reporting `2.0.0` while the releases walk `0.0.101` is not "up to date" in any
    /// useful sense, it is on a newer number than the release line and will never be offered one.
    /// </summary>
    public bool InstalledIsAhead { get; set; }

    /// <summary>Why the check answered what it did, for a log line the person can act on.</summary>
    public string Message { get; set; } = string.Empty;
}

public sealed class UpdateService
{
    public static UpdateService Instance { get; } = new();

    /// <summary>
    /// The release list, not the "latest" pointer.
    ///
    /// GitHub calls a release latest by publication time, which is not the same as the highest version:
    /// a v1.0.0 published after v1.0.103 would win the pointer while being older. Reading the list and
    /// comparing tags is the only way `0.0.100`…`0.0.102` and `v1.0.0`…`v1.0.103` both behave.
    /// </summary>
    private const string ReleasesApiUrl = "https://api.github.com/repos/ZerroDevs/AgentsHub/releases?per_page=30";
    private static readonly SemaphoreSlim _downloadLock = new(1, 1);

    // Only the project's own HTTPS release assets are installable. The renderer supplies the URL,
    // so an arbitrary host or scheme must never reach Process.Start.
    private static readonly string[] TrustedReleaseHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    ];

    private const long MaxInstallerBytes = 512L * 1024 * 1024;

    // Digest published by the release API, keyed by asset URL. Used to verify the bytes that
    // actually arrive, not only the URL they came from.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ExpectedDigests = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    static UpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AgentsHub-Updater/1.0 (Windows Desktop; contact: zerrodevs@github.com)");
    }

    public static string CurrentVersionString => AppInfo.Version;

    public async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var info = new UpdateInfo
        {
            CurrentVersion = CurrentVersionString
        };

        try
        {
            using var response = await _httpClient.GetAsync(ReleasesApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                info.Result = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? UpdateCheckResult.NoReleasesFound
                    : UpdateCheckResult.CheckFailed;
                info.Message = $"GitHub answered {(int)response.StatusCode}.";
                return info;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!ReadNewestRelease(body, info))
            {
                info.Result = UpdateCheckResult.NoReleasesFound;
                info.Message = "No published release carries a version tag.";
                return info;
            }

            if (!AppVersion.TryParse(info.CurrentVersion, out var current))
            {
                info.Result = UpdateCheckResult.CheckFailed;
                info.Message = $"This build reports a version that cannot be compared: {info.CurrentVersion}.";
                return info;
            }

            var latest = AppVersion.Parse(info.LatestVersion);
            bool newer = latest.IsNewerThan(current);

            info.IsUpdateAvailable = newer && !string.IsNullOrEmpty(info.DownloadUrl);
            info.InstalledIsAhead = !newer && current.IsNewerThan(latest);
            info.Result = info.IsUpdateAvailable ? UpdateCheckResult.UpdateAvailable : UpdateCheckResult.UpToDate;

            info.Message = info.IsUpdateAvailable
                ? $"v{info.LatestVersion} is available; this build is v{info.CurrentVersion}."
                : newer
                    ? $"v{info.LatestVersion} is published but has no installer attached."
                    : info.InstalledIsAhead
                        ? $"This build (v{info.CurrentVersion}) is ahead of the newest published release (v{info.LatestVersion})."
                        : $"Up to date (v{info.CurrentVersion}; newest published is v{info.LatestVersion}).";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            info.Result = UpdateCheckResult.CheckFailed;
            info.Message = $"The release list could not be read: {ex.Message}";
        }

        return info;
    }

    /// <summary>
    /// Fills <paramref name="info"/> from the release whose tag is the highest version, not the one
    /// published most recently. Drafts are skipped, and pre-releases are used only when no stable
    /// release exists — publishing a v1.0.0 beta must not roll everyone onto it.
    /// </summary>
    public static bool ReadNewestRelease(string json, UpdateInfo info)
    {
        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            JsonElement? best = null;
            AppVersion bestVersion = default;
            bool bestIsPreRelease = false;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object) continue;
                if (release.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True) continue;

                string tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
                if (!AppVersion.TryParse(tag, out var version)) continue;

                bool preRelease = release.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True;

                // A stable release always outranks a pre-release, whatever the numbers say.
                if (best != null)
                {
                    if (bestIsPreRelease && !preRelease)
                    {
                        // keep scanning: a stable one is strictly better
                    }
                    else if (!bestIsPreRelease && preRelease)
                    {
                        continue;
                    }
                    else if (!version.IsNewerThan(bestVersion))
                    {
                        continue;
                    }
                }

                best = release;
                bestVersion = version;
                bestIsPreRelease = preRelease;
            }

            if (best is not { } chosen) return false;

            info.LatestVersion = chosen.TryGetProperty("tag_name", out var chosenTag)
                ? (chosenTag.GetString() ?? string.Empty).Trim().TrimStart('v', 'V')
                : string.Empty;

            info.ReleaseName = chosen.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? info.LatestVersion : info.LatestVersion;
            info.ReleaseNotes = chosen.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;

            if (chosen.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTime(out var published))
            {
                info.PublishedAtUtc = published.ToUniversalTime();
            }

            info.DownloadUrl = string.Empty;
            info.AssetSha256 = string.Empty;
            info.AssetSizeBytes = 0;

            if (!chosen.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return true;

            string? bestUrl = null;
            string bestDigest = string.Empty;
            long bestSize = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;

                var assetName = asset.TryGetProperty("name", out var anProp) ? anProp.GetString() ?? string.Empty : string.Empty;
                if (!assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                var url = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() ?? string.Empty : string.Empty;
                var size = asset.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;
                var digest = asset.TryGetProperty("digest", out var dProp) ? dProp.GetString() ?? string.Empty : string.Empty;

                if (assetName.StartsWith("AgentsHub-Setup", StringComparison.OrdinalIgnoreCase))
                {
                    bestUrl = url;
                    bestSize = size;
                    bestDigest = digest;
                    break;
                }

                if (bestUrl == null)
                {
                    bestUrl = url;
                    bestSize = size;
                    bestDigest = digest;
                }
            }

            info.DownloadUrl = bestUrl ?? string.Empty;
            info.AssetSizeBytes = bestSize;
            info.AssetSha256 = bestDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? bestDigest[7..]
                : string.Empty;

            if (!string.IsNullOrEmpty(info.AssetSha256) && !string.IsNullOrEmpty(info.DownloadUrl))
            {
                ExpectedDigests[info.DownloadUrl] = info.AssetSha256;
            }

            return true;
        }
    }

    /// <summary>True only for an HTTPS asset on a host the GitHub release flow can serve from.</summary>
    public static bool IsTrustedReleaseUrl(string? downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        return TrustedReleaseHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    public async Task DownloadAndInstallAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new ArgumentException("Download URL cannot be empty.", nameof(downloadUrl));
        }

        if (!IsTrustedReleaseUrl(downloadUrl))
        {
            throw new ArgumentException("Refusing to install from an untrusted URL.", nameof(downloadUrl));
        }

        // Prevent parallel download attempts from colliding on the same file
        if (!await _downloadLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "AgentsHub_Updates");
            Directory.CreateDirectory(tempDir);

            var uri = new Uri(downloadUrl);
            var originalFileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(originalFileName) || !originalFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                originalFileName = "AgentsHub_Setup.exe";
            }

            var baseName = Path.GetFileNameWithoutExtension(originalFileName);
            var tempDownloadFile = Path.Combine(tempDir, $"{baseName}_{Guid.NewGuid():N}.tmp");
            string downloadedSha256 = string.Empty;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                if (totalBytes > MaxInstallerBytes)
                {
                    throw new InvalidOperationException("The update installer is larger than this app will download.");
                }

                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fileStream = new FileStream(tempDownloadFile, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    totalRead += read;

                    if (totalRead > MaxInstallerBytes)
                    {
                        throw new InvalidOperationException("The update installer is larger than this app will download.");
                    }

                    if (totalBytes > 0 && progress != null)
                    {
                        progress.Report(Math.Clamp((double)totalRead / totalBytes, 0.0, 1.0));
                    }
                }

                downloadedSha256 = Convert.ToHexString(hasher.GetHashAndReset());
            }

            if (ExpectedDigests.TryGetValue(downloadUrl, out var expectedSha256)
                && !string.Equals(expectedSha256, downloadedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tempDownloadFile); } catch { }
                throw new InvalidOperationException("The downloaded update did not match the published SHA-256 and was discarded.");
            }

            // Resolve executable path for launching
            var targetFile = Path.Combine(tempDir, originalFileName);
            string finalExecutable = tempDownloadFile;

            try
            {
                if (File.Exists(targetFile))
                {
                    File.Delete(targetFile);
                }
                File.Move(tempDownloadFile, targetFile);
                finalExecutable = targetFile;
            }
            catch
            {
                // If targetFile is currently locked by a running setup wizard or antivirus,
                // move to a unique .exe path so it executes cleanly
                var uniqueExe = Path.Combine(tempDir, $"{baseName}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.exe");
                try
                {
                    File.Move(tempDownloadFile, uniqueExe);
                    finalExecutable = uniqueExe;
                }
                catch
                {
                    finalExecutable = tempDownloadFile;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = finalExecutable,
                Arguments = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            };

            Process.Start(psi);

            // Allow installer to initiate and close current instance gracefully
            try
            {
#if WINDOWS
                if (Application.OpenForms.Count > 0 && Application.OpenForms[0]?.InvokeRequired == true)
                {
                    Application.OpenForms[0]?.BeginInvoke(new Action(() => Application.Exit()));
                }
                else
                {
                    Application.Exit();
                }
#else
                Environment.Exit(0);
#endif
            }
            catch
            {
                Environment.Exit(0);
            }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

}
