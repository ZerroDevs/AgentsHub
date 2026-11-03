// The tool protocol and the MCP registry live in Services/DeepSeek, which only the desktop build
// compiles (they need WebView2 for the other engines). The headless server build therefore keeps
// reaching a local model through the CLI, and this runner exists where the chat does.
#if DEEPSEEK_UI
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

/// <summary>One image the page attached, already base64: a local model takes it inline, not as a path.</summary>
public sealed record LocalImage(string MimeType, string Base64);

/// <summary>
/// A local chat with no Claude Code in the middle.
///
/// The Claude Code CLI is a fine transport for a hosted provider — it brings a tool loop, a
/// permission model and a thinking stream — but for a server on 127.0.0.1 it is a wrapper that has to
/// be reached through a translating bridge. This class talks to llama.cpp's own OpenAI-compatible
/// endpoint instead: one SSE stream per step, so tokens appear as they are generated, a thought shows
/// up while it is had, and a tool request is a fenced block the app executes here.
///
/// The tool protocol is the one the DeepSeek and Qwen web engines use (<see cref="DeepSeekTools"/>),
/// in its short form: same parser, same actions, a fraction of the text. A 2B has little context to
/// spend on a manual, and the guards in the turn loop below enforce what the long guide only asks for.
/// </summary>
public sealed class LocalChatEngine
{
    public static LocalChatEngine Instance { get; } = new();

    public LocalChatEngine() => SweepOldThreads();

    /// <summary>Steps in one turn. A local model that keeps asking for tools has to stop somewhere.</summary>
    private const int MaxSteps = 12;

    /// <summary>History kept per conversation, in characters, so the context does not fill with chat.</summary>
    private const int HistoryBudget = 20_000;

    /// <summary>
    /// Openers of the turns this engine writes for itself. Used twice: when it composes them, and
    /// when it loads an older thread that still has them in it.
    /// </summary>
    private const string ToolResultsPrefix = "TOOL RESULTS\n\n";
    private const string UnknownActionPrefix = "There is no action named ";
    private const string UnreadFilePrefix = "You answered without reading ";
    private const string ReadBeforeWriteMark = "before changing it: reply with only the read_file block";

    /// <summary>How long a thread nobody opened again is kept on disk.</summary>
    private const int ThreadRetentionDays = 30;

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
        IReadOnlyList<LocalImage>? images = null,
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

            // A model loaded without a projector accepts the image and then ignores it, which reads as
            // a model that cannot see. Refusing is the honest answer, and it names the missing file.
            if (images is { Count: > 0 } && await VisionSupportedAsync(linked.Token).ConfigureAwait(false) == false)
            {
                throw new InvalidOperationException(
                    "This local model was started without a vision projector, so it cannot read an image. " +
                    "Put the mmproj file beside the model, or pick it in the model's flags, then load it again.");
            }

            var runtime = service.Runtime;
            var thread = Thread(id);
            thread.Add(new LocalMessage("user", prompt));

            var pending = new List<LocalMessage>();
            string answer = string.Empty;
            int tokensIn = 0;                    int tokensOut = 0;
                    double rate = 0;

            // Guards that exist for small models, which invent action names and rewrite files they
            // never opened. Each corrects once, so a stubborn model still ends the turn.
            var readThisTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool correctedAction = false;
            bool correctedRead = false;
            bool wroteThisTurn = false;

