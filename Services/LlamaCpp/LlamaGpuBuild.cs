using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace AgentsHub.Services.LlamaCpp;

/// <summary>
/// The official llama.cpp build that can use the graphics card.
///
/// llama.cpp ships one binary per compute backend, and a generic installer can land on the CPU one:
/// the build then spends every prompt on the processor and the GPU layers setting has nothing to
/// point at. GitHub publishes CUDA, Vulkan and Metal builds, so this fetches the best asset for the
/// machine, unpacks it under the app's own folder, and checks that the binary really sees a device
/// before it is kept.
/// </summary>
public static class LlamaGpuBuild
{
    private const string ReleasesApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=10";

    /// <summary>A build larger than this is not a llama.cpp archive, so the download stops.</summary>
    private const long MaxBytes = 900L * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>One downloadable archive, named well enough to say what it is in a log line.</summary>
    public sealed record Asset(string Tag, string Name, Uri Url, string Backend);

    /// <summary>
    /// A build to try: the server archive, plus the CUDA runtime beside it when the build needs one.
    /// </summary>
    public sealed record Plan(string Backend, Asset Main, Asset? Runtime);

    /// <summary>
    /// The backend this machine should try first. CUDA is faster on an NVIDIA card and the runtime can
    /// be fetched with it; Vulkan drives NVIDIA, AMD and Intel from one file; macOS has Metal built in.
    /// </summary>
    public static string BackendName { get; } =
        OperatingSystem.IsMacOS() ? "Metal" : HasNvidia() ? "CUDA" : "Vulkan";

