using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentsHub.Services.LlamaCpp;

/// <summary>
/// The flags the server is started with. llama-server's own defaults are conservative — no GPU
/// offload at all — so the ones that decide whether a reply takes one second or one minute are
/// exposed here instead of being guessed per machine.
/// </summary>
public sealed record LlamaRuntimeOptions(
    int ContextSize,
    int GpuLayers,
    int Threads,
    int BatchSize,
    string CacheTypeK,
    string CacheTypeV,
    bool FlashAttention,
    string ExtraArgs,
    double Temperature,
    int MaxTokens)
{
    public static LlamaRuntimeOptions Default => new(49152, 99, 0, 512, "f16", "f16", true, string.Empty, 0.7, 4096);
}

/// <summary>What the local model server is doing right now.</summary>
public sealed record LlamaCppStatus(
    bool Installed,
    string Path,
    string Version,
    bool Running,
    int Port,
    string Endpoint,
    string Message,
    bool UsesCustomPath,
    string LoadedModel,
    bool RunOnRequest,
    bool AllowMultiple,
    LlamaRuntimeOptions Runtime,
    string Runner,
    string Gpu,
    int IdleUnloadMinutes,
    int UnloadInSeconds,
    string Backend);

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
/// OpenAI-compatible endpoint on 127.0.0.1:8080 that answers the chat. That shape is why a local
/// chat needs no new transport: it becomes a provider with a base URL, and the existing bridge and
/// Claude Code CLI drive it like any other gateway.
///
/// The binary is looked for in this order: a path the person pointed at, then PATH and the usual
/// install places, then a bounded scan of the folders a hand-built llama.cpp usually lands in.
/// Nothing starts on its own. A local model costs RAM and a GPU slot, and a person who never runs
/// one should not pay for it at launch.
/// </summary>
public sealed class LlamaCppService
{
    public static LlamaCppService Instance { get; } = new();

    public const int DefaultPort = 8080;

    /// <summary>
    /// The port the server listens on: the one in settings, else <see cref="DefaultPort"/>. Configurable
    /// because 8080 is a popular port and another tool on this machine can already hold it.
    /// </summary>
    public int EffectivePort
    {
        get
        {
            int port = ReadSettings().Port;
            return port is > 0 and < 65536 ? port : DefaultPort;
        }
    }

    /// <summary>True while a start is in flight, so two clicks cannot spawn two servers.</summary>
    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>
    /// The context one slot gets. llama-server defaults to 4096, and Claude Code's own system prompt
    /// plus tool definitions are about 32k tokens before a word is typed, so every turn through the
    /// bridge failed with "request exceeds the available context size". This is the smallest size
    /// that leaves room for a conversation on top of that; it costs KV-cache memory, not model memory.
    /// </summary>
    public const int DefaultContextSize = 49152;

    /// <summary>The provider row a local chat runs on. The key is a placeholder the router ignores.</summary>
    public const string ProviderId = "local_llamacpp";
    public const string PlaceholderKey = "no-key";