            for (int step = 0; step < MaxSteps; step++)
            {
                // The images belong to the prompt this turn started with; a later step is a tool
                // round-trip, and re-attaching them there would only cost tokens.
                var messages = BuildMessages(root, thread, pending, step == 0 ? images : null);
                var streamed = await StreamAsync(modelId, messages, runtime.Temperature, runtime.MaxTokens, onText, onThinking, linked.Token)
                    .ConfigureAwait(false);

                // The prompt is re-sent on every step, so adding the steps up would count the same
                // history several times. The largest prompt is what the turn actually cost to read;
                // generated tokens are new every step and do accumulate.
                tokensIn = Math.Max(tokensIn, streamed.TokensIn);
                tokensOut += streamed.TokensOut;
                if (streamed.TokensPerSecond > 0) rate = streamed.TokensPerSecond;

                var calls = DeepSeekTools.Extract(streamed.Text);

                // An invented action name produced no call at all, and the JSON then sat in the reply
                // as if it had run. Naming the allowed actions is a cheap, deterministic recovery.
                if (calls.Count == 0 && !correctedAction && DeepSeekTools.UnknownActions(streamed.Text) is { Count: > 0 } invented)
                {
                    correctedAction = true;
                    pending.Add(new LocalMessage("user",
                        $"{UnknownActionPrefix}{string.Join(", ", invented)}. " +
                        $"Allowed actions: {string.Join(", ", DeepSeekTools.Actions)}. " +
                        "Reply with one tool block that uses one of those names."));
                    continue;
                }

                // A small model often describes the file it was asked about instead of reading it,
                // and the description looks like a finished turn. When the request names a file that
                // is actually there, one pointed retry turns that into the tool call; without it a 2B
                // invents the contents and the mistake is silent.
                if (calls.Count == 0 && step == 0 && MentionedFile(prompt, root) is { } named)
                {
                    // The exact block, not a description of one: a 2B copies a snippet far more
                    // reliably than it follows an instruction to produce the same thing.
                    pending.Add(new LocalMessage("user",
                        $"{UnreadFilePrefix}{named}. Reply with exactly this and nothing else:\n" +
                        "```tool\n" +
                        $"{{\"action\": \"read_file\", \"path\": {System.Text.Json.JsonSerializer.Serialize(named)}}}\n" +
                        "```"));

                    continue;
                }

                pending.Add(new LocalMessage("assistant", streamed.Text));

                if (calls.Count == 0)
                {
                    answer = DeepSeekTools.Strip(streamed.Text);
                    break;
                }

                var results = new StringBuilder();
                bool askedToRead = false;

                foreach (var call in calls)
                {
                    string action = call.TryGetProperty("action", out var actionEl) ? actionEl.GetString() ?? string.Empty : string.Empty;
                    if (action.Length == 0) continue;

                    string callPath = call.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;
                    string callFull = callPath.Length == 0 ? string.Empty : SafeFullPath(root, callPath);

                    // Writing a file it never opened is how a 2B replaces real code with remembered
                    // guesses. The guide asks it to read first; here that is enforced once per file.
                    if (!correctedRead
                        && action is "write_file" or "apply_patch"
                        && callFull.Length > 0
                        && File.Exists(callFull)
                        && !readThisTurn.Contains(callFull))
                    {
                        correctedRead = true;
                        askedToRead = true;
                        pending.Add(new LocalMessage("user",
                            $"Read {callPath} {ReadBeforeWriteMark}, then edit what you get back."));
                        break;
                    }

                    if (action == "read_file" && callFull.Length > 0) readThisTurn.Add(callFull);

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
                        if (action is "write_file" or "apply_patch") wroteThisTurn = true;

                        onTool?.Invoke(new LocalToolStep(callId, action, false, true, preview, null, summary));
                        results.AppendLine($"### {action}\n{summary}\n{body}\n");
                    }
                    catch (Exception ex)
                    {
                        onTool?.Invoke(new LocalToolStep(callId, action, false, false, preview, ex.Message, ex.Message));
                        results.AppendLine($"### {action}\nFailed: {ex.Message}\n");
                    }
                }

                if (askedToRead) continue;

                // Said at the end of the results because that is the last thing the model reads, and
                // a 2B otherwise answers with the corrected file pasted into the chat instead of a
                // write call — the work is done and nothing on disk changed.
                results.AppendLine("If a file must change, reply with only the write_file or apply_patch block.");

