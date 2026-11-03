using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentsHub.Services.DeepSeek;

/// <summary>
/// A free DeepSeek chat engine that drives chat.deepseek.com inside our own WebView2.
///
/// The reference implementation in `refrenceDS/deepseek` does the same thing through the Playwriter
/// Chrome extension, which needs Node, npm, a browser extension and a local CDP relay. Read from its
/// source, all of that exists only to reach a tab that is not ours: `chrome.debugger.sendCommand`
/// plus `Runtime.evaluate`. WebView2 exposes the same protocol in-process, so the extension, the
/// relay and the CLI are all unnecessary here.
///
/// Verified before this file existed (`tools/DeepSeekSpike`): a signed-in profile, a bearer token
/// from localStorage, and a full turn returning THINK and RESPONSE fragments.
///
/// Two DOM facts the send depends on, both measured: React ignores `textarea.value =` and needs the
/// native prototype setter plus an `input` event, and there is no stable send-button selector so the
/// submit is a synthetic Enter keydown.
/// </summary>
public sealed class DeepSeekEngine
{
    public static DeepSeekEngine Instance { get; } = new();

    private const string StartUrl = "https://chat.deepseek.com/";

    /// <summary>How long a turn may take before it is reported as timed out.</summary>
    private const int SendBudgetMs = 120_000;

    /// <summary>The active account's browser profile. One folder per account is what an account is.</summary>
    private static string ProfileDir => DeepSeekAccounts.CurrentProfileDir;

