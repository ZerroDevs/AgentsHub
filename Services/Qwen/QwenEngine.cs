using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentsHub.Services.Qwen;

/// <summary>What the page is, as far as we can tell.</summary>
public enum QwenStatus
{
    Unknown,
    Anonymous,
    SignedIn,
}

/// <summary>One assistant answer, and the selector that produced it.</summary>
public sealed record QwenReply(string Text, string Selector, string Thinking = "");

/// <summary>Read-only state for the health panel. Nothing here starts a page load.</summary>
public sealed record QwenHealth(
    bool Initialized,
    bool PageLoaded,
    string? PrimedIn,
    DateTime? LastTurnAt,
    string? LastError);

/// <summary>
/// Drives chat.qwen.ai in a hidden WebView2, the way <c>DeepSeekEngine</c> drives its own page.
///
/// Differences on purpose: no accounts. Guest use is allowed, so a missing sign-in is a nudge rather
/// than a gate. The tool loop, the guide and the parser are this engine's own (QwenTools) because a
/// rendered page gives no code fences, while execution is shared with the DeepSeek layer.
///
/// Selectors below came from a Phase 0 probe of the live page, not from guessing:
///   composer  textarea.message-input-textarea
///   send      button[aria-label~="send"]  (appears once the box has text)
///   model     button[aria-label="Select Model"]
///   mode      button[aria-label="Select Mode"]  (Auto / Thinking / Fast)
/// The answer container was NOT observed, because a first guest message raises an age gate. The
/// extractor therefore tries several selectors and reports which one matched, instead of assuming.
/// </summary>
public sealed class QwenEngine : IDisposable
{
    public static QwenEngine Instance { get; } = new();