                pending.Add(new LocalMessage("user", ToolResultsPrefix + results.ToString().TrimEnd()));
            }

            if (answer.Length == 0)
            {
                answer = "The model kept asking for tools and the turn was stopped after " + MaxSteps + " steps.";
            }

            // A small model reads the file and then hands the change back as chat text: the reply sounds
            // finished while nothing on disk moved. Naming the size that follows the write step is more
            // useful than letting the person conclude the tooling is broken.
            if (!wroteThisTurn && MentionedFile(prompt, root) is { } target && WantsChange(prompt))
            {
                var (kind, label) = ModelSize(modelId);

                string note = kind switch
                {
                    ModelSizeKind.Small =>
                        $"> This model is about {label}. It described the change to `{target}` instead of writing it. " +
                        "File edits need a bigger model — around 7B and up usually follow the write step; pick one in Local models.",

                    // Size unknown: the advice still holds, but naming a number would be a guess.
                    ModelSizeKind.Unknown =>
                        $"> This model described the change to `{target}` instead of writing it. Some models skip the " +
                        "write step and answer in prose; if it keeps happening, a larger model (around 7B and up) or " +
                        "one tuned for tools usually follows through.",

                    _ => string.Empty
                };

                if (note.Length > 0) answer += "\n\n" + note;
            }

            // Only the prompt and the prose answer outlive the turn. Keeping the tool exchange made
            // the saved thread mostly file dumps and "you forgot to read it" retries, and a small
            // model then spent its attention on the plumbing instead of the conversation. The live
            // turn is unaffected: `pending` still carries every tool result until the answer lands.
            thread.Add(new LocalMessage("assistant", answer));
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
                if (stored is { Count: > 0 })
                {
                    // Threads written before the split above still hold the tool traffic. Filtering
                    // on load means an existing chat gets the clean history from its next turn
                    // instead of carrying the noise until the budget trims it.
                    stored.RemoveAll(IsProtocolMessage);
                    loaded.AddRange(WithoutRepeatedAnswers(stored));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A damaged file starts a fresh conversation instead of failing the turn.
        }

        _threads[id] = loaded;
        return loaded;
    }

    /// <summary>
    /// True for a turn this engine wrote to itself: tool results, and the three corrections it sends
    /// when a small model invents an action, skips the read or edits blind.
    /// </summary>
    private static bool IsProtocolMessage(LocalMessage message)
    {
        if (message.Role == "user")
        {
            return message.Content.StartsWith(ToolResultsPrefix, StringComparison.Ordinal)
                || message.Content.StartsWith(UnknownActionPrefix, StringComparison.Ordinal)
                || message.Content.StartsWith(UnreadFilePrefix, StringComparison.Ordinal)
                || message.Content.Contains(ReadBeforeWriteMark, StringComparison.Ordinal);
        }

        // The tool-call replies: the whole message was a block, so nothing of it was said to a person.
        return message.Role == "assistant"
            && DeepSeekTools.Extract(message.Content).Count > 0
            && DeepSeekTools.Strip(message.Content).Trim().Length == 0;
    }

    /// <summary>
    /// Drops an assistant message that repeats the one before it. An earlier build stored every
    /// answer twice, and a small model reading that concludes it already replied — the same reason
    /// the live path no longer appends a second copy.
    /// </summary>
    private static IEnumerable<LocalMessage> WithoutRepeatedAnswers(List<LocalMessage> messages)
    {
        LocalMessage? previous = null;

        foreach (var message in messages)
        {
            if (previous is { Role: "assistant" }
                && message.Role == "assistant"
                && message.Content == previous.Content)
            {
                continue;
            }

            previous = message;
            yield return message;
        }
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
    /// Removes conversations nobody has touched in a month.
    ///
    /// A deleted chat is forgotten through <see cref="Forget"/>, but a session removed while the app
    /// was closed, or a local chat whose page state was cleared, leaves its file behind. One sweep per
    /// run keeps the folder from growing by a file per chat forever.
    /// </summary>
    private static void SweepOldThreads()
    {
        try
        {
            if (!Directory.Exists(ThreadFolder)) return;

            var cutoff = DateTime.UtcNow.AddDays(-ThreadRetentionDays);

            foreach (string file in Directory.EnumerateFiles(ThreadFolder, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is not worth failing the first turn over.
        }
    }

    /// <summary>
    /// System prompt, then the conversation, then this turn's tool traffic. The protocol text is a
    /// system message that has to stay first: a chat template rejects one in the middle, which is
    /// exactly what llama.cpp reported before.
    /// </summary>
    private static List<object> BuildMessages(
        string root,
        List<LocalMessage> thread,
        List<LocalMessage> pending,
        IReadOnlyList<LocalImage>? images = null)
    {
        IReadOnlyList<SkillEntry> skills;
        IReadOnlyList<McpToolInfo>? mcp = null;

        try { skills = SkillCatalog.Discover(root); }
        catch { skills = []; }

        try { mcp = McpRegistry.Tools(); }
        catch { mcp = null; }

        var messages = new List<object>
        {
            // No "confirm you understand" tail here: a local chat has no priming turn, so a small
            // model spends its whole reply on that sentence instead of calling the tool.
            new { role = "system", content = DeepSeekTools.LocalGuide(root, skills, mcp) }
        };

        var history = thread.Concat(pending).Where(m => m.Content.Length > 0).ToList();

        for (int index = 0; index < history.Count; index++)
        {
            var message = history[index];
            bool carryImages = images is { Count: > 0 } && index == history.Count - 1 && message.Role == "user";

            if (!carryImages)
            {
                messages.Add(new { role = message.Role, content = message.Content });
                continue;
            }

            // The OpenAI shape llama.cpp reads for a multimodal turn: text part first, then each image
            // inline as a data URL. Nothing is written to disk, so the stored thread stays small.
            var parts = new List<object> { new { type = "text", text = message.Content } };

            foreach (var image in images!)
            {
                parts.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:{image.MimeType};base64,{image.Base64}" }
                });
            }

            messages.Add(new { role = "user", content = parts });
        }

        return messages;
    }

    /// <summary>
    /// Drops the oldest exchanges until the conversation fits the budget below the context.
    ///
    /// The cut lands on a real prompt, not on whatever message happens to cross the budget line: a
    /// template that requires the turns to alternate rejects a history that opens with an assistant
    /// message, and a tool-result block whose call was trimmed is only half a turn.
    /// </summary>
    private static void Trim(List<LocalMessage> thread)
    {
        int total = thread.Sum(m => m.Content.Length);
        if (total <= HistoryBudget) return;

        int drop = 0;

        while (thread.Count - drop > 4 && total > HistoryBudget)
        {
            total -= thread[drop].Content.Length;
            drop++;
        }

        // `TOOL RESULTS` is addressed to the assistant, so it can only follow a turn, never start one.
        while (drop < thread.Count - 1 && !IsTurnStart(thread[drop]))
        {
            total -= thread[drop].Content.Length;
            drop++;
        }

        if (drop > 0) thread.RemoveRange(0, drop);
    }

    /// <summary>Whether the prompt asks for a change rather than an explanation.</summary>
    private static bool WantsChange(string prompt) => ChangeVerb.IsMatch(prompt);

    private static readonly Regex ChangeVerb = new(
        @"\b(add|create|write|change|update|fix|edit|refactor|rename|remove|delete|implement|patch|insert|replace|append|move)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum ModelSizeKind { Small, Large, Unknown }

    /// <summary>
    /// How big the model is, asked of the file it was loaded from.
    ///
    /// A list of known model names would only cover the ones shipped when it was written, so the
    /// answer comes from the artefact itself: the name first because reading it is free, then the
    /// GGUF header, which carries `general.size_label` and `general.name` even in a renamed file.
    /// </summary>
    private static (ModelSizeKind Kind, string Label) ModelSize(string modelId)
    {
        string loaded = LlamaCppService.Instance.LoadedModelPath;

        foreach (string? candidate in new[] { loaded, modelId })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (SizeOf(candidate) is { } found) return found;
        }

        return (ModelSizeKind.Unknown, string.Empty);
    }

    private static (ModelSizeKind, string)? SizeOf(string pathOrName)
    {
        if (FromText(Path.GetFileNameWithoutExtension(pathOrName)) is { } named) return named;

        // The header, for a file whose name says nothing. Only a real file is opened.
        if (!File.Exists(pathOrName)) return null;

        var metadata = LocalModelMetadataReader.For(pathOrName);
        if (FromText(metadata.SizeLabel) is { } labelled) return labelled;
        if (FromText(metadata.Name) is { } declared) return declared;

        return null;
    }

    private static (ModelSizeKind, string)? FromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = SizeInName.Match(text);
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double billions)) return null;
        if (billions <= 0) return null;

        return (billions <= 4.5 ? ModelSizeKind.Small : ModelSizeKind.Large, $"{match.Groups[1].Value}B");
    }

    /// <summary>A parameter count in a file name: `2B`, `1.5B`, `0.8B`. Not `Qwen3.8`, which has no B.</summary>
    private static readonly Regex SizeInName = new(@"(?<![\d.])(\d+(?:\.\d+)?)[bB](?![a-zA-Z0-9])", RegexOptions.Compiled);

    /// <summary>
    /// A path in the prompt that exists under the working directory, or null.
    ///
    /// Only an existing file counts: a retry pushed by a name the model cannot open would burn a
    /// second round trip and end with the same answer.
    /// </summary>
    private static string? MentionedFile(string prompt, string root)
    {
        foreach (string raw in prompt.Split([' ', '\n', '\r', '\t', '"', '\'', '`', ',', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim('.', ':', '?', '!');
            if (token.Length < 3 || token.Length > 260) continue;
            if (!token.Contains('.') && !token.Contains('/') && !token.Contains('\\')) continue;

            try
            {
                string full = Path.GetFullPath(Path.Combine(root, token));
                if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) return token;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a path after all.
            }
        }

        return null;
    }

    /// <summary>A request path resolved inside the working directory, or empty when it escapes it.</summary>
    private static string SafeFullPath(string root, string path)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, path));
            return full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ? full : string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static bool IsTurnStart(LocalMessage message) =>
        message.Role == "user" && !message.Content.StartsWith("TOOL RESULTS", StringComparison.Ordinal);

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

        // Only prose reaches the page. The raw stream carries the tool blocks, and appending those
        // put the model's own JSON in the reply until the turn settled over it.
        var shown = new VisibleStream();
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
                    if (value.Length > 0) { text.Append(value); shown.Report(value, onText); }
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
                    if (piece.Length > 0) { text.Append(piece); shown.Report(piece, onText); }
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

    /// <summary>
    /// Whether the running server can read an image at all. llama.cpp reports the modalities it was
    /// launched with; a build that does not answer leaves this unknown, and the request is allowed.
    /// </summary>
    private static async Task<bool?> VisionSupportedAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http
                .GetAsync($"{LlamaCppService.Instance.Endpoint}/props", ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            if (json.RootElement.TryGetProperty("modalities", out var modalities)
                && modalities.TryGetProperty("vision", out var vision))
            {
                return vision.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // No answer is not a "no"; the turn proceeds and llama.cpp decides.
        }

        return null;
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

    /// <summary>
    /// Turns a raw reply stream into visible deltas.
    ///
    /// The stream carries the tool blocks. Reporting it straight put the model's own JSON in the
    /// bubble until the turn settled over it, and a block it never closed stayed there. The shared
    /// <see cref="DeepSeekTools.Visible"/> already knows where a tool block starts and holds back an
    /// unfinished fence, so this only keeps the prefix already sent and reports the new tail.
    /// </summary>
    private sealed class VisibleStream
    {
        private readonly StringBuilder _raw = new();
        private string _emitted = string.Empty;

        public void Report(string chunk, IProgress<string>? onText)
        {
            _raw.Append(chunk);

            string visible = DeepSeekTools.Visible(_raw.ToString());
            if (visible.Length <= _emitted.Length)
            {
                // A fence that turned out to be ordinary code becomes visible as a whole, so the tail
                // only ever grows; anything else would mean re-sending text already on screen.
                return;
            }

            onText?.Report(visible[_emitted.Length..]);
            _emitted = visible;
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
