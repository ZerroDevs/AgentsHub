using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentsHub.Services.Platform;

namespace AgentsHub.Services.OpenCode;

/// <summary>One answer, the CLI session it belongs to, and what it cost.</summary>
internal sealed record OpenCodeTurn(string Text, string? SessionId, int TokensIn, int TokensOut, double Cost);

/// <summary>One tool call the CLI reported, ready for the transcript's own tool rows.</summary>
internal sealed record OpenCodeToolEvent(string CallId, string Tool, string Input, string Output, bool Success);

/// <summary>Whether a Console account is signed in, and which one. Never carries a secret.</summary>
internal sealed record OpenCodeAuth(bool SignedIn, string? Label, string? Method, IReadOnlyList<string> Accounts);

/// <summary>A device sign-in waiting for the user: the code to type, and where to type it.</summary>
internal sealed record OpenCodeLogin(string Code, string Url);

/// <summary>A pending (or just finished) sign-in, as the panel polls it.</summary>
internal sealed record OpenCodeLoginState(string Code, string Url, bool Running, bool? Succeeded);

/// <summary>Both facts the model panel needs: who is signed in, and any sign-in in flight.</summary>
internal sealed record OpenCodeStatus(OpenCodeAuth Auth, OpenCodeLoginState? Login);

/// <summary>
/// One stdout line, decoded.
///
/// Parsing is separate from the process so it can be checked against real event lines: a provider
/// error, a completed text part, a tool call, a step's token counts.
/// </summary>
internal sealed record OpenCodeEvent(
    string Type,
    string SessionId,
    string Text,
    OpenCodeToolEvent? Tool,
    string? Error,
    int TokensIn,
    int TokensOut,
    double Cost)
{
    public static readonly OpenCodeEvent Empty = new(string.Empty, string.Empty, string.Empty, null, null, 0, 0, 0);
}

/// <summary>
/// Runs the OpenCode CLI headless: one process per turn, one JSON event per line.
///
/// Unlike Vyce, OpenCode does not need a console. `opencode run --format json` writes every event
/// it handles to stdout as JSONL, so the answer and the tool calls can be read directly instead of
/// scraped off a screen. Each line carries the session id, which is what continues a conversation.
///
/// Text arrives as a completed part per step, not token by token, because that is what the CLI
/// emits. The app renders each part as it lands.
/// </summary>
internal sealed class OpenCodeEngine : IDisposable
{
    public static OpenCodeEngine Instance { get; } = new();

    /// <summary>Ceiling for one turn, so a wedged CLI cannot hold the session forever.</summary>
    private const int TurnTimeoutMs = 600000;