    private const string HomeUrl = "https://chat.qwen.ai/";
    private const string LoginUrl = "https://chat.qwen.ai/auth";

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "agentshub-qwen.log");

    /// <summary>One page means one turn. Two sessions would otherwise type into the same composer.</summary>
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private Form? _host;
    private WebView2? _web;
    private bool _navigationStarted;
    private bool _shuttingDown;
    private string? _primedRoot;

    /// <summary>Characters of tool output one turn may add to the context, in total.</summary>
    private const int ResultBudget = 12000;

    private DateTime? _lastTurnAt;
    private string? _lastError;

    /// <summary>
    /// What the health panel shows. Deliberately passive: it reads state the engine already has and
    /// never navigates the page, because asking the page a question is exactly what can hang.
    /// </summary>
    public QwenHealth Health() => new(
        Initialized: _host != null,
        PageLoaded: _web?.CoreWebView2 != null,
        PrimedIn: _primedChat,
        LastTurnAt: _lastTurnAt,
        LastError: _lastError);

    /// <summary>Ceiling for a single tool result, regardless of the turn budget.</summary>
    private const int PerResultLimit = 4000;

    /// <summary>
    /// Appended to every prompt. The guide is the oldest message in the chat, so a provider that
    /// compacts a long conversation can summarise it away; this keeps the format where it is read last.
    /// </summary>
    private const string ProtocolReminder =
        "\n\n[If this needs a file or command action, reply with a tool block.]";

    /// <summary>The page path the primer was sent in. A new chat has a new path, and a new chat has
    /// no primer.</summary>
    private string? _primedChat;

    /// <summary>
    /// Asks the user before a destructive action, exactly like the DeepSeek engine does.
    ///
    /// Set per turn by the bridge, which owns the pending-request table and the Approve card. Null
    /// means nobody is listening, so the action is refused rather than run unattended.
    /// </summary>
    public Func<string, string, string, Task<bool>>? ApprovalRequested { get; set; }

    /// <summary>
    /// Reports a tool step so the transcript can draw it, the way DeepSeek's nodes are drawn.
    /// (callId, action, path)
    /// </summary>
    public Action<string, string, string>? ToolStarted { get; set; }

    /// <summary>(callId, action, success, summary)</summary>
    public Action<string, string, bool, string>? ToolFinished { get; set; }

    public bool IsBusy => _turnLock.CurrentCount == 0;

    public QwenStatus Status { get; private set; } = QwenStatus.Unknown;

    public void Dispose() => Shutdown();

    /// <summary>Back to the invisible state: still shown, so Chromium keeps rendering, but away.</summary>
    private void ParkOffScreen()
    {
        if (_host is not { } host) return;

        host.TopMost = false;
        host.FormBorderStyle = FormBorderStyle.None;
        host.Location = new Point(-32000, -32000);
        Log("window parked");
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

    // ------------------------------------------------------------------ lifecycle

    private Task UiAsync(Func<Task> work) => UiAsync(async () =>
    {
        await work().ConfigureAwait(true);
        return true;
    });

    /// <summary>
    /// Marshals onto the WebView2 thread.
    ///
    /// The first version of this ran the work on a bare STA thread with no message loop. The form
    /// appeared, WebView2 never initialised, and every turn hung waiting for a token that could not
    /// arrive. The window has to be created by the app's own UI thread and reached through
    /// BeginInvoke, exactly like the DeepSeek engine.
    /// </summary>
    private Task<T> UiAsync<T>(Func<Task<T>> work)
    {
        var host = _host ?? throw new InvalidOperationException("Qwen engine was not initialized on the UI thread.");
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
            Text = "Agents Hub Qwen",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            // Off the desktop, not hidden and not zero-size: Chromium throttles an unshown surface
            // and the turn then produces nothing. This is the same trade the DeepSeek engine makes,
            // and it is safe with one window created once.
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

        // No warm-up. Loading a heavy single-page app at startup puts a second renderer to work at
        // the exact moment a DeepSeek chat opens, and DeepSeek streams from its own page. The Qwen
        // page is navigated on its first turn instead: a slower first message is a fair trade for
        // not disturbing the engine that was already working.
    }

    private async Task EnsureWebViewAsync()
    {
        if (_host == null) throw new InvalidOperationException("Qwen engine was not initialized.");
        if (_web == null) throw new InvalidOperationException("The Qwen window was disposed.");

        if (_web.CoreWebView2 == null)
        {
            string profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub", "qwen-profile");
            Directory.CreateDirectory(profileDir);

            // A window off the desktop counts as occluded, and Chromium then throttles timers, which
            // stalls a heavy single-page app mid-hydration. These flags keep it running at full speed.
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows");

            var env = await CoreWebView2Environment.CreateAsync(null, profileDir, options).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            // Re-read after the await: the earlier null check does not survive it.
            var core = _web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 did not initialise.");
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
        }

        if (!_navigationStarted)
        {
            _navigationStarted = true;
            _web.CoreWebView2.Navigate(HomeUrl);
        }
    }

    /// <summary>
    /// Returns the window to the chat page when it is sitting on the sign-in form.
    ///
    /// The sign-in flow is a navigation to /auth, and nothing navigated back. That left every later
    /// turn looking for a composer on a login page, which is exactly what the log showed.
    /// </summary>
    private async Task EnsureOnChatPageAsync()
    {
        string url = _web?.CoreWebView2?.Source ?? string.Empty;
        if (url.Length == 0) return;
        if (url.Contains("chat.qwen.ai", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("/auth", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Log($"navigating back to the chat page from {url}");
        _web?.CoreWebView2?.Navigate(HomeUrl);
        await WaitForReadyAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(true);
    }

    /// <summary>
    /// Waits for the composer, and re-navigates once if the page looks stuck.
    ///
    /// A cold profile can land on a page that never hydrates. One reload fixes that; a silent 45
    /// second wait does not, which is what the first attempt did.
    /// </summary>
    private async Task WaitForReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var started = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // The page renders one textarea, but its class varies between builds and between
                // signed-in and guest states. Asking for the class failed while `textareas=1` was
                // true on the very same page, which is what the log showed.
                // JsonElement.ToString() renders a boolean as "True", so comparing against "true"
                // never matched and this loop could not succeed even on a fully loaded page. Read
                // the JSON kind instead of its text.
                bool ready = await EvalBoolAsync(
                    "!!(document.querySelector('textarea.message-input-textarea') || document.querySelector('textarea') || document.querySelector('[contenteditable=\"true\"]'))")
                    .ConfigureAwait(true);

                if (ready)
                {
                    // The elapsed time is worth keeping: a cold profile can take a long while, and
                    // that is the difference between "too slow" and "never".
                    Log($"composer ready after {(DateTime.UtcNow - started).TotalSeconds:F0}s");
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Mid-navigation.
            }

            // No reload here. The previous version reloaded at 12s and restarted hydration, which
            // made a slow page slower; the page in the screenshot was still on its skeleton.
            await Task.Delay(500).ConfigureAwait(true);
        }

        // Say what is on the page, not just that it failed. Without this the next report is a guess.
        string detail;
        try
        {
            detail = await EvalAsync("""
                (() => {
                  const body = (document.body.innerText || '').replace(/\s+/g, ' ').slice(0, 300);
                  const areas = document.querySelectorAll('textarea').length;
                  const editables = document.querySelectorAll('[contenteditable="true"]').length;
                  return `title="${document.title}" url=${location.href} textareas=${areas} editables=${editables} body="${body}"`;
                })()
                """).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            detail = $"could not read the page: {ex.GetType().Name}: {ex.Message}";
        }

        Log($"composer never appeared: {detail}");
        throw new TimeoutException($"Qwen did not finish loading its composer. {detail}");
    }

    // ------------------------------------------------------------------ public surface

    public async Task<QwenStatus> ProbeAsync()
    {
        try
        {
            return await UiAsync(async () =>
            {
                await EnsureWebViewAsync().ConfigureAwait(true);
                Status = await ReadStatusAsync().ConfigureAwait(true);
                Log($"probe -> {Status}");
                return Status;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"probe failed: {ex.GetType().Name}: {ex.Message}");
            return QwenStatus.Unknown;
        }
    }

    /// <summary>Shows the page so the user can sign in. Same window, since there is one profile.</summary>
    public Task ShowLoginWindowAsync() => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);

        // Navigating to /auth is useless if the window is still parked off the desktop. This is the
        // same mistake the Open button had: opacity and taskbar alone do not move it into view.
        BringOnScreen();

        _web?.CoreWebView2?.Navigate(LoginUrl);
        Log("login window shown at /auth");
        return true;
    });

    /// <summary>Moves the parked window onto the desktop and gives it a normal frame.</summary>
    private void BringOnScreen()
    {
        if (_host is not { } host) return;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);

        host.FormBorderStyle = FormBorderStyle.Sizable;
        host.Opacity = 1;
        host.Location = new Point(
            area.X + Math.Max(0, (area.Width - host.Width) / 2),
            area.Y + Math.Max(0, (area.Height - host.Height) / 2));

        if (!host.Visible) host.Show();
        host.TopMost = true;
        host.Activate();
    }

    public Task ShowWindowAsync() => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);

        // The window lives off the desktop so Chromium keeps rendering it; showing it means moving
        // it back on screen.
        BringOnScreen();

        return true;
    });

    /// <summary>
    /// The models the page offers, read from its own menu.
    ///
    /// The menu is opened, scraped and closed again, so the list is whatever Qwen currently serves
    /// rather than a hardcoded copy that would drift the moment they add a model.
    /// </summary>
    public Task<List<string>> ListModelsAsync() => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);

        var opened = await EvalAsync("""
            (() => {
              const trigger = document.querySelector('button[aria-label="Select Model"]');
              if (!trigger) return 'no-trigger';
              trigger.click();
              return 'opened';
            })()
            """).ConfigureAwait(true);

        if (opened != "opened") return new List<string>();

        await Task.Delay(700).ConfigureAwait(true);

        var raw = await EvalAsync("""
            (() => {
              const names = [...document.querySelectorAll('[role="option"], [role="menuitem"]')]
                .map(el => (el.textContent || '').trim())
                .filter(t => t.length > 0 && t.length < 60);
              return JSON.stringify([...new Set(names)]);
            })()
            """).ConfigureAwait(true);

        // Close it again: the menu is a view of the page, not something to leave open.
        await PressKeyAsync("Escape", 27).ConfigureAwait(true);

        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>();
            Log($"models -> {string.Join(", ", list)}");
            return list;
        }
        catch (JsonException)
        {
            Log($"models unreadable: {raw}");
            return new List<string>();
        }
    });

    /// <summary>Selects a model by name in the page's own menu.</summary>
    public Task<bool> SetModelAsync(string model) => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);

        var opened = await EvalAsync("""
            (() => {
              const trigger = document.querySelector('button[aria-label="Select Model"]');
              if (!trigger) return 'no-trigger';
              trigger.click();
              return 'opened';
            })()
            """).ConfigureAwait(true);

        if (opened != "opened") return false;

        await Task.Delay(700).ConfigureAwait(true);

        var picked = await EvalAsync($$"""
            (() => {
              const wanted = {{JsonSerializer.Serialize(model)}}.trim().toLowerCase();
              const options = [...document.querySelectorAll('[role="option"], [role="menuitem"]')];

              const exact = options.find(el => (el.textContent || '').trim().toLowerCase() === wanted);
              const partial = options.find(el => (el.textContent || '').trim().toLowerCase().includes(wanted));
              const target = exact || partial;

              if (!target) return 'not-found';

              target.click();
              return 'picked';
            })()
            """).ConfigureAwait(true);

        Log($"set model {model} -> {picked}");
        return picked == "picked";
    });

    /// <summary>Mode chip: Auto, Thinking, Fast.</summary>
    public Task<bool> SetModeAsync(string mode) => UiAsync(async () =>
    {
        await EnsureWebViewAsync().ConfigureAwait(true);

        var result = await EvalAsync($$"""
            (() => {
              const wanted = '{{mode}}'.toLowerCase();
              const trigger = document.querySelector('button[aria-label="Select Mode"]')
                || [...document.querySelectorAll('button, [role="button"]')]
                     .find(el => /^(auto|thinking|fast)$/i.test((el.textContent || '').trim()));
              if (!trigger) return 'no-trigger';
              trigger.click();
              return 'opened';
            })()
            """).ConfigureAwait(true);

        if (result != "opened") return false;

        await Task.Delay(400).ConfigureAwait(true);

        var picked = await EvalAsync($$"""
            (() => {
              const wanted = '{{mode}}'.toLowerCase();
              const item = [...document.querySelectorAll('[role="option"], li, button')]
                .find(el => (el.textContent || '').trim().toLowerCase() === wanted);
              if (!item) return 'not-found';
              item.click();
              return 'picked';
            })()
            """).ConfigureAwait(true);

        Log($"set mode {mode} -> {picked}");
        return picked == "picked";
    });

    /// <summary>One turn. No tools, no priming: prompt in, text out.</summary>
    /// <summary>
    /// One turn, with the same tool loop DeepSeek uses.
    ///
    /// The protocol, the parser and the executor all come from <see cref="QwenTools"/>:
    /// they act on the filesystem, not on that engine, so Qwen can reuse them unchanged. What is
    /// specific to Qwen is only how a message is typed and how the answer is read.
    /// </summary>
    public async Task<QwenReply> SendAsync(
        string prompt,
        string? root = null,
        bool toolsEnabled = false,
        bool skipPermissions = false,
        IProgress<string>? progress = null,
        IProgress<string>? thinking = null,
        string? conversationId = null,
        Action<string>? onChatId = null,
        IReadOnlyList<string>? attachmentFiles = null,
        CancellationToken ct = default)
    {
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        Log($"turn start, {prompt.Length} chars, tools={toolsEnabled}, files={attachmentFiles?.Count ?? 0}");

        _lastTurnAt = DateTime.UtcNow;

        try
        {
            // Resume the session's own conversation. Without this a reopened chat started from
            // nothing, because the page keeps whichever conversation ran last.
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                await EnsureOnChatAsync(conversationId!).ConfigureAwait(false);
            }

            if (!toolsEnabled || string.IsNullOrWhiteSpace(root))
            {
                // The files go on the page before the text, or the message is sent without them.
                await AttachAllAsync(attachmentFiles, ct).ConfigureAwait(false);

                var plain = await SendOnceAsync(prompt, progress, thinking, ct).ConfigureAwait(false);
                _lastError = null;
                await ReportChatIdAsync(onChatId).ConfigureAwait(false);
                return plain;
            }

            var reply = await RunWithToolsAsync(prompt, root!, skipPermissions, progress, thinking, attachmentFiles, ct)
                .ConfigureAwait(false);

            _lastError = null;
            await ReportChatIdAsync(onChatId).ConfigureAwait(false);
            return reply;
        }
        catch (Exception ex)
        {
            // The health panel reads this instead of making the user grep a log file.
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            Log($"turn failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>
    /// Primes once per session, then runs up to six rounds of action, result, continue.
    ///
    /// Approvals are honoured: an action that needs one is refused unless the profile has full access,
    /// and the model is told so plainly instead of the app silently doing it.
    /// </summary>
    private async Task<QwenReply> RunWithToolsAsync(
        string prompt,
        string root,
        bool skipPermissions,
        IProgress<string>? progress,
        IProgress<string>? thinking,
        IReadOnlyList<string>? attachmentFiles,
        CancellationToken ct)
    {
        // Priming is per conversation, not per process. The guide names a working directory, and it
        // is the oldest message in the chat, so a new chat, a cleared history or a provider-side
        // compaction drops it while this object still believes it is primed. DeepSeek re-checks the
        // chat before every turn for the same reason; the page path is the cheapest equivalent here.
        string pathBefore = await CurrentPagePathAsync().ConfigureAwait(false);
        bool needsPriming = _primedRoot != root || pathBefore != _primedChat;

        if (needsPriming)
        {
            _primedRoot = root;

            // Same three inputs DeepSeek gives its guide: the working directory, the skills the
            // project ships, and the MCP tools the servers expose. The rules travel inside the guide.
            var skills = DeepSeek.SkillCatalog.Discover(root);

            // Enumeration starts a process per server, so a broken one must cost its tools and
            // nothing else. Priming is never allowed to fail the turn.
            IReadOnlyList<DeepSeek.McpToolInfo> mcpTools = Array.Empty<DeepSeek.McpToolInfo>();
            try
            {
                mcpTools = DeepSeek.McpRegistry.Tools();
            }
            catch (Exception ex)
            {
                Log($"mcp tools unavailable: {ex.GetType().Name}: {ex.Message}");
            }

            var guide = QwenTools.Guide(root, skills, mcpTools);
            Log($"priming with the tool guide, skills={skills.Count} mcpTools={mcpTools.Count}");

            await SendOnceAsync(guide, null, null, ct).ConfigureAwait(false);

            // Read the path after priming: sending the guide is what creates the conversation, so
            // the path before it is not the one the primer now lives in.
            _primedChat = await CurrentPagePathAsync().ConfigureAwait(false);
            Log($"primed in {_primedChat}");
        }

        // After priming and before the prompt: the guide is not the message the files belong to.
        await AttachAllAsync(attachmentFiles, ct).ConfigureAwait(false);

        // The guide is the oldest message in the chat, and a provider that compacts a long conversation
        // can summarise it away. One short line at the end of the prompt keeps the format where it is
        // read last, which is the only mitigation that survives compaction.
        string message = prompt + ProtocolReminder;
        string lastText = string.Empty;
        int spent = 0;

        for (int round = 0; round < 6; round++)
        {
            ct.ThrowIfCancellationRequested();

            var reply = await SendOnceAsync(message, progress, thinking, ct).ConfigureAwait(false);
            var actions = QwenTools.Extract(reply.Text);
            lastText = QwenTools.Strip(reply.Text);

            if (actions.Count == 0)
            {
                return new QwenReply(lastText, reply.Selector, reply.Thinking);
            }

            var results = new StringBuilder();
            foreach (var action in actions)
            {
                string kind = action.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(kind))
                {
                    continue;
                }

                if (QwenTools.RequiresApproval(kind) && !skipPermissions)
                {
                    // Ask, rather than refuse. This is the same card DeepSeek shows, so a Qwen turn
                    // can write a file once the user approves it.
                    string callId = Guid.NewGuid().ToString("N")[..8];
                    var preview = QwenTools.Preview(kind, action);

                    bool approved = ApprovalRequested is { } ask
                        ? await ask(callId, kind, preview).ConfigureAwait(false)
                        : false;

                    Log($"tool {kind} approval -> {approved}");

                    if (!approved)
                    {
                        results.AppendLine($"[{kind}] denied by the user. Do not retry it; say what you would have done.");
                        continue;
                    }
                }

                string stepId = Guid.NewGuid().ToString("N")[..8];
                string stepPath = action.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String
                    ? pathProp.GetString() ?? string.Empty
                    : string.Empty;

                ToolStarted?.Invoke(stepId, kind, stepPath);

                var (summary, body) = QwenTools.Execute(kind, action, root);
                ToolFinished?.Invoke(stepId, kind, true, summary);

                Log($"tool {kind} -> {summary}");

                    results.AppendLine($"[{kind}] {summary}");
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        // Per result and per turn. Capping only the single result lets a dozen reads
                        // fill the context between them, which is what triggers the compaction that
                        // loses the guide in the first place.
                        int room = Math.Max(0, ResultBudget - spent);
                        int allow = Math.Min(PerResultLimit, room);

                        if (allow <= 0)
                        {
                            results.AppendLine("[truncated: this turn's result budget is spent. Ask for a smaller range.]");
                        }
                        else
                        {
                            string clipped = body.Length > allow ? body[..allow] : body;
                            results.AppendLine(clipped);
                            spent += clipped.Length;

                            if (clipped.Length < body.Length)
                            {
                                results.AppendLine($"[truncated at {allow} of {body.Length} characters]");
                            }
                        }
                    }
            }

            message = $"Tool results:\n{results}\nContinue. Use another action if needed, otherwise answer.";
        }

        return new QwenReply(lastText, "tools", string.Empty);
    }

    /// <summary>
    /// Drops a trailing line that is only a number.
    ///
    /// A rendered code block prints its line-number gutter above the object, and that gutter reaches
    /// us as its own delta before the object exists. Sending it once means it stays in an append-only
    /// transcript forever, so it is held back until real text follows it.
    /// </summary>
    private static string HoldBackGutter(string text)
    {
        int end = text.Length;

        while (end > 0 && (text[end - 1] == '\n' || text[end - 1] == ' ' || text[end - 1] == '\t'))
        {
            end--;
        }

        int lineStart = text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        string lastLine = text[lineStart..end].Trim();

        return lastLine.Length is > 0 and <= 3 && int.TryParse(lastLine, out _)
            ? text[..lineStart]
            : text;
    }

    /// <summary>Qwen puts the conversation id in the path: /c/&lt;id&gt;.</summary>
    private static string? ChatIdFromPath(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && string.Equals(parts[0], "c", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : null;
    }

    /// <summary>
    /// Reopens a session's own conversation.
    ///
    /// The page keeps whichever chat ran last, so without this a reopened session continued somebody
    /// else's conversation — the same fault DeepSeek had when it used --continue.
    /// </summary>
    private async Task EnsureOnChatAsync(string conversationId)
    {
        string target = $"/c/{conversationId}";
        string path = await CurrentPagePathAsync().ConfigureAwait(false);

        if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) return;

        await UiAsync(async () =>
        {
            await EnsureWebViewAsync().ConfigureAwait(true);
            _web!.CoreWebView2.Navigate($"https://chat.qwen.ai{target}");
            return true;
        }).ConfigureAwait(false);

        // The page renders the conversation before it accepts a message; typing sooner lands in a
        // composer that is about to be replaced.
        await Task.Delay(2500).ConfigureAwait(false);
        Log($"opened conversation {conversationId}");
    }

    /// <summary>Tells the host which conversation this turn landed in, so the session can keep it.</summary>
    private async Task ReportChatIdAsync(Action<string>? onChatId)
    {
        if (onChatId is null) return;

        string path = await CurrentPagePathAsync().ConfigureAwait(false);
        if (ChatIdFromPath(path) is { Length: > 0 } id) onChatId(id);
    }

    /// <summary>
    /// The page path, used as a conversation identity.
    ///
    /// `/` is a new chat, `/c/...` is an existing one, `/auth` is the sign-in form. Comparing it with
    /// the path the primer was sent in is enough to notice that the conversation changed underneath
    /// us, without parsing the transcript or trusting a virtualised message list.
    /// </summary>
    private async Task<string> CurrentPagePathAsync()
    {
        try
        {
            return await UiAsync(() => EvalAsync("location.pathname")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A page that cannot answer tells us nothing about the conversation; do not re-prime on
            // a failed probe, which would put the guide into every turn.
            Log($"path probe failed: {ex.GetType().Name}: {ex.Message}");
            return _primedChat ?? string.Empty;
        }
    }

    /// <summary>One exchange: type, send, read. No tool handling.</summary>
    /// <summary>
    /// Puts this turn's files on the page's upload control.
    ///
    /// A chat box carries text only, so an image or a PDF reaches Qwen through the page's own input.
    /// CDP sets that input from outside; a page that exposes none is reported by name rather than
    /// sending the turn as if the attachment had never been added.
    /// </summary>
    /// <summary>
    /// Reports whether this page exposes an upload control, and how it was found.
    ///
    /// Attaching guesses at a bare `input[type=file]` and, failing that, clicks something that looks
    /// like a paperclip. That markup belongs to the page, not to this app, so when an upload fails
    /// this sentence is the difference between a diagnosis and a shrug.
    /// </summary>
    public Task<string> ProbeUploadAsync() => UiAsync(async () =>
    {
        var core = _web?.CoreWebView2;
        if (core == null) return "The Qwen window is not open yet, so there is nothing to check.";

        if (await FindFileInputAsync(core).ConfigureAwait(true) > 0)
        {
            return "Found an input[type=file] on the page. Attachments should upload normally.";
        }

        try
        {
            await core.ExecuteScriptAsync(
                "document.querySelector('[aria-label*=\"ttach\"], [aria-label*=\"pload\"], [data-testid*=\"upload\"]')?.click()").ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return "No file input, and the attach button could not be clicked.";
        }

        await Task.Delay(600).ConfigureAwait(true);

        return await FindFileInputAsync(core).ConfigureAwait(true) > 0
            ? "The input appears once the attach button is clicked; that is what the upload does. Attachments work."
            : "No file input, even after clicking the attach button. Uploads will fail on this page.";
    });

    private async Task AttachAllAsync(IReadOnlyList<string>? files, CancellationToken ct)
    {
        if (files is null || files.Count == 0) return;

        foreach (string file in files)
        {
            bool attached = await UiAsync(() => AttachFileAsync(file)).ConfigureAwait(false);

            if (!attached)
            {
                throw new InvalidOperationException(
                    $"Qwen would not take {Path.GetFileName(file)}: the page has no file input. " +
                    "Remove the attachment, or use an engine that accepts files such as Claude Code or OpenCode.");
            }
        }

        // The page uploads in the background; a message sent mid-upload arrives without the file.
        await Task.Delay(1500, ct).ConfigureAwait(false);
    }

    private async Task<bool> AttachFileAsync(string path)
    {
        var core = _web?.CoreWebView2;
        if (core == null) return false;

        int nodeId = await FindFileInputAsync(core).ConfigureAwait(false);

        if (nodeId == 0)
        {
            // Some builds create the input only once the paperclip has been used.
            try
            {
                await core.ExecuteScriptAsync(
                    "document.querySelector('[aria-label*=\"ttach\"], [aria-label*=\"pload\"], [data-testid*=\"upload\"]')?.click()");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // A page that refuses the click is the same as a page with no control.
            }

            await Task.Delay(400).ConfigureAwait(false);
            nodeId = await FindFileInputAsync(core).ConfigureAwait(false);
        }

        if (nodeId == 0) return false;

        await core.CallDevToolsProtocolMethodAsync(
            "DOM.setFileInputFiles",
            JsonSerializer.Serialize(new { files = new[] { path }, nodeId })).ConfigureAwait(false);

        Log($"attached {Path.GetFileName(path)}");
        return true;
    }

    private static async Task<int> FindFileInputAsync(CoreWebView2 core)
    {
        string docRaw = await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":-1}").ConfigureAwait(false);

        using var doc = JsonDocument.Parse(docRaw);
        int rootId = doc.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();

        string queryRaw = await core.CallDevToolsProtocolMethodAsync(
            "DOM.querySelector",
            JsonSerializer.Serialize(new { nodeId = rootId, selector = "input[type=file]" })).ConfigureAwait(false);

        using var query = JsonDocument.Parse(queryRaw);

        return query.RootElement.TryGetProperty("nodeId", out var id) ? id.GetInt32() : 0;
    }

    private async Task<QwenReply> SendOnceAsync(
        string prompt,
        IProgress<string>? progress,
        IProgress<string>? thinking,
        CancellationToken ct)
    {
        return await UiAsync(async () =>
            {
                await EnsureWebViewAsync().ConfigureAwait(true);

                // Signing in navigates to /auth and stays there, so a later turn would type into a
                // login form. Every turn starts from the chat page.
                await EnsureOnChatPageAsync().ConfigureAwait(true);

                // The guest nudge appears after a turn and covers the composer, so clear it before
                // typing rather than discovering it when the message goes nowhere.
                await DismissSignInNudgeAsync().ConfigureAwait(true);

                // The page hydrates after navigation, so the composer is absent for the first few
                // seconds. Without this wait the very first turn failed with "no-composer".
                await WaitForReadyAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(true);
                await ClearAgeGateAsync().ConfigureAwait(true);

                // Focus the composer, then type with the browser's own input path.
                //
                // Setting `value` through the prototype setter looked like it worked but left the box
                // empty: this editor is React-controlled and ignores a value swap it did not see
                // typed. Input.insertText goes through the same path a keystroke does.
                // Everything already rendered belongs to earlier turns, including the primer's
                // acknowledgement. Counting first is what stops that acknowledgement being returned
                // as this turn's answer.
                int baseline = 0;
                try
                {
                    var counted = await EvalAsync(
                        "document.querySelectorAll('[class*=\"qwen-markdown\"]').length").ConfigureAwait(true);
                    if (!int.TryParse(counted, out baseline))
                    {
                        baseline = 0;
                    }
                }
                catch (InvalidOperationException)
                {
                    // A page mid-render simply means no baseline; the read falls back to the last node.
                }

                string focused = await EvalAsync("""
                    (() => {
                      const box = document.querySelector('textarea.message-input-textarea')
                        || document.querySelector('textarea')
                        || document.querySelector('[contenteditable="true"]');
                      if (!box) return 'no-composer';
                      box.click();
                      box.focus();
                      return box.tagName.toLowerCase();
                    })()
                    """).ConfigureAwait(true);

                if (focused == "no-composer")
                    throw new InvalidOperationException("Could not type into the composer: no-composer");

                await TypeTextAsync(prompt).ConfigureAwait(true);
                await Task.Delay(250, ct).ConfigureAwait(true);

                // If the page ignored it, fall back to the value setter and check again.
                string filled = await EvalAsync("""
                    (() => {
                      const box = document.querySelector('textarea.message-input-textarea')
                        || document.querySelector('textarea');
                      const text = box ? (box.value || '') : (document.activeElement?.textContent || '');
                      return String(text).trim().length > 0 ? 'filled' : 'empty';
                    })()
                    """).ConfigureAwait(true);

                if (filled != "filled")
                {
                    Log("insertText left the box empty, falling back to the value setter");
                    await EvalAsync($$"""
                        (() => {
                          const box = document.querySelector('textarea.message-input-textarea')
                            || document.querySelector('textarea');
                          if (!box) return 'no-composer';
                          const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
                          setter.call(box, {{JsonSerializer.Serialize(prompt)}});
                          box.dispatchEvent(new Event('input', { bubbles: true }));
                          return 'typed';
                        })()
                        """).ConfigureAwait(true);
                    await Task.Delay(250, ct).ConfigureAwait(true);
                }

                // Enter first, because that is the only path measured to work on this page. The Send
                // button is a fallback, and a click is only trusted if the box actually clears.
                await PressEnterAsync().ConfigureAwait(true);
                await Task.Delay(1200, ct).ConfigureAwait(true);

                // The age gate can appear on the first send rather than before typing, which would
                // swallow the message silently. Clear it and press Enter again.
                bool gateAfterSend = await EvalBoolAsync("/year were you born/i.test(document.body.innerText || '')")
                    .ConfigureAwait(true);

                if (gateAfterSend)
                {
                    Log("age gate appeared on send, clearing and retrying");
                    await ClearAgeGateAsync().ConfigureAwait(true);
                    await Task.Delay(600, ct).ConfigureAwait(true);

                    string stillHeld = await EvalAsync("""
                        (() => {
                          const box = document.querySelector('textarea');
                          return box && (box.value || '').trim().length > 0 ? 'held' : 'gone';
                        })()
                        """).ConfigureAwait(true);

                    if (stillHeld == "held")
                    {
                        await PressEnterAsync().ConfigureAwait(true);
                        await Task.Delay(1200, ct).ConfigureAwait(true);
                    }
                }

                string sent = await EvalAsync("""
                    (() => {
                      const box = document.querySelector('textarea');
                      return box && (box.value || '').trim().length === 0 ? 'cleared' : 'still-filled';
                    })()
                    """).ConfigureAwait(true);

                if (sent != "cleared")
                {
                    await EvalAsync("""
                        (() => {
                          const btn = document.querySelector('button[aria-label="Send"]');
                          if (btn && !btn.disabled) { btn.click(); return 'clicked'; }
                          return 'no-button';
                        })()
                        """).ConfigureAwait(true);

                    await Task.Delay(1200, ct).ConfigureAwait(true);
                    sent = await EvalAsync("""
                        (() => {
                          const box = document.querySelector('textarea');
                          return box && (box.value || '').trim().length === 0 ? 'cleared-after-click' : 'not-sent';
                        })()
                        """).ConfigureAwait(true);

                    if (sent == "not-sent")
                        throw new InvalidOperationException("Qwen did not accept the message: the composer still holds it.");
                }

                Log($"composer {filled}, send -> {sent}");
                if (sent == "no-target") throw new InvalidOperationException("No send control on the page.");

                return await ReadAnswerAsync(progress, thinking, baseline, ct).ConfigureAwait(true);
            }).ConfigureAwait(false);
    }

    public Task StopAsync() => UiAsync(async () =>
    {
        if (_web?.CoreWebView2 == null) return true;

        var stopped = await EvalAsync("""
            (() => {
              const btn = [...document.querySelectorAll('button')]
                .find(b => /stop/i.test(b.getAttribute('aria-label') || ''));
              if (!btn) return 'no-stop-button';
              btn.click();
              return 'clicked';
            })()
            """).ConfigureAwait(true);

        Log($"stop -> {stopped}");
        return true;
    });

    // ------------------------------------------------------------------ page scripts

    private async Task<QwenStatus> ReadStatusAsync()
    {
        var result = await EvalAsync("""
            (() => {
              const text = document.body.innerText || '';
              const hasLogin = [...document.querySelectorAll('button')]
                .some(b => /^\s*log in\s*$/i.test((b.textContent || '').trim()));
              if (hasLogin) return 'anonymous';
              return text.includes('New Chat') ? 'signed-in' : 'unknown';
            })()
            """).ConfigureAwait(true);

        return result switch
        {
            "anonymous" => QwenStatus.Anonymous,
            "signed-in" => QwenStatus.SignedIn,
            _ => QwenStatus.Unknown,
        };
    }

    /// <summary>
    /// A first guest message raises "What year were you born?". The year is pre-filled, so Continue
    /// is the only control that matters.
    /// </summary>
    /// <summary>
    /// Closes the "Welcome to Qwen" sign-in nudge that appears after a guest turn.
    ///
    /// It covers the composer, so the next message would be typed into a covered page and lost. The
    /// dialog is closed, never "Log in": signing in is the user's choice, not ours.
    /// </summary>
    private async Task DismissSignInNudgeAsync()
    {
        // A/B feedback dialog: two responses, each with a Skip. It covers the composer, so a turn
        // arriving while it is up types into nothing and looks like a tool failure.
        bool survey = await EvalBoolAsync(
            "/Which response do you prefer|evaluate and improve/i.test(document.body.innerText || '')")
            .ConfigureAwait(true);

        if (survey)
        {
            var skipped = await EvalAsync("""
                (() => {
                  const buttons = [...document.querySelectorAll('button')];
                  const skip = buttons.find(b => /^\s*skip\s*$/i.test((b.textContent || '').trim()));
                  if (skip) { skip.click(); return 'skipped'; }
                  return 'no-skip';
                })()
                """).ConfigureAwait(true);

            Log($"feedback survey -> {skipped}");
            await Task.Delay(600).ConfigureAwait(true);

            bool stillUp = await EvalBoolAsync(
                "/Which response do you prefer/i.test(document.body.innerText || '')")
                .ConfigureAwait(true);

            if (stillUp)
            {
                await PressKeyAsync("Escape", 27).ConfigureAwait(true);
                await Task.Delay(500).ConfigureAwait(true);
            }
        }

        var found = await EvalBoolAsync(
            "/Welcome to Qwen|Log in to unlock/i.test(document.body.innerText || '')")
            .ConfigureAwait(true);

        if (!found)
        {
            return;
        }

        // Escape first. Measured on the live page: the dialog's X has no aria-label, so guessing at a
        // close button clicks a message action instead and leaves the nudge up. Escape closes it.
        await PressKeyAsync("Escape", 27).ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        if (!await IsNudgeOpenAsync().ConfigureAwait(true))
        {
            Log("sign-in nudge closed with escape");
            return;
        }

        // Fallback for a build that ignores Escape: the X is the text-less button nearest the
        // dialog's top-right corner.
        var closed = await EvalAsync("""
            (() => {
              const text = document.body.innerText || '';
              if (!/Welcome to Qwen|Log in to unlock/i.test(text)) return 'no-nudge';

              const dialog = document.querySelector('[role="dialog"]')
                || [...document.querySelectorAll('div')]
                     .find(el => /Welcome to Qwen|Log in to unlock/i.test(el.textContent || '')
                                 && (el.textContent || '').length < 400);

              if (!dialog) return 'no-dialog';

              const box = dialog.getBoundingClientRect();
              const buttons = [...dialog.querySelectorAll('button')]
                .filter(b => (b.textContent || '').trim().length === 0)
                .map(b => ({ b, r: b.getBoundingClientRect() }))
                .filter(x => x.r.width > 0);

              // The close control sits in the top band of the dialog, on the right.
              const corner = buttons
                .filter(x => x.r.top - box.top < 60)
                .sort((a, b) => b.r.right - a.r.right)[0];

              if (!corner) return 'no-corner-button';

              corner.b.click();
              return 'clicked-corner';
            })()
            """).ConfigureAwait(true);

        Log($"sign-in nudge fallback -> {closed}");
        await Task.Delay(600).ConfigureAwait(true);

        Log($"sign-in nudge still open = {await IsNudgeOpenAsync().ConfigureAwait(true)}");
    }

    private Task<bool> IsNudgeOpenAsync() =>
        EvalBoolAsync("/Welcome to Qwen|Log in to unlock/i.test(document.body.innerText || '')");

    /// <summary>A single key, pressed and released, with no text field for the protocol to reject.</summary>
    private async Task PressKeyAsync(string key, int virtualKey)
    {
        var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("Qwen page is not ready");

        var keyDown = new Dictionary<string, object>
        {
            ["type"] = "keyDown",
            ["key"] = key,
            ["code"] = key,
            ["windowsVirtualKeyCode"] = virtualKey,
            ["nativeVirtualKeyCode"] = virtualKey,
        };

        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(keyDown))
            .ConfigureAwait(true);

        await Task.Delay(60).ConfigureAwait(true);

        var keyUp = new Dictionary<string, object>(keyDown) { ["type"] = "keyUp" };
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(keyUp))
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Clears "Confirm your age to continue".
    ///
    /// The year is a dropdown, pre-filled with 2000, and the dialog only cares that the answer makes
    /// you an adult. So it picks a year at least 18 back rather than trusting the default, which
    /// could be a recent year on a different build.
    /// </summary>
    private async Task ClearAgeGateAsync()
    {
        var picked = await EvalAsync("""
            (() => {
              const text = document.body.innerText || '';
              if (!/year were you born/i.test(text)) return 'no-gate';

              const oldestAllowed = new Date().getFullYear() - 18;

              const select = document.querySelector('select');
              if (select) {
                const options = [...select.options];
                if (options.length === 0) return 'select-empty';

                const yearOf = (o) => parseInt((o.textContent || '').trim(), 10);
                const valid = options.filter(o => Number.isFinite(yearOf(o)) && yearOf(o) <= oldestAllowed);

                // 2000 when it qualifies, otherwise the most recent year that still clears 18.
                const target = valid.find(o => yearOf(o) === 2000)
                  || valid.sort((a, b) => yearOf(b) - yearOf(a))[0];

                if (!target) return 'no-adult-option';

                const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
                setter.call(select, target.value);
                select.dispatchEvent(new Event('input', { bubbles: true }));
                select.dispatchEvent(new Event('change', { bubbles: true }));
                return 'select:' + (target.textContent || '').trim();
              }

              return 'custom-control';
            })()
            """).ConfigureAwait(true);

        Log($"age gate year -> {picked}");

        if (picked == "no-gate")
        {
            return;
        }

        // A custom dropdown needs one click to open, then one on the option.
        if (picked == "custom-control")
        {
            await EvalAsync("""
                (() => {
                  const oldestAllowed = new Date().getFullYear() - 18;
                  const trigger = [...document.querySelectorAll('[role="combobox"], button, div')]
                    .find(el => /^\\s*(19|20)\\d\\d\\s*$/.test((el.textContent || '').trim()) && el.children.length <= 2);
                  if (trigger) { trigger.click(); return 'opened'; }
                  return 'no-trigger';
                })()
                """).ConfigureAwait(true);

            await Task.Delay(500).ConfigureAwait(true);

            await EvalAsync("""
                (() => {
                  const oldestAllowed = new Date().getFullYear() - 18;
                  const option = [...document.querySelectorAll('[role="option"], li')]
                    .find(el => parseInt((el.textContent || '').trim(), 10) === 2000)
                    || [...document.querySelectorAll('[role="option"], li')]
                      .find(el => parseInt((el.textContent || '').trim(), 10) <= oldestAllowed);

                  if (option) { option.click(); return 'picked'; }
                  return 'no-option';
                })()
                """).ConfigureAwait(true);

            await Task.Delay(400).ConfigureAwait(true);
        }

        var cleared = await EvalAsync("""
            (() => {
              const btn = [...document.querySelectorAll('button')]
                .find(b => /^\s*continue\s*$/i.test((b.textContent || '').trim()));
              if (!btn) return 'no-button';

              btn.click();
              return 'cleared';
            })()
            """).ConfigureAwait(true);

        Log($"age gate -> {cleared}");

        if (cleared == "cleared")
        {
            await Task.Delay(1500).ConfigureAwait(true);

            // Confirm it actually closed; a gate that stays up would silently swallow the message.
            var stillOpen = await EvalBoolAsync("/year were you born/i.test(document.body.innerText || '')")
                .ConfigureAwait(true);
            Log($"age gate still open after continue: {stillOpen}");
        }
    }

    /// <summary>
    /// Reads the answer until it stops growing.
    ///
    /// The container was never observed in the probe, so this tries known shapes in order and reports
    /// which one worked. A miss returns empty text and a logged selector list rather than a silent
    /// blank answer.
    /// </summary>
    private async Task<QwenReply> ReadAnswerAsync(IProgress<string>? progress, IProgress<string>? thinking, int baseline, CancellationToken ct)
    {
        string Extract = """
            (() => {
              // Measured on the live page. Only these two carry the answer text alone:
              // message-content also holds the user's own message, and the assistant wrapper
              // prepends "Thinking completed". Emitting either would put the prompt in the reply.
              const candidates = [
                '[class*="qwen-markdown"]',
                '[class*="markdown-body"]'
              ];

              const skip = %BASELINE%;

              for (const sel of candidates) {
                const nodes = [...document.querySelectorAll(sel)]
                  .slice(skip)
                  .filter(el => (el.textContent || '').trim().length > 0);
                if (nodes.length === 0) continue;

                const last = nodes[nodes.length - 1];
                const text = (last.innerText || last.textContent || '').trim();
                if (text.length > 0) return { selector: sel, text: text };
              }

              return { selector: 'none', text: '' };
            })()
            """;

        string emitted = string.Empty;
        string selector = "none";
        string lastText = string.Empty;
        string reasoning = string.Empty;
        string reported = string.Empty;
        int stableTicks = 0;
        // The template carries the baseline into the page script.
        Extract = Extract.Replace("%BASELINE%", baseline.ToString(CultureInfo.InvariantCulture));

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            JsonElement snapshot;
            try
            {
                snapshot = await EvalAsyncRaw(Extract).ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(400, ct).ConfigureAwait(true);
                continue;
            }

            string text = snapshot.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (snapshot.TryGetProperty("selector", out var sel)) selector = sel.GetString() ?? selector;

            // Guest quota is a dead end, not a slow answer. Waiting the full two minutes for a token
            // that will never arrive is what left the chat on "Waiting for the first token".
            bool limited = await EvalBoolAsync(
                "/guest chat limit|reached the guest|log in to continue/i.test(document.body.innerText || '')")
                .ConfigureAwait(true);

            if (limited)
            {
                Log("guest chat limit reached");
                throw new InvalidOperationException(
                    "You have reached Qwen's guest chat limit. Open the Qwen menu and choose Sign in to continue.");
            }

            // The reasoning block, reported as it grows. The mode chip also matches "thinking", so
            // selector-shaped nodes and the bare mode labels are excluded.
            try
            {
                string current = await EvalAsync("""
                    (() => {
                      const nodes = [...document.querySelectorAll('[class*="thinking"]')]
                        .filter(el => !/selector|dropdown|menu/i.test((el.className || '').toString()))
                        .filter(el => {
                          const t = (el.innerText || '').trim();
                          return t.length > 0 && !/^(auto|thinking|fast)$/i.test(t);
                        });

                      if (nodes.length === 0) return '';

                      const last = nodes[nodes.length - 1];
                      return (last.innerText || '')
                        .replace(/Thinking completed/gi, '')
                        .replace(/^Thinking\b[^\n]*\n?/i, '')
                        .trim();
                    })()
                    """).ConfigureAwait(true);

                if (current.Length > 0 && current != reasoning)
                {
                    reasoning = current;
                    thinking?.Report(reasoning);
                }
            }
            catch (InvalidOperationException)
            {
                // Mid-navigation; the next tick usually answers.
            }

            if (text.Length > emitted.Length && text.StartsWith(emitted, StringComparison.Ordinal))
            {
                emitted = text;

                // Report what a person should see, and only the new part of it. The transcript
                // appends every token, so sending the whole answer again duplicated it: that is
                // where the repeated "tooltool" came from.
                string visible = HoldBackGutter(QwenTools.Visible(emitted));

                if (visible.Length > reported.Length && visible.StartsWith(reported, StringComparison.Ordinal))
                {
                    progress?.Report(visible[reported.Length..]);
                    reported = visible;
                }
                else if (visible.Length > reported.Length)
                {
                    // A stripped action changed the prefix. Start the report over from what is new.
                    reported = visible;
                    progress?.Report(visible);
                }
            }

            stableTicks = text == lastText ? stableTicks + 1 : 0;
            lastText = text;

            // Three quiet ticks at 600ms means generation stopped, or never started.
            if (emitted.Length > 0 && stableTicks >= 3) break;

            await Task.Delay(600, ct).ConfigureAwait(true);
        }

        // An empty answer has two very different causes: the message never sent, or it sent and the
        // reply container was not matched. The page state separates them.
        if (emitted.Length == 0)
        {
            try
            {
                var probe = await EvalAsync("""
                    (() => {
                      const body = (document.body.innerText || '').replace(/\\s+/g, ' ').slice(0, 240);
                      const bubbles = document.querySelectorAll('[class*="message"], [class*="bubble"]').length;
                      return 'bubbles=' + bubbles + ' body="' + body + '"';
                    })()
                    """).ConfigureAwait(true);
                Log($"no answer text; page: {probe}");
            }
            catch (Exception ex)
            {
                Log($"no answer text; could not read page: {ex.Message}");
            }
        }

        Log($"answer via {selector}, {emitted.Length} chars");
        return new QwenReply(emitted, selector, reasoning);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>Types through CDP, the same path a physical keystroke takes.</summary>
    private async Task TypeTextAsync(string text)
    {
        var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("Qwen page is not ready");
        await core.CallDevToolsProtocolMethodAsync(
            "Input.insertText",
            JsonSerializer.Serialize(new { text })).ConfigureAwait(true);
    }

    /// <summary>Enter as keyDown / char / keyUp, which is what a form actually listens for.</summary>
    /// <summary>
    /// Enter as keyDown then keyUp.
    ///
    /// Measured against the live page: this is the only submit path that works. A synthetic
    /// `.click()` on the Send button does nothing, and a real mouse press at its centre does nothing
    /// either, because the page's own state never sees a value that was not typed. A null `text`
    /// field also makes the protocol reject the call with "Class not registered", so the payload is
    /// built without that key at all.
    /// </summary>
    private Task PressEnterAsync() => PressKeyAsync("Enter", 13);

    /// <summary>Evaluates to a real boolean, without going through the string form.</summary>
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
        var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("Qwen page is not ready");

        // Same transient COM failure the DeepSeek engine hits: a DevTools call made while the
        // renderer is being replaced answers "Class not registered", then works a moment later.
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

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return JsonDocument.Parse("null").RootElement.Clone();

        if (result.TryGetProperty("value", out var value)) return value.Clone();
        if (result.TryGetProperty("description", out var description))
            return JsonDocument.Parse(JsonSerializer.Serialize(description.ToString())).RootElement.Clone();

        return JsonDocument.Parse("null").RootElement.Clone();
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (IOException)
        {
            // Logging must never break a turn.
        }
    }
}
