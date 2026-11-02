// The tool protocol and the MCP registry live in Services/DeepSeek, which only the desktop build
// compiles (they need WebView2 for the other engines). The headless server build therefore keeps
// reaching a local model through the CLI, and this runner exists where the chat does.
#if DEEPSEEK_UI
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AgentsHub.Services.DeepSeek;

namespace AgentsHub.Services.LlamaCpp;

/// <summary>One step of a direct local turn: a file or shell action the model asked for.</summary>
public sealed record LocalToolStep(
    string CallId,
    string Action,
    bool IsStart,
    bool Success,
    string Detail,
    string? Error,
    string? Summary);

/// <summary>What one direct local turn produced.</summary>
public sealed record LocalChatTurn(
    string Answer,
    string ConversationId,
    int TokensIn,
    int TokensOut,
    double TokensPerSecond);

/// <summary>One message in a local conversation, so the thread can be written to disk as it stands.</summary>
internal sealed record LocalMessage(string Role, string Content);

/// <summary>
/// A local chat with no Claude Code in the middle.
///
/// The Claude Code CLI is a fine transport for a hosted provider — it brings a tool loop, a
/// permission model and a thinking stream — but for a server on 127.0.0.1 it is a wrapper that has to
/// be reached through a translating bridge. This class talks to llama.cpp's own OpenAI-compatible
/// endpoint instead: one SSE stream per step, so tokens appear as they are generated, a thought shows
/// up while it is had, and a tool request is a fenced block the app executes here.
///
/// The tool protocol is the same one the DeepSeek and Z.ai engines use (<see cref="DeepSeekTools"/>),
/// so a local model gets file reads, writes, patches and shell commands without a second
/// implementation of them.
/// </summary>
public sealed class LocalChatEngine
{
    public static LocalChatEngine Instance { get; } = new();

    /// <summary>Steps in one turn. A local model that keeps asking for tools has to stop somewhere.</summary>
    private const int MaxSteps = 12;

    /// <summary>History kept per conversation, in characters, so the context does not fill with chat.</summary>
    private const int HistoryBudget = 20_000;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly string ThreadFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub", "local_chats");

    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly ConcurrentDictionary<string, List<LocalMessage>> _threads = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;