    /// <summary>
    /// Every engine call appends one line here. The sign-in window once failed to appear and there
    /// was no way to tell whether the call was never made, threw, or hung.
    /// </summary>
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "agentshub-deepseek.log");

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:O}\t{message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Diagnostics must never break the feature.
        }
    }

    /// <summary>
    /// The bearer token, or null. Strict on purpose: the signed-out page still has a
    /// `userToken` entry holding the string "null", and a bare truthiness check read that as a
    /// valid session. That single mistake hid the sign-in bar and closed the sign-in window
    /// seconds after it opened.
    /// </summary>
    private const string TokenJs = """
        (() => {
          try {
            const raw = localStorage.getItem('userToken');
            if (!raw) return null;
            let parsed = null;
            try { parsed = JSON.parse(raw); } catch (e) { parsed = null; }
            const value = (parsed && typeof parsed === 'object') ? parsed.value : null;
            return typeof value === 'string' && value.length > 0 ? value : null;
          } catch (e) { return null; }
        })()
        """;

    /// <summary>
    /// Asked before a destructive action runs: (callId, action, preview) -> approved.
    ///
    /// Null means no approval channel, which is how the engine behaves before the host wires one up;
    /// in that case destructive calls are refused rather than silently allowed.
    /// </summary>
    public Func<string, string, string, Task<bool>>? ApprovalRequested { get; set; }

    /// <summary>
    /// Serialises turns. There is one page, so two sessions sending at once would type into the same
    /// composer and then poll each other's chat ids, mixing two conversations into one reply.
    /// </summary>
    /// <summary>The turn in flight, so Stop can reach it.</summary>
    private CancellationTokenSource? _turnCts;

    private readonly SemaphoreSlim _turnLock = new(1, 1);

    /// <summary>True while a turn holds the page, so the host can show a queued state.</summary>
    public bool IsBusy => _turnLock.CurrentCount == 0;

    private Form? _host;
    private WebView2? _web;
    private System.Windows.Forms.Timer? _watchLogin;
    /// <summary>Conversations that already carry the tool guide, so it is not re-sent every turn.</summary>
    private readonly HashSet<string> _primedChats = new(StringComparer.Ordinal);
    private bool _navigationStarted;
    private bool _shuttingDown;

    public bool IsInitialized => _host != null;

    /// <summary>True once a userToken is present in the profile.</summary>
    public bool IsSignedIn { get; private set; }

    /// <summary>
    /// Creates the off-screen host window. Must run on the UI thread, and only once.
    ///
    /// The window is shown at a negative coordinate rather than being hidden or sized to zero:
    /// Chromium throttles an unshown or zero-size surface and the turn stops producing output.
    /// </summary>
    public void Initialize(Form owner)
    {
        if (_host != null) return;
        Log($"initialize profile={ProfileDir}");

        _host = new Platform.ToolWindowHost
        {
            Text = "Agents Hub DeepSeek",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1280, 900),
        };

        _web = new WebView2 { Dock = DockStyle.Fill };
        _host.Controls.Add(_web);

        // Closing the sign-in window would dispose WebView2 and leave a dead engine behind, so the
        // X button just hides it again. The app's own shutdown is the only real close.
        _host.FormClosing += (_, e) =>
        {
            if (_shuttingDown) return;
            e.Cancel = true;
            ParkOffScreen();
        };

        _host.Show();
        owner.FormClosed += (_, _) => Shutdown();
    }

    /// <summary>
    /// Shows the window so the user can sign in, and parks it once a token appears.
    ///
    /// The watcher only arms when there is no token yet. Arming it while signed in closed the window
    /// seconds after opening, because the first tick saw a valid session and parked it.
    /// </summary>
    public Task ShowLoginWindowAsync() => ShowWindowAsync(autoPark: true);

    /// <summary>Shows the window and leaves it alone until the user closes it.</summary>
    public Task ShowWindowAsync() => ShowWindowAsync(autoPark: false);

    private Task ShowWindowAsync(bool autoPark) => UiAsync(async () =>
    {
        Log($"show window autoPark={autoPark}");
        await EnsureWebViewAsync();

        if (_host is not { } host) return true;

        // A window that was closed once must be able to come back, so show it explicitly.
        if (!host.Visible) host.Show();
        host.FormBorderStyle = FormBorderStyle.Sizable;
        host.StartPosition = FormStartPosition.Manual;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        host.Location = new Point(
            area.X + Math.Max(0, (area.Width - host.Width) / 2),
            area.Y + Math.Max(0, (area.Height - host.Height) / 2));

        // TopMost while it is up: a window that opens behind the app looks like nothing happened,
        // which is exactly how the first attempt was reported.
        host.TopMost = true;
        host.Activate();
        host.BringToFront();

        _watchLogin?.Dispose();
        _watchLogin = null;

        bool alreadySignedIn = false;
        try
        {
            var token = await EvalAsync(TokenJs);
            alreadySignedIn = token.ValueKind == JsonValueKind.String;
            IsSignedIn = alreadySignedIn;
        }
        catch (InvalidOperationException)
        {
            // Mid-navigation: treat it as signed out and let the watcher decide.
        }

        if (autoPark && !alreadySignedIn) _watchLogin = StartLoginWatch();
        return true;
    });

    /// <summary>Polls for the token while the window is up, then hides it without the user closing anything.</summary>
    private System.Windows.Forms.Timer StartLoginWatch()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += async (_, _) =>
        {
            if (_host is not { Visible: true }) return;
            try
            {
                var token = await EvalAsync(TokenJs);
                if (token.ValueKind != JsonValueKind.String) return;
                IsSignedIn = true;
                timer.Stop();
                timer.Dispose();
                _watchLogin = null;
                ParkOffScreen();
            }
            catch (InvalidOperationException)
            {
                // Mid-navigation; the next tick usually answers.
            }
        };
        timer.Start();
        return timer;
    }

    /// <summary>Back to the invisible state: shown (so Chromium keeps rendering) but out of the way.</summary>
    private void ParkOffScreen()
    {
        if (_host is not { } host) return;
        host.TopMost = false;
        host.FormBorderStyle = FormBorderStyle.None;
        host.StartPosition = FormStartPosition.Manual;
        host.Location = new Point(-32000, -32000);
    }

    /// <summary>
    /// Switches to another account by rebuilding the WebView2 against its folder.
    ///
    /// Everything cached is per-profile: the page, the navigation flag, the primed conversations.
    /// Keeping any of it would leak one account's session into the next.
    /// </summary>
    public Task<bool> SwitchAccountAsync(string id) => UiAsync(async () =>
    {
        if (!DeepSeekAccounts.Switch(id)) return false;

        Log($"switch account -> {id} ({ProfileDir})");
        ReleaseWeb();

        await EnsureWebViewAsync();
        return true;
    });

    /// <summary>
    /// Removes an account. The browser is released before the profile folder is deleted: a live
    /// WebView2 keeps files like BrowserMetrics-*.pma open, and deleting under it fails with access
    /// denied. Only the active account has a browser attached, so removing any other one leaves the
    /// open page, and a turn in flight, alone.
    /// </summary>
    public async Task<bool> RemoveAccountAsync(string id)
    {
        bool active = string.Equals(id, DeepSeekAccounts.ActiveId, StringComparison.Ordinal);

        if (active)
        {
            await UiAsync(() =>
            {
                ReleaseWeb();
                return Task.FromResult(true);
            }).ConfigureAwait(false);
        }

        bool removed = DeepSeekAccounts.Remove(id);
        if (removed && active) await SwitchAccountAsync(DeepSeekAccounts.ActiveId).ConfigureAwait(false);
        return removed;
    }

    /// <summary>Signs out by deleting the profile, which is where the session actually lives.</summary>
    public Task SignOutAsync() => UiAsync(async () =>
    {
        Log("sign out");
        ReleaseWeb();

        try
        {
            if (Directory.Exists(ProfileDir)) Directory.Delete(ProfileDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file keeps the profile; the next sign-in still works.
        }

        await EnsureWebViewAsync();
        return true;
    });

    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        var ok = await UiAsync(async () =>
        {
            await EnsureWebViewAsync();
            return await WaitForTokenAsync(20_000, ct);
        }).ConfigureAwait(false);

        IsSignedIn = ok;
        return ok;
    }

    /// <summary>
    /// A quick "are we signed in?" for the click path. Short on purpose: this runs when the user
    /// asks for a DeepSeek chat, and nobody wants a 20 second pause before the window appears.
    /// </summary>
    public async Task<bool> ProbeSignedInAsync(int timeoutMs = 2_500, CancellationToken ct = default)
    {
        try
        {
            var ok = await UiAsync(async () =>
            {
                await EnsureWebViewAsync();
                return await WaitForTokenAsync(timeoutMs, ct);
            }).ConfigureAwait(false);

            IsSignedIn = ok;
            Log($"probe -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            // A probe that throws would leave the composer with no answer at all, which is how the
            // sign-in bar disappeared. Report "not signed in" and keep the reason in the log.
            Log($"probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>One turn's progress. Reasoning and answer grow separately.</summary>
    public sealed record DeepSeekDelta(string? Reasoning, string? Answer);

    /// <summary>
    /// One turn: submit the prompt, then poll the history API and report deltas.
    ///
    /// Split into prepare + poll rather than one long script for three reasons: the answer can be
    /// streamed as it grows, the caller can cancel between polls, and a hang becomes a timeout
    /// instead of an eval that never returns.
    /// </summary>
    /// <summary>One tool step, for the timeline. Start and finish are separate reports.</summary>
    public sealed record DeepSeekToolStep(
        string CallId,
        string Action,
        string Path,
        bool IsStart,
        bool Success,
        string? Summary,
        string? Error,
        int DurationMs,
        /** Command line or preview, so a `Ran` step can show what actually ran. */
        string? Detail = null);

    /// <summary>Rounds of tool use allowed in one turn, matching the Python reference.</summary>
    private const int MaxToolRounds = 8;

    /// <summary>
    /// One turn, with file access when a working directory is available.
    ///
    /// DeepSeek's web chat has no function calling, so the protocol is text: the conversation is
    /// primed once with the tool guide, then every reply is scanned for `tool` blocks, executed
    /// inside the working directory, and answered with the results. A reply with no block ends the
    /// turn. Ported from `refrenceDS/deepseek/cli.py`, which is the version that was proven.
    /// </summary>
    /// <summary>One turn, serialised against every other turn on this page.</summary>
    public async Task<DeepSeekReply> SendAsync(
        string prompt,
        string? chatId,
        string? root,
        IProgress<DeepSeekDelta>? progress = null,
        IProgress<DeepSeekToolStep>? tools = null,
        Action<string>? onChatId = null,
        bool toolsAllowed = true,
        IReadOnlyList<string>? attachments = null,
        bool deepThink = true,
        bool webSearch = false,
        CancellationToken ct = default)
    {
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A DeepSeek turn can prime, run several tool rounds and take minutes. Until now nothing
            // could stop it: the Stop button had no path into this engine at all.
            using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _turnCts = turn;

            try
            {
                return await SendLockedAsync(prompt, chatId, root, progress, tools, onChatId, toolsAllowed, attachments, deepThink, webSearch, turn.Token)
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

    /// <summary>Cancels the turn in flight, if there is one. Safe to call from any thread.</summary>
    public void Stop()
    {
        try
        {
            _turnCts?.Cancel();
            Log("stop requested");
        }
        catch (ObjectDisposedException)
        {
            // The turn finished between the check and the cancel; nothing to stop.
        }
    }

    private async Task<DeepSeekReply> SendLockedAsync(
        string prompt,
        string? chatId,
        string? root,
        IProgress<DeepSeekDelta>? progress = null,
        IProgress<DeepSeekToolStep>? tools = null,
        Action<string>? onChatId = null,
        bool toolsAllowed = true,
        IReadOnlyList<string>? attachments = null,
        bool deepThink = true,
        bool webSearch = false,
        CancellationToken ct = default)
    {
        string? chat = chatId;

        // A session can turn tools off entirely, which also skips the protocol line: an answer-only
        // chat should not carry a tool guide it will never use.
        bool toolsEnabled = toolsAllowed && !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);

        Log(toolsEnabled ? $"tools root={root}" : "tools disabled: no working directory");

        // The protocol has to exist in the conversation history before any request that needs it.
        // Checked against the chat itself, not a local set, so a restart does not re-prime a
        // conversation that already has it.
        bool needsPrime = toolsEnabled
            && (chat is null || (!_primedChats.Contains(chat) && !await HasProtocolAsync(chat, ct).ConfigureAwait(false)));

        if (needsPrime)
        {
            var skills = SkillCatalog.Discover(root!);
            // MCP enumeration costs a process start per server, so it happens once per conversation
            // and never blocks the turn: an unreachable server simply contributes no tools.
            // A broken MCP setup must cost its tools and nothing else: priming is not allowed to
            // fail the turn, which is how one dead server took down every message.
            IReadOnlyList<McpToolInfo> mcpTools = Array.Empty<McpToolInfo>();
            try
            {
                mcpTools = McpRegistry.Tools();
            }
            catch (Exception ex)
            {
                Log($"mcp tools unavailable: {ex.GetType().Name}: {ex.Message}");
            }
            var primed = await SendOnceAsync(DeepSeekTools.Guide(root!, skills, mcpTools), chat, null, null, deepThink, webSearch, ct).ConfigureAwait(false);
            Log($"primed chat={chat} skills={skills.Count} mcpTools={mcpTools.Count}");
            if (!string.IsNullOrWhiteSpace(primed.ChatId)) chat = primed.ChatId;
            if (chat is not null) _primedChats.Add(chat);
            Log($"primed chat={chat}");
        }

        // Files are attached here, after priming on purpose. Attaching before it made the protocol
        // message carry the image, so the guide went out with the user's file attached to it and the
        // real question arrived as a plain text follow-up.
        if (attachments is { Count: > 0 })
        {
            var noUploadControl = new List<string>();

            foreach (var file in attachments)
            {
                try
                {
                    await UiAsync(() => AttachFileAsync(file)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A page that has no upload control will not grow one on retry; anything else is
                    // treated as transient, because a turn lost to a slow render helps nobody.
                    if (ex.Message.Contains("file input", StringComparison.OrdinalIgnoreCase))
                    {
                        noUploadControl.Add(Path.GetFileName(file));
                    }

                    Log($"attach failed {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            if (noUploadControl.Count > 0)
            {
                throw new InvalidOperationException(
                    $"DeepSeek would not take {string.Join(", ", noUploadControl)}: the page has no upload control. " +
                    "Remove the attachment, or use an engine that accepts files such as Claude Code or OpenCode.");
            }

            await Task.Delay(1200, ct).ConfigureAwait(false);
        }

        string message = prompt;

        for (int round = 0; round <= MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var reply = await SendOnceAsync(message, chat, progress, onChatId, deepThink, webSearch, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reply.ChatId)) chat = reply.ChatId;

            if (!toolsEnabled) return reply;

            var calls = DeepSeekTools.Extract(reply.Answer);
            if (calls.Count == 0)
            {
                string prose = DeepSeekTools.Strip(reply.Answer);
                return reply with
                {
                    Answer = prose.Length > 0 ? prose : "(DeepSeek returned no text for this turn.)",
                };
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

                string callId = "ds_" + Guid.NewGuid().ToString("N")[..8];

                // Destructive calls are held before anything happens, so the card describes an
                // intention rather than a completed action.
                if (DeepSeekTools.RequiresApproval(action))
                {
                    bool allowed = ApprovalRequested is null
                        ? false
                        : await ApprovalRequested(callId, action, DeepSeekTools.Preview(action, call)).ConfigureAwait(false);

                    if (!allowed)
                    {
                        Log($"declined {action} {path}");
                        tools?.Report(new DeepSeekToolStep(callId, action, path, true, true, null, null, 0, DeepSeekTools.Preview(action, call)));
                        tools?.Report(new DeepSeekToolStep(callId, action, path, false, false, null, "declined by the user", 0));
                        results.Add($"[{results.Count + 1}] {action}({path}) was declined by the user. Do not retry it; explain what you need instead.");
                        continue;
                    }
                }

                tools?.Report(new DeepSeekToolStep(callId, action, path, true, true, null, null, 0, DeepSeekTools.Preview(action, call)));

                long started = Environment.TickCount64;
                try
                {
                    var (summary, body) = DeepSeekTools.Execute(action, call, root!);
                    tools?.Report(new DeepSeekToolStep(
                        callId, action, path, false, true, summary, null, (int)(Environment.TickCount64 - started)));
                    results.Add($"[{results.Count + 1}] {summary}\n{body}");
                }
                catch (Exception ex)
                {
                    tools?.Report(new DeepSeekToolStep(
                        callId, action, path, false, false, null, ex.Message, (int)(Environment.TickCount64 - started)));
                    results.Add($"[{results.Count + 1}] {action}({path}) failed: {ex.Message}");
                }
            }

            string joined = string.Join("\n\n", results);
            if (joined.Length > DeepSeekTools.MaxResultChars)
            {
                joined = joined[..DeepSeekTools.MaxResultChars] + "\n\n… more results omitted (size cap) …";
            }

            Log($"tool round {round + 1}: {calls.Count} call(s)");
            message = "Tool results://n//n" + joined + "\n\nContinue. Use another `tool` block only if you still need something.";
        }

        throw new InvalidOperationException(
            $"Stopped after {MaxToolRounds} tool rounds. Ask again if you need more.");
    }

    private async Task<DeepSeekReply> SendOnceAsync(
        string prompt,
        string? chatId,
        IProgress<DeepSeekDelta>? progress,
        Action<string>? onChatId,
        bool deepThink,
        bool webSearch,
        CancellationToken ct)
    {
        Log($"send start chat={chatId ?? "-"} chars={prompt.Length}");

        var prepared = await UiAsync(() => EvalAsync(BuildPrepareScript(prompt, chatId, deepThink, webSearch))).ConfigureAwait(false);
        if (!IsTrue(prepared, "ok"))
        {
            string error = StringOf(prepared, "error") ?? "unknown";
            Log($"send prepare failed: {error}");
            throw new InvalidOperationException(Describe(error));
        }

        string? chat = StringOf(prepared, "chat_id");
        long baseline = (long)(NumberOf(prepared, "baseline") ?? 0);

        // Reported now rather than at the end: a stop or a crash mid-turn used to lose the id and
        // the next turn started a fresh web chat.
        if (!string.IsNullOrWhiteSpace(chat)) onChatId?.Invoke(chat);

        var reasoning = new StringBuilder();
        var answer = new StringBuilder();
        string emitted = string.Empty;
        int lastLength = 0;
        double? seconds = null;
        long deadline = Environment.TickCount64 + SendBudgetMs;
        bool finished = false;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var state = await UiAsync(() => EvalAsync(BuildReadScript(chat, baseline))).ConfigureAwait(false);
            Accumulate(reasoning, StringOf(state, "reasoning"), progress, isReasoning: true);

            // The raw answer carries the tool block. Only the visible part is streamed, and the
            // filter holds text back while a fence is still open.
            string? rawAnswer = StringOf(state, "answer");
            if (!string.IsNullOrEmpty(rawAnswer))
            {
                answer.Clear();
                answer.Append(rawAnswer);

                string visible = DeepSeekTools.Visible(rawAnswer);
                if (visible.Length > emitted.Length)
                {
                    progress?.Report(new DeepSeekDelta(null, visible[emitted.Length..]));
                    emitted = visible;
                }
            }

            seconds ??= NumberOf(state, "seconds");

            if (string.Equals(StringOf(state, "status"), "FINISHED", StringComparison.OrdinalIgnoreCase) && answer.Length > 0)
            {
                finished = true;
                break;
            }

            // Fast while the answer is growing, slow when nothing changes. A flat 900ms made the
            // reply arrive in visible chunks even though the page had it sooner.
            int grew = answer.Length > lastLength ? 250 : 900;
            lastLength = answer.Length;
            await Task.Delay(grew, ct).ConfigureAwait(false);
        }

        if (!finished && answer.Length == 0)
        {
            Log("send timed out");
            throw new InvalidOperationException(Describe("timed_out"));
        }

        // Raw text, block included. The tool loop has to see the block to act on it, and stripping
        // here meant every turn ended as prose with nothing executed.
        string raw = answer.ToString();
        Log($"send ok chat={chat} answer={raw.Length} reasoning={reasoning.Length}");
        return new DeepSeekReply(chat ?? string.Empty, raw, reasoning.ToString(), seconds);
    }

    private static void Accumulate(
        StringBuilder buffer,
        string? full,
        IProgress<DeepSeekDelta>? progress,
        bool isReasoning)
    {
        if (string.IsNullOrEmpty(full)) return;

        string so_far = buffer.ToString();
        string delta;
        if (full.Length > so_far.Length && full.StartsWith(so_far, StringComparison.Ordinal))
        {
            delta = full.Substring(so_far.Length);
        }
        else if (full.Length > so_far.Length)
        {
            // The page rewrote earlier text; resend the whole thing rather than splice garbage.
            buffer.Clear();
            delta = full;
        }
        else
        {
            return;
        }

        buffer.Append(delta);
        progress?.Report(isReasoning ? new DeepSeekDelta(delta, null) : new DeepSeekDelta(null, delta));
    }

    /// <summary>
    /// The chats in the sidebar, newest first.
    ///
    /// Read from the page's own DOM rather than calling an undocumented endpoint: the anchors carry
    /// both the id and the title, so this cannot drift from what the user sees.
    /// </summary>
    public Task<List<(string Id, string Title)>> ListChatsAsync() => UiAsync(async () =>
    {
        var result = await EvalAsync("""
            (() => {
              const seen = new Set();
              const out = [];
              for (const a of document.querySelectorAll('a[href*="/a/chat/s/"]')) {
                const id = (a.getAttribute('href') || '').match(/\/s\/([0-9a-fA-F-]{20,})/);
                if (!id || seen.has(id[1])) continue;
                seen.add(id[1]);
                const title = (a.textContent || '').trim();
                if (title) out.push({ id: id[1], title: title.slice(0, 80) });
              }
              return out;
            })()
            """).ConfigureAwait(false);

        var chats = new List<(string, string)>();
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
            {
                string? id = StringOf(item, "id");
                string? title = StringOf(item, "title");
                if (!string.IsNullOrWhiteSpace(id)) chats.Add((id!, title ?? id!));
            }
        }

        return chats;
    });

    /// <summary>True when this chat already carries the protocol line.</summary>
    private async Task<bool> HasProtocolAsync(string? chatId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chatId)) return false;

        try
        {
            var result = await UiAsync(() => EvalAsync(BuildSentinelScript(chatId))).ConfigureAwait(false);
            return IsTrue(result, "hasProtocol");
        }
        catch (InvalidOperationException)
        {
            // A page mid-navigation answers nothing; priming once more is harmless.
            return false;
        }
    }

    private static string BuildSentinelScript(string chatId) => $$"""
        (async () => {
          const token = {{TokenJs}};
          if (!token) return { hasProtocol: false };

          const res = await (await fetch('/api/v0/chat/history_messages?chat_session_id=' +
            {{JsonSerializer.Serialize(chatId)}}, { headers: { Authorization: 'Bearer ' + token } })).json();
          const all = (((res || {}).data || {}).biz_data || {}).chat_messages || [];
          const marker = {{JsonSerializer.Serialize(DeepSeekTools.Sentinel)}};

          const found = all.some((m) => (m.fragments || []).some((f) => (f.content || '').includes(marker)));
          return { hasProtocol: found };
        })()
        """;

    /// <summary>
    /// Puts a real file on the page's file input.
    ///
    /// A textarea cannot carry bytes, so the only honest way to hand DeepSeek an image is the
    /// upload control, and a file input can only be set from outside the page. CDP does that.
    /// </summary>
    private async Task AttachFileAsync(string path)
    {
        var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not ready.");

        var docRaw = await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":-1}");
        using var doc = JsonDocument.Parse(docRaw);
        int rootId = doc.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();

        var queryRaw = await core.CallDevToolsProtocolMethodAsync(
            "DOM.querySelector",
            JsonSerializer.Serialize(new { nodeId = rootId, selector = "input[type=file]" }));

        using var query = JsonDocument.Parse(queryRaw);
        int nodeId = query.RootElement.GetProperty("nodeId").GetInt32();
        if (nodeId == 0) throw new InvalidOperationException("the page has no file input");

        await core.CallDevToolsProtocolMethodAsync(
            "DOM.setFileInputFiles",
            JsonSerializer.Serialize(new { files = new[] { path }, nodeId }));

        Log($"attached {Path.GetFileName(path)}");
    }

    private static string Describe(string error) => error switch
    {
        "not_logged_in" => "DeepSeek is not signed in. Open the sign-in window and log in once.",
        "composer_missing" => "The DeepSeek page has no composer. Open the window to check whether the page changed.",
        "timed_out" => "DeepSeek did not answer in time.",
        _ => $"DeepSeek send failed: {error}",
    };

    private static bool IsTrue(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? NumberOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>Types the prompt, submits it, and reports the chat id plus the message baseline.</summary>
    private static string BuildPrepareScript(string prompt, string? chatId, bool deepThink, bool webSearch) => $$"""
        (async () => {
          const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
          const token = {{TokenJs}};
          if (!token) return { ok: false, error: 'not_logged_in' };

          const chatIdOf = (u) => (String(u).match(/\/s\/([0-9a-fA-F-]{20,})/) || [])[1] || null;
          const api = async (p) => (await fetch(p, { headers: { Authorization: 'Bearer ' + token } })).json();
          const msgs = (j) => (((j || {}).data || {}).biz_data || {}).chat_messages || [];

          // Resume the stored conversation. Without this the page stayed wherever it was, the prompt
          // went into a different chat, and the poll watched an id nothing was writing to.
          const wanted = {{JsonSerializer.Serialize(chatId)}};
          if (wanted && !location.pathname.includes(wanted)) {
            location.href = 'https://chat.deepseek.com/a/chat/s/' + wanted;
            for (let i = 0; i < 40; i++) {
              await sleep(400);
              if (document.querySelector("textarea[name='search']")) break;
            }
          }

          // Their composer carries two chips, DeepThink and Search. Both are plain buttons whose
          // pressed state is in aria-pressed or a selected class, so one setter drives both.
          const setChip = (label, wanted) => {
            const chip = Array.from(document.querySelectorAll(".ds-toggle-button, [role='button'], button"))
              .find((e) => label.test((e.textContent || '').trim()) && (e.textContent || '').trim().length < 24);
            if (!chip) return 'missing';

            const pressed = chip.getAttribute('aria-pressed') === 'true'
              || /selected/i.test(chip.className || '')
              || /--on|--active/.test(chip.className || '');

            if (pressed === wanted) return 'already';
            chip.click();
            return 'clicked';
          };

          const chips = {
            deepThink: setChip(/^deep\s*think$/i, {{(deepThink ? "true" : "false")}}),
            search: setChip(/^search$/i, {{(webSearch ? "true" : "false")}})
          };

          const box = document.querySelector("textarea[name='search']") || document.querySelector('textarea');
          if (!box) return { ok: false, error: 'composer_missing' };

          // A stored id that the page did not land on means the chat was deleted on the web side;
          // start a fresh one rather than polling a conversation that no longer exists.
          let chatId = {{JsonSerializer.Serialize(chatId)}};
          if (chatId && !location.pathname.includes(chatId)) chatId = null;
          if (!chatId) chatId = chatIdOf(location.href);
          let baseline = 0;
          if (chatId) {
            baseline = msgs(await api('/api/v0/chat/history_messages?chat_session_id=' + chatId))
              .reduce((m, x) => Math.max(m, x.message_id || 0), 0);
          }

          // React ignores a plain assignment; the prototype setter plus input event is what it sees.
          const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
          setter.call(box, {{JsonSerializer.Serialize(prompt)}});
          box.dispatchEvent(new Event('input', { bubbles: true }));
          await sleep(120);
          box.focus();
          box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));

          // Enter is the fragile step: if the composer still holds the text, try the send button.
          await sleep(900);
          if ((box.value || '').trim().length > 0) {
            const button = Array.from(document.querySelectorAll('button')).find((b) =>
              /send/i.test(b.getAttribute('aria-label') || '') || /send/i.test(b.getAttribute('title') || ''));
            if (button) button.click();
            else box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
            await sleep(600);
          }

          const started = Date.now();
          while (!chatId && Date.now() - started < 20000) {
            chatId = chatIdOf(location.href);
            if (!chatId) await sleep(400);
          }

          return { ok: !!chatId, chat_id: chatId, baseline, chips, submitted: (box.value || '').trim().length === 0 };
        })()
        """;

    /// <summary>Reads the assistant message for this turn. Returns the whole text so far, not a delta.</summary>
    private static string BuildReadScript(string? chatId, long baseline) => $$"""
        (async () => {
          const token = {{TokenJs}};
          if (!token) return { error: 'not_logged_in' };

          const chatId = {{JsonSerializer.Serialize(chatId)}};
          if (!chatId) return { status: 'pending' };

          const res = await (await fetch('/api/v0/chat/history_messages?chat_session_id=' + chatId,
            { headers: { Authorization: 'Bearer ' + token } })).json();
          const all = (((res || {}).data || {}).biz_data || {}).chat_messages || [];
          const mine = all.filter((m) => (m.message_id || 0) > {{baseline}} && m.role === 'ASSISTANT');
          const last = mine[mine.length - 1];
          if (!last) return { status: 'pending' };

          const frags = last.fragments || [];
          const pick = (type) => frags
            .filter((f) => (f.type || '').toUpperCase() === type)
            .map((f) => f.content || '')
            .join('');
          const think = frags.find((f) => (f.type || '').toUpperCase() === 'THINK');

          return {
            status: String(last.status || '').toUpperCase(),
            reasoning: pick('THINK'),
            answer: pick('RESPONSE'),
            seconds: think && think.elapsed_secs !== undefined ? think.elapsed_secs : null
          };
        })()
        """;

    // ---------------------------------------------------------------- internals

    private Task UiAsync(Func<Task> work) => UiAsync(async () =>
    {
        await work().ConfigureAwait(true);
        return true;
    });

    /// <summary>
    /// Marshals onto the WebView2 thread. Every CoreWebView2 call has thread affinity, and chat
    /// dispatch arrives on a bridge thread, so nothing here may be called directly.
    /// </summary>
    private Task<T> UiAsync<T>(Func<Task<T>> work)
    {
        var host = _host ?? throw new InvalidOperationException("DeepSeek engine was not initialized on the UI thread.");
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

    /// <summary>
    /// Drops the current control so EnsureWebViewAsync builds the next one against the active
    /// profile. Everything cached is per-profile: the page, the navigation flag, the primed
    /// conversations. Keeping any of it would leak one account's session into the next.
    /// </summary>
    private void ReleaseWeb()
    {
        var old = _web;
        _web = null;
        _navigationStarted = false;
        _primedChats.Clear();
        IsSignedIn = false;

        if (old is null) return;

        // Take it out of the host's layout before disposal: a disposed child that stays in the
        // Controls collection still occupies a slot in a 1280x900 window we rebuild repeatedly.
        _host?.Controls.Remove(old);
        old.Dispose();
    }

    private async Task EnsureWebViewAsync()
    {
        // A disposed control is recoverable: switching accounts and signing out dispose it on
        // purpose and rely on this method to build a fresh one against the current profile. Only a
        // missing host window means the engine was never initialized or has shut down.
        if (_host == null || _shuttingDown)
        {
            throw new InvalidOperationException("The DeepSeek engine was not initialized on the UI thread.");
        }

        if (_web == null)
        {
            _web = new WebView2 { Dock = DockStyle.Fill };
            _host.Controls.Add(_web);

            // A brand-new control has never navigated, whatever the old one had done.
            _navigationStarted = false;
        }

        if (_web.CoreWebView2 == null)
        {
            Directory.CreateDirectory(ProfileDir);
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir);
            await _web.EnsureCoreWebView2Async(env);
        }

        if (!_navigationStarted)
        {
            var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not ready.");
            _navigationStarted = true;
            core.Navigate(StartUrl);
        }
    }

    private async Task<bool> WaitForTokenAsync(int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var token = await EvalAsync(TokenJs);
                if (token.ValueKind == JsonValueKind.String) return true;
            }
            catch (InvalidOperationException)
            {
                // The page is mid-navigation; the next poll usually succeeds.
            }
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>
    /// Runs script in the page and returns its value.
    ///
    /// `ExecuteScriptAsync` does not await promises, so async scripts go through CDP with
    /// `awaitPromise`. This is the same call the Playwriter extension makes over chrome.debugger.
    /// </summary>
    private async Task<JsonElement> EvalAsync(string expression)
    {
        var core = _web?.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not ready.");
        var request = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });

        // A DevTools call made while the renderer is being replaced fails with the COM error whose
        // text is "Class not registered". It is transient — the same call answers a moment later,
        // which is why pressing Retry appeared to fix it. Retry here instead of failing the turn.
        string? raw = null;
        for (int attempt = 0; raw is null; attempt++)
        {
            try
            {
                raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request);
            }
            catch (COMException ex) when (attempt < 3)
            {
                Log($"cdp retry {attempt + 1} after {ex.Message}");
                await Task.Delay(200 * (attempt + 1));
            }
        }

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("exceptionDetails", out var thrown))
        {
            var text = thrown.TryGetProperty("text", out var t) ? t.GetString() : "script threw";
            throw new InvalidOperationException(text ?? "script threw");
        }
        return doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value)
            ? value.Clone()
            : default;
    }

    private void Shutdown()
    {
        _shuttingDown = true;
        try
        {
            _web?.Dispose();
            _web = null;
            _host?.Dispose();
            _host = null;
        }
        catch (ObjectDisposedException)
        {
            // Closing twice is not an error.
        }
    }
}

/// <summary>One DeepSeek turn. ChatId maps onto the session's conversation id.</summary>
public sealed record DeepSeekReply(string ChatId, string Answer, string Reasoning, double? ReasoningSeconds);