    private const string ReleaseApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";
    private const int ScanDirectoryBudget = 4000;
    private static readonly TimeSpan ScanCacheLifetime = TimeSpan.FromSeconds(60);

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "agentshub-llamacpp.log");

    /// <summary>The pid of the server this app started, so a crashed run can be cleaned up next time.</summary>
    private static readonly string PidPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub", "llama_server.pid");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly HttpClient Upstream = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub", "llama_settings.json");

    private readonly object _lock = new();
    private Process? _server;
    private string _message = "Not started.";

    /// <summary>
    /// What the installed build offloads to, read from the binary once. Empty until the first probe:
    /// the shelf shows that as "checking" rather than guessing.
    /// </summary>
    private string _backend = string.Empty;
    private DateTime _backendAt = DateTime.MinValue;

    // ---- idle unload ----

    private readonly object _idleLock = new();
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private int _activeRequests;
    // Named in full: the desktop build also sees System.Windows.Forms.Timer, which is a different clock.
    private System.Threading.Timer? _idleTimer;

    /// <summary>
    /// The process actually answering on the port. The llama.app front end can hand the work to a
    /// child and exit, so the launcher's own pid is not always the server that has to be stopped.
    /// </summary>
    private int _listenerPid;

    private string? _scannedPath;
    private DateTime _scannedAt = DateTime.MinValue;

    /// <summary>True once the load lines said where the layers went, so the CPU warning is said once.</summary>
    private bool _offloadReported;

    /// <summary>GPU layers the running server was started with, per-model flags included.</summary>
    private int _startedGpuLayers;

    /// <summary>Server output, so Settings → Live Logs shows the same lines the log file does.</summary>
    public event Action<string, string>? LogEmitted;

    /// <summary>
    /// Raised when the server reports how fast it generated. The number comes from llama.cpp's own
    /// timing line, which it prints when a reply ends, so this is measured, not estimated.
    /// </summary>
    public event Action? RateChanged;

    /// <summary>Generation speed of the last reply, in tokens per second. Zero until one is reported.</summary>
    public double TokensPerSecond { get; private set; }

    /// <summary>
    /// The reply's own average, from the server's `eval time` line: total tokens divided by total
    /// generation time. It is lower than the peak figure llama.cpp also prints, which is why both
    /// are shown rather than one standing in for the other.
    /// </summary>
    public double AverageTokensPerSecond { get; private set; }

    /// <summary>Tokens in that reply, as the same timing line counts them.</summary>
    public int GeneratedTokens { get; private set; }

    /// <summary>
    /// What the model load actually offloaded, read from the server's own load lines — e.g.
    /// `29/29 layers on CUDA0`. Empty until a model reports it, and the honest answer when a
    /// machine has no usable GPU.
    /// </summary>
    public string GpuSummary { get; private set; } = string.Empty;

    /// <summary>
    /// Raised when the server starts or stops, whoever asked. A chat, Settings and the shelf all show
    /// the same state, so each of them needs to hear about a change made by the others.
    /// </summary>
    public event Action? StateChanged;

    private void NotifyStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
            // A dead listener is not a reason to fail a start.
        }
    }

    public string Endpoint => $"http://127.0.0.1:{EffectivePort}/v1";

    public string CustomPath => ReadSettings().CustomPath.Trim();

    /// <summary>
    /// Whether a chat that needs a local model may start one on its own. Off means the person runs a
    /// model from the shelf first, which is the only way to keep a machine free of surprise model loads.
    /// </summary>
    public bool RunOnRequest => ReadSettings().RunOnRequest;

    /// <summary>The file currently served, or empty. Set by RunModelAsync, cleared on stop.</summary>
    public string LoadedModelPath { get; private set; } = string.Empty;

    /// <summary>Idle minutes before the model is unloaded. Zero means it stays loaded.</summary>
    public int IdleUnloadMinutes
    {
        get
        {
            int minutes = ReadSettings().IdleUnloadMinutes;
            return minutes is >= 1 and <= 1440 ? minutes : 0;
        }
    }

    public void SetIdleUnloadMinutes(int minutes)
    {
        var settings = ReadSettings();
        settings.IdleUnloadMinutes = Math.Clamp(minutes, 0, 1440);
        WriteSettings(settings);
        RestartIdleTimer();
    }

    /// <summary>Marks the server as in use, which pushes the idle deadline out.</summary>
    public void NoteActivity()
    {
        lock (_idleLock) _lastActivityUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// A turn in flight counts as activity even when it is quiet for a minute: prompt processing on a
    /// large context produces no traffic, and unloading under it would kill the turn.
    /// </summary>
    public void BeginRequest()
    {
        Interlocked.Increment(ref _activeRequests);
        NoteActivity();
    }

    public void EndRequest()
    {
        Interlocked.Decrement(ref _activeRequests);
        NoteActivity();
    }

    /// <summary>Seconds left before the idle unload, or zero when it is off or already due.</summary>
    public int SecondsUntilUnload()
    {
        int minutes = IdleUnloadMinutes;
        if (minutes <= 0) return 0;

        double idle;
        lock (_idleLock) idle = (DateTime.UtcNow - _lastActivityUtc).TotalSeconds;

        double left = minutes * 60 - idle;
        return left <= 0 ? 0 : (int)Math.Round(left);
    }

    private void RestartIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;

        if (IdleUnloadMinutes <= 0) return;

        // Checked on a timer rather than on a request: the whole point is to act when nothing is
        // happening, so there is no traffic to hang the check on.
        _idleTimer = new System.Threading.Timer(_ => UnloadIfIdle(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15));
    }

    private void UnloadIfIdle()
    {
        int minutes = IdleUnloadMinutes;
        if (minutes <= 0 || !AnyServerRunning) return;

        if (Volatile.Read(ref _activeRequests) > 0)
        {
            NoteActivity();
            return;
        }

        double idle;
        lock (_idleLock) idle = (DateTime.UtcNow - _lastActivityUtc).TotalMinutes;

        if (idle < minutes) return;

        Emit("info", $"Unloaded the local model after {Math.Floor(idle)} minute{(Math.Floor(idle) == 1 ? "" : "s")} idle, freeing its memory.");
        Stop();
    }

    /// <summary>True while a server this app started is still alive.</summary>
    public bool AnyServerRunning
    {
        get
        {
            lock (_lock)
            {
                return _server is { HasExited: false } || IsAlive(_listenerPid);
            }
        }
    }

    /// <summary>The flags a start would use right now.</summary>
    public LlamaRuntimeOptions Runtime => RuntimeOf(ReadSettings());

    /// <summary>
    /// How a local chat answers: `direct` talks to the server's OpenAI endpoint from this app,
    /// `claude` hands the turn to the Claude Code CLI through the translating bridge.
    /// </summary>
    public string Runner => NormaliseRunner(ReadSettings().Runner);

    public void SetRunner(string runner)
    {
        var settings = ReadSettings();
        settings.Runner = NormaliseRunner(runner);
        WriteSettings(settings);
    }

    private static string NormaliseRunner(string? runner) =>
        string.Equals(runner, "claude", StringComparison.OrdinalIgnoreCase) ? "claude" : "direct";

    /// <summary>
    /// Saves the launch flags. A running server keeps the flags it was started with, so changing one
    /// stops it — the next start uses the new ones, which is the only way the change can take effect.
    /// </summary>
    public bool SetRuntimeOptions(LlamaRuntimeOptions options)
    {
        var clean = Sanitise(options);
        var settings = ReadSettings();

        // Sampling is sent with every request rather than at startup, so editing it must not cost a
        // reload of the model. Only the flags the process is launched with count as a change.
        bool changed = LaunchFlags(RuntimeOf(settings)) != LaunchFlags(clean);

        settings.ContextSize = clean.ContextSize;
        settings.GpuLayers = clean.GpuLayers;
        settings.Threads = clean.Threads;
        settings.BatchSize = clean.BatchSize;
        settings.CacheTypeK = clean.CacheTypeK;
        settings.CacheTypeV = clean.CacheTypeV;
        settings.FlashAttention = clean.FlashAttention;
        settings.ExtraArgs = clean.ExtraArgs;
        settings.Temperature = clean.Temperature;
        settings.MaxTokens = clean.MaxTokens;
        WriteSettings(settings);

        if (changed && AnyServerRunning) Stop();
        return changed;
    }

    private static LlamaRuntimeOptions LaunchFlags(LlamaRuntimeOptions options) =>
        options with { Temperature = 0, MaxTokens = 0 };

    /// <summary>Clamps one submitted value per field. Anything unreadable falls back to the default.</summary>
    private static LlamaRuntimeOptions Sanitise(LlamaRuntimeOptions options)
    {
        static int Clamp(int value, int min, int max, int fallback) =>
            value <= 0 ? fallback : Math.Clamp(value, min, max);

        var fallback = LlamaRuntimeOptions.Default;

        return new LlamaRuntimeOptions(
            // A context under 8k cannot hold a chat plus a system prompt; over 1M is a typo, not a plan.
            ContextSize: Clamp(options.ContextSize, 8192, 1_048_576, fallback.ContextSize),
            GpuLayers: options.GpuLayers < 0 ? 0 : Math.Min(options.GpuLayers, 512),
            Threads: Clamp(options.Threads, 1, 256, 0),
            BatchSize: Clamp(options.BatchSize, 32, 8192, fallback.BatchSize),
            CacheTypeK: NormalisedCache(options.CacheTypeK),
            CacheTypeV: NormalisedCache(options.CacheTypeV),
            FlashAttention: options.FlashAttention,
            ExtraArgs: (options.ExtraArgs ?? string.Empty).Trim(),
            // A sampler at 0 would be greedy; 2 is where a model starts inventing words.
            Temperature: Math.Round(Math.Clamp(options.Temperature > 0 ? options.Temperature : fallback.Temperature, 0.01, 2.0), 2),
            MaxTokens: Clamp(options.MaxTokens, 128, 131_072, fallback.MaxTokens));
    }

    private static LlamaRuntimeOptions RuntimeOf(LlamaSettings settings) => new(
        settings.ContextSize > 0 ? settings.ContextSize : LlamaRuntimeOptions.Default.ContextSize,
        settings.GpuLayers,
        settings.Threads,
        settings.BatchSize > 0 ? settings.BatchSize : LlamaRuntimeOptions.Default.BatchSize,
        string.IsNullOrWhiteSpace(settings.CacheTypeK) ? "f16" : settings.CacheTypeK,
        string.IsNullOrWhiteSpace(settings.CacheTypeV) ? "f16" : settings.CacheTypeV,
        settings.FlashAttention,
        settings.ExtraArgs ?? string.Empty,
        settings.Temperature > 0 ? settings.Temperature : LlamaRuntimeOptions.Default.Temperature,
        settings.MaxTokens > 0 ? settings.MaxTokens : LlamaRuntimeOptions.Default.MaxTokens);

    public void SetCustomPath(string path)
    {
        var settings = ReadSettings();
        settings.CustomPath = path ?? string.Empty;
        WriteSettings(settings);

        lock (_lock)
        {
            _scannedPath = null;
            _scannedAt = DateTime.MinValue;
            // A different binary can be a different backend, so the cached answer goes with it.
            _backend = string.Empty;
            _backendAt = DateTime.MinValue;
            _message = string.IsNullOrWhiteSpace(path) ? "Using automatic detection." : "Using the file you picked.";
        }
    }

    public void SetRunOnRequest(bool enabled)
    {
        var settings = ReadSettings();
        settings.RunOnRequest = enabled;
        WriteSettings(settings);
    }

    /// <summary>
    /// Off: one model in memory, a new one replaces the old. On: router mode, several at once.
    /// Changing it stops the server, because the running process was started the other way.
    /// </summary>
    public void SetAllowMultipleModels(bool enabled)
    {
        var settings = ReadSettings();
        settings.AllowMultipleModels = enabled;
        WriteSettings(settings);

        Stop();
    }

    /// <summary>Moves the server to another port. A running server is stopped; the provider follows.</summary>
    public bool SetPort(int port)
    {
        if (port is < 1025 or > 65535) return false;

        int before = EffectivePort;
        var settings = ReadSettings();
        settings.Port = port;
        WriteSettings(settings);

        if (before != port) Stop();
        RepointProvider();

        return true;
    }

    /// <summary>
    /// Keeps a saved provider pointing at the port the server actually uses. Without this, a port
    /// change leaves the chat sending to a port nothing listens on.
    /// </summary>
    public void RepointProvider()
    {
        var stored = AiConfigService.Instance.GetClaudeGatewayProviders()
            .FirstOrDefault(p => string.Equals(p.Id, ProviderId, StringComparison.OrdinalIgnoreCase));

        if (stored == null) return;
        if (string.Equals(stored.BaseUrl, Endpoint, StringComparison.OrdinalIgnoreCase)) return;

        stored.BaseUrl = Endpoint;
        AiConfigService.Instance.SaveClaudeGatewayProvider(stored);
    }

    private LlamaSettings ReadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new LlamaSettings();
            return JsonSerializer.Deserialize<LlamaSettings>(File.ReadAllText(SettingsPath)) ?? new LlamaSettings();
        }
        catch
        {
            return new LlamaSettings();
        }
    }

    private void WriteSettings(LlamaSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            Emit("error", $"Could not save the llama.cpp settings: {ex.Message}");
        }
    }

    /// <summary>Absolute path of the server binary, or empty when none exists.</summary>
    public string ResolvedPath => Resolve()?.Path ?? string.Empty;

    /// <summary>
    /// The compute backend the installed build will use, asked of the binary instead of inferred from
    /// the load log. A build without CUDA/Vulkan/Metal spends minutes loading a model onto the CPU,
    /// and saying so beforehand is the difference between a setting that helps and one that lies.
    /// </summary>
    public async Task<string> BackendAsync(CancellationToken ct = default)
    {
        var resolved = Resolve();
        if (resolved == null) return "Not installed";

        if (_backend.Length > 0 && DateTime.UtcNow - _backendAt < TimeSpan.FromMinutes(10)) return _backend;

        string backend = string.Empty;
        bool listedDevices = false;

        // The vendor front end may not take the flag at all while the server it launches does, so both
        // are asked before anything is concluded.
        foreach (string binary in ProbeCandidates(resolved.Path))
        {
            var devices = await RunBinaryAsync(binary, "--list-devices", ct).ConfigureAwait(false);
            if (!devices.Ran) continue;

            listedDevices |= devices.Output.Contains("Available devices", StringComparison.OrdinalIgnoreCase);

            backend = ParseBackend(devices.Output);
            if (backend.Length > 0) break;
        }

        if (backend.Length == 0)
        {
            // Older builds have no --list-devices; the version banner still names the backends it has.
            var version = await RunBinaryAsync(resolved.Path, "--version", ct).ConfigureAwait(false);
            backend = ParseBackend(version.Output);
        }

        // An empty device list is evidence of a CPU-only build. A binary that refused every probe is
        // unknown, and calling that "CPU only" would be a guess dressed as a fact.
        _backend = backend.Length > 0 ? backend : listedDevices ? "CPU only" : "Unknown";
        _backendAt = DateTime.UtcNow;

        return _backend;
    }

    /// <summary>
    /// Puts the server on a GPU build and reports what it ended up with.
    ///
    /// The old process is stopped first: its DLLs sit in the folder that gets replaced, and a file
    /// lock would fail the swap. The model that was loaded is loaded again afterwards, so switching
    /// builds does not quietly end the session someone was in the middle of.
    /// </summary>
    public async Task<(bool Ok, string Message, string Backend, string Path)> InstallGpuBuildAsync(
        Action<string> report, CancellationToken ct = default)
    {
        if (!LlamaGpuBuild.Supported)
        {
            return (false,
                $"llama.cpp publishes no {LlamaGpuBuild.BackendName} build for this platform.",
                string.Empty, string.Empty);
        }

        string loaded = LoadedModelPath;
        bool wasRunning = AnyServerRunning;

        if (wasRunning) Stop();

        try
        {
            var (server, plan, device) = await LlamaGpuBuild.InstallAsync(report, ct).ConfigureAwait(false);

            // The custom path wins over detection, so the old CPU build stops answering even though
            // it is still installed where the vendor put it.
            SetCustomPath(server);

            string backend = await BackendAsync(ct).ConfigureAwait(false);

            if (loaded.Length > 0 && File.Exists(loaded))
            {
                report("Loading the model again on the new build…");
                await RunModelAsync(loaded, ct).ConfigureAwait(false);
            }

            bool unusable = backend is "CPU only" or "Unknown";
            string message = unusable
                ? $"Installed the {plan.Main.Tag} {plan.Backend} build, but it still reports {backend}."
                : $"Using the {plan.Main.Tag} {plan.Backend} build: {device}.";

            Emit(unusable ? "warn" : "info", message);
            return (true, message, backend, server);
        }
        catch (Exception ex)
        {
            string message = $"The GPU build did not install: {ex.Message}";
            Emit("error", message);

            // A failure leaves the old build in place, so the model that was running goes back on it
            // rather than the machine sitting with nothing loaded.
            if (wasRunning && loaded.Length > 0 && File.Exists(loaded))
            {
                try { await RunModelAsync(loaded, ct).ConfigureAwait(false); } catch { }
            }

            return (false, message, string.Empty, string.Empty);
        }
    }

    /// <summary>The binary that was resolved, plus a llama-server beside it when that is a different file.</summary>
    private static IEnumerable<string> ProbeCandidates(string resolvedPath)
    {
        yield return resolvedPath;

        string sibling = string.Empty;

        try
        {
            string? folder = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(folder))
            {
                sibling = Path.Combine(folder, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
            }
        }
        catch (ArgumentException)
        {
            yield break;
        }

        if (sibling.Length > 0
            && File.Exists(sibling)
            && !string.Equals(sibling, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return sibling;
        }
    }

    /// <summary>Reads a device line, or the compiled-in backend name when the build lists no devices.</summary>
    private static string ParseBackend(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;

        string[] kinds = ["CUDA", "Vulkan", "Metal", "SYCL", "ROCm", "HIP", "OpenCL"];

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string head = line[..colon].Trim();

            foreach (string kind in kinds)
            {
                if (!head.StartsWith(kind, StringComparison.OrdinalIgnoreCase)) continue;

                string name = line[(colon + 1)..].Trim();
                int paren = name.IndexOf('(');
                if (paren > 0) name = name[..paren].Trim();

                return $"{head} · {(name.Length > 0 ? name : head)}";
            }
        }

        foreach (string kind in kinds)
        {
            if (output.Contains(kind, StringComparison.OrdinalIgnoreCase)) return $"{kind} build, no device listed";
        }

        return string.Empty;
    }

    /// <summary>
    /// Runs the binary for a line of output. A flag it does not know exits at once, so this is cheap.
    /// `Ran` separates "answered with nothing" from "refused to run", which are different answers.
    /// </summary>
    private static async Task<(string Output, bool Ran)> RunBinaryAsync(string binary, string arguments, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process == null) return (string.Empty, false);

            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (string.Empty, false);
            }

            string output = string.Concat(await stdout.ConfigureAwait(false), "\n", await stderr.ConfigureAwait(false));
            return (output, output.Trim().Length > 0);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (string.Empty, false);
        }
    }

    public LlamaCppStatus Status()
    {
        var resolved = Resolve();
        bool running;

        lock (_lock)
        {
            running = _server is { HasExited: false } || IsAlive(_listenerPid);
        }

        return new LlamaCppStatus(
            Installed: resolved != null,
            Path: resolved?.Path ?? string.Empty,
            // Asked of the binary that will actually run, not of whatever sits on PATH: someone
            // pointing at their own build should see that build's version.
            Version: resolved == null ? string.Empty : VersionOf(resolved.Path),
            Running: running,
            Port: EffectivePort,
            Endpoint: Endpoint,
            Message: running ? $"Serving on 127.0.0.1:{EffectivePort}." : _message,
            UsesCustomPath: resolved?.Custom ?? false,
            LoadedModel: running ? LoadedModelPath : string.Empty,
            RunOnRequest: RunOnRequest,
            AllowMultiple: ReadSettings().AllowMultipleModels,
            Runtime: Runtime,
            Runner: Runner,
            Gpu: GpuSummary,
            IdleUnloadMinutes: IdleUnloadMinutes,
            UnloadInSeconds: running ? SecondsUntilUnload() : 0,
            Backend: _backend);
    }

    /// <summary>
    /// Starts the server and waits until it answers. Returns the state afterwards rather than
    /// throwing on a slow machine: the panel then shows how far it got.
    ///
    /// One model at a time by default: with no path it serves the model used last, and refuses when
    /// there is none. Router mode — several models in memory — is a setting, because it runs a worker
    /// process per model and is how two llama.cpp servers ended up side by side.
    /// </summary>
    public async Task<LlamaCppStatus> StartAsync(string? modelPath = null, CancellationToken ct = default)
    {
        // A click, a chat and a settings refresh can all ask at once. Without this gate they both pass
        // the "already listening" check before either binds, and the loser is left as a stray process.
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(modelPath, ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<LlamaCppStatus> StartCoreAsync(string? modelPath, CancellationToken ct)
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

        var settings = ReadSettings();

        string? target = string.IsNullOrWhiteSpace(modelPath) ? null : modelPath;
        if (target == null && !settings.AllowMultipleModels)
        {
            target = File.Exists(settings.LastModel) ? settings.LastModel : null;
            if (target == null)
            {
                _message = "Nothing to serve yet. Press Load on a model in Local models, or allow several models in Settings.";
                return Status();
            }
        }

        // Named before the load: a CPU-only build otherwise spends minutes loading a model before the
        // log admits there was never a device to put it on.
        // Emitted only when the probe actually ran: repeating it on every load turned a one-time
        // fact into log noise.
        bool freshProbe = _backend.Length == 0;
        string backend = await BackendAsync(ct).ConfigureAwait(false);
        int requestedLayers = (string.IsNullOrWhiteSpace(target) ? null : LlamaModelLibrary.Instance.FlagsFor(target)?.GpuLayers) ?? Runtime.GpuLayers;

        if (freshProbe && backend == "CPU only" && requestedLayers > 0)
        {
            Emit("warn",
                "This llama.cpp build reports no GPU device, so the GPU layers setting has nothing to run on. " +
                "A CUDA, Vulkan or Metal build uses the card; a CPU build cannot.");
        }

        int port = EffectivePort;

        // A server this app does not own can already own the port — a previous run whose process
        // outlived it, or another tool. Waiting for "listening" would then report success for
        // something we did not start.
        if (await IsListeningAsync(port, ct).ConfigureAwait(false))
        {
            _message = $"Port {port} is already used by another server. Stop it, or change the port in Settings.";
            Emit("warn", _message);
            return Status();
        }

        Process? process = null;

        try
        {
            Emit("info", target == null
                ? $"Starting {Path.GetFileName(resolved.Path)} on port {port}."
                : $"Loading {Path.GetFileName(target)} on port {port}.");

            GpuSummary = string.Empty;
            _offloadReported = false;
            _startedGpuLayers = requestedLayers;

            process = Launch(resolved.Path, port, target, settings);
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

            _listenerPid = ListenerPid(port);
            RememberPid(_listenerPid > 0 ? _listenerPid : process.Id);

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

                if (await IsListeningAsync(port, ct).ConfigureAwait(false))
                {
                    _message = $"Serving on 127.0.0.1:{port}.";
                    Emit("info", _message);

                    // A fresh server is in use right now: the idle clock starts at the load, not at the
                    // last request this app made to a previous one.
                    NoteActivity();
                    RestartIdleTimer();

                    // Remembered so "Start server" has a model to serve next time, without asking.
                    if (target != null)
                    {
                        LoadedModelPath = target;

                        var saved = ReadSettings();
                        saved.LastModel = target;
                        WriteSettings(saved);
                    }

                    NotifyStateChanged();
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
            LoadedModelPath = string.Empty;
            try
            {
                if (_server is { HasExited: false })
                {
                    _server.Kill(entireProcessTree: true);
                    Emit("info", "Local server stopped.");
                }

                // The front end may have exited already, leaving the worker it started on the port.
                if (KillLlamaProcess(_listenerPid)) Emit("info", "Local server worker stopped.");
            }
            catch (Exception ex)
            {
                Emit("warn", $"Stop failed: {ex.GetType().Name}");
            }
            finally
            {
                _server = null;
                _listenerPid = 0;
                _message = "Stopped.";

                // Nothing loaded means nothing to unload.
                _idleTimer?.Dispose();
                _idleTimer = null;
                // A stopped server has no speed. Keeping the last one showed it next to the next
                // model's first reply as if that model had produced it.
                TokensPerSecond = 0;
                AverageTokensPerSecond = 0;
                GeneratedTokens = 0;
                GpuSummary = string.Empty;
                _offloadReported = false;
            }
        }

        ForgetPid();
        NotifyStateChanged();
    }

    /// <summary>
    /// Kills a server this app started in a previous run and never stopped.
    ///
    /// A llama.cpp child outlives the window that spawned it when the app is closed hard, so the next
    /// launch found the port taken and refused to start — while the old server kept the memory. Only a
    /// pid this app wrote is touched, never a server somebody started themselves.
    /// </summary>
    public void ReapStaleServer()
    {
        string text;

        try
        {
            if (!File.Exists(PidPath)) return;
            text = File.ReadAllText(PidPath).Trim();
            File.Delete(PidPath);
        }
        catch
        {
            return;
        }

        if (!int.TryParse(text, out int pid) || pid <= 0) return;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return;

            // Pids get reused. A process with another name is not the server this app started.
            if (!process.ProcessName.Contains("llama", StringComparison.OrdinalIgnoreCase)) return;

            process.Kill(entireProcessTree: true);
            Emit("info", $"Stopped a llama.cpp server left over from a previous run (pid {pid}).");
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            Emit("warn", $"Could not stop the previous local server: {ex.GetType().Name}");
        }
    }

    /// <summary>The pid listening on `port` on this machine, or zero when nothing is.</summary>
    private static int ListenerPid(int port)
    {
        if (!OperatingSystem.IsWindows()) return 0;

        try
        {
            var info = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var netstat = Process.Start(info);
            if (netstat == null) return 0;

            string output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(3000);

            foreach (string line in output.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!string.Equals(parts[3], "LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (!parts[1].EndsWith(":" + port, StringComparison.Ordinal)) continue;
                if (int.TryParse(parts[4], out int pid)) return pid;
            }
        }
        catch
        {
            // Without an answer the launcher's own pid is used instead.
        }

        return 0;
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kills a llama process by pid, and only a llama one: pids get reused.</summary>
    private static bool KillLlamaProcess(int pid)
    {
        if (!IsAlive(pid)) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Contains("llama", StringComparison.OrdinalIgnoreCase)) return false;

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RememberPid(int pid)
    {
        try
        {
            var directory = Path.GetDirectoryName(PidPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(PidPath, pid.ToString());
        }
        catch
        {
            // Cleanup is a convenience, not a requirement.
        }
    }

    private static void ForgetPid()
    {
        try
        {
            if (File.Exists(PidPath)) File.Delete(PidPath);
        }
        catch
        {
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
        // Stop-then-start has to be one step: a start that slips between them would bind the port on
        // the old process and leave the new one with nothing to do but exit.
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Stop();
            await Task.Delay(300, ct).ConfigureAwait(false);

            var status = await StartCoreAsync(modelPath, ct).ConfigureAwait(false);
            if (status.Running) status = status with { LoadedModel = modelPath };
            string modelId = await FirstModelIdAsync(ct).ConfigureAwait(false);

            if (modelId.Length == 0) modelId = Path.GetFileNameWithoutExtension(modelPath);

            var provider = EnsureProvider(modelId);
            return (status, provider);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// The models a local chat can pick from: the id the request must name, and the name to show.
    ///
    /// The two differ. llama.cpp serves a file path when it was started on a file, and a repository
    /// name when it runs as a router, and neither is what the shelf calls the model — so the picker
    /// was listing raw paths even after someone renamed them. The label is for the eye only; the id
    /// travels unchanged to the server.
    /// </summary>
    public IReadOnlyList<(string Id, string Label)> PickerModels()
    {
        var shelf = LlamaModelLibrary.Instance.List();
        var stored = AiConfigService.Instance.GetClaudeGatewayProviders()
            .FirstOrDefault(p => string.Equals(p.Id, ProviderId, StringComparison.OrdinalIgnoreCase));

        var entries = new List<(string Id, string Label)>();

        void Add(string? id, string? label)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (entries.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))) return;
            entries.Add((id, string.IsNullOrWhiteSpace(label) ? NameFromId(id) : label!));
        }

        // Whatever is answering right now comes first: it is the choice a new chat would land on.
        string loaded = LoadedModelPath;

        foreach (var model in shelf)
        {
            if (string.Equals(model.Path, loaded, StringComparison.OrdinalIgnoreCase)) Add(model.Path, model.Name);
        }

        foreach (var model in shelf)
        {
            Add(model.Path, model.Name);
        }

        // A router's ids are not files, so they are not on the shelf. Keeping them means a fetched
        // list is not erased by the next read.
        foreach (var id in stored?.Models ?? [])
        {
            var match = shelf.FirstOrDefault(m => string.Equals(m.Path, id, StringComparison.OrdinalIgnoreCase))
                ?? MatchByFolder(id, shelf);
            Add(id, match?.Name);
        }

        return entries;
    }

    /// <summary>
    /// A shelf entry that lives in the folder a server id names.
    ///
    /// A llama.cpp router reports a model by its cache folder (`…\models--owner--name`), not by the
    /// file it loaded, so an exact path match never happens — yet that folder name sits inside the
    /// real file's path. Matching on it is what lets a renamed model show its name here.
    /// </summary>
    private static LocalModel? MatchByFolder(string id, IReadOnlyList<LocalModel> shelf)
    {
        string trimmed = id.TrimEnd('/', '\\');
        string folder = trimmed.Contains('/') || trimmed.Contains('\\')
            ? trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..]
            : trimmed;

        if (folder.Length < 8) return null;

        return shelf.FirstOrDefault(m => m.Path.Contains(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A path or repo id reduced to something readable, for a model nobody renamed.</summary>
    private static string NameFromId(string id)
    {
        string trimmed = id.TrimEnd('/', '\\');
        string name = trimmed.Contains('/') || trimmed.Contains('\\')
            ? trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..]
            : trimmed;

        if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) name = name[..^5];

        // A Hugging Face cache folder is the repo id with its separators escaped.
        if (name.StartsWith("models--", StringComparison.OrdinalIgnoreCase)) name = name["models--".Length..].Replace("--", "/");

        return name;
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
            // The same parser the app's own updater uses: tags that omit a part, carry a v, or add a
            // suffix are compared as numbers instead of failing into "no update".
            var installedSemver = AppVersion.TryParse(installed, out var parsedInstalled) ? parsedInstalled : (AppVersion?)null;
            var latestSemver = AppVersion.TryParse(tag, out var parsedLatest) ? parsedLatest : (AppVersion?)null;
            int latestBuild = BuildNumber(tag);

            bool newer;
            string latest;
            string current;

            if (installedSemver.HasValue && latestSemver.HasValue)
            {
                newer = latestSemver.Value.IsNewerThan(installedSemver.Value);
                latest = $"v{latestSemver.Value}";
                current = $"v{installedSemver.Value}";
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

        /// <summary>Default on: a local chat that finds nothing loaded loads the first model on the shelf.</summary>
        public bool RunOnRequest { get; set; } = true;

        /// <summary>Listening port. Zero means <see cref="DefaultPort"/>.</summary>
        public int Port { get; set; }

        /// <summary>
        /// Off by default: one model in memory at a time. On runs llama.cpp as a router, which keeps a
        /// worker per model and can therefore hold several at once — useful, and expensive.
        /// </summary>
        public bool AllowMultipleModels { get; set; }

        /// <summary>The file last served, so "Start server" has something to start without a picker.</summary>
        public string LastModel { get; set; } = "";

        /// <summary>`direct` (this app streams from the OpenAI endpoint) or `claude` (the CLI).</summary>
        public string Runner { get; set; } = "direct";

        // ---- launch flags ----

        public int ContextSize { get; set; } = LlamaRuntimeOptions.Default.ContextSize;

        /// <summary>99 means "every layer": llama.cpp clamps it to what the model has.</summary>
        public int GpuLayers { get; set; } = LlamaRuntimeOptions.Default.GpuLayers;

        /// <summary>Zero leaves llama.cpp's own thread count alone.</summary>
        public int Threads { get; set; }

        public int BatchSize { get; set; } = LlamaRuntimeOptions.Default.BatchSize;

        public string CacheTypeK { get; set; } = "f16";

        public string CacheTypeV { get; set; } = "f16";

        public bool FlashAttention { get; set; } = true;

        public string ExtraArgs { get; set; } = "";

        /// <summary>Sampling for a chat this app drives itself; the CLI keeps its own.</summary>
        public double Temperature { get; set; } = 0.7;

        public int MaxTokens { get; set; } = 4096;

        /// <summary>
        /// Unload the model after this many idle minutes, freeing the card. Zero keeps it loaded until
        /// the app closes, which is what a person running a long job wants.
        /// </summary>
        public int IdleUnloadMinutes { get; set; }
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

    private static Process Launch(string binary, int port, string? modelPath, LlamaSettings settings)
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

        var args = ServerArgs(port, modelPath, settings);
        foreach (string arg in args) info.ArgumentList.Add(arg);

        try
        {
            return Process.Start(info)
                ?? throw new InvalidOperationException("llama.cpp did not start.");
        }
        catch (Exception) when (Services.Platform.PlatformHelper.IsWindows)
        {
            // A WindowsApps alias is a reparse point, and not every child launcher can follow one.
            // cmd resolves it the way a terminal would. The same argument list is reused, so the
            // fallback cannot drift from the normal path.
            string quoted = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            var fallback = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{binary}\" {(classicServer ? string.Empty : "serve ")}{quoted}\""
            };

            return Process.Start(fallback)
                ?? throw new InvalidOperationException("llama.cpp did not start.");
        }
    }

    /// <summary>
    /// The flags a start uses. GPU offload is the one that matters most: llama-server defaults to
    /// zero layers on the GPU, so a machine with a working card still answered from the CPU until
    /// this passed `--n-gpu-layers`.
    /// </summary>
    private static List<string> ServerArgs(int port, string? modelPath, LlamaSettings settings)
    {
        var runtime = RuntimeOf(settings);

        // Per-model flags win over the app-wide runtime, field by field: an unset field still means
        // "whatever the global setting says".
        var flags = string.IsNullOrWhiteSpace(modelPath) ? null : LlamaModelLibrary.Instance.FlagsFor(modelPath);

        int contextSize = Math.Clamp(flags?.ContextSize ?? runtime.ContextSize, 8192, 1_048_576);
        int gpuLayers = Math.Clamp(flags?.GpuLayers ?? runtime.GpuLayers, 0, 512);
        bool flashAttention = flags?.FlashAttention ?? runtime.FlashAttention;
        string extra = string.IsNullOrWhiteSpace(flags?.ExtraArgs) ? runtime.ExtraArgs : flags!.ExtraArgs!;

        var args = new List<string>
        {
            "--port", port.ToString(CultureInfo.InvariantCulture),

            // One slot of this size, not four smaller ones: the defaults split the context across
            // slots and leave a single conversation with 4k, which no agent turn fits in.
            "--ctx-size", contextSize.ToString(CultureInfo.InvariantCulture),
            "--parallel", "1",

            "--n-gpu-layers", gpuLayers.ToString(CultureInfo.InvariantCulture),

            // A quantised cache halves the KV-cache memory, which is the difference between a long
            // context fitting on the card and being pushed to the CPU.
            "--cache-type-k", NormalisedCache(flags?.CacheTypeK ?? runtime.CacheTypeK),
            "--cache-type-v", NormalisedCache(flags?.CacheTypeV ?? runtime.CacheTypeV),
            "--batch-size", runtime.BatchSize.ToString(CultureInfo.InvariantCulture),
        };

        if (runtime.Threads > 0) args.AddRange(["--threads", runtime.Threads.ToString(CultureInfo.InvariantCulture)]);
        // A bare `--flash-attn` is not valid on newer builds: the flag takes a value, so the next
        // argument (the model path) was read as one and the server exited before it could load.
        if (flashAttention) args.AddRange(["--flash-attn", "on"]);

        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            args.AddRange(["-m", modelPath]);

            // A vision model without its projector loads happily and then ignores every image. The
            // file sits beside the model in a GGUF repo, so it is found rather than asked for.
            string projector = LlamaModelLibrary.Instance.ProjectorFor(modelPath);
            if (projector.Length > 0) args.AddRange(["--mmproj", projector]);
        }

        foreach (string extraArg in SplitArgs(extra)) args.Add(extraArg);

        return args;
    }

    /// <summary>One of the cache types llama.cpp accepts; anything else falls back to f16.</summary>
    private static string NormalisedCache(string? value) =>
        value is "q8_0" or "q5_1" or "q5_0" or "q4_1" or "q4_0" or "f16" or "bf16" or "f32" ? value : "f16";

    /// <summary>Splits the extra-arguments box, keeping quoted paths together.</summary>
    private static IEnumerable<string> SplitArgs(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;

        var current = new StringBuilder();
        bool quoted = false;

        foreach (char c in value)
        {
            if (c == '"') { quoted = !quoted; continue; }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) yield return current.ToString();
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

            // The launcher exiting is not the server exiting: the llama.app front end hands the work
            // to a child and returns, and the port still answers.
            if (IsAlive(_listenerPid))
            {
                _message = $"Serving on 127.0.0.1:{EffectivePort}.";
                NotifyStateChanged();
                return;
            }

            Emit(process.ExitCode == 0 ? "info" : "error", $"Local server exited with code {process.ExitCode}.");
            _message = $"The server exited with code {process.ExitCode}. {TailOfLog()}";
            ForgetPid();
            NotifyStateChanged();
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

        NoteGpu(line);
        NoteRate(line);
        Emit(LevelOf(line), line.Trim());
    }

    /// <summary>
    /// Reads where the model actually went.
    ///
    /// Older builds name it — `offloaded 29/29 layers to CUDA0` — and that line is the best answer.
    /// Newer ones print eight lines in total and say nothing about the offload at all: taking that
    /// silence as "CPU only" claimed the card was idle while it held 1.4 GB of the model, and the
    /// warning it raised told people to install the build they were already running.
    ///
    /// So the fallback asks the binary instead: a probed CUDA/Vulkan/Metal device plus a positive GPU
    /// layer count means the layers were requested where they can go. The load still fails outright
    /// when they do not fit, so this is not a promise the build can break silently.
    /// </summary>
    private void NoteGpu(string line)
    {
        var offload = OffloadLine.Match(line);
        if (offload.Success)
        {
            _offloadReported = true;
            GpuSummary = $"{offload.Groups["done"].Value}/{offload.Groups["total"].Value} layers on {offload.Groups["dev"].Value}";
            return;
        }

        if (_offloadReported) return;

        if (!line.Contains("model loaded", StringComparison.OrdinalIgnoreCase)) return;

        _offloadReported = true;

        int wanted = _startedGpuLayers > 0 ? _startedGpuLayers : Runtime.GpuLayers;
        string device = _backend.Length > 0 ? _backend.Split('·')[0].Trim() : string.Empty;
        bool usable = device.Length > 0 && !string.Equals(_backend, "CPU only", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_backend, "Unknown", StringComparison.OrdinalIgnoreCase);

        if (usable && wanted > 0)
        {
            GpuSummary = wanted >= 99 ? $"all layers on {device}" : $"{wanted} layers on {device}";

            // Said once, because this log format hides the offload and the number otherwise looks made up.
            Emit("info", $"This llama.cpp build does not name the offload; {wanted} GPU layers were requested on {device}.");
            return;
        }

        GpuSummary = "CPU only";

        if (wanted > 0)
        {
            Emit("warn",
                "No GPU offload was reported, so the model is running on the CPU. " +
                "A build without CUDA/Vulkan/Metal support cannot use the card — the GPU layers setting " +
                "has no effect on it.");
        }
    }

    /// <summary>
    /// Reads the speed out of a timing line: `… n_gen = 490, tg = 162.68 t/s, tg_3s = …`.
    ///
    /// llama.cpp measures this itself and prints it once per reply, which makes it the honest number
    /// to show next to the chat. Mirroring the server's arithmetic here would only be a guess.
    /// </summary>
    private void NoteRate(string line)
    {
        // The reply's own average, printed when it ends:
        // `eval time = 4652.51 ms / 717 tokens ( 6.50 ms per token, 153.90 tokens per second)`
        var done = EvalTimeLine.Match(line);
        if (done.Success
            && double.TryParse(done.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double average)
            && average > 0)
        {
            AverageTokensPerSecond = Math.Round(average, 1);
            if (int.TryParse(done.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int counted) && counted > 0)
            {
                GeneratedTokens = counted;
            }

            RaiseRateChanged();
            return;
        }

        if (!line.Contains("t/s", StringComparison.Ordinal)) return;

        // The peak figure from the same batch: `… n_gen = 490, tg = 162.68 t/s, tg_3s = …`
        var rate = GenerationRate.Match(line);
        if (!rate.Success) return;
        if (!double.TryParse(rate.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double tps)) return;
        if (tps <= 0) return;

        var count = GeneratedTokensMatch.Match(line);
        int tokens = count.Success && int.TryParse(count.Groups["n"].Value, out int parsed) ? parsed : 0;

        TokensPerSecond = Math.Round(tps, 1);
        if (tokens > 0) GeneratedTokens = tokens;

        RaiseRateChanged();
    }

    private void RaiseRateChanged()
    {
        try
        {
            RateChanged?.Invoke();
        }
        catch
        {
            // A dead listener is not a reason to lose a measurement.
        }
    }

    private static readonly Regex GenerationRate = new(@"tg\s*=\s*(?<rate>\d+(?:\.\d+)?)\s*t/s", RegexOptions.Compiled);
    private static readonly Regex GeneratedTokensMatch = new(@"n_gen\s*=\s*(?<n>\d+)", RegexOptions.Compiled);
    private static readonly Regex EvalTimeLine = new(
        @"eval time\s*=.*?/\s*(?<n>\d+)\s+tokens.*?(?<rate>\d+(?:\.\d+)?)\s+tokens per second",
        RegexOptions.Compiled);
    private static readonly Regex OffloadLine = new(
        @"offloaded\s+(?<done>\d+)/(?<total>\d+)\s+layers to (?:the )?(?<dev>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

    /// <summary>
    /// A short, comparable label: `v0.8.0` when the build reports one, else its first line.
    ///
    /// Cached per binary because reading it starts a process: the settings card asks for the status
    /// on every refresh and on every provider switch, and spawning `--version` each time is what made
    /// those clicks feel slow.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    private static string VersionOf(string binary)
    {
        if (_versions.TryGetValue(binary, out var known)) return known;

        string line = ProbeVersionLine(binary);
        string value = AppVersion.TryParse(line, out var semver) ? $"v{semver}" : line;
        _versions[binary] = value;

        return value;
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
