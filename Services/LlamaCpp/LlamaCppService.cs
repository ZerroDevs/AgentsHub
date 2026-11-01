using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentsHub.Services.LlamaCpp;

/// <summary>What the local model server is doing right now.</summary>
public sealed record LlamaCppStatus(
    bool Installed,
    string Path,
    string Version,
    bool Running,
    int Port,
    string Endpoint,
    string Message,
    bool UsesCustomPath);

/// <summary>The result of asking GitHub what the newest llama.cpp release is.</summary>
public sealed record LlamaCppUpdate(
    bool Ok,
    string Current,
    string Latest,
    bool UpdateAvailable,
    string Url,
    string Message);

/// <summary>
/// Runs open models on this machine through llama.cpp.
///
/// `llama serve` (the vendor's front end) and a plain `llama-server` build both end up as one
/// OpenAI-compatible endpoint on 127.0.0.1:9931 that loads a model when a request names one. That
/// shape is why a local chat needs no new transport: it becomes a provider with a base URL, and the
/// existing bridge and Claude Code CLI drive it like any other gateway.
///
/// The binary is looked for in this order: a path the person pointed at, then PATH and the usual
/// install places, then a bounded scan of the folders a hand-built llama.cpp usually lands in.
/// Nothing starts on its own. A local model costs RAM and a GPU slot, and a person who never runs
/// one should not pay for it at launch.
/// </summary>
public sealed class LlamaCppService
{
    public static LlamaCppService Instance { get; } = new();

    public const int DefaultPort = 9931;

    /// <summary>The provider row a local chat runs on. The key is a placeholder the router ignores.</summary>
    public const string ProviderId = "local_llamacpp";
    public const string PlaceholderKey = "no-key";

