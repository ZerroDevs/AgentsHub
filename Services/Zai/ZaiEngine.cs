using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentsHub.Services.DeepSeek;
using AgentsHub.Services.Qwen;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentsHub.Services.Zai;

/// <summary>What the page is, as far as we can tell.</summary>
public enum ZaiStatus
{
    Unknown,
    /// <summary>A guest session: the page works, but only the Flash model and with a quota.</summary>
    Guest,
    SignedIn,
}

/// <summary>One selectable model, and whether the current session may pick it.</summary>
public sealed record ZaiModel(string Id, string Name, bool Locked);

/// <summary>One assistant answer, with whatever reasoning the page exposed.</summary>
public sealed record ZaiReply(string ChatId, string Answer, string Reasoning);

/// <summary>A streamed piece of one turn: either the reasoning or the answer, never both.</summary>
public sealed record ZaiDelta(string? Reasoning, string? Answer);

/// <summary>One tool step, for the transcript timeline. Start and finish are separate reports.</summary>
public sealed record ZaiToolStep(
    string CallId,
    string Action,
    string Path,
    bool IsStart,
    bool Success,
    string? Summary,
    string? Error,
    int DurationMs,
    string? Detail = null);

/// <summary>
/// Drives chat.z.ai in a hidden WebView2, the way the DeepSeek and Qwen engines drive their pages.
///
/// No sign-in is required: the site hands every visitor a guest token, so the engine works out of the
/// box and only recommends signing in (GLM-5.3 and GLM-5.2 stay locked for guests, which the model
/// list reports so the UI can say so instead of failing on send).
///
/// Selectors and behaviour below came from a live probe of the page, not from guessing:
///   composer   textarea (single, placeholder "Send a Message")
///   send       #send-message-button (absent while a turn runs, which is the done signal)
///   answer     .chat-assistant .markdown-prose, with .thinking-chain-container inside it
///   models     button[aria-label="Select a model"], then button[aria-label="model-item"]
///   new chat   #new-chat-button
///   chat id    /c/&lt;uuid&gt; in the address
/// </summary>
public sealed class ZaiEngine : IDisposable
{
    public static ZaiEngine Instance { get; } = new();

    private const string HomeUrl = "https://chat.z.ai/";
    private const string LogPathName = "agentshub-zai.log";

    /// <summary>
    /// Reads the reasoning out of the page's own completion stream.
    ///
    /// z.ai renders its thought block collapsed and does not put the reasoning body in the DOM until
    /// it is expanded, so reading the answer node can never see it. The stream that fills that block
    /// does reach the page, so the fetch call is wrapped once and the reasoning deltas are collected
    /// into <c>window.__zaiHub.reasoning</c>. The original response is untouched: the page gets the
    /// response object it expects, and a clone is read alongside it.
    /// </summary>
    private const string ReasoningHookJs = """
        (() => {
          if (window.__zaiHubHooked) return;
          window.__zaiHubHooked = true;

          const state = { reasoning: '', answer: '', lastChunk: '' };
          state.reset = () => { state.reasoning = ''; state.answer = ''; state.lastChunk = ''; };
          window.__zaiHub = state;

          const append = value => {
            if (!value || typeof value !== 'string') return;
            if (state.lastChunk && value.startsWith(state.lastChunk)) {
              state.reasoning = state.reasoning.slice(0, state.reasoning.length - state.lastChunk.length) + value;
            } else {
              state.reasoning += value;
            }
            state.lastChunk = value;
          };

          const walk = (node, depth) => {
            if (!node || depth > 6) return;
            if (Array.isArray(node)) { node.forEach(entry => walk(entry, depth + 1)); return; }
            if (typeof node !== 'object') return;

            // Measured on the live stream: reasoning arrives as delta_content with phase "thinking".
            // The key says nothing on its own, so the phase is what separates thought from answer.
            if (typeof node.delta_content === 'string' && /think|reason/i.test(String(node.phase || ''))) {
              append(node.delta_content);
            }

            for (const key of Object.keys(node)) {
              const value = node[key];
              if (typeof value === 'string') {
                if (/reason|thought/i.test(key) || /think(ing)?_(content|text)/i.test(key)) append(value);
              } else {
                walk(value, depth + 1);
              }
            }
          };

          const consume = text => {
            for (const line of text.split('\n')) {
              const trimmed = line.trim();
              if (!trimmed.startsWith('data:')) continue;
              const payload = trimmed.slice(5).trim();
              if (!payload || payload === '[DONE]') continue;
              try { walk(JSON.parse(payload), 0); } catch (e) { /* a partial chunk is not an error */ }
            }
          };

          const originalFetch = window.fetch;
          if (typeof originalFetch !== 'function') return;

          window.fetch = function (input, init) {
            const url = typeof input === 'string' ? input : (input && input.url) || '';
            const promise = originalFetch.apply(this, arguments);
            if (!/chat\/completions/.test(url)) return promise;

            return promise.then(response => {
              try {
                const reader = response.clone().body.getReader();
                const decoder = new TextDecoder();
                let buffer = '';

                const pump = () => reader.read().then(({ done, value }) => {
                  if (done) return;
                  buffer += decoder.decode(value, { stream: true });
                  const cut = buffer.lastIndexOf('\n');
                  if (cut >= 0) {
                    consume(buffer.slice(0, cut));
                    buffer = buffer.slice(cut + 1);
                  }
                  return pump();
                }).catch(() => {});

                pump();
              } catch (e) { /* a page without a readable body still works */ }

              return response;
            });
          };
        })()
        """;

