using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentsHub.Services;
using AgentsHub.Services.Platform;

namespace AgentsHub.Services.Byok;

/// <summary>One tool call the CLI reported, start or finish.</summary>
public sealed record ByokToolEvent(
    string CallId,
    string Tool,
    bool IsStart,
    bool Success,
    string? Detail,
    string? Output);

/// <summary>What one turn produced.</summary>
public sealed record ByokTurn(string Answer, string? SessionId, int TokensIn, int TokensOut, double Cost);

/// <summary>
/// Runs the user's own API key through the Claude Code CLI.
///
/// The CLI is the transport on purpose: it already speaks the Anthropic protocol, owns the tool loop,
/// the permission model and the thinking stream, and everything it needs can be handed to it on the
/// command line. Reimplementing that against a provider's HTTP API would be a second, worse copy of
/// the same thing.
///
/// Measured against claude 2.1.x:
///   claude -p "&lt;prompt&gt;" --output-format stream-json --verbose --settings &lt;json&gt;
///   stdout is JSONL: system/init, assistant, user(tool_result), result
///
/// The provider travels in `--settings` rather than in the child's environment. A user whose
/// ~/.claude/settings.json already points ANTHROPIC_BASE_URL at a local proxy would otherwise keep
/// talking to that proxy: settings beat the environment, and the turn silently ran on somebody
/// else's endpoint. Command-line settings win over the user file, so this is the only form that
/// actually takes effect. Nothing is written to disk; the key lives in this process's memory and in
/// one child's argument list.
/// </summary>
public sealed class ByokEngine
{
    public static ByokEngine Instance { get; } = new();

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "agentshub-byok.log");

    private const int TurnTimeoutMs = 15 * 60 * 1000;

    private readonly SemaphoreSlim _turn = new(1, 1);
    private Process? _process;
    private bool _disposed;

    /// <summary>
    /// True while the turn in flight was stopped by the Stop button. Killing the child makes the read
    /// loop end on a non-zero exit, which is indistinguishable from a crash, so the caller needs a way
    /// to tell a deliberate stop from a failure.
    /// </summary>
    public bool Stopped { get; private set; }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (IOException)
        {
            // Diagnostics must never break a turn.
        }
    }

    /// <summary>Kills the turn in flight. Safe from any thread.</summary>
    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                Stopped = true;
                _process.Kill(entireProcessTree: true);
                Log("stop sent");
            }
        }
        catch (Exception ex)
        {
            Log($"stop failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Runs one prompt. `provider` carries what the user pasted; nothing is read from global config,
    /// so two sessions on two providers cannot borrow each other's key.
    /// </summary>
    public async Task<ByokTurn> SendAsync(
        string prompt,
        string root,
        ClaudeGatewayProvider provider,
        string? sessionId,
        string? model,
        bool autoApprove,
        IProgress<string>? progress,
        Action<string>? onThinking,
        Action<ByokToolEvent>? onTool,
        CancellationToken ct = default)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            string key = provider.GetEffectiveApiKeys().FirstOrDefault() ?? provider.ApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException(
                    $"No API key is stored for '{provider.Name}'. Add one in AccountsHub → Claude Code Gateway.");
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                throw new InvalidOperationException($"'{provider.Name}' has no base URL. Add it in AccountsHub.");
            }

            // An OpenAI-style endpoint cannot be handed to Claude Code directly: the CLI speaks the
            // Anthropic Messages API. The app's local bridge translates between the two, so those
            // providers are reached through it. It is started per turn, on the port the app owns, and
            // the token names this provider so the bridge cannot serve a different one.
            string endpointBase = provider.BaseUrl.Trim();
            string endpointToken = key.Trim();

            if (provider.IsOpenAiCompatible)
            {
                if (!provider.UseBridge)
                {
                    throw new InvalidOperationException(
                        $"'{provider.Name}' is marked as OpenAI-compatible but the translating bridge is off. " +
                        "Claude Code only speaks the Anthropic Messages API, so turn the bridge back on in your provider settings " +
                        "or set the endpoint type to Anthropic.");
                }

                if (!ClaudeOpenRouterProxyService.Instance.Start(ClaudeOpenRouterProxyService.DefaultPort))
                {
                    throw new InvalidOperationException(
                        $"The local bridge could not start on port {ClaudeOpenRouterProxyService.DefaultPort}. Another process may hold it.");
                }

                endpointBase = $"http://127.0.0.1:{ClaudeOpenRouterProxyService.DefaultPort}";
                endpointToken = "agentshub-bridge:" + provider.Id;
            }

            // A session with no choice of its own still needs a concrete model: the provider may not
            // offer the CLI's default, and the user's own ANTHROPIC_MODEL would leak in otherwise.
            string chosen = PickModel(model, provider);

            var args = new List<string>
            {
                "-p",
                prompt,
                "--output-format",
                "stream-json",
                "--verbose",
                // Deltas, so a thought reaches the screen while it is being had instead of all at
                // once when the block closes.
                "--include-partial-messages",
                "--settings",
                SettingsJson(endpointBase, endpointToken, chosen),
            };

            if (!string.IsNullOrWhiteSpace(chosen))
            {
                args.Add("--model");
                args.Add(chosen);
            }

            if (autoApprove) args.Add("--dangerously-skip-permissions");

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                args.Add("--resume");
                args.Add(sessionId);
            }

            Stopped = false;

            using var process = Start(root, args, endpointBase, endpointToken, chosen);
            _process = process;

            Log($"turn start, {prompt.Length} chars, provider={provider.Name}, model={chosen ?? "default"}, resume={sessionId ?? "new"}");

            var text = new StringBuilder();
            var stderr = new StringBuilder();
            bool streamedThinking = false;
            string capturedSession = sessionId ?? string.Empty;
            string? failure = null;
            string resultText = string.Empty;
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

                    ByokEvent parsed;
                    try
                    {
                        parsed = Parse(line);
                    }
                    catch (JsonException)
                    {
                        Log($"unparsed line: {Tail(line, 120)}");
                        continue;
                    }

                    if (parsed.SessionId.Length > 0) capturedSession = parsed.SessionId;

                    // A thought arrives twice: once as deltas while it is being had, then complete
                    // with the message. Only the deltas are sent while streaming, and the complete
                    // copy is used only if no delta came (a gateway that sends no partial events).
                    if (parsed.Thinking.Length > 0)
                    {
                        if (parsed.ThinkingIsDelta) streamedThinking = true;
                        if (parsed.ThinkingIsDelta || !streamedThinking) onThinking?.Invoke(parsed.Thinking);
                    }

                    if (parsed.Text.Length > 0)
                    {
                        text.Append(parsed.Text);
                        progress?.Report(parsed.Text);
                    }

                    if (parsed.Result.Length > 0) resultText = parsed.Result;
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

            string answer = text.Length > 0 ? text.ToString().Trim() : resultText.Trim();

            if (failure is not null)
            {
                Log("stderr:\n" + stderr.ToString().Trim());
                throw new InvalidOperationException(Explain(failure));
            }

            if (process.ExitCode != 0 && answer.Length == 0)
            {
                string tail = Tail(stderr.ToString());
                Log("stderr:\n" + tail);
                throw new InvalidOperationException(
                    $"Claude Code exited with code {process.ExitCode}. {(tail.Length > 0 ? tail : "Check the base URL and key for this provider.")}");
            }

            if (answer.Length == 0)
            {
                throw new InvalidOperationException("Claude Code finished the turn without emitting any text.");
            }

            Log($"turn done, {answer.Length} chars, session={capturedSession}");

            return new ByokTurn(answer, capturedSession.Length > 0 ? capturedSession : null, tokensIn, tokensOut, cost);
        }
        finally
        {
            _process = null;
            _turn.Release();
        }
    }

    /// <summary>One stdout line, decoded. Unknown lines decode to empty and change nothing.</summary>
    internal readonly record struct ByokEvent(
        string SessionId,
        string Text,
        string Thinking,
        string Result,
        ByokToolEvent? Tool,
        string? Error,
        int TokensIn,
        int TokensOut,
        double Cost,
        bool ThinkingIsDelta = false);

    internal static ByokEvent Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        string sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? string.Empty : string.Empty;
        string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;

        string text = string.Empty;
        string thinking = string.Empty;
        string result = string.Empty;
        ByokToolEvent? tool = null;
        string? error = null;
        int tokensIn = 0;
        int tokensOut = 0;
        double cost = 0;
        bool thinkingIsDelta = false;

        switch (type)
        {
            case "assistant":
            case "user":
                ReadContent(root, type, ref text, ref thinking, ref tool, ref error);
                break;

            // A partial-message delta: the only place a thought can be seen while it happens.
            case "stream_event":
                if (root.TryGetProperty("event", out var streamEvent)
                    && streamEvent.TryGetProperty("type", out var streamTypeEl)
                    && string.Equals(streamTypeEl.GetString(), "content_block_delta", StringComparison.Ordinal)
                    && streamEvent.TryGetProperty("delta", out var streamDelta)
                    && streamDelta.TryGetProperty("type", out var streamDeltaTypeEl)
                    && string.Equals(streamDeltaTypeEl.GetString(), "thinking_delta", StringComparison.Ordinal)
                    && streamDelta.TryGetProperty("thinking", out var thinkingDeltaEl))
                {
                    thinking = thinkingDeltaEl.GetString() ?? string.Empty;
                    thinkingIsDelta = thinking.Length > 0;
                }
                break;

            case "result":
                result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString() ?? string.Empty
                    : string.Empty;

                if (root.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                {
                    error = result.Length > 0 ? result : "Claude Code reported an error.";
                }

                if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number)
                {
                    cost = c.GetDouble();
                }

                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number) tokensIn = i.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number) tokensOut = o.GetInt32();
                }
                break;
        }

        return new ByokEvent(sessionId, text, thinking, result, tool, error, tokensIn, tokensOut, cost, thinkingIsDelta);
    }

    private static void ReadContent(
        JsonElement root,
        string type,
        ref string text,
        ref string thinking,
        ref ByokToolEvent? tool,
        ref string? error)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            string kind = block.TryGetProperty("type", out var k) ? k.GetString() ?? string.Empty : string.Empty;

            switch (kind)
            {
                case "text" when type == "assistant":
                    text += block.TryGetProperty("text", out var body) ? body.GetString() ?? string.Empty : string.Empty;
                    break;

                case "thinking":
                    thinking += block.TryGetProperty("thinking", out var thought) ? thought.GetString() ?? string.Empty : string.Empty;
                    break;

                case "tool_use":
                    tool = new ByokToolEvent(
                        block.TryGetProperty("id", out var id) ? id.GetString() ?? "toolu" : "toolu",
                        block.TryGetProperty("name", out var name) ? name.GetString() ?? "tool" : "tool",
                        true,
                        true,
                        block.TryGetProperty("input", out var input) ? Tail(input.ToString(), 400) : null,
                        null);
                    break;

                case "tool_result":
                    bool failed = block.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True;
                    string output = block.TryGetProperty("content", out var body2)
                        ? body2.ValueKind == JsonValueKind.String ? body2.GetString() ?? string.Empty : body2.ToString()
                        : string.Empty;

                    tool = new ByokToolEvent(
                        block.TryGetProperty("tool_use_id", out var useId) ? useId.GetString() ?? "toolu" : "toolu",
                        string.Empty,
                        false,
                        !failed,
                        null,
                        Tail(output, 4000));

                    if (failed) error = Head(output, 400);
                    break;
            }
        }
    }

    /// <summary>Turns the CLI's own wording into something a person can act on.</summary>
    private static string Explain(string failure)
    {
        string trimmed = failure.Trim();

        if (Matches(trimmed, "401|unauthor|invalid.*(key|token)|authentication"))
        {
            return $"The provider rejected the key: {Head(trimmed, 300)}. Re-paste it in AccountsHub → Claude Code Gateway.";
        }

        if (Matches(trimmed, "404|not found|no such model|model.*(invalid|not found)"))
        {
            return $"The provider does not know that model: {Head(trimmed, 300)}. Use Fetch models to pick one it lists.";
        }

        if (Matches(trimmed, "credit|quota|billing|insufficient"))
        {
            return $"The provider account is out of credit: {Head(trimmed, 300)}";
        }

        return Head(trimmed, 400);
    }

    /// <summary>
    /// The settings payload for one turn: where to send requests, which key to present, and which
    /// model to ask for. An empty model is left out so the CLI's own default applies instead of an
    /// empty name it cannot resolve.
    /// </summary>
    internal static string SettingsJson(string baseUrl, string token, string? model)
    {
        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = baseUrl.Trim(),
            ["ANTHROPIC_AUTH_TOKEN"] = token.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(model)) env["ANTHROPIC_MODEL"] = model.Trim();

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["env"] = env });
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    /// <summary>
    /// The model this turn asks for.
    ///
    /// A session started from the engine picker can carry the Antigravity default
    /// ("Gemini 3.8 Flash (High)") as its model. That is a display label, not an id any provider
    /// here knows, so a name the provider did not list is replaced by the provider's own choice
    /// rather than being sent and failing the turn on a 404.
    /// </summary>
    internal static string PickModel(string? requested, ClaudeGatewayProvider provider)
    {
        string wanted = FirstNonEmpty(requested, provider.SelectedModel);

        if (provider.Models is { Count: > 0 })
        {
            if (provider.Models.Contains(wanted, StringComparer.OrdinalIgnoreCase)) return wanted;

            return FirstNonEmpty(provider.SelectedModel, provider.Models.FirstOrDefault());
        }

        // Nothing fetched yet: the only thing that can be judged is the shape of the name.
        if (IsPlausibleId(wanted)) return wanted;

        return IsPlausibleId(provider.SelectedModel) ? provider.SelectedModel : string.Empty;
    }

    private static bool IsPlausibleId(string value) =>
        value.Length > 0 && !value.Contains(' ') && !value.Contains('(');

    private Process Start(string root, IReadOnlyList<string> args, string baseUrl, string token, string? model)
    {
        string binary = ResolveClaude();

        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Directory.Exists(root)
                ? root
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            FileName = binary,
        };

        foreach (string arg in args) info.ArgumentList.Add(arg);

        // The child is told where to send requests and which key to present. ANTHROPIC_API_KEY is
        // removed on purpose: when both it and the token are set, the CLI warns and may prefer the
        // stale one, which is how a turn ends up on somebody else's account.
        info.Environment["ANTHROPIC_BASE_URL"] = baseUrl.Trim();
        info.Environment["ANTHROPIC_AUTH_TOKEN"] = token.Trim();
        // The command line carries these as well; the environment is the fallback for older CLIs that
        // ignore --settings. The local bridge ignores the caller's token in both cases.
        info.Environment.Remove("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(model)) info.Environment["ANTHROPIC_MODEL"] = model;

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("Claude Code could not be started. Install it with `npm install -g @anthropic-ai/claude-code`.");

        try { process.StandardInput.Close(); } catch { /* already gone */ }

        return process;
    }

    /// <summary>
    /// Finds the CLI. A GUI process inherits the PATH Explorer had, which often predates a user-level
    /// npm install, so the usual install folders are checked directly as well.
    /// </summary>
    private static string ResolveClaude()
    {
        foreach (string name in new[] { "claude.exe", "claude.cmd", "claude" })
        {
            string? found = SearchPath(name);
            if (found is not null) return found;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string candidate in new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, ".local", "bin", "claude.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        // Let the shell resolve it: a missing binary then reports as a normal exit error.
        return PlatformHelper.IsWindows ? "claude.cmd" : "claude";
    }

    private static string? SearchPath(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return null;
    }

    private static bool Matches(string value, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            value, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Head(string value, int max) => value.Length <= max ? value : value[..max] + "…";

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