    /// <summary>True when the current turn was ended by the Stop button, not by a failure.</summary>
    public bool Stopped { get; private set; }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            Stopped = true;
        }
        catch (ObjectDisposedException)
        {
            // The turn finished between the click and this call.
        }
    }

    /// <summary>Forgets a conversation, on disk as well. A new chat should not inherit the old one's files.</summary>
    public void Forget(string conversationId)
    {
        _threads.TryRemove(conversationId, out _);

        try
        {
            string path = ThreadFile(conversationId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A conversation nobody can load again is not worth failing a request over.
        }
    }

    public async Task<LocalChatTurn> SendAsync(
        string prompt,
        string root,
        string? model,
        string? conversationId,
        bool autoApprove,
        IProgress<string>? onText,
        Action<string>? onThinking,
        Func<string, string, string, Task<bool>>? approve,
        Action<LocalToolStep>? onTool,
        CancellationToken ct = default)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = linked;
        Stopped = false;

        var service = LlamaCppService.Instance;
        service.BeginRequest();

        try
        {
            // Asked of the endpoint rather than of this app's own process list: a server started by
            // hand answers here too, and the model id is what the request has to name anyway.
            string reachable = await FirstModelIdAsync(linked.Token).ConfigureAwait(false);
            if (reachable.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Nothing is answering at {service.Endpoint}. Load a model in Local models, " +
                    "or turn on \u201cRun a local model automatically\u201d in Settings.");
            }

            string id = string.IsNullOrWhiteSpace(conversationId)
                ? "local-" + Guid.NewGuid().ToString("N")[..12]
                : conversationId;

            string modelId = string.IsNullOrWhiteSpace(model) ? reachable : model.Trim();

            var runtime = service.Runtime;
            var thread = Thread(id);
            thread.Add(new LocalMessage("user", prompt));

            var pending = new List<LocalMessage>();
            string answer = string.Empty;
            int tokensIn = 0;
            int tokensOut = 0;
            double rate = 0;

            for (int step = 0; step < MaxSteps; step++)
            {
                var messages = BuildMessages(root, thread, pending);
                var streamed = await StreamAsync(modelId, messages, runtime.Temperature, runtime.MaxTokens, onText, onThinking, linked.Token)
                    .ConfigureAwait(false);

                // The prompt is re-sent on every step, so adding the steps up would count the same
                // history several times. The largest prompt is what the turn actually cost to read;
                // generated tokens are new every step and do accumulate.
                tokensIn = Math.Max(tokensIn, streamed.TokensIn);
                tokensOut += streamed.TokensOut;
                if (streamed.TokensPerSecond > 0) rate = streamed.TokensPerSecond;

                pending.Add(new LocalMessage("assistant", streamed.Text));

                var calls = DeepSeekTools.Extract(streamed.Text);
                if (calls.Count == 0)
                {
                    answer = DeepSeekTools.Strip(streamed.Text);
                    break;
                }

                var results = new StringBuilder();

                foreach (var call in calls)
                {
                    string action = call.TryGetProperty("action", out var actionEl) ? actionEl.GetString() ?? string.Empty : string.Empty;
                    if (action.Length == 0) continue;

                    string callId = "local-" + Guid.NewGuid().ToString("N")[..8];
                    string preview = DeepSeekTools.Preview(action, call);

                    onTool?.Invoke(new LocalToolStep(callId, action, true, true, preview, null, null));

                    bool allowed = autoApprove || !DeepSeekTools.RequiresApproval(action);
                    if (!allowed && approve is not null) allowed = await approve(callId, action, preview).ConfigureAwait(false);

                    if (!allowed)
                    {
                        onTool?.Invoke(new LocalToolStep(callId, action, false, false, preview, "declined", "The user declined this action."));
                        results.AppendLine($"### {action}\nThe user declined this action. Do not retry it; explain what you need instead.\n");
                        continue;
                    }

                    try
                    {
                        var (summary, body) = DeepSeekTools.Execute(action, call, root);
                        onTool?.Invoke(new LocalToolStep(callId, action, false, true, preview, null, summary));
                        results.AppendLine($"### {action}\n{summary}\n{body}\n");
                    }
                    catch (Exception ex)
                    {
                        onTool?.Invoke(new LocalToolStep(callId, action, false, false, preview, ex.Message, ex.Message));
                        results.AppendLine($"### {action}\nFailed: {ex.Message}\n");
                    }
                }

                pending.Add(new LocalMessage("user", "TOOL RESULTS\n\n" + results.ToString().TrimEnd()));
            }

            if (answer.Length == 0)
            {
                answer = "The model kept asking for tools and the turn was stopped after " + MaxSteps + " steps.";
            }

            pending.Add(new LocalMessage("assistant", answer));
            thread.AddRange(pending);
            Trim(thread);
            Save(id, thread);

            return new LocalChatTurn(answer, id, tokensIn, tokensOut, rate);
        }
        finally
        {
            service.EndRequest();
            _cts = null;
            _turn.Release();
            linked.Dispose();
        }
    }

    // ---- history on disk ----

    /// <summary>
    /// The conversation for an id, read from disk the first time it is asked for. Without this a
    /// local chat started from scratch on every launch: the transcript in the page survived, the
    /// model's own copy of it did not, so a resumed session answered as if it had never spoken.
    /// </summary>
    private List<LocalMessage> Thread(string id)
    {
        if (_threads.TryGetValue(id, out var known)) return known;

        var loaded = new List<LocalMessage>();

        try
        {
            string path = ThreadFile(id);
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<List<LocalMessage>>(File.ReadAllText(path));
                if (stored is { Count: > 0 }) loaded.AddRange(stored);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A damaged file starts a fresh conversation instead of failing the turn.
        }

        _threads[id] = loaded;
        return loaded;
    }

    private static void Save(string id, List<LocalMessage> thread)
    {
        try
        {
            string path = ThreadFile(id);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(thread));
        }
        catch (IOException)
        {
            // Persistence is a convenience; the turn already answered.
        }
    }

    private static string ThreadFile(string id)
    {
        string safe = new([.. id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')]);
        if (safe.Length == 0) safe = "local";

        return Path.Combine(ThreadFolder, safe + ".json");
    }

    /// <summary>
    /// System prompt, then the conversation, then this turn's tool traffic. The protocol text is a
    /// system message that has to stay first: a chat template rejects one in the middle, which is
    /// exactly what llama.cpp reported before.
    /// </summary>
    private static List<object> BuildMessages(
        string root,
        List<LocalMessage> thread,
        List<LocalMessage> pending)
    {
        IReadOnlyList<SkillEntry> skills;
        IReadOnlyList<McpToolInfo>? mcp = null;

        try { skills = SkillCatalog.Discover(root); }
        catch { skills = []; }

        try { mcp = McpRegistry.Tools(); }
        catch { mcp = null; }

        var messages = new List<object>
        {
            new { role = "system", content = DeepSeekTools.Guide(root, skills, mcp) }
        };

        foreach (var message in thread.Concat(pending))
        {
            if (message.Content.Length > 0) messages.Add(new { role = message.Role, content = message.Content });
        }

        return messages;
    }

    /// <summary>Drops the oldest exchanges until the conversation fits the budget below the context.</summary>
    private static void Trim(List<LocalMessage> thread)
    {
        int total = thread.Sum(m => m.Content.Length);

        while (thread.Count > 4 && total > HistoryBudget)
        {
            total -= thread[0].Content.Length;
            thread.RemoveAt(0);
        }
    }

    private sealed record StreamResult(string Text, int TokensIn, int TokensOut, double TokensPerSecond);

    /// <summary>
    /// One streamed completion. Text and reasoning are reported as they arrive: llama.cpp sends
    /// `reasoning_content` for a thinking model, and a model that writes its thought inline is
    /// split apart afterwards so the transcript still gets one.
    /// </summary>
    private static async Task<StreamResult> StreamAsync(
        string model,
        List<object> messages,
        double temperature,
        int maxTokens,
        IProgress<string>? onText,
        Action<string>? onThinking,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            // Asks the server to end the stream with a usage chunk, which is where the real token
            // counts come from. A build that does not know the option ignores it.
            ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true },
            // Qwen and friends keep thinking off unless the template is told; a template without the
            // variable ignores it.
            ["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = true }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{LlamaCppService.Instance.Endpoint}/chat/completions");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"llama.cpp answered {(int)response.StatusCode}: {Condense(body)}");
        }

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        int tokensIn = 0;
        int tokensOut = 0;
        double rate = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) tokensIn = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ctout) && ctout.ValueKind == JsonValueKind.Number) tokensOut = ctout.GetInt32();
                }

                // llama.cpp reports its own measured speed for the request. Taking it here means the
                // readout is right even when the server's log lines are not being watched.
                if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
                {
                    if (timings.TryGetProperty("predicted_per_second", out var pps) && pps.ValueKind == JsonValueKind.Number) rate = pps.GetDouble();
                    if (timings.TryGetProperty("predicted_n", out var pn) && pn.ValueKind == JsonValueKind.Number && pn.GetInt32() > 0) tokensOut = pn.GetInt32();
                    if (timings.TryGetProperty("prompt_n", out var ppn) && ppn.ValueKind == JsonValueKind.Number && ppn.GetInt32() > 0) tokensIn = ppn.GetInt32();
                }

                if (root.TryGetProperty("error", out var error))
                {
                    string message = error.ValueKind == JsonValueKind.String
                        ? error.GetString() ?? "llama.cpp reported an error."
                        : error.TryGetProperty("message", out var mEl) ? mEl.GetString() ?? "llama.cpp reported an error." : error.ToString();
                    throw new InvalidOperationException(Condense(message));
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;

                var choice = choices[0];

                // A server that ignores `stream` answers once with a whole message.
                if (choice.TryGetProperty("message", out var whole) && whole.TryGetProperty("content", out var wholeContent))
                {
                    string value = wholeContent.GetString() ?? string.Empty;
                    if (value.Length > 0) { text.Append(value); onText?.Report(value); }
                    if (whole.TryGetProperty("reasoning_content", out var wholeReasoning))
                    {
                        string r = wholeReasoning.GetString() ?? string.Empty;
                        if (r.Length > 0) { thinking.Append(r); onThinking?.Invoke(r); }
                    }
                    continue;
                }

                if (!choice.TryGetProperty("delta", out var delta)) continue;

                if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                {
                    string piece = reasoning.GetString() ?? string.Empty;
                    if (piece.Length > 0) { thinking.Append(piece); onThinking?.Invoke(piece); }
                }

                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    string piece = content.GetString() ?? string.Empty;
                    if (piece.Length > 0) { text.Append(piece); onText?.Report(piece); }
                }
            }
        }

        string answer = text.ToString();

        // Some builds send the thought inside the answer as  thinking…</think>. Move it out so it lands
        // in the Thought row instead of the reply.
        if (thinking.Length == 0 && answer.Contains("think>", StringComparison.Ordinal))
        {
            var (visible, thought) = SplitInlineThinking(answer);
            if (thought.Length > 0)
            {
                onThinking?.Invoke(thought);
                answer = visible;
            }
        }

        return new StreamResult(answer, tokensIn, tokensOut, rate);
    }

    /// <summary>Splits `… content` into the visible answer and the thought it carried.</summary>
    private static (string Visible, string Thought) SplitInlineThinking(string answer)
    {
        var visible = new StringBuilder();
        var thought = new StringBuilder();
        int index = 0;

        while (index < answer.Length)
        {
            int open = answer.IndexOf("<think", index, StringComparison.OrdinalIgnoreCase);
            if (open < 0) { visible.Append(answer, index, answer.Length - index); break; }

            int bodyStart = answer.IndexOf('>', open);
            if (bodyStart < 0) break;

            visible.Append(answer, index, open - index);

            int close = answer.IndexOf("</think", bodyStart, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                thought.Append(answer, bodyStart + 1, answer.Length - bodyStart - 1);
                index = answer.Length;
                break;
            }

            thought.Append(answer, bodyStart + 1, close - bodyStart - 1);
            index = close;

            int closeEnd = answer.IndexOf('>', close);
            index = closeEnd < 0 ? answer.Length : closeEnd + 1;
        }

        return (visible.ToString().Trim(), thought.ToString().Trim());
    }

    private static async Task<string> FirstModelIdAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"{LlamaCppService.Instance.Endpoint}/models", ct).ConfigureAwait(false);
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

    /// <summary>One line of the server's answer, short enough to sit in a chat line.</summary>
    private static string Condense(string body)
    {
        string flat = body.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");

        return flat.Length <= 300 ? flat : flat[..300] + "…";
    }
}
#endif
