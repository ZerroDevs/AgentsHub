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
}

public sealed class UpdateService
{
    public static UpdateService Instance { get; } = new();

    private const string LatestReleaseApiUrl = "https://api.github.com/repos/ZerroDevs/AgentsHub/releases/latest";
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
            using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                info.Result = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? UpdateCheckResult.NoReleasesFound
                    : UpdateCheckResult.CheckFailed;
                return info;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            info.LatestVersion = tagName.Trim().TrimStart('v', 'V');
            info.ReleaseName = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName;
            info.ReleaseNotes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;

            if (root.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTime(out var pubDate))
            {
                info.PublishedAtUtc = pubDate.ToUniversalTime();
            }

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                string? bestUrl = null;
                string bestDigest = string.Empty;
                long bestSize = 0;

                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var anProp) ? anProp.GetString() ?? string.Empty : string.Empty;
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
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
            }

            if (TryParseVersion(info.LatestVersion, out var latestVer) &&
                TryParseVersion(info.CurrentVersion, out var currentVer))
            {
                info.IsUpdateAvailable = latestVer > currentVer && !string.IsNullOrEmpty(info.DownloadUrl);
                info.Result = info.IsUpdateAvailable ? UpdateCheckResult.UpdateAvailable : UpdateCheckResult.UpToDate;
            }
            else
            {
                info.Result = UpdateCheckResult.UpToDate;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            info.Result = UpdateCheckResult.CheckFailed;
        }

        return info;
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

    private static bool TryParseVersion(string input, out Version version)
    {
        var cleaned = input.Trim().TrimStart('v', 'V');
        if (Version.TryParse(cleaned, out var v))
        {
            version = v;
            return true;
        }

        if (Version.TryParse(cleaned + ".0", out v))
        {
            version = v;
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }
}