    private const string ReleaseApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";
    private const int ScanDirectoryBudget = 4000;
    private static readonly TimeSpan ScanCacheLifetime = TimeSpan.FromSeconds(60);

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "agentshub-llamacpp.log");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly HttpClient Upstream = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub", "llama_settings.json");

    private readonly object _lock = new();
    private Process? _server;
    private string _message = "Not started.";

    private string? _scannedPath;
    private DateTime _scannedAt = DateTime.MinValue;

    /// <summary>Server output, so Settings → Live Logs shows the same lines the log file does.</summary>
    public event Action<string, string>? LogEmitted;

    public string Endpoint => $"http://127.0.0.1:{DefaultPort}/v1";

    public string CustomPath
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsPath)) return string.Empty;
                var settings = JsonSerializer.Deserialize<LlamaSettings>(File.ReadAllText(SettingsPath));
                return settings?.CustomPath?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public void SetCustomPath(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new LlamaSettings { CustomPath = path ?? string.Empty }));
        }
        catch (Exception ex)
        {
            Emit("error", $"Could not save the llama.cpp path: {ex.Message}");
        }

        lock (_lock)
        {
            _scannedPath = null;
            _scannedAt = DateTime.MinValue;
            _message = string.IsNullOrWhiteSpace(path) ? "Using automatic detection." : "Using the file you picked.";
        }
    }

    /// <summary>Absolute path of the server binary, or empty when none exists.</summary>
    public string ResolvedPath => Resolve()?.Path ?? string.Empty;

    public LlamaCppStatus Status()
    {
        var resolved = Resolve();
        bool running;

        lock (_lock)
        {
            running = _server is { HasExited: false };
        }

        return new LlamaCppStatus(
            Installed: resolved != null,
            Path: resolved?.Path ?? string.Empty,
            // Asked of the binary that will actually run, not of whatever sits on PATH: someone
            // pointing at their own build should see that build's version.
            Version: resolved == null ? string.Empty : VersionOf(resolved.Path),
            Running: running,
            Port: DefaultPort,
            Endpoint: Endpoint,
            Message: running ? $"Serving on 127.0.0.1:{DefaultPort}." : _message,
            UsesCustomPath: resolved?.Custom ?? false);
    }

    /// <summary>
    /// Starts the server and waits until it answers. Returns the state afterwards rather than
    /// throwing on a slow machine: the panel then shows how far it got.
    ///
    /// With no model path the server runs in router mode and loads whichever model a request names.
    /// With one, that file is preloaded — the shape a model picked from the shelf needs.
    /// </summary>
    public async Task<LlamaCppStatus> StartAsync(string? modelPath = null, CancellationToken ct = default)
    {
        var resolved = Resolve();
        if (resolved == null)
        {
            _message = "No llama.cpp server was found. Download it, or point at llama-server yourself.";
            return Status();
        }

        lock (_lock)
        {
            if (_server is { HasExited: false }) return Status();
        }

        // A server this app does not own can already own the port — a previous run whose process
        // outlived it, or another tool. Waiting for "listening" would then report success for
        // something we did not start.
        if (await IsListeningAsync(DefaultPort, ct).ConfigureAwait(false))
        {
            _message = $"Port {DefaultPort} is already used by another server. Stop it, then start again.";
            Emit("warn", _message);
            return Status();
        }

        Process? process = null;

        try
        {
            Emit("info", string.IsNullOrWhiteSpace(modelPath)
                ? $"Starting {Path.GetFileName(resolved.Path)} on port {DefaultPort}."
                : $"Loading {Path.GetFileName(modelPath)} on port {DefaultPort}.");

            process = Launch(resolved.Path, DefaultPort, modelPath);
            process.OutputDataReceived += (_, e) => Forward(e.Data);
            process.ErrorDataReceived += (_, e) => Forward(e.Data);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnServerExited(process);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_lock)
            {
                _server = process;
                _message = "Starting…";
            }

            // A router answers as soon as it listens; the first request is what loads a model, which
            // can take minutes on a large file. Waiting here only for the listener keeps the button
            // honest instead of blocking on a download.
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (process.HasExited)
                {
                    _message = $"The server exited with code {process.ExitCode}. {TailOfLog()}";
                    return Status();
                }

                if (await IsListeningAsync(DefaultPort, ct).ConfigureAwait(false))
                {
                    _message = $"Serving on 127.0.0.1:{DefaultPort}.";
                    Emit("info", _message);
                    return Status();
                }

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            _message = $"Still starting. {TailOfLog()}";
            return Status();
        }
        catch (Exception ex)
        {
            _message = $"Could not start llama.cpp: {ex.Message}";
            Emit("error", _message);
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            return Status();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                if (_server is { HasExited: false })
                {
                    _server.Kill(entireProcessTree: true);
                    Emit("info", "Local server stopped.");
                }
            }
            catch (Exception ex)
            {
                Emit("warn", $"Stop failed: {ex.GetType().Name}");
            }
            finally
            {
                _server = null;
                _message = "Stopped.";
            }
        }
    }

    /// <summary>
    /// Runs one model file and points the provider at it.
    ///
    /// A single-file server only serves the model it was started with, so switching models means a
    /// restart. The id the chat must ask for is whatever the server reports, not the file name.
    /// </summary>
    public async Task<(LlamaCppStatus Status, ClaudeGatewayProvider Provider)> RunModelAsync(string modelPath, CancellationToken ct = default)
    {
        Stop();
        await Task.Delay(300, ct).ConfigureAwait(false);

        var status = await StartAsync(modelPath, ct).ConfigureAwait(false);
        string modelId = await FirstModelIdAsync(ct).ConfigureAwait(false);

        if (modelId.Length == 0) modelId = Path.GetFileNameWithoutExtension(modelPath);

        var provider = EnsureProvider(modelId);
        return (status, provider);
    }

    /// <summary>The id a request has to name, read back from the server that just started.</summary>
    private async Task<string> FirstModelIdAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"{Endpoint}/models", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return string.Empty;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!json.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return string.Empty;

            return data[0].TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Adds (or refreshes) the provider a local chat runs on, and makes it the one new chats use.
    /// The key is a placeholder: a local server has no account to bill.
    /// </summary>
    public ClaudeGatewayProvider EnsureProvider(string modelId = "")
    {
        var stored = AiConfigService.Instance.GetClaudeGatewayProviders()
            .FirstOrDefault(p => string.Equals(p.Id, ProviderId, StringComparison.OrdinalIgnoreCase));

        var provider = new ClaudeGatewayProvider
        {
            Id = ProviderId,
            Name = "llama.cpp (local)",
            BaseUrl = Endpoint,
            ApiKey = PlaceholderKey,
            ApiKeys = [PlaceholderKey],
            IsPreset = false,
            // Router mode lists models on demand, so an empty id must not erase what a fetch found.
            Models = modelId.Length > 0 ? [modelId] : stored?.Models ?? [],
            SelectedModel = modelId.Length > 0 ? modelId : stored?.SelectedModel ?? string.Empty,
            // llama.cpp speaks the OpenAI shape; the app's bridge is what Claude Code reaches.
            Protocol = "openai",
            UseBridge = true
        };

        AiConfigService.Instance.SaveClaudeGatewayProvider(provider);
        AiConfigService.Instance.SetClaudeSelectedGatewayId(ProviderId);

        return AiConfigService.Instance.GetClaudeGatewayProviders()
            .FirstOrDefault(p => string.Equals(p.Id, ProviderId, StringComparison.OrdinalIgnoreCase))
            ?? provider;
    }

    /// <summary>
    /// Asks GitHub for the newest release and compares build numbers with the installed binary.
    /// A hand-built llama-server can say nothing useful, so the answer stays honest about that.
    /// </summary>
    public async Task<LlamaCppUpdate> CheckUpdateAsync(CancellationToken ct = default)
    {
        var path = ResolvedPath;
        if (path.Length == 0)
        {
            return new LlamaCppUpdate(false, "", "", false, ReleaseApi, "llama.cpp is not installed yet.");
        }

        string installed = ProbeVersionLine(path);
        int installedBuild = BuildNumber(installed);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
            request.Headers.UserAgent.ParseAdd("AgentsHub/2.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Upstream.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Emit("warn", $"Update check failed: GitHub answered {(int)response.StatusCode}.");
                return new LlamaCppUpdate(false, installed, "", false, ReleaseApi,
                    $"GitHub answered {(int)response.StatusCode}. Try again later.");
            }

            using var json = JsonDocument.Parse(body);
            string tag = json.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            string pageUrl = json.RootElement.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? ReleaseApi : ReleaseApi;

            // Upstream has shipped both `b11370` tags and semver ones, so both sides are compared in
            // whatever shape they actually have instead of assuming one.
            var installedSemver = Semver(installed);
            var latestSemver = Semver(tag);
            int latestBuild = BuildNumber(tag);

            bool newer;
            string latest;
            string current;

            if (installedSemver.HasValue && latestSemver.HasValue)
            {
                newer = CompareSemver(latestSemver.Value, installedSemver.Value) > 0;
                latest = Format(latestSemver.Value);
                current = Format(installedSemver.Value);
            }
            else if (installedBuild > 0 && latestBuild > 0)
            {
                newer = latestBuild > installedBuild;
                latest = $"b{latestBuild}";
                current = $"b{installedBuild}";
            }
            else
            {
                return new LlamaCppUpdate(true, installed, tag, false, pageUrl,
                    $"Newest release is {tag}. This build does not report a comparable version.");
            }

            Emit("info", $"Update check: installed {current}, newest {latest}.");

            return new LlamaCppUpdate(true, current, latest, newer, pageUrl, newer
                ? $"{latest} is available; you have {current}."
                : $"Up to date ({current}).");
        }
        catch (Exception ex)
        {
            Emit("warn", $"Update check failed: {ex.Message}");
            return new LlamaCppUpdate(false, installed, "", false, ReleaseApi, $"Could not reach GitHub: {ex.Message}");
        }
    }

    // ---- detection ----

    private sealed record ResolvedBinary(string Path, bool Custom);

    private sealed class LlamaSettings
    {
        public string CustomPath { get; set; } = "";
    }

    private ResolvedBinary? Resolve()
    {
        string custom = CustomPath;
        if (custom.Length > 0 && File.Exists(custom)) return new ResolvedBinary(custom, true);

        var detected = DependencyCheckerService.GetStatus("llamacpp");
        if (detected is { IsInstalled: true } && File.Exists(detected.Path)) return new ResolvedBinary(detected.Path, false);

        lock (_lock)
        {
            if (_scannedPath != null && DateTime.UtcNow - _scannedAt < ScanCacheLifetime)
            {
                return File.Exists(_scannedPath) ? new ResolvedBinary(_scannedPath, false) : null;
            }
        }

        var scanned = ScanForServer();
        lock (_lock)
        {
            _scannedPath = scanned;
            _scannedAt = DateTime.UtcNow;
        }

        if (scanned != null) Emit("info", $"Found a llama.cpp server at {scanned}.");
        return scanned == null ? null : new ResolvedBinary(scanned, false);
    }

    /// <summary>
    /// Looks for `llama-server` (the usual name in a hand-built tree) in the folders such a build
    /// lands in. Bounded by depth and a directory budget, so a big Downloads folder cannot stall the
    /// settings page.
    /// </summary>
    private static string? ScanForServer()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new[]
        {
            Path.Combine(userProfile, "llama.cpp"),
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "Documents"),
            Path.Combine(userProfile, "source"),
            Path.Combine(localAppData, "Programs"),
            Path.Combine(localAppData, "llama.cpp"),
            "/opt/llama.cpp",
            "/usr/local/llama.cpp",
            Path.Combine(userProfile, "llama")
        };

        int budget = ScanDirectoryBudget;

        foreach (var root in roots)
        {
            if (budget <= 0) break;
            if (!Directory.Exists(root)) continue;

            var hit = Walk(root, 3, ref budget);
            if (hit != null) return hit;
        }

        return null;
    }

    private static string? Walk(string directory, int depth, ref int budget)
    {
        if (budget <= 0) return null;

        try
        {
            foreach (var name in CandidateFileNames())
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }

            if (depth == 0) return null;

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (budget-- <= 0) return null;

                var info = new DirectoryInfo(child);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                var hit = Walk(child, depth - 1, ref budget);
                if (hit != null) return hit;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A protected folder is not a failure; the next root is tried.
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static string[] CandidateFileNames() => OperatingSystem.IsWindows()
        ? ["llama-server.exe", "llama.exe"]
        : ["llama-server", "llama"];

    // ---- process ----

    private static Process Launch(string binary, int port, string? modelPath = null)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(binary) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            FileName = binary
        };

        // A classic build has no `serve` subcommand: llama-server itself is the server. Passing the
        // router's subcommand to it would exit on an unknown argument.
        bool classicServer = Path.GetFileNameWithoutExtension(binary)
            .StartsWith("llama-server", StringComparison.OrdinalIgnoreCase);

        if (!classicServer) info.ArgumentList.Add("serve");
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(port.ToString());

        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            info.ArgumentList.Add("-m");
            info.ArgumentList.Add(modelPath);
        }

        try
        {
            return Process.Start(info)
                ?? throw new InvalidOperationException("llama.cpp did not start.");
        }
        catch (Exception) when (Services.Platform.PlatformHelper.IsWindows)
        {
            // A WindowsApps alias is a reparse point, and not every child launcher can follow one.
            // cmd resolves it the way a terminal would.
            var fallback = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                FileName = "cmd.exe",
                Arguments = modelPath is { Length: > 0 } ? $"/c \"\"{binary}\" {(classicServer ? "" : "serve ")}--port {port} -m \"{modelPath}\"\""
                    : classicServer
                        ? $"/c \"\"{binary}\" --port {port}\""
                        : $"/c \"\"{binary}\" serve --port {port}\""
            };

            return Process.Start(fallback)
                ?? throw new InvalidOperationException("llama.cpp did not start.");
        }
    }

    private void OnServerExited(Process process)
    {
        try
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_server, process)) return;
                _server = null;
            }

            Emit(process.ExitCode == 0 ? "info" : "error", $"Local server exited with code {process.ExitCode}.");
            _message = $"The server exited with code {process.ExitCode}. {TailOfLog()}";
        }
        catch
        {
            // Nothing here is worth breaking a turn over.
        }
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"http://127.0.0.1:{port}/v1/models", ct).ConfigureAwait(false);
            return (int)response.StatusCode is >= 200 and < 500;
        }
        catch
        {
            return false;
        }
    }

    // ---- logging ----

    private void Forward(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        Emit(LevelOf(line), line.Trim());
    }

    private static string LevelOf(string line) =>
        line.Contains(" error", StringComparison.OrdinalIgnoreCase)
        || line.Contains(" E ", StringComparison.Ordinal)
        || line.Contains("exited with", StringComparison.OrdinalIgnoreCase)
        ? "error"
        : line.Contains(" W ", StringComparison.Ordinal) || line.Contains("warn", StringComparison.OrdinalIgnoreCase)
            ? "warn"
            : "info";

    private void Emit(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        string line = message.Length > 500 ? message[..500] + "…" : message;

        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} [{level}] {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (IOException)
        {
            // Diagnostics must never break the server.
        }

        try
        {
            LogEmitted?.Invoke(level, line);
        }
        catch
        {
            // A dead listener is not a reason to lose the line.
        }
    }

    private static string TailOfLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return "No output yet.";
            var lines = File.ReadAllLines(LogPath);
            var last = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
            return last.Length > 160 ? "Last line: " + last[^160..] : "Last line: " + last;
        }
        catch
        {
            return "";
        }
    }

    // ---- versions ----

    /// <summary>A short, comparable label: `v0.8.0` when the build reports one, else its first line.</summary>
    private static string VersionOf(string binary)
    {
        string line = ProbeVersionLine(binary);
        var semver = Semver(line);
        return semver.HasValue ? Format(semver.Value) : line;
    }

    private static string ProbeVersionLine(string binary)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(info);
            if (process == null) return "";

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            return first.Length > 120 ? first[..120] : first;
        }
        catch
        {
            return "";
        }
    }

    private static (int Major, int Minor, int Patch)? Semver(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = Regex.Match(text, @"(\d+)\.(\d+)\.(\d+)");
        if (!match.Success) return null;

        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    private static int CompareSemver((int Major, int Minor, int Patch) left, (int Major, int Minor, int Patch) right)
    {
        if (left.Major != right.Major) return left.Major.CompareTo(right.Major);
        if (left.Minor != right.Minor) return left.Minor.CompareTo(right.Minor);
        return left.Patch.CompareTo(right.Patch);
    }

    private static string Format((int Major, int Minor, int Patch) version) => $"v{version.Major}.{version.Minor}.{version.Patch}";

    /// <summary>
    /// The build number worth comparing. `llama --version` prints "… (build 11370, commit …)"; a
    /// plain llama-server prints "version: 4791 (hash)". A build-tag release is `b11370`.
    /// </summary>
    private static int BuildNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var withBuild = Regex.Match(text, @"\(build\s+(\d+)", RegexOptions.IgnoreCase);
        if (withBuild.Success && int.TryParse(withBuild.Groups[1].Value, out var build)) return build;

        var tagOnly = Regex.Match(text, @"^b(\d+)$", RegexOptions.IgnoreCase);
        if (tagOnly.Success && int.TryParse(tagOnly.Groups[1].Value, out var tag)) return tag;

        var plain = Regex.Match(text, @"^\s*(\d{3,})\b");
        if (plain.Success && int.TryParse(plain.Groups[1].Value, out var number)) return number;

        return 0;
    }
}