    /// <summary>One page means one turn. Two sessions would otherwise type into the same composer.</summary>
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    /// <summary>Rounds of tool use allowed in one turn, matching the other web engines.</summary>
    private const int MaxToolRounds = 8;

    /// <summary>Ceiling for the whole turn, so a wedged page becomes an error and not a spinner.</summary>
    private static readonly TimeSpan TurnBudget = TimeSpan.FromMinutes(3);

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), LogPathName);

    private Form? _host;
    private WebView2? _web;
    private CancellationTokenSource? _turnCts;
    private bool _navigationStarted;
    private bool _shuttingDown;

    /// <summary>Chats that already carry the tool guide, so it is not re-sent every turn.</summary>
    private readonly HashSet<string> _primedChats = new(StringComparer.Ordinal);

    private string? _lastError;

    public bool IsInitialized => _host != null;
    public bool IsBusy => _turnLock.CurrentCount == 0;
    public string? LastError => _lastError;

    /// <summary>The model a new turn asks the page for. Null leaves the page's own selection alone.</summary>
    public string? Model { get; private set; }

    /// <summary>
    /// MCP tools for the turn's guide. Set by the bridge before a turn, because the registry has to
    /// be told which profile's home to read and that is bridge knowledge, not page knowledge.
    /// </summary>
    internal IReadOnlyList<McpToolInfo> McpTools { get; set; } = Array.Empty<McpToolInfo>();

    /// <summary>
    /// Asked before a destructive action runs: (callId, action, preview) -> approved. Null refuses,
    /// because running a delete with nobody watching is worse than refusing it.
    /// </summary>
    public Func<string, string, string, Task<bool>>? ApprovalRequested { get; set; }

    public void Dispose() => Shutdown();

    // ------------------------------------------------------------------ lifecycle

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (IOException)
        {
            // Diagnostics must never break the feature.
        }
    }

    private void ParkOffScreen()
    {
        if (_host is not { } host) return;

        host.TopMost = false;
        host.FormBorderStyle = FormBorderStyle.None;
        host.Location = new Point(-32000, -32000);
    }

    private void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        try { _host?.Close(); } catch (Exception) { }
        _host = null;
        _web = null;
        Log("shutdown");
    }

    private Task UiAsync(Func<Task> work) => UiAsync(async () =>
    {
        await work().ConfigureAwait(true);
        return true;
    });

    /// <summary>
    /// Marshals onto the WebView2 thread.
    ///
    /// The window has to be created by the app's own UI thread and reached through BeginInvoke: an
    /// STA thread without a message loop shows the form, never initialises WebView2, and every turn
    /// then waits for a page that cannot answer.
    /// </summary>
    private Task<T> UiAsync<T>(Func<Task<T>> work)
    {
        var host = _host ?? throw new InvalidOperationException("Z.ai engine was not initialized on the UI thread.");
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.BeginInvoke(new Action(async () =>
        {
            try
            {
                tcs.TrySetResult(await work().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));

        return tcs.Task;
    }

    /// <summary>Creates the window once, on the UI thread. Called from MainForm.</summary>
    public void Initialize(Form owner)
    {
        if (_host != null) return;
        Log("initialize");

        _host = new Platform.ToolWindowHost
        {
            Text = "Agents Hub Z.ai",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            // Off the desktop, not hidden and not zero-size: Chromium throttles an unshown surface and
            // the turn then produces nothing.
            Location = new Point(-32000, -32000),
            Size = new Size(1280, 900),
        };

        _web = new WebView2 { Dock = DockStyle.Fill };
        _host.Controls.Add(_web);

        _host.FormClosing += (_, e) =>
        {
            if (_shuttingDown) return;
            e.Cancel = true;
            ParkOffScreen();
        };

        _host.Show();
        owner.FormClosed += (_, _) => Shutdown();
    }

    private async Task EnsureWebViewAsync()
    {
        if (_host == null) throw new InvalidOperationException("Z.ai engine was not initialized.");
        if (_web == null) throw new InvalidOperationException("The Z.ai window was disposed.");

        if (_web.CoreWebView2 == null)
        {
            string profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub", "zai-profile");
            Directory.CreateDirectory(profileDir);

            // An off-desktop window counts as occluded, and Chromium then throttles timers, which
            // stalls this app mid-hydration.
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows");

            var env = await CoreWebView2Environment.CreateAsync(null, profileDir, options).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            var core = _web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 did not initialise.");
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Registered before the first navigation so the page's very first completion is observed.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ReasoningHookJs).ConfigureAwait(true);
        }

        if (!_navigationStarted)
        {
            _navigationStarted = true;
            _web.CoreWebView2.Navigate(HomeUrl);
        }
    }

    /// <summary>Waits for the composer, then for the page's own scripts to settle.</summary>
    private async Task WaitForReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await EvalBoolAsync("!!document.querySelector('textarea')").ConfigureAwait(true))
                {
                    // The composer renders before the model list and the send button do; a short
                    // settle avoids typing into a box whose state is not wired yet.
                    await Task.Delay(600).ConfigureAwait(true);
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Mid-navigation.
            }

            await Task.Delay(500).ConfigureAwait(true);
        }

        string detail;
        try
        {
            detail = await EvalAsync("""
                (() => {
                  const body = (document.body.innerText || '').replace(/\s+/g, ' ').slice(0, 300);
                  return `title="${document.title}" url=${location.href} textareas=${document.querySelectorAll('textarea').length} body="${body}"`;
                })()
                """).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            detail = $"could not read the page: {ex.GetType().Name}: {ex.Message}";
        }

        Log($"composer never appeared: {detail}");
        throw new TimeoutException($"chat.z.ai did not finish loading its composer. {detail}");
    }

    // ------------------------------------------------------------------ public surface

    /// <summary>
    /// Guest or signed in, read from the token the page stores. The site mints a guest token for
    /// every visitor, so "no token" means the page has not booted rather than "signed out".
    /// </summary>
    public Task<ZaiStatus> ProbeAsync() => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);

        // A cold profile answers with no token at all until the page has booted, which read as
        // "unknown" on the very first turn of a fresh install.
        try
        {
            await WaitForReadyAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            return ZaiStatus.Unknown;
        }

        try
        {
            string email = await EvalAsync("""
                (() => {
                  try {
                    const raw = localStorage.getItem('token');
                    if (!raw) return '';
                    const payload = JSON.parse(atob(raw.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
                    return String(payload.email || '');
                  } catch (e) { return ''; }
                })()
                """).ConfigureAwait(true);

            if (email.Length == 0) return ZaiStatus.Unknown;
            return email.Contains("@guest.com", StringComparison.OrdinalIgnoreCase) ? ZaiStatus.Guest : ZaiStatus.SignedIn;
        }
        catch (InvalidOperationException)
        {
            return ZaiStatus.Unknown;
        }
    });

    /// <summary>
    /// Shows the page so the user can sign in. Signing in is a recommendation, not a gate: the guest
    /// path already answers, it just cannot reach GLM-5.3 or GLM-5.2.
    /// </summary>
    public Task ShowLoginWindowAsync() => ShowWindowAsync();

    public Task ShowWindowAsync() => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);

        if (_host is not { } host) return false;

        host.FormBorderStyle = FormBorderStyle.Sizable;
        host.StartPosition = FormStartPosition.CenterScreen;
        host.Location = new Point(80, 60);
        host.Size = new Size(1180, 820);
        host.TopMost = true;
        host.Show();
        host.Activate();
        Log("window shown for sign-in");
        return true;
    });

    public Task ParkAsync() => UiAsync(() =>
    {
        ParkOffScreen();
        return Task.CompletedTask;
    });

    /// <summary>The models the page offers, and which of them this session may pick.</summary>
    public Task<List<ZaiModel>> ListModelsAsync() => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);
        await WaitForReadyAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(true);

        // The menu is not in the DOM until it is opened, so this reads it the way a person would:
        // open, read, close.
        string raw = await EvalAsync("""
            (async () => {
              const open = document.querySelector('button[aria-label="Select a model"]');
              if (!open) return '[]';
              open.click();
              await new Promise(r => setTimeout(r, 350));
              const items = [...document.querySelectorAll('button[aria-label="model-item"]')]
                .map(b => ({ name: (b.innerText || '').trim(), locked: b.disabled === true }));
              document.body.click();
              return JSON.stringify(items);
            })()
            """).ConfigureAwait(true);

        var models = new List<ZaiModel>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                bool locked = item.TryGetProperty("locked", out var l) && l.ValueKind == JsonValueKind.True;
                if (name.Length == 0) continue;

                models.Add(new ZaiModel(IdFor(name), name, locked));
            }
        }
        catch (JsonException ex)
        {
            Log($"model list unreadable: {ex.Message}");
        }

        if (models.Count == 0)
        {
            // The probe also saw these three behind the menu; a closed menu is not a reason to show
            // an empty picker.
            models.AddRange(new[]
            {
                new ZaiModel("x-preview-l", "GLM-5.3-Flash", false),
                new ZaiModel("glm-5.3", "GLM-5.3", true),
                new ZaiModel("glm-5.2", "GLM-5.2", true),
            });
        }

        return models;
    });

    /// <summary>Model ids as the page's own selector exposes them.</summary>
    private static string IdFor(string name) => name switch
    {
        "GLM-5.3-Flash" => "x-preview-l",
        "GLM-5.3" => "glm-5.3",
        "GLM-5.2" => "glm-5.2",
        _ => name,
    };

    /// <summary>
    /// Picks a model in the page's own menu. Returns false when the item is locked or absent, so the
    /// caller can say "sign in for this one" instead of sending on a model the user did not choose.
    /// </summary>
    public Task<bool> SetModelAsync(string name) => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);

        string result = await EvalAsync($$"""
            (async () => {
              const open = document.querySelector('button[aria-label="Select a model"]');
              if (!open) return 'no-selector';
              open.click();
              await new Promise(r => setTimeout(r, 350));

              const item = [...document.querySelectorAll('button[aria-label="model-item"]')]
                .find(b => (b.innerText || '').trim() === {{JsonSerializer.Serialize(name)}});

              if (!item) { document.body.click(); return 'not-found'; }
              if (item.disabled) { document.body.click(); return 'locked'; }

              item.click();
              await new Promise(r => setTimeout(r, 400));

              const current = document.querySelector('button[aria-label="Select a model"]');
              const now = current ? (current.innerText || '').trim() : '';
              document.body.click();
              return now === {{JsonSerializer.Serialize(name)}} ? 'selected' : 'ignored:' + now;
            })()
            """).ConfigureAwait(true);

        Log($"set model {name} -> {result}");

        if (result == "selected")
        {
            Model = name;
            return true;
        }

        return false;
    });

    /// <summary>Stops the turn in flight. Safe to call from any thread.</summary>
    public void Stop()
    {
        try
        {
            _turnCts?.Cancel();
            Log("stop requested");
        }
        catch (ObjectDisposedException)
        {
            // The turn finished between the check and the cancel.
        }
    }

    /// <summary>
    /// One turn, with files, skills and MCP tools when a working directory is available.
    ///
    /// The page has no function calling, so the protocol is text: the conversation is primed once
    /// with the guide, every reply is scanned for `tool` blocks, those are executed inside the
    /// working directory, and the results go back as the next message. A reply with no block ends
    /// the turn. This is deliberately the same protocol and the same executor the DeepSeek engine
    /// uses, so a skill or an MCP server works identically on both.
    /// </summary>
    public async Task<ZaiReply> SendAsync(
        string prompt,
        string? chatId,
        string? root,
        IProgress<ZaiDelta>? progress = null,
        IProgress<ZaiToolStep>? tools = null,
        Action<string>? onChatId = null,
        bool toolsAllowed = true,
        CancellationToken ct = default)
    {
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            turn.CancelAfter(TurnBudget);
            _turnCts = turn;

            try
            {
                return await SendLockedAsync(prompt, chatId, root, progress, tools, onChatId, toolsAllowed, turn.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _turnCts = null;
            }
        }
        finally
        {
            _turnLock.Release();
        }
    }

    private async Task<ZaiReply> SendLockedAsync(
        string prompt,
        string? chatId,
        string? root,
        IProgress<ZaiDelta>? progress,
        IProgress<ZaiToolStep>? tools,
        Action<string>? onChatId,
        bool toolsAllowed,
        CancellationToken ct)
    {
        string? chat = chatId;

        bool toolsEnabled = toolsAllowed && !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        Log(toolsEnabled ? $"tools root={root}" : "tools disabled: no working directory");

        // A fresh conversation has no guide, and the guide has to exist before any request that
        // needs it. Keyed on the chat id so a restarted app does not re-prime a conversation.
        bool needsPrime = toolsEnabled && (chat is null || !_primedChats.Contains(chat));

        if (needsPrime)
        {
            // QwenTools.Guide is the shared guide plus the rule about not pretending a client tool
            // does not exist, which every rendered-page engine needs.
            var primed = await SendOnceAsync(
                QwenTools.Guide(root!, SkillCatalog.Discover(root!), McpTools), chat, null, null, ct).ConfigureAwait(false);

            if (primed.ChatId.Length > 0) chat = primed.ChatId;
            if (chat is not null) _primedChats.Add(chat);
            Log($"primed chat={chat} skills={SkillCatalog.Discover(root!).Count} mcpTools={McpTools.Count}");
        }

        string message = prompt;

        for (int round = 0; round <= MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var reply = await SendOnceAsync(message, chat, progress, onChatId, ct).ConfigureAwait(false);
            if (reply.ChatId.Length > 0) chat = reply.ChatId;

            if (!toolsEnabled) return reply;

            // The page renders markdown, so its answer has no ``` fences: the bare-object parser is
            // the one that finds a tool block here.
            var calls = QwenTools.Extract(reply.Answer);
            if (calls.Count == 0)
            {
                string prose = QwenTools.Strip(reply.Answer);
                return reply with { Answer = prose.Length > 0 ? prose : "(Z.ai returned no text for this turn.)" };
            }

            var results = new List<string>();

            foreach (var call in calls)
            {
                string action = call.TryGetProperty("action", out var act) && act.ValueKind == JsonValueKind.String
                    ? act.GetString() ?? "?"
                    : "?";
                string path = call.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "."
                    : ".";

                string callId = "zai_" + Guid.NewGuid().ToString("N")[..8];

                if (DeepSeekTools.RequiresApproval(action))
                {
                    bool allowed = ApprovalRequested is null
                        ? false
                        : await ApprovalRequested(callId, action, DeepSeekTools.Preview(action, call)).ConfigureAwait(false);

                    if (!allowed)
                    {
                        Log($"declined {action} {path}");
                        tools?.Report(new ZaiToolStep(callId, action, path, true, true, null, null, 0, DeepSeekTools.Preview(action, call)));
                        tools?.Report(new ZaiToolStep(callId, action, path, false, false, null, "declined by the user", 0));
                        results.Add($"[{results.Count + 1}] {action}({path}) was declined by the user. Do not retry it; explain what you need instead.");
                        continue;
                    }
                }

                tools?.Report(new ZaiToolStep(callId, action, path, true, true, null, null, 0, DeepSeekTools.Preview(action, call)));

                long started = Environment.TickCount64;
                try
                {
                    var (summary, body) = DeepSeekTools.Execute(action, call, root!);
                    tools?.Report(new ZaiToolStep(callId, action, path, false, true, summary, null, (int)(Environment.TickCount64 - started)));
                    results.Add($"[{results.Count + 1}] {summary}\n{body}");
                }
                catch (Exception ex)
                {
                    tools?.Report(new ZaiToolStep(callId, action, path, false, false, null, ex.Message, (int)(Environment.TickCount64 - started)));
                    results.Add($"[{results.Count + 1}] {action}({path}) failed: {ex.Message}");
                }
            }

            string joined = string.Join("\n\n", results);
            if (joined.Length > DeepSeekTools.MaxResultChars)
            {
                joined = joined[..DeepSeekTools.MaxResultChars] + "\n\n… more results omitted (size cap) …";
            }

            Log($"tool round {round + 1}: {calls.Count} call(s)");
            message = "Tool results:\n\n" + joined + "\n\nContinue. Use another `tool` block only if you still need something.";
        }

        throw new InvalidOperationException($"Stopped after {MaxToolRounds} tool rounds. Ask again if you need more.");
    }

    /// <summary>
    /// Sends one message and reads one answer, reporting the chat id the page moved to as soon as it
    /// exists rather than at the end of a long tool loop.
    /// </summary>
    private async Task<ZaiReply> SendOnceAsync(
        string prompt,
        string? chatId,
        IProgress<ZaiDelta>? progress,
        Action<string>? onChatId,
        CancellationToken ct)
    {
        return await UiAsync(async () =>
        {
            await EnsureWebViewAsync().ConfigureAwait(true);
            await OpenChatAsync(chatId).ConfigureAwait(true);
            await WaitForReadyAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(true);

            // The reasoning buffer belongs to one turn; a previous answer's thoughts must not be
            // streamed again as this one's.
            await EvalAsync("(() => { if (window.__zaiHub && window.__zaiHub.reset) { window.__zaiHub.reset(); return 'reset'; } return 'no-hook'; })()")
                .ConfigureAwait(true);

            // Everything already on screen belongs to earlier turns, including the primer's
            // acknowledgement. Counting first is what stops that answer being returned as this one.
            int baseline = 0;
            try
            {
                if (int.TryParse(await EvalAsync("document.querySelectorAll('.chat-assistant').length").ConfigureAwait(true), out int counted))
                {
                    baseline = counted;
                }
            }
            catch (InvalidOperationException)
            {
                // A mid-render page just means no baseline; the read falls back to the last node.
            }

            string focused = await EvalAsync("""
                (() => {
                  const box = document.querySelector('textarea');
                  if (!box) return 'no-composer';
                  box.click();
                  box.focus();
                  return 'focused';
                })()
                """).ConfigureAwait(true);

            if (focused == "no-composer")
                throw new InvalidOperationException("Could not type into the Z.ai composer: no textarea on the page.");

            // The composer is a Svelte-controlled textarea, so the value is set through the native
            // setter plus an input event; a plain `.value =` leaves the page's own state empty and
            // the send button never enables.
            string filled = await EvalAsync($$"""
                (() => {
                  const box = document.querySelector('textarea');
                  if (!box) return 'no-composer';
                  const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
                  setter.call(box, {{JsonSerializer.Serialize(prompt)}});
                  box.dispatchEvent(new Event('input', { bubbles: true }));
                  return (box.value || '').length > 0 ? 'filled' : 'empty';
                })()
                """).ConfigureAwait(true);

            await Task.Delay(300, ct).ConfigureAwait(true);

            // Enter is the measured path: the send button is disabled until the page's own state sees
            // the text, which a synthetic click on a fresh render can beat.
            await PressEnterAsync().ConfigureAwait(true);

            var sentBy = DateTime.UtcNow.AddSeconds(12);
            string sent = "still-filled";
            while (DateTime.UtcNow < sentBy)
            {
                await Task.Delay(400, ct).ConfigureAwait(true);

                sent = await EvalAsync("""
                    (() => {
                      const box = document.querySelector('textarea');
                      return box && (box.value || '').trim().length === 0 ? 'cleared' : 'still-filled';
                    })()
                    """).ConfigureAwait(true);

                if (sent == "cleared") break;

                // The button is the fallback when Enter was swallowed, which happens while the page
                // is still wiring the composer after a navigation.
                string clicked = await EvalAsync("""
                    (() => {
                      const btn = document.querySelector('#send-message-button');
                      if (!btn || btn.disabled) return 'no-button';
                      btn.click();
                      return 'clicked';
                    })()
                    """).ConfigureAwait(true);

                Log($"send retry: {clicked}");
            }

            Log($"composer {filled}, send -> {sent}");
            if (sent != "cleared")
                throw new InvalidOperationException("Z.ai did not accept the message: the composer still holds it.");

            string pageChat = await CurrentChatIdAsync().ConfigureAwait(true);
            if (pageChat.Length > 0 && !string.Equals(pageChat, chatId, StringComparison.Ordinal))
            {
                onChatId?.Invoke(pageChat);
            }

            var answer = await ReadAnswerAsync(progress, baseline, ct).ConfigureAwait(true);
            string finalChat = await CurrentChatIdAsync().ConfigureAwait(true);
            return new ZaiReply(finalChat.Length > 0 ? finalChat : pageChat, answer.Text, answer.Reasoning);
        }).ConfigureAwait(false);
    }

    /// <summary>Puts the page on a specific conversation, or on a fresh one when there is none.</summary>
    private async Task OpenChatAsync(string? chatId)
    {
        string url = _web?.CoreWebView2?.Source ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(chatId))
        {
            if (url.Contains($"/c/{chatId}", StringComparison.OrdinalIgnoreCase)) return;

            Log($"navigating to chat {chatId}");
            _web?.CoreWebView2?.Navigate($"{HomeUrl}c/{chatId}");
            await WaitForReadyAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(true);
            return;
        }

        // No conversation yet: start one. In a chat, the page's own button keeps the SPA warm, which
        // is faster and does not race a reload.
        if (url.Contains("/c/", StringComparison.OrdinalIgnoreCase))
        {
            await EvalAsync("""
                (() => {
                  const btn = document.querySelector('#new-chat-button');
                  if (!btn) return 'no-button';
                  btn.click();
                  return 'clicked';
                })()
                """).ConfigureAwait(true);
            await Task.Delay(900).ConfigureAwait(true);
        }
    }

    private async Task<string> CurrentChatIdAsync()
    {
        try
        {
            string path = await EvalAsync("location.pathname").ConfigureAwait(true);
            int at = path.IndexOf("/c/", StringComparison.Ordinal);
            return at < 0 ? string.Empty : path[(at + 3)..].Split('/')[0];
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The reasoning block on this page is collapsed: its text is the header "Thought Process" and
    /// the reasoning body is not rendered until a person expands it. Reporting the header as the
    /// thought is what made the transcript say "Thought Process" and nothing else, so the header is
    /// dropped and an empty result means "no readable reasoning", which the caller handles by simply
    /// leaving the thought node open until the turn ends.
    /// </summary>
    private static string StripThinkingHeader(string thinking)
    {
        string trimmed = thinking.Trim();

        foreach (string header in new[] { "Thought Process", "Thinking...", "Thinking…", "Thinking" })
        {
            if (trimmed.Equals(header, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[header.Length..].TrimStart();
        }

        return trimmed;
    }

    /// <summary>"Thinking..." and its variants are the page's skeleton, not an answer.</summary>
    private static bool IsPlaceholder(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.Equals("Thinking...", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Thinking…", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Thinking", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the answer from the last assistant node.
    ///
    /// The reasoning block is nested inside the answer's own markdown container, so its text is
    /// hidden before the answer is read and restored after; otherwise every reply would begin with
    /// "Thought Process".
    /// </summary>
    private async Task<(string Text, string Reasoning)> ReadAnswerAsync(
        IProgress<ZaiDelta>? progress,
        int baseline,
        CancellationToken ct)
    {
        string extract = $$"""
            (() => {
              const nodes = [...document.querySelectorAll('.chat-assistant')].slice({{baseline}});
              if (nodes.length === 0) return { text: '', thinking: '' };

              const last = nodes[nodes.length - 1];
              const body = last.querySelector('.markdown-prose') || last;
              const thought = body.querySelector('.thinking-chain-container');
              const previous = thought ? thought.style.display : null;

              if (thought) thought.style.display = 'none';
              const text = (body.innerText || '').trim();
              if (thought) thought.style.display = previous || '';

              const thinking = thought ? (thought.innerText || '').replace(/^Thought Process\s*/i, '').trim() : '';
              return { text: text, thinking: thinking };
            })()
            """;

        string emitted = string.Empty;
        string reported = string.Empty;
        string reasoningReported = string.Empty;
        string lastText = string.Empty;
        int stableTicks = 0;
        var deadline = DateTime.UtcNow + TurnBudget;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            JsonElement snapshot;
            try
            {
                snapshot = await EvalAsyncRaw(extract).ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(400, ct).ConfigureAwait(true);
                continue;
            }

            string text = snapshot.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            string thinking = snapshot.TryGetProperty("thinking", out var th) ? th.GetString() ?? string.Empty : string.Empty;

            // The hook sees the reasoning as it streams; the DOM only has the collapsed header.
            bool fromHook = false;
            try
            {
                string hooked = await EvalAsync("(window.__zaiHub && window.__zaiHub.reasoning) || ''").ConfigureAwait(true);
                if (hooked.Length > 0)
                {
                    thinking = hooked;
                    fromHook = true;
                }
            }
            catch (InvalidOperationException)
            {
                // Mid-navigation; the header path still applies.
            }

            // While it reasons, the answer node holds the word "Thinking..." and nothing else. Read
            // as an answer, that placeholder locked the transcript on "Thinking..." and every later
            // chunk failed the prefix test, so the real reply only appeared at the end.
            if (IsPlaceholder(text)) text = string.Empty;

            // Only the DOM's header needs dropping; streamed reasoning is real text and a sentence
            // that happens to open with "Thinking" keeps its first word.
            if (!fromHook) thinking = StripThinkingHeader(thinking);

            // A guest who ran out of messages gets a wall, not a slow answer. Saying so beats a
            // three-minute wait that ends in "no answer".
            if (await EvalBoolAsync("/sign in to (continue|keep chatting)|guest.*limit|quota/i.test(document.body.innerText || '')").ConfigureAwait(true))
            {
                Log("guest limit reached");
                throw new InvalidOperationException(
                    "Z.ai's guest allowance for this chat is used up. Open the Z.ai menu and sign in to keep going.");
            }

            if (thinking.Length > reasoningReported.Length && thinking.StartsWith(reasoningReported, StringComparison.Ordinal))
            {
                progress?.Report(new ZaiDelta(thinking[reasoningReported.Length..], null));
                reasoningReported = thinking;
            }

            if (text.Length > emitted.Length && text.StartsWith(emitted, StringComparison.Ordinal))
            {
                emitted = text;

                // What a person should see, and only the new part. A half-written tool object is held
                // back here: the transcript appends, so anything streamed once cannot be taken back.
                string visible = QwenTools.Visible(emitted);

                if (visible.Length > reported.Length && visible.StartsWith(reported, StringComparison.Ordinal))
                {
                    progress?.Report(new ZaiDelta(null, visible[reported.Length..]));
                    reported = visible;
                }
                else if (visible.Length > reported.Length)
                {
                    reported = visible;
                    progress?.Report(new ZaiDelta(null, visible));
                }
            }
            else if (text.Length > 0 && text != emitted)
            {
                // A reply that rewrites its own opening (a rendered component, a table) cannot be
                // streamed as a suffix. Report only the part beyond what was already shown, so the
                // transcript never repeats a paragraph it has printed once.
                string visible = QwenTools.Visible(text);
                int shared = 0;
                while (shared < visible.Length && shared < reported.Length && visible[shared] == reported[shared]) shared++;

                emitted = text;
                if (visible.Length > shared)
                {
                    reported = visible;
                    progress?.Report(new ZaiDelta(null, visible[shared..]));
                }
            }

            // Done when the send button is back AND the text stopped growing: a fast model can pause
            // mid-stream, and the button alone flickers during a retry.
            bool idle = await EvalBoolAsync("!!document.querySelector('#send-message-button')").ConfigureAwait(true);
            stableTicks = text == lastText && text.Length > 0 ? stableTicks + 1 : 0;
            lastText = text;

            if (idle && stableTicks >= 2) break;

            await Task.Delay(450, ct).ConfigureAwait(true);
        }

        Log($"answer {emitted.Length} chars, thinking {reasoningReported.Length} chars");
        return (emitted, reasoningReported);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>Enter as keyDown then keyUp, which is what the composer actually listens for.</summary>
    private async Task PressEnterAsync()
    {
        var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("The Z.ai page is not ready");

        foreach (var type in new[] { "keyDown", "keyUp" })
        {
            await core.CallDevToolsProtocolMethodAsync(
                "Input.dispatchKeyEvent",
                JsonSerializer.Serialize(new
                {
                    type,
                    windowsVirtualKeyCode = 13,
                    nativeVirtualKeyCode = 13,
                    key = "Enter",
                    code = "Enter",
                    text = type == "keyDown" ? "\r" : string.Empty,
                })).ConfigureAwait(true);
        }
    }

    private async Task<bool> EvalBoolAsync(string expression)
    {
        var value = await EvalAsyncRaw(expression).ConfigureAwait(true);
        return value.ValueKind == JsonValueKind.True;
    }

    private async Task<string> EvalAsync(string expression)
    {
        var value = await EvalAsyncRaw(expression).ConfigureAwait(true);

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner))
            return inner.ToString();

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private async Task<JsonElement> EvalAsyncRaw(string expression)
    {
        var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("The Z.ai page is not ready");

        string? json = null;
        for (int attempt = 0; json is null; attempt++)
        {
            try
            {
                json = await core.CallDevToolsProtocolMethodAsync(
                    "Runtime.evaluate",
                    JsonSerializer.Serialize(new
                    {
                        expression,
                        returnByValue = true,
                        awaitPromise = true,
                        userGesture = true,
                    })).ConfigureAwait(true);
            }
            catch (COMException ex) when (attempt < 3)
            {
                Log($"cdp retry {attempt + 1} after {ex.Message}");
                await Task.Delay(200 * (attempt + 1)).ConfigureAwait(true);
            }
        }

        using var doc = JsonDocument.Parse(json!);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return JsonDocument.Parse("null").RootElement.Clone();

        if (result.TryGetProperty("value", out var value)) return value.Clone();
        if (result.TryGetProperty("description", out var description))
            return JsonDocument.Parse(JsonSerializer.Serialize(description.ToString())).RootElement.Clone();

        return JsonDocument.Parse("null").RootElement.Clone();
    }
}