    /// <summary>Whether llama.cpp publishes any build for this platform at all.</summary>
    public static bool Supported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>Where the unpacked build lives. Kept beside the app's other state, not in PATH.</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentsHub",
        "llama-gpu");

    private static bool IsArm => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
        == System.Runtime.InteropServices.Architecture.Arm64;

    /// <summary>
    /// The archives this machine can run, best first.
    ///
    /// The releases list is read rather than the "latest" endpoint because llama.cpp tags both
    /// numbered builds and semver ones, and the semver tag has no binaries attached — trusting it
    /// would announce a download that does not exist.
    /// </summary>
    public static async Task<IReadOnlyList<Plan>> PlansAsync(CancellationToken ct = default)
    {
        if (!Supported) return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.UserAgent.ParseAdd("AgentsHub/2.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub answered {(int)response.StatusCode} when asked for llama.cpp releases.");
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];

        string[] wanted = OperatingSystem.IsMacOS()
            ? ["Metal"]
            : HasNvidia() ? ["CUDA", "Vulkan"] : ["Vulkan"];

        foreach (var release in json.RootElement.EnumerateArray())
        {
            string tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            var found = new List<Asset>();

            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                if (name.Length == 0 || url.Length == 0) continue;

                string? backend = DescribeBackend(name);
                if (backend == null) continue;

                found.Add(new Asset(tag.Length > 0 ? tag : name, name, new Uri(url), backend));
            }

            if (found.Count == 0) continue;

            // Both plans come from one release, so falling back to Vulkan does not also change build.
            var plans = new List<Plan>();

            foreach (string backend in wanted)
            {
                var main = found.FirstOrDefault(a => a.Backend == backend && !a.Name.StartsWith("cudart", StringComparison.OrdinalIgnoreCase));
                if (main == null) continue;

                var runtime = found.FirstOrDefault(a =>
                    a.Name.StartsWith("cudart", StringComparison.OrdinalIgnoreCase) && SharesVersion(a.Name, main.Name));

                plans.Add(new Plan(backend, main, runtime));
            }

            if (plans.Count > 0) return plans;
        }

        return [];
    }

    /// <summary>
    /// The backend an asset belongs to, or null when it is not a build this app can run.
    /// The archive names carry the platform and the compute backend, which is all that is needed.
    /// </summary>
    private static string? DescribeBackend(string name)
    {
        string arch = IsArm ? "arm64" : "x64";

        if (OperatingSystem.IsWindows())
        {
            if (!name.StartsWith("llama", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("cudart", StringComparison.OrdinalIgnoreCase)) return null;
            if (!name.EndsWith($"-{arch}.zip", StringComparison.OrdinalIgnoreCase)) return null;

            if (name.Contains("-win-cuda-", StringComparison.OrdinalIgnoreCase)) return "CUDA";
            if (name.Contains("-win-vulkan-", StringComparison.OrdinalIgnoreCase)) return "Vulkan";
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            // Metal is compiled into the macOS build; there is no separate asset to look for.
            return name.EndsWith($"-bin-macos-{arch}.tar.gz", StringComparison.OrdinalIgnoreCase) ? "Metal" : null;
        }

        if (!name.EndsWith($"-{arch}.tar.gz", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Contains("-ubuntu-cuda-", StringComparison.OrdinalIgnoreCase)) return "CUDA";
        if (name.Contains("-ubuntu-vulkan-", StringComparison.OrdinalIgnoreCase)) return "Vulkan";
        return null;
    }

    /// <summary>
    /// Whether a CUDA runtime archive belongs to the same build as the server archive.
    ///
    /// The cudart packages are named after the toolkit (`cudart-llama-bin-win-cuda-13.4-x64.zip`)
    /// while the server names its build number, so the shareable part is the CUDA version.
    /// </summary>
    private static bool SharesVersion(string runtime, string server)
    {
        string? wanted = CudaVersion(server);
        return wanted != null && string.Equals(CudaVersion(runtime), wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CudaVersion(string name)
    {
        int at = name.IndexOf("cuda-", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        string rest = name[(at + 5)..];
        int end = rest.IndexOf('-');
        return end > 0 ? rest[..end] : rest;
    }

    /// <summary>
    /// Downloads the best build that actually sees a device, and returns the server binary.
    ///
    /// Each candidate is unpacked beside the final folder and run with `--list-devices` before it is
    /// kept: a CUDA build on a driver too old for it starts fine and finds nothing, and that answer is
    /// the only honest proof that the switch worked. A partial or failed download never leaves a
    /// folder that looks installed but has nothing to run.
    /// </summary>
    public static async Task<(string ServerPath, Plan Plan, string Device)> InstallAsync(
        Action<string> report, CancellationToken ct = default)
    {
        if (!Supported) throw new InvalidOperationException("llama.cpp publishes no GPU build for this platform.");

        report($"Looking up the newest {BackendName} build on GitHub…");
        var plans = await PlansAsync(ct).ConfigureAwait(false);

        if (plans.Count == 0)
        {
            throw new InvalidOperationException(
                "No GPU build is attached to the recent llama.cpp releases. The vendor installer is the fallback.");
        }

        // A build this app already unpacked for the same release is not downloaded again. The marker
        // names the asset, so a newer release invalidates it instead of reusing a stale binary.
        var kept = await ReusableAsync(plans, ct).ConfigureAwait(false);
        if (kept != null)
        {
            report($"Using the {kept.Value.Plan.Backend} build already unpacked; it sees {kept.Value.Device}.");
            return kept.Value;
        }

        string failure = string.Empty;

        foreach (var plan in plans)
        {
            report($"Downloading {plan.Main.Name} ({plan.Main.Tag})…");

            string staging = InstallDirectory + $".staging-{Guid.NewGuid():N}";

            try
            {
                Directory.CreateDirectory(staging);

                string main = Path.Combine(Path.GetTempPath(), $"agentshub-llama-{Guid.NewGuid():N}{Extension(plan.Main.Name)}");
                try
                {
                    await DownloadAsync(plan.Main.Url, main, report, ct).ConfigureAwait(false);

                    if (plan.Runtime != null)
                    {
                        report($"Downloading the CUDA runtime ({plan.Runtime.Name})…");

                        string runtime = Path.Combine(Path.GetTempPath(), $"agentshub-llama-{Guid.NewGuid():N}{Extension(plan.Runtime.Name)}");
                        try
                        {
                            await DownloadAsync(plan.Runtime.Url, runtime, report, ct).ConfigureAwait(false);
                            report("Unpacking the build…");
                            Extract(main, staging);
                            Extract(runtime, staging);
                        }
                        finally
                        {
                            TryDeleteFile(runtime);
                        }
                    }
                    else
                    {
                        report("Unpacking the build…");
                        Extract(main, staging);
                    }
                }
                finally
                {
                    TryDeleteFile(main);
                }

                string? server = FindServer(staging)
                    ?? throw new InvalidOperationException("The archive did not contain llama-server.");

                if (!OperatingSystem.IsWindows()) MakeExecutable(staging);

                // Where the server sits inside the archive, so the path stays right after the move.
                string relative = Path.GetRelativePath(staging, server);

                string? device = await FirstDeviceAsync(server, ct).ConfigureAwait(false);
                if (device == null)
                {
                    failure = $"The {plan.Backend} build starts but sees no device.";
                    report($"{failure} Trying the next build.");
                    TryDelete(staging);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(InstallDirectory) ?? InstallDirectory);

                // Locked files in the old folder mean a server is still running; the caller stops it,
                // and the error says so instead of reporting a half-done install.
                if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, recursive: true);
                Directory.Move(staging, InstallDirectory);

                try
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(InstallDirectory, "build.txt"), plan.Main.Name, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The marker only saves a later download; the build itself is in place.
                }

                report($"Unpacked the {plan.Main.Tag} {plan.Backend} build; it sees {device}.");
                return (Path.Combine(InstallDirectory, relative), plan, device);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex.Message;
                report($"{plan.Backend}: {ex.Message}");
                TryDelete(staging);
            }
        }

        throw new InvalidOperationException(failure.Length > 0
            ? failure
            : "No llama.cpp build on GitHub could use this machine's graphics card.");
    }

    /// <summary>
    /// The build already on disk, when it is the same asset the newest release offers and it still
    /// sees a device. Anything else is downloaded again.
    /// </summary>
    private static async Task<(string ServerPath, Plan Plan, string Device)?> ReusableAsync(
        IReadOnlyList<Plan> plans, CancellationToken ct)
    {
        string marker = Path.Combine(InstallDirectory, "build.txt");
        string recorded;

        try
        {
            if (!File.Exists(marker)) return null;
            recorded = (await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (recorded.Length == 0) return null;

        var plan = plans.FirstOrDefault(p => string.Equals(p.Main.Name, recorded, StringComparison.OrdinalIgnoreCase));
        if (plan == null) return null;

        string? server = FindServer(InstallDirectory);
        if (server == null) return null;

        string? device = await FirstDeviceAsync(server, ct).ConfigureAwait(false);
        return device == null ? null : (server, plan, device);
    }

    /// <summary>The first device a freshly unpacked server reports, or null when it sees none.</summary>
    private static async Task<string?> FirstDeviceAsync(string serverPath, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = "--list-devices",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process == null) return null;

            var outText = process.StandardOutput.ReadToEndAsync(ct);
            var errText = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string output = await outText.ConfigureAwait(false) + await errText.ConfigureAwait(false);

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                string head = line[..colon].Trim();
                string[] kinds = ["CUDA", "Vulkan", "Metal", "SYCL", "ROCm", "HIP", "OpenCL"];

                foreach (string kind in kinds)
                {
                    if (!head.StartsWith(kind, StringComparison.OrdinalIgnoreCase)) continue;

                    string name = line[(colon + 1)..].Trim();
                    int paren = name.IndexOf('(');
                    if (paren > 0) name = name[..paren].Trim();

                    return name.Length > 0 ? $"{kind} on {name}" : kind;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // A binary that will not start is a build that cannot serve either.
        }

        return null;
    }

    private static string Extension(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz";

    private static async Task DownloadAsync(Uri url, string destination, Action<string> report, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? 0;
        if (total > MaxBytes) throw new InvalidOperationException($"The download is {total / 1048576} MB, larger than this app will fetch.");

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long received = 0;
        int reported = -1;

        while (true)
        {
            int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;

            if (received > MaxBytes) throw new InvalidOperationException("The download grew past the size this app will fetch.");

            // Percent only when the size is known; every 5% is enough to look alive without flooding.
            int percent = total > 0 ? (int)(received * 100 / total) : 0;
            if (total > 0 && percent / 5 != reported / 5)
            {
                reported = percent;
                report($"Downloading… {percent}% of {total / 1048576} MB");
            }
        }
    }

    private static void Extract(string archive, string folder)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, folder, overwriteFiles: true);
            return;
        }

        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, folder, overwriteFiles: true);
    }

    private static string? FindServer(string folder)
    {
        string name = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)) return file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder the walk cannot enter holds no server this app could run either.
        }

        return null;
    }

    /// <summary>An archive keeps its modes unevenly, and a server without +x is a file, not a program.</summary>
    private static void MakeExecutable(string folder)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith("llama", StringComparison.OrdinalIgnoreCase)) continue;

                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the vendor installer's own files are already executable.
        }
    }

    /// <summary>
    /// Whether this machine has an NVIDIA card, which is what makes a CUDA build worth trying first.
    /// nvidia-smi ships with the driver, so a missing command means no CUDA to offload to.
    /// </summary>
    private static bool HasNvidia()
    {
        if (OperatingSystem.IsMacOS()) return false;

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process == null) return false;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);

            return process.HasExited && process.ExitCode == 0 && output.Trim().Length > 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