    /// <summary>How long a model list is reused before the CLI is asked again.</summary>
    private static readonly TimeSpan ModelCacheTtl = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "agentshub-opencode.log");

    /// <summary>How long an auth answer is reused; a sign-in forces the next read.</summary>
    private static readonly TimeSpan AuthCacheTtl = TimeSpan.FromSeconds(20);

    private static readonly Regex Ansi = new("\\x1B\\[[0-9;?]*[a-zA-Z]", RegexOptions.Compiled);
    private static readonly Regex CodeLine = new(@"Enter code:\s*([A-Za-z0-9][A-Za-z0-9-]{3,15})", RegexOptions.Compiled);
    private static readonly Regex UrlLine = new(@"(https://\S+)", RegexOptions.Compiled);

    private readonly System.Threading.Lock _loginLock = new();

    private Process? _process;
    private Process? _loginProcess;
    private string _loginCode = string.Empty;
    private string _loginUrl = string.Empty;
    private bool? _loginSucceeded;
    private bool _disposed;
    private string[] _cachedModels = [];
    private DateTime _modelsReadAt = DateTime.MinValue;
    private OpenCodeAuth? _cachedAuth;
    private DateTime _authReadAt = DateTime.MinValue;

    public bool IsRunning => _process is { HasExited: false };

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.UtcNow:O}\t{message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // A log that cannot be written must never fail a turn.
        }
    }

    /// <summary>
    /// Sends one prompt and streams what the CLI reports.
    /// </summary>
    /// <param name="sessionId">The `ses_…` id to continue, or null for a new conversation.</param>
    /// <param name="model">`provider/model`, or null to let the CLI use its own default.</param>
    /// <param name="autoApprove">
    /// Opens tools. A headless run rejects every permission request it cannot ask about, so this is
    /// the only way a turn can write or run anything.
    /// </param>
    public async Task<OpenCodeTurn> SendAsync(
        string prompt,
        string root,
        string? sessionId,
        string? model,
        bool autoApprove,
        IReadOnlyList<string>? files,
        IProgress<string>? progress,
        Action<OpenCodeToolEvent>? onTool,
        /// <summary>Reasoning parts, reported separately so they never land in the answer.</summary>
        Action<string>? onThinking = null,
        CancellationToken ct = default)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var args = new List<string> { "run", "--format", "json" };

            if (!string.IsNullOrWhiteSpace(model))
            {
                args.Add("--model");
                args.Add(model);
            }

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                args.Add("--session");
                args.Add(sessionId);
            }

            if (autoApprove) args.Add("--auto");

            // The composer's attachments are already materialised as paths by the host.
            if (files is not null)
            {
                foreach (string file in files)
                {
                    args.Add("--file");
                    args.Add(file);
                }
            }

            args.Add(prompt);

            using var process = Start(root, args);
            _process = process;

            Log($"turn start, {prompt.Length} chars, session={sessionId ?? "new"}, model={model ?? "default"}, auto={autoApprove}");

            var text = new StringBuilder();
            var stderr = new StringBuilder();
            string capturedSession = sessionId ?? string.Empty;
            string? failure = null;
            int tokensIn = 0;
            int tokensOut = 0;
            double cost = 0;

            var stderrDrain = Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = process.StandardError.ReadLine()) is not null)
                    {
                        lock (stderr) stderr.AppendLine(line);
                    }
                }
                catch (Exception ex)
                {
                    Log($"stderr read failed: {ex.GetType().Name}");
                }
            }, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TurnTimeoutMs);

            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is not null)
                {
                    if (line.Length == 0) continue;

                    var parsed = ParseEvent(line);

                    if (parsed.Type.Length == 0) Log($"unparsed line: {Tail(line, 120)}");

                    if (parsed.SessionId.Length > 0) capturedSession = parsed.SessionId;

                    if (parsed.Text.Length > 0)
                    {
                        // Reasoning is not the answer. It goes to its own sink so the transcript can
                        // draw a Thought row, and so the reply stays exactly what the model said.
                        if (parsed.Type is "reasoning" or "thinking")
                        {
                            onThinking?.Invoke(parsed.Text);
                        }
                        else
                        {
                            text.Append(parsed.Text);
                            progress?.Report(parsed.Text);
                        }
                    }

                    if (parsed.Tool is not null) onTool?.Invoke(parsed.Tool);
                    if (parsed.Error is not null) failure = parsed.Error;

                    tokensIn += parsed.TokensIn;
                    tokensOut += parsed.TokensOut;
                    cost += parsed.Cost;
                }

                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                try { await stderrDrain.ConfigureAwait(false); } catch { /* diagnostic only */ }
            }

            string answer = text.ToString().Trim();

            if (failure is not null)
            {
                LogScreen(stderr);
                throw new InvalidOperationException(Explain(failure));
            }

            if (process.ExitCode != 0)
            {
                LogScreen(stderr);
                string tail = Tail(stderr.ToString());
                throw new InvalidOperationException(
                    $"OpenCode exited with code {process.ExitCode}. {(tail.Length > 0 ? tail : "Run `opencode auth login` if no provider is signed in.")}");
            }

            if (capturedSession.Length == 0 && answer.Length == 0)
            {
                LogScreen(stderr);
                throw new InvalidOperationException(
                    "OpenCode started but reported nothing. Check `opencode auth list`: a provider has to be signed in "
                    + "and the model has to be reachable from here.");
            }

            if (answer.Length == 0)
            {
                LogScreen(stderr);
                throw new InvalidOperationException(
                    "OpenCode finished the turn without emitting any text, so there is nothing to show.");
            }

            Log($"turn done, {answer.Length} chars, session={capturedSession}");

            return new OpenCodeTurn(answer, capturedSession.Length > 0 ? capturedSession : null, tokensIn, tokensOut, cost);
        }
        finally
        {
            _process = null;
            _turn.Release();
        }
    }

    /// <summary>Turns one stdout line into the turn's facts. Unknown or broken lines change nothing.</summary>
    internal static OpenCodeEvent ParseEvent(string line)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A log line leaked onto stdout, or the stream was cut mid-event. Neither is fatal.
            return OpenCodeEvent.Empty;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return OpenCodeEvent.Empty;

            string sessionId = StringOf(root, "sessionID");
            string type = StringOf(root, "type");

            if (type == "error") return OpenCodeEvent.Empty with { Type = type, SessionId = sessionId, Error = ErrorText(root) };

            if (!root.TryGetProperty("part", out var part) || part.ValueKind != JsonValueKind.Object)
            {
                return OpenCodeEvent.Empty with { Type = type, SessionId = sessionId };
            }

            switch (type)
            {
                case "text":
                    return OpenCodeEvent.Empty with
                    {
                        Type = type,
                        SessionId = sessionId,
                        Text = StringOf(part, "text"),
                    };

                case "tool_use":
                    return OpenCodeEvent.Empty with
                    {
                        Type = type,
                        SessionId = sessionId,
                        Tool = ReadTool(part),
                    };

                case "reasoning":
                case "thinking":
                    // Reported through `Text`, tagged by `Type`, so the loop can route it without a
                    // second field on the event record.
                    return OpenCodeEvent.Empty with
                    {
                        Type = type,
                        SessionId = sessionId,
                        Text = StringOf(part, "text"),
                    };

                case "step_finish":
                    {
                        int tokensIn = 0;
                        int tokensOut = 0;
                        double cost = 0;

                        ReadUsage(part, ref tokensIn, ref tokensOut, ref cost);

                        return OpenCodeEvent.Empty with
                        {
                            Type = type,
                            SessionId = sessionId,
                            TokensIn = tokensIn,
                            TokensOut = tokensOut,
                            Cost = cost,
                        };
                    }

                default:
                    return OpenCodeEvent.Empty with { Type = type, SessionId = sessionId };
            }
        }
    }

    private static OpenCodeToolEvent ReadTool(JsonElement part)
    {
        string name = StringOf(part, "tool");

        if (name.Length == 0) name = StringOf(part, "name");

        string callId = StringOf(part, "callID");

        if (callId.Length == 0) callId = StringOf(part, "id");

        if (!part.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
        {
            return new OpenCodeToolEvent(callId, name, string.Empty, string.Empty, true);
        }

        string input = state.TryGetProperty("input", out var inputElement)
            ? inputElement.GetRawText()
            : string.Empty;

        string output = state.TryGetProperty("output", out var outputElement)
            ? outputElement.ValueKind == JsonValueKind.String
                ? outputElement.GetString() ?? string.Empty
                : outputElement.GetRawText()
            : string.Empty;

        string status = StringOf(state, "status");

        return new OpenCodeToolEvent(callId, name, input, output, !string.Equals(status, "error", StringComparison.OrdinalIgnoreCase));
    }

    private static void ReadUsage(JsonElement part, ref int tokensIn, ref int tokensOut, ref double cost)
    {
        if (part.TryGetProperty("cost", out var costElement) && costElement.ValueKind == JsonValueKind.Number)
        {
            cost += costElement.GetDouble();
        }

        if (!part.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return;

        if (tokens.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Number)
        {
            tokensIn += input.GetInt32();
        }

        if (tokens.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Number)
        {
            tokensOut += output.GetInt32();
        }
    }

    private static string ErrorText(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return "OpenCode reported an error.";

        if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "OpenCode reported an error.";

        // Provider failures arrive as { name, data: { message } }, plain ones as { name, message }.
        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            string nested = StringOf(data, "message");

            if (nested.Length > 0) return nested;
        }

        string message = StringOf(error, "message");

        if (message.Length > 0) return message;

        string name = StringOf(error, "name");

        return name.Length > 0 ? $"OpenCode reported {name}." : "OpenCode reported an error.";
    }

    private static string StringOf(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>The models the CLI can route to, in the `provider/model` form `--model` accepts.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cachedModels.Length > 0 && DateTime.UtcNow - _modelsReadAt < ModelCacheTtl)
        {
            return _cachedModels;
        }

        var models = new List<string>();

        try
        {
            using var process = Start(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ["models"]);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is not null)
            {
                string id = line.Trim();

                // A row is a provider/model id; anything else (a log line, a banner) is skipped.
                if (id.Length == 0 || id.Contains(' ') || !id.Contains('/')) continue;

                models.Add(id);
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"model list failed: {ex.GetType().Name}");
        }

        if (models.Count > 0)
        {
            _cachedModels = models.ToArray();
            _modelsReadAt = DateTime.UtcNow;
        }

        return _cachedModels;
    }

    /// <summary>
    /// The free models that answered from here, in order. Used only when the list still offers them;
    /// the first free `opencode/…` row is the fallback, because the catalogue changes between releases.
    /// </summary>
    private static readonly string[] PreferredFreeModels =
    [
        "opencode/deepseek-v4.1-flash-free",
        "opencode/mimo-v2.6-flash-free",
        "opencode/glm-5.3-flash-free",
    ];

    /// <summary>
    /// The model a fresh chat should use.
    ///
    /// The CLI's own default here is routed through the user's local proxy, which wants a key of its
    /// own, so a turn with no `--model` fails before it starts. A free Console model is the one an
    /// account can always run.
    /// </summary>
    public async Task<string?> DefaultModelAsync(CancellationToken ct = default)
    {
        var models = await ListModelsAsync(ct: ct).ConfigureAwait(false);

        foreach (string preferred in PreferredFreeModels)
        {
            if (models.Contains(preferred, StringComparer.OrdinalIgnoreCase)) return preferred;
        }

        return models.FirstOrDefault(id => id.StartsWith("opencode/", StringComparison.OrdinalIgnoreCase) && id.Contains("free", StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(id => id.Contains("free", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Who is signed in to OpenCode Console.
    ///
    /// `auth export` is the only list that says which account is active; only labels and method
    /// names are kept, never the credential values inside.
    /// </summary>
    public async Task<OpenCodeAuth> GetAuthAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cachedAuth is not null && DateTime.UtcNow - _authReadAt < AuthCacheTtl)
        {
            return _cachedAuth;
        }

        var accounts = new List<string>();
        bool signedIn = false;
        string? label = null;
        string? method = null;

        try
        {
            using var process = Start(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ["auth", "export"]);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            string json = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var credential in document.RootElement.EnumerateArray())
                {
                    if (credential.ValueKind != JsonValueKind.Object) continue;
                    if (StringOf(credential, "integrationID") != "opencode") continue;

                    string name = StringOf(credential, "label");

                    if (name.Length > 0) accounts.Add(name);

                    bool active = credential.TryGetProperty("active", out var activeElement)
                        && activeElement.ValueKind == JsonValueKind.True;

                    if (!active) continue;

                    signedIn = true;
                    label = name.Length > 0 ? name : null;

                    if (credential.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                    {
                        string id = StringOf(value, "methodID");

                        method = id.Length > 0 ? id : StringOf(value, "type");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"auth read failed: {ex.GetType().Name}");
        }

        _cachedAuth = new OpenCodeAuth(signedIn, label, method, accounts);
        _authReadAt = DateTime.UtcNow;

        return _cachedAuth;
    }

    /// <summary>
    /// Starts `opencode auth login opencode --method device` and returns as soon as the CLI prints
    /// the code and the page, so the panel can hand both to the user while the CLI polls.
    /// </summary>
    public async Task<OpenCodeLogin> BeginLoginAsync(CancellationToken ct = default)
    {
        Process process;

        lock (_loginLock)
        {
            if (_loginProcess is { HasExited: false }) return new OpenCodeLogin(_loginCode, _loginUrl);

            process = Start(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["auth", "login", "opencode", "--method", "device"]);

            _loginProcess = process;
            _loginCode = string.Empty;
            _loginUrl = string.Empty;
            _loginSucceeded = null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is not null)
            {
                string plain = Ansi.Replace(line, string.Empty);

                if (_loginCode.Length == 0)
                {
                    var match = CodeLine.Match(plain);
                    if (match.Success) _loginCode = match.Groups[1].Value;
                }

                if (_loginUrl.Length == 0)
                {
                    var match = UrlLine.Match(plain);
                    if (match.Success) _loginUrl = match.Groups[1].Value;
                }

                if (_loginCode.Length > 0 && _loginUrl.Length > 0) break;
            }
        }
        catch (OperationCanceledException)
        {
            // The panel still gets whatever was read; the process keeps waiting.
        }

        // Draining both streams matters: the CLI blocks on a full pipe, and it exits on its own once
        // the code is approved (or the code expires).
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = process.StandardError.ReadToEndAsync();

                string? rest;
                while ((rest = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    Log($"login: {Ansi.Replace(rest, string.Empty)}");
                }

                await process.WaitForExitAsync().ConfigureAwait(false);

                lock (_loginLock)
                {
                    _loginSucceeded = process.ExitCode == 0;
                }

                Log($"login finished, exit={process.ExitCode}");

                if (process.ExitCode != 0) Log($"login stderr: {Tail(await stderr.ConfigureAwait(false), 300)}");

                _authReadAt = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                lock (_loginLock) _loginSucceeded = false;

                Log($"login watch failed: {ex.GetType().Name}");
            }
        });

        return new OpenCodeLogin(_loginCode, _loginUrl);
    }

    /// <summary>Both facts the model panel needs, and it settles a finished sign-in.</summary>
    public async Task<OpenCodeStatus> GetStatusAsync(bool force = false, CancellationToken ct = default)
    {
        OpenCodeLoginState? login;

        lock (_loginLock)
        {
            login = _loginProcess is null
                ? null
                : new OpenCodeLoginState(_loginCode, _loginUrl, !_loginProcess.HasExited, _loginSucceeded);
        }

        // A finished sign-in is the one moment the cached answer is certainly stale.
        var auth = await GetAuthAsync(force || login is { Running: false }, ct).ConfigureAwait(false);

        if (login is { Running: false } && auth.SignedIn)
        {
            ClearLogin();
            login = null;
        }

        return new OpenCodeStatus(auth, login);
    }

    /// <summary>Drops the last sign-in attempt, so the panel can offer the button again.</summary>
    public void ClearLogin()
    {
        lock (_loginLock)
        {
            if (_loginProcess is { HasExited: false }) return;

            _loginProcess = null;
            _loginCode = string.Empty;
            _loginUrl = string.Empty;
            _loginSucceeded = null;
        }
    }

    /// <summary>Abandons a waiting sign-in; nothing was stored, so the CLI can simply be killed.</summary>
    public void CancelLogin()
    {
        lock (_loginLock)
        {
            try
            {
                if (_loginProcess is { HasExited: false }) _loginProcess.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log($"login cancel failed: {ex.GetType().Name}");
            }

            _loginProcess = null;
            _loginCode = string.Empty;
            _loginUrl = string.Empty;
            _loginSucceeded = null;
        }
    }

    /// <summary>
    /// A provider rejection almost always means the model is not covered by the current account, so
    /// the message says what the user can do instead of only what happened.
    /// </summary>
    private static string Explain(string message)
    {
        bool auth = message.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthor", StringComparison.OrdinalIgnoreCase)
            || message.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || message.Contains("401", StringComparison.Ordinal);

        return auth
            ? $"{message} (Sign in from the OpenCode model menu, or pick a free model.)"
            : message;
    }

    /// <summary>Kills the turn's process tree. The CLI holds its own children, so the tree matters.</summary>
    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                Log("stop sent");
            }
        }
        catch (Exception ex)
        {
            Log($"stop failed: {ex.GetType().Name}");
        }
    }

    private Process Start(string root, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // A headless run still looks at stdin, and an inherited handle that never closes leaves it
            // waiting instead of answering. It is closed as soon as the process starts.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Directory.Exists(root)
                ? root
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (PlatformHelper.IsWindows)
        {
            // `opencode` installs as a .cmd, which only a shell can start.
            info.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            info.Arguments = CommandLine(args);
        }
        else
        {
            info.FileName = "opencode";

            foreach (string arg in args) info.ArgumentList.Add(arg);
        }

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("OpenCode could not be started. Run `npm install -g opencode-ai`.");

        try { process.StandardInput.Close(); } catch { /* already gone */ }

        return process;
    }

    /// <summary>
    /// One command string for cmd.exe: every argument quoted, quotes inside doubled for it.
    /// </summary>
    private static string CommandLine(IReadOnlyList<string> args)
    {
        var builder = new StringBuilder("/d /s /c \"opencode");

        foreach (string arg in args)
        {
            builder.Append(' ').Append('"').Append(arg.Replace("\"", "\"\"")).Append('"');
        }

        return builder.Append('"').ToString();
    }

    private void LogScreen(StringBuilder stderr)
    {
        try
        {
            string text = stderr.ToString().Trim();

            Log(text.Length > 0 ? "stderr:\n" + text : "stderr empty");
        }
        catch (Exception ex)
        {
            Log($"stderr dump failed: {ex.GetType().Name}");
        }
    }

    private static string Tail(string value, int max = 400)
    {
        string trimmed = value.Trim();

        return trimmed.Length <= max ? trimmed : trimmed[^max..];
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Stop();
        _turn.Dispose();
    }
}
