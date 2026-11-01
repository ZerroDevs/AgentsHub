#nullable enable
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentsHub.Helpers;
using AgentsHub.Models;
#if WINDOWS
using System.Windows.Forms;
using AgentsHub.Forms;
using Microsoft.Web.WebView2.Core;
#endif

namespace AgentsHub.Services;

public sealed partial class DashboardWebBridge : IDisposable
{

#if DEEPSEEK_UI
#if WINDOWS
    private async Task HandleQwenStatusAsync(string? requestId)
    {
        var status = await Services.Qwen.QwenEngine.Instance.ProbeAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new
        {
            signedIn = status == Services.Qwen.QwenStatus.SignedIn,
            anonymous = status == Services.Qwen.QwenStatus.Anonymous,
            known = status != Services.Qwen.QwenStatus.Unknown,
        });
    }

    private async Task HandleQwenLoginAsync(string? requestId)
    {
        await Services.Qwen.QwenEngine.Instance.ShowLoginWindowAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { shown = true });
    }

    private async Task HandleQwenOpenAsync(string? requestId)
    {
        await Services.Qwen.QwenEngine.Instance.ShowWindowAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { shown = true });
    }

    private async Task HandleQwenModelsAsync(string? requestId)
    {
        var models = await Services.Qwen.QwenEngine.Instance.ListModelsAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { models });
    }

    private async Task HandleQwenModelAsync(JsonElement data, string? requestId)
    {
        string wanted = data.TryGetProperty("model", out var modelProp) ? modelProp.GetString() ?? string.Empty : string.Empty;
        bool applied = !string.IsNullOrWhiteSpace(wanted)
            && await Services.Qwen.QwenEngine.Instance.SetModelAsync(wanted).ConfigureAwait(false);

        SendResponse(requestId, true, new { applied, model = wanted });
    }

    private async Task HandleQwenModeAsync(JsonElement data, string? requestId)
    {
        string mode = data.TryGetProperty("mode", out var modeProp) ? modeProp.GetString() ?? "auto" : "auto";
        bool ok = await Services.Qwen.QwenEngine.Instance.SetModeAsync(mode).ConfigureAwait(false);
        SendResponse(requestId, true, new { applied = ok, mode });
    }

    private async Task HandleQwenStopAsync(string? requestId)
    {
        await Services.Qwen.QwenEngine.Instance.StopAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { stopped = true });
    }

    private async Task HandleQwenSendAsync(JsonElement data, string? requestId)
    {
        string qSessionId = data.TryGetProperty("sessionId", out var qs) ? qs.GetString() ?? string.Empty : string.Empty;
        string qPrompt = data.TryGetProperty("prompt", out var qp) ? qp.GetString() ?? string.Empty : string.Empty;

        if (string.IsNullOrWhiteSpace(qPrompt))
        {
            SendResponse(requestId, false, "Empty prompt");
            return;
        }

        // The page is one composer, so the engine serialises turns itself.
        var qProgress = new Progress<string>(text =>
            PostEventToUi("chat_token", new { sessionId = qSessionId, text, timestamp = DateTime.UtcNow.ToString("O") }));

        string? qDir = data.TryGetProperty("workingDirectory", out var qw) ? qw.GetString() : null;
        var reply = await Services.Qwen.QwenEngine.Instance
            .SendAsync(qPrompt, qDir, false, false, qProgress)
            .ConfigureAwait(false);

        SendResponse(requestId, true, new
        {
            sessionId = qSessionId,
            text = reply.Text,
            selector = reply.Selector,
            empty = reply.Text.Length == 0,
        });
    }

#endif
#endif
#if DEEPSEEK_UI
    // ------------------------------------------------------------------ Z.ai (GLM)

    private async Task HandleZaiStatusAsync(string? requestId)
    {
        var status = await Services.Zai.ZaiEngine.Instance.ProbeAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new
        {
            // Anonymous use is supported on purpose: the site hands every visitor a guest token.
            // `signedIn` only decides whether the advanced models are unlocked.
            signedIn = status == Services.Zai.ZaiStatus.SignedIn,
            guest = status == Services.Zai.ZaiStatus.Guest,
            status = status.ToString().ToLowerInvariant(),
        });
    }

    private async Task HandleZaiLoginAsync(string? requestId)
    {
        await Services.Zai.ZaiEngine.Instance.ShowLoginWindowAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { opened = true });
    }

    private async Task HandleZaiOpenAsync(string? requestId)
    {
        await Services.Zai.ZaiEngine.Instance.ShowWindowAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { opened = true });
    }

    private async Task HandleZaiModelsAsync(string? requestId)
    {
        var models = await Services.Zai.ZaiEngine.Instance.ListModelsAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new
        {
            models = models.Select(m => new { id = m.Id, name = m.Name, locked = m.Locked }).ToList(),
            current = Services.Zai.ZaiEngine.Instance.Model,
        });
    }

    private async Task HandleZaiModelAsync(JsonElement data, string? requestId)
    {
        string name = data.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? string.Empty : string.Empty;
        bool applied = await Services.Zai.ZaiEngine.Instance.SetModelAsync(name).ConfigureAwait(false);
        SendResponse(requestId, true, new { applied, name });
    }

    private Task HandleZaiStopAsync(string? requestId)
    {
        Services.Zai.ZaiEngine.Instance.Stop();
        SendResponse(requestId, true, new { stopped = true });
        return Task.CompletedTask;
    }

    private Task HandleZaiCancelAsync(JsonElement data, string? requestId)
    {
        string sessionId = data.TryGetProperty("sessionId", out var sElem) ? sElem.GetString() ?? string.Empty : string.Empty;
        bool cancelled = CancelZai(sessionId);
        SendResponse(requestId, true, new { cancelled });
        return Task.CompletedTask;
    }

    /// <summary>
    /// One Z.ai turn, reported through the same events every other engine posts.
    ///
    /// Tools, skills and MCP are the shared text protocol: the engine primes the conversation with
    /// the guide, executes the blocks it gets back inside the working directory, and reports each
    /// step as a timeline node. Nothing here is Z.ai specific beyond the transport.
    /// </summary>
    private async Task DispatchZaiAsync(
        string sessionId,
        string prompt,
        string? chatId,
        string? cwd,
        bool skipPermissions,
        string? profile,
        bool toolsAllowed,
        string? model = null)
    {
        var cts = new CancellationTokenSource();
        _zaiRuns[sessionId] = cts;

        static string Now() => DateTime.UtcNow.ToString("O");

        try
        {
            Services.DeepSeek.McpRegistry.UseProfileHome(
                string.IsNullOrWhiteSpace(profile) ? null : AntigravityProfileService.GetVirtualHomeDirectory(profile));
        }
        catch (Exception ex)
        {
            PostEventToUi("session_status", new
            {
                sessionId,
                state = "running",
                error = $"MCP profile home could not be resolved: {ex.Message}",
                timestamp = Now(),
            });
        }

        // An unreachable MCP server costs its tools and nothing else.
        try
        {
            Services.Zai.ZaiEngine.Instance.McpTools = Services.DeepSeek.McpRegistry.Tools();
        }
        catch (Exception ex)
        {
            Services.Zai.ZaiEngine.Instance.McpTools = Array.Empty<Services.DeepSeek.McpToolInfo>();
            EmitLog("Bridge", "warning", $"zai mcp tools unavailable: {ex.Message}");
        }

        PostEventToUi("session_status", new { sessionId, state = "running", timestamp = Now() });

        // The page renders its reasoning collapsed: the body is not in the DOM until a person expands
        // it, so there is no reasoning text to stream. The node is opened here and closed when the
        // turn ends, the way Vyce reports a turn it can only observe from outside.
        PostEventToUi("thinking", new
        {
            sessionId,
            state = "running",
            text = string.Empty,
            timestamp = Now(),
        });

        var turnStarted = DateTime.UtcNow;

        // The page keeps its own model selection, so a session's choice has to be applied before the
        // turn or the answer comes from whatever was picked last. A locked model refuses quietly:
        // the turn then runs on the page's current model instead of failing.
        //
        // Only a GLM name can mean anything to that menu. A session created before Z.ai was picked
        // still carries the agy default ("Gemini 3.8 Flash (High)"), and asking the page for that
        // logs a failure and then runs on whatever was already selected.
        if (!string.IsNullOrWhiteSpace(model) && model.StartsWith("GLM", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                bool applied = await Services.Zai.ZaiEngine.Instance.SetModelAsync(model).ConfigureAwait(false);
                if (!applied)
                {
                    EmitLog("Bridge", "warning", $"zai model {model} not applied; using the page's current model");
                }
            }
            catch (Exception ex)
            {
                EmitLog("Bridge", "warning", $"zai model {model} could not be set: {ex.Message}");
            }
        }

        var progress = new Progress<Services.Zai.ZaiDelta>(delta =>
        {
            if (!string.IsNullOrEmpty(delta.Reasoning))
            {
                PostEventToUi("thinking", new
                {
                    sessionId,
                    state = "running",
                    text = delta.Reasoning,
                    thinkingTokens = 0,
                    timestamp = Now(),
                });
            }

            if (!string.IsNullOrEmpty(delta.Answer))
            {
                PostEventToUi("chat_token", new { sessionId, text = delta.Answer, timestamp = Now() });
            }
        });

        var toolProgress = new Progress<Services.Zai.ZaiToolStep>(step =>
        {
            var (label, kind) = Services.DeepSeek.DeepSeekTools.Describe(step.Action);

            if (step.IsStart)
            {
                PostEventToUi("tool_start", new
                {
                    sessionId,
                    callId = step.CallId,
                    tool = label,
                    @params = new Dictionary<string, object?>
                    {
                        ["toolKind"] = kind,
                        ["path"] = step.Path,
                        ["action"] = step.Action,
                        ["commandLine"] = step.Action == "run_command" ? step.Detail : null,
                    },
                    timestamp = Now(),
                });
                return;
            }

            PostEventToUi("tool_output", new
            {
                sessionId,
                callId = step.CallId,
                tool = label,
                success = step.Success,
                result = step.Summary,
                error = step.Error,
                durationMs = step.DurationMs,
                timestamp = Now(),
            });
        });

        // The approval card is shared with DeepSeek: same event, same reply action.
        Services.Zai.ZaiEngine.Instance.ApprovalRequested = skipPermissions
            ? (_, _, _) => Task.FromResult(true)
            : async (callId, action, preview) =>
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deepSeekApprovals[callId] = pending;

            PostEventToUi("deepseek_approval_request", new
            {
                sessionId,
                callId,
                action,
                preview,
                timestamp = Now(),
            });

            using var registration = cts.Token.Register(() => pending.TrySetResult(false));
            bool approved = await pending.Task.ConfigureAwait(false);
            _deepSeekApprovals.TryRemove(callId, out _);
            return approved;
        };

        try
        {
            string? knownChat = chatId;

            var reply = await Services.Zai.ZaiEngine.Instance
                .SendAsync(prompt, chatId, cwd, progress, toolProgress, id =>
                {
                    if (string.IsNullOrWhiteSpace(id) || string.Equals(id, knownChat, StringComparison.Ordinal)) return;
                    knownChat = id;
                    PostEventToUi("conversation_started", new
                    {
                        sessionId,
                        conversationId = id,
                        timestamp = Now(),
                    });
                }, toolsAllowed, cts.Token)
                .ConfigureAwait(false);

            PostEventToUi("thinking", new
            {
                sessionId,
                state = "done",
                durationSeconds = Math.Round((DateTime.UtcNow - turnStarted).TotalSeconds, 1),
                thinkingTokens = 0,
                timestamp = Now(),
            });

            PostEventToUi("chat_final", new
            {
                sessionId,
                text = reply.Answer,
                timestamp = Now(),
            });

            PostEventToUi("session_status", new { sessionId, state = "idle", timestamp = Now() });
        }
        catch (OperationCanceledException)
        {
            PostEventToUi("thinking", new
            {
                sessionId,
                state = "done",
                durationSeconds = Math.Round((DateTime.UtcNow - turnStarted).TotalSeconds, 1),
                thinkingTokens = 0,
                timestamp = Now(),
            });

            PostEventToUi("session_status", new { sessionId, state = "idle", timestamp = Now() });
        }
        catch (Exception ex)
        {
            // A failed turn must not leave the thought node spinning forever.
            PostEventToUi("thinking", new
            {
                sessionId,
                state = "done",
                durationSeconds = Math.Round((DateTime.UtcNow - turnStarted).TotalSeconds, 1),
                thinkingTokens = 0,
                timestamp = Now(),
            });

            PostEventToUi("session_status", new
            {
                sessionId,
                state = "error",
                error = ex.Message,
                timestamp = Now(),
            });
        }
        finally
        {
            _zaiRuns.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    private bool CancelZai(string sessionId)
    {
        if (!_zaiRuns.TryGetValue(sessionId, out var cts)) return false;
        try
        {
            cts.Cancel();
            Services.Zai.ZaiEngine.Instance.Stop();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// One Bring-Your-Own-Key turn through the Claude Code CLI.
    ///
    /// The CLI owns the tool loop, the permission model and the thinking stream, so this only
    /// translates its JSONL into the same events every other engine posts: a Thought node, one
    /// timeline row per tool call, then the answer.
    /// </summary>
    private async Task DispatchByokAsync(
        string sessionId,
        string prompt,
        string? conversationId,
        string? cwd,
        bool skipPermissions,
        string? model)
    {
        static string Now() => DateTime.UtcNow.ToString("O");

        var provider = AiConfigService.Instance.GetActiveClaudeGatewayProvider();

        PostEventToUi("session_status", new { sessionId, state = "running", timestamp = Now() });

        // Opened with no text and closed on every path out, so a turn that dies still settles the row.
        PostEventToUi("thinking", new
        {
            sessionId,
            state = "running",
            text = string.Empty,
            timestamp = Now(),
        });

        var turnStarted = DateTime.UtcNow;

        var progress = new Progress<string>(text =>
            PostEventToUi("chat_token", new { sessionId, text, timestamp = Now() }));

        void OnThinking(string text)
        {
            PostEventToUi("thinking", new
            {
                sessionId,
                state = "running",
                text,
                thinkingTokens = 0,
                timestamp = Now(),
            });
        }

        void OnTool(Services.Byok.ByokToolEvent evt)
        {
            if (evt.IsStart)
            {
                var (label, kind) = DescribeByokTool(evt.Tool);

                PostEventToUi("tool_start", new
                {
                    sessionId,
                    callId = evt.CallId,
                    tool = label,
                    @params = new Dictionary<string, object?>
                    {
                        ["toolKind"] = kind,
                        ["path"] = null,
                        ["action"] = evt.Tool,
                        ["commandLine"] = kind == "command" ? evt.Detail : null,
                        ["detail"] = evt.Detail,
                    },
                    timestamp = Now(),
                });
            }
            else
            {
                PostEventToUi("tool_output", new
                {
                    sessionId,
                    callId = evt.CallId,
                    tool = string.Empty,
                    success = evt.Success,
                    result = evt.Output,
                    error = (string?)null,
                    durationMs = 0,
                    timestamp = Now(),
                });
            }
        }

        void CloseThinking() => PostEventToUi("thinking", new
        {
            sessionId,
            state = "done",
            durationSeconds = Math.Round((DateTime.UtcNow - turnStarted).TotalSeconds, 1),
            thinkingTokens = 0,
            timestamp = Now(),
        });

        try
        {
            var turn = await Services.Byok.ByokEngine.Instance
                .SendAsync(
                    prompt,
                    string.IsNullOrWhiteSpace(cwd)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                        : cwd,
                    provider,
                    conversationId,
                    model,
                    skipPermissions,
                    progress,
                    OnThinking,
                    OnTool)
                .ConfigureAwait(false);

            CloseThinking();

            // The CLI's own session id continues the conversation on the next turn.
            if (!string.IsNullOrWhiteSpace(turn.SessionId) && !string.Equals(turn.SessionId, conversationId, StringComparison.Ordinal))
            {
                PostEventToUi("conversation_started", new
                {
                    sessionId,
                    conversationId = turn.SessionId,
                    timestamp = Now(),
                });
            }

            PostEventToUi("chat_final", new { sessionId, text = turn.Answer, timestamp = Now() });
            PostEventToUi("session_status", new { sessionId, state = "idle", timestamp = Now() });
        }
        catch (Exception ex)
        {
            CloseThinking();

            // A Stop is not a failure: killing the child looks like a non-zero exit, and painting
            // that red after the user asked for it would be a lie about what happened.
            if (Services.Byok.ByokEngine.Instance.Stopped)
            {
                PostEventToUi("session_status", new { sessionId, state = "idle", timestamp = Now() });
                return;
            }

            PostEventToUi("chat_final", new
            {
                sessionId,
                text = $"{provider.Name} could not answer: {ex.Message}",
                timestamp = Now(),
            });

            PostEventToUi("session_status", new
            {
                sessionId,
                state = "error",
                error = ex.Message,
                timestamp = Now(),
            });
        }
    }

    /// <summary>
    /// Claude Code names its own tools (Bash, Read, Write, Edit, Grep, WebFetch). The timeline reads
    /// kinds, not names, so this is the only translation it needs.
    /// </summary>
    private static (string Label, string Kind) DescribeByokTool(string tool) => tool switch
    {
        "Bash" or "BashOutput" or "KillShell" => ("Ran", "command"),
        "Read" or "NotebookRead" => ("Read", "read"),
        "Write" => ("Wrote", "write"),
        "Edit" or "MultiEdit" or "NotebookEdit" => ("Edited", "write"),
        "Glob" or "Grep" or "LS" => ("Searched", "search"),
        "WebFetch" or "WebSearch" => ("Fetched", "other"),
        "Task" or "Agent" => ("Delegated", "other"),
        "TodoWrite" => ("Planned", "other"),
        _ => (tool, "other"),
    };

    private async Task HandleDeepseekStatusAsync(string? requestId)
    {
        bool signedIn = await Services.DeepSeek.DeepSeekEngine.Instance.ProbeSignedInAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { signedIn });
    }

    private async Task HandleDeepseekLoginAsync(string? requestId)
    {
        await Services.DeepSeek.DeepSeekEngine.Instance.ShowLoginWindowAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { opened = true });
    }

    private Task HandleDeepseekAccountsAsync(string? requestId)
    {
        {
            var accounts = Services.DeepSeek.DeepSeekAccounts.All()
                .Select(a => new { id = a.Id, label = a.Label })
                .ToList();
            SendResponse(requestId, true, new { active = Services.DeepSeek.DeepSeekAccounts.ActiveId, accounts });
        }

        return Task.CompletedTask;
    }

    private async Task HandleDeepseekAddAccountAsync(string? requestId)
    {
        var added = Services.DeepSeek.DeepSeekAccounts.Add();
        await Services.DeepSeek.DeepSeekEngine.Instance.SwitchAccountAsync(added.Id).ConfigureAwait(false);
        await Services.DeepSeek.DeepSeekEngine.Instance.ShowLoginWindowAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { id = added.Id, label = added.Label });
    }

    private async Task HandleDeepseekSwitchAccountAsync(JsonElement data, string? requestId)
    {
        string id = data.TryGetProperty("id", out var idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
        bool switched = await Services.DeepSeek.DeepSeekEngine.Instance.SwitchAccountAsync(id).ConfigureAwait(false);
        SendResponse(requestId, true, new { switched });
    }

    private Task HandleDeepseekRenameAccountAsync(JsonElement data, string? requestId)
    {
        {
            string id = data.TryGetProperty("id", out var idElem3) ? idElem3.GetString() ?? string.Empty : string.Empty;
            string label = data.TryGetProperty("label", out var labelElem) ? labelElem.GetString() ?? string.Empty : string.Empty;
            bool renamed = Services.DeepSeek.DeepSeekAccounts.Rename(id, label);
            SendResponse(requestId, true, new { renamed });
        }

        return Task.CompletedTask;
    }

    private async Task HandleDeepseekRemoveAccountAsync(JsonElement data, string? requestId)
    {
        string id = data.TryGetProperty("id", out var idElem2) ? idElem2.GetString() ?? string.Empty : string.Empty;
        bool removed = await Services.DeepSeek.DeepSeekEngine.Instance
            .RemoveAccountAsync(id)
            .ConfigureAwait(false);
        SendResponse(requestId, true, new { removed });
    }

    private async Task HandleDeepseekChatsAsync(string? requestId)
    {
        var chats = await Services.DeepSeek.DeepSeekEngine.Instance.ListChatsAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new
        {
            chats = chats.Take(25).Select(c => new { id = c.Id, title = c.Title }).ToList(),
        });
    }

    private Task HandleDeepseekApproveAsync(JsonElement data, string? requestId)
    {
        {
            string callId = data.TryGetProperty("callId", out var cElem) ? cElem.GetString() ?? string.Empty : string.Empty;
            bool approved = data.TryGetProperty("approved", out var aElem) && aElem.ValueKind is JsonValueKind.True or JsonValueKind.False
                && aElem.GetBoolean();

            bool resolved = _deepSeekApprovals.TryGetValue(callId, out var pending) && pending.TrySetResult(approved);
            SendResponse(requestId, true, new { resolved });
        }

        return Task.CompletedTask;
    }

    private Task HandleDeepseekOpenChatAsync(JsonElement data, string? requestId)
    {
        {
            // The webview has no NewWindowRequested handler, so an anchor with target=_blank
            // would open nothing. The host opens the default browser instead.
            string chatId = data.TryGetProperty("conversationId", out var cElem) ? cElem.GetString() ?? string.Empty : string.Empty;

            // No id means "the chat list", which is the cleanup helper: we cannot delete a
            // web chat through any API, so the honest tool is a link to where they live.
            if (chatId.Length == 0)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "https://chat.deepseek.com/") { UseShellExecute = true });
                    SendResponse(requestId, true, new { opened = true });
                }
                catch (Exception)
                {
                    SendResponse(requestId, true, new { opened = false });
                }
                return Task.CompletedTask;
            }

            // Same test as ^[0-9a-fA-F-]{20,}$ without a regex: a chat id is hex and dashes.
            bool valid = chatId.Length >= 20 && chatId.All(c => char.IsAsciiHexDigit(c) || c == '-');

            if (valid)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        $"https://chat.deepseek.com/a/chat/s/{chatId}") { UseShellExecute = true });
                }
                catch (Exception)
                {
                    valid = false;
                }
            }

            SendResponse(requestId, true, new { opened = valid });
        }

        return Task.CompletedTask;
    }

    private async Task HandleDeepseekOpenAsync(string? requestId)
    {
        await Services.DeepSeek.DeepSeekEngine.Instance.ShowWindowAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { opened = true });
    }

    private Task HandleDeepseekCancelAsync(JsonElement data, string? requestId)
    {
        {
            string sessionId = data.TryGetProperty("sessionId", out var cElem) ? cElem.GetString() ?? string.Empty : string.Empty;
            bool cancelled = CancelDeepSeek(sessionId);
            SendResponse(requestId, true, new { cancelled });
        }

        return Task.CompletedTask;
    }

    private async Task HandleDeepseekSignoutAsync(string? requestId)
    {
        await Services.DeepSeek.DeepSeekEngine.Instance.SignOutAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { signedOut = true });
    }

#endif
    private Task HandleVyceStopAsync(string? requestId)
    {
        {
            Services.Vyce.VyceEngine.Instance.Stop();
            SendResponse(requestId, true, new { stopped = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleVyceModelCurrentAsync(string? requestId)
    {
        // Vyce keeps its model list inside its own TUI, so both of these drive that menu. The
        // names come back from the CLI, never from a copy here.
        {
            // A label, not a menu: this must never start a console.
            SendResponse(requestId, true, new
            {
                model = Services.Vyce.VyceEngine.Instance.CurrentModel(),
            });
        }

        return Task.CompletedTask;
    }

    private async Task HandleOpencodeModelsAsync(string? requestId)
    {
        // Off the UI thread: this runs the CLI and can take a couple of seconds cold.
        try
        {
            var openModels = await Task.Run(() => Services.OpenCode.OpenCodeEngine.Instance.ListModelsAsync())
                .ConfigureAwait(false);

            // Sent along so the panel can show which model a fresh chat will use.
            string? openDefault = await Services.OpenCode.OpenCodeEngine.Instance.DefaultModelAsync()
                .ConfigureAwait(false);

            SendResponse(requestId, true, new { models = openModels, defaultModel = openDefault });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, $"OpenCode did not list its models: {ex.Message}");
        }
    }

    private async Task HandleOpencodeAuthAsync(string? requestId)
    {
        // Who is signed in, and any sign-in waiting for approval. The panel polls this while a
        // login is open, so the answer is cached and a finished login is what forces a re-read.
        {
            try
            {
                var status = await Task.Run(() => Services.OpenCode.OpenCodeEngine.Instance.GetStatusAsync())
                    .ConfigureAwait(false);

                object? login = status.Login is { } pending
                    ? new
                    {
                        code = pending.Code,
                        url = pending.Url,
                        running = pending.Running,
                        succeeded = pending.Succeeded,
                    }
                    : null;

                SendResponse(requestId, true, new
                {
                    signedIn = status.Auth.SignedIn,
                    label = status.Auth.Label,
                    method = status.Auth.Method,
                    accounts = status.Auth.Accounts,
                    login,
                });
            }
            catch (Exception ex)
            {
                SendResponse(requestId, false, $"OpenCode auth could not be read: {ex.Message}");
            }
        }
    }

    private async Task HandleOpencodeLoginAsync(string? requestId)
    {
        // The CLI's own device flow, so the user signs in the way `opencode auth login` does.
        {
            try
            {
                var started = await Task.Run(() => Services.OpenCode.OpenCodeEngine.Instance.BeginLoginAsync())
                    .ConfigureAwait(false);

                SendResponse(requestId, true, new { code = started.Code, url = started.Url });
            }
            catch (Exception ex)
            {
                SendResponse(requestId, false, $"OpenCode sign-in could not start: {ex.Message}");
            }
        }
    }

    private Task HandleOpencodeLoginCancelAsync(string? requestId)
    {
        {
            Services.OpenCode.OpenCodeEngine.Instance.CancelLogin();
            SendResponse(requestId, true, new { cancelled = true });
        }

        return Task.CompletedTask;
    }

    private async Task HandleVyceModelsAsync(JsonElement data, string? requestId)
    {
        string? modelsCwd = data.TryGetProperty("workingDirectory", out var vcElem) ? vcElem.GetString() : null;

        try
        {
            // Off the UI thread: this starts a console and waits for its menu, and the web
            // message handler runs on the window's thread.
            var list = await Task.Run(() => Services.Vyce.VyceEngine.Instance
                .ListModelsAsync(modelsCwd))
                .ConfigureAwait(false);

            SendResponse(requestId, true, new
            {
                current = list.Current,
                models = list.Models.Select(row => new
                {
                    name = row.Name,
                    note = row.Note,
                    available = row.Available,
                }),
            });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, $"Vyce did not answer its model menu: {ex.Message}");
        }
    }

    private async Task HandleVyceModelAsync(JsonElement data, string? requestId)
    {
        string model = data.TryGetProperty("model", out var vmElem) ? vmElem.GetString() ?? string.Empty : string.Empty;
        string? modelCwd = data.TryGetProperty("workingDirectory", out var mcElem) ? mcElem.GetString() : null;

        if (model.Length == 0)
        {
            SendResponse(requestId, false, "model is required");
            return;
        }

        try
        {
            bool switched = await Task.Run(() => Services.Vyce.VyceEngine.Instance
                .SetModelAsync(model, modelCwd))
                .ConfigureAwait(false);

            SendResponse(requestId, true, new { model, switched });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, $"Could not switch Vyce's model: {ex.Message}");
        }
    }

    private Task HandleGetOpencodeStateAsync(string? requestId)
    {
        SendResponse(requestId, true, GetOpenCodeState());

        return Task.CompletedTask;
    }

    private Task HandleFocusOpencodeSessionAsync(JsonElement data, string? requestId)
    {
        {
            // The payload may still carry a session id; the implementation never used it.
            string? sDir = data.TryGetProperty("directory", out var dEl) ? dEl.GetString() : (data.TryGetProperty("path", out var pEl) ? pEl.GetString() : null);
            var ok = FocusOpenCodeSession(sDir);
            SendResponse(requestId, ok, new { success = ok });
        }

        return Task.CompletedTask;
    }

    private async Task HandleRefreshOpencodeSidecarAsync(string? requestId)
    {
        var freshState = await RefreshOpenCodeSidecarAsync();
        SendResponse(requestId, true, freshState);
    }

    private async Task HandleStartOpencodeServiceAsync(string? requestId)
    {
        if (_openCodeWatcher != null)
        {
            var (ok, msg) = await _openCodeWatcher.StartOpenCodeServiceAsync();
            EmitLog("OpenCode", ok ? "info" : "error", msg);
            SendResponse(requestId, ok, new { success = ok, message = msg, state = _openCodeWatcher.GetStateDto() });
        }
        else
        {
            SendResponse(requestId, false, "OpenCode watcher service not initialized");
        }
    }

    private async Task HandleStopOpencodeServiceAsync(string? requestId)
    {
        if (_openCodeWatcher != null)
        {
            var (ok, msg) = await _openCodeWatcher.StopOpenCodeServiceAsync();
            EmitLog("OpenCode", "info", msg);
            SendResponse(requestId, ok, new { success = ok, message = msg, state = _openCodeWatcher.GetStateDto() });
        }
        else
        {
            SendResponse(requestId, false, "OpenCode watcher service not initialized");
        }
    }

    private async Task HandleRecheckOpencodeServiceAsync(string? requestId)
    {
        if (_openCodeWatcher != null)
        {
            var freshState = await _openCodeWatcher.RefreshAsync();
            EmitLog("OpenCode", "info", $"Rechecked service status: {(freshState.IsOnline ? "Online (Port " + freshState.Port + ")" : "Offline")}");
            SendResponse(requestId, true, freshState);
        }
        else
        {
            SendResponse(requestId, false, "OpenCode watcher service not initialized");
        }
    }

    private async Task HandleReplyOpencodePromptAsync(JsonElement data, string? requestId)
    {
        if (_openCodeWatcher != null)
        {
            string pId = data.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
            string pType = data.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "permission" : "permission";
            bool pApprove = data.TryGetProperty("approved", out var appProp) && appProp.GetBoolean();
            string? pAnswer = data.TryGetProperty("answer", out var ansProp) ? ansProp.GetString() : null;

            var ok = await _openCodeWatcher.ReplyPromptAsync(pId, pType, pApprove, pAnswer);
            EmitLog("OpenCode", ok ? "info" : "error", $"Replied to prompt {pId}: {(pApprove ? "Approved" : "Rejected")} (success: {ok})");
            SendResponse(requestId, ok, new { success = ok });
        }
        else
        {
            SendResponse(requestId, false, "OpenCode watcher service not initialized");
        }
    }

#if DEEPSEEK_UI
    /// <summary>
    /// Runs one DeepSeek turn and reports it in the vocabulary the chat already understands.
    ///
    /// Deltas are forwarded as they arrive: reasoning becomes `thinking` chunks (which is what the
    /// timeline renders as a Thought node) and answer text becomes `chat_token` chunks. ChatId is
    /// emitted as `conversation_started` so the next turn continues the same DeepSeek chat.
    /// </summary>
    /// <summary>
    /// Writes the payload's data URLs to temp files the page can be given.
    ///
    /// The store keeps attachments as data URLs and no path, and a file input needs a path, so the
    /// bytes have to land on disk first. The caller deletes them when the turn ends.
    /// </summary>
    /// <summary>
    /// Draws an OpenCode tool call the way the other engines draw theirs.
    ///
    /// The CLI reports a tool once it has finished, so the row is opened and settled in the same
    /// breath. Kinds match the ones the transcript already colours: commands, file work, and a
    /// plain custom row for anything else.
    /// </summary>
    private void PostOpenCodeTool(string sessionId, Services.OpenCode.OpenCodeToolEvent tool)
    {
        string name = tool.Tool.ToLowerInvariant();

        // The store classifies by `toolKind`, so these have to be the store's own words: a write is
        // an edit row, a grep is a search row, and only a genuine read is a read.
        string kind = name switch
        {
            "bash" or "shell" or "execute" => "command",
            "write" or "edit" or "patch" or "multiedit" or "create" => "write",
            "grep" or "glob" or "search" or "find" => "search",
            "read" or "list" or "view" or "cat" => "read",
            _ => "other",
        };

        string path = string.Empty;
        string command = string.Empty;

        try
        {
            using var input = JsonDocument.Parse(string.IsNullOrWhiteSpace(tool.Input) ? "{}" : tool.Input);

            if (input.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "filePath", "path", "filename", "command" })
                {
                    if (!input.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;

                    if (key == "command") command = value.GetString() ?? string.Empty;
                    else if (path.Length == 0) path = value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // A tool whose input is not JSON still gets its row; the path is just empty.
        }

        string label = ToolLabel(name);

        PostEventToUi("tool_start", new
        {
            sessionId,
            callId = tool.CallId,
            tool = label,
            @params = new Dictionary<string, object?>
            {
                ["toolKind"] = kind,
                ["path"] = path,
                ["commandLine"] = command.Length > 0 ? command : null,
            },
            timestamp = DateTime.UtcNow.ToString("O"),
        });

        PostEventToUi("tool_output", new
        {
            sessionId,
            callId = tool.CallId,
            tool = label,
            success = tool.Success,
            result = tool.Output,
            error = tool.Success ? null : tool.Output,
            durationMs = 0,
            timestamp = DateTime.UtcNow.ToString("O"),
        });
    }
#endif

#if DEEPSEEK_UI
    private async Task DispatchDeepSeekAsync(string sessionId, string prompt, string? chatId, string? cwd, bool skipPermissions, string? profile, bool toolsAllowed, List<string> attachmentFiles, bool deepThink, bool webSearch)
    {
        var cts = new CancellationTokenSource();
        _deepSeekRuns[sessionId] = cts;

        // MCP servers live in the profile's VirtualHome, so the registry has to be told which
        // profile this turn belongs to before the guide is built.
        try
        {
            Services.DeepSeek.McpRegistry.UseProfileHome(
                string.IsNullOrWhiteSpace(profile) ? null : AntigravityProfileService.GetVirtualHomeDirectory(profile));
        }
        catch (Exception ex)
        {
            PostEventToUi("session_status", new
            {
                sessionId,
                state = "running",
                error = $"MCP profile home could not be resolved: {ex.Message}",
                timestamp = DateTime.UtcNow.ToString("O"),
            });
        }

        static string Now() => DateTime.UtcNow.ToString("O");

        PostEventToUi("session_status", new { sessionId, state = "running", timestamp = Now() });

        var progress = new Progress<Services.DeepSeek.DeepSeekEngine.DeepSeekDelta>(delta =>
        {
            if (!string.IsNullOrEmpty(delta.Reasoning))
            {
                PostEventToUi("thinking", new
                {
                    sessionId,
                    state = "running",
                    text = delta.Reasoning,
                    thinkingTokens = 0,
                    timestamp = Now(),
                });
            }

            if (!string.IsNullOrEmpty(delta.Answer))
            {
                PostEventToUi("chat_token", new { sessionId, text = delta.Answer, timestamp = Now() });
            }
        });

        // File steps surface as the same timeline nodes the CLI engine produces. `toolKind` is what
        // classifies them, so the node reads "Wrote hi.py" instead of a generic row.
        var toolProgress = new Progress<Services.DeepSeek.DeepSeekEngine.DeepSeekToolStep>(step =>
        {
            var (label, kind) = Services.DeepSeek.DeepSeekTools.Describe(step.Action);

            if (step.IsStart)
            {
                PostEventToUi("tool_start", new
                {
                    sessionId,
                    callId = step.CallId,
                    tool = label,
                    // A `Ran` node reads its command from params, so the command line has to travel
                    // there or the step shows the word "Ran" and nothing else.
                    @params = new Dictionary<string, object?>
                    {
                        ["toolKind"] = kind,
                        ["path"] = step.Path,
                        ["action"] = step.Action,
                        ["commandLine"] = step.Action == "run_command" ? step.Detail : null,
                    },
                    timestamp = Now(),
                });
                return;
            }

            PostEventToUi("tool_output", new
            {
                sessionId,
                callId = step.CallId,
                tool = label,
                success = step.Success,
                result = step.Summary,
                error = step.Error,
                durationMs = step.DurationMs,
                timestamp = Now(),
            });
        });

        // Full access is the same per-profile flag the CLI engine uses, so the two agree and the
        // terminal tab's checkbox reflects it. Off means every destructive call asks first.
        Services.DeepSeek.DeepSeekEngine.Instance.ApprovalRequested = skipPermissions
            ? (_, _, _) => Task.FromResult(true)
            : async (callId, action, preview) =>
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deepSeekApprovals[callId] = pending;

            PostEventToUi("deepseek_approval_request", new
            {
                sessionId,
                callId,
                action,
                preview,
                timestamp = Now(),
            });

            using var registration = cts.Token.Register(() => pending.TrySetResult(false));
            bool approved = await pending.Task.ConfigureAwait(false);
            _deepSeekApprovals.TryRemove(callId, out _);
            return approved;
        };

        try
        {
            // The id is announced the moment it exists, not when the turn ends, so a stop or a crash
            // no longer costs the conversation and forces a fresh web chat next time.
            string? knownChat = chatId;

            var reply = await Services.DeepSeek.DeepSeekEngine.Instance
                .SendAsync(prompt, chatId, cwd, progress, toolProgress, id =>
                {
                    if (string.IsNullOrWhiteSpace(id) || string.Equals(id, knownChat, StringComparison.Ordinal)) return;
                    knownChat = id;
                    PostEventToUi("conversation_started", new
                    {
                        sessionId,
                        conversationId = id,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });
                }, toolsAllowed, attachmentFiles, deepThink, webSearch, cts.Token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(reply.ChatId) && !string.Equals(reply.ChatId, knownChat, StringComparison.Ordinal))
            {
                PostEventToUi("conversation_started", new
                {
                    sessionId,
                    conversationId = reply.ChatId,
                    timestamp = Now(),
                });
            }

            // Closing the thought node, with the real reasoning duration from the page.
            PostEventToUi("thinking", new
            {
                sessionId,
                state = "done",
                durationSeconds = reply.ReasoningSeconds ?? 0,
                thinkingTokens = 0,
                timestamp = Now(),
            });

            // The web app titles the conversation itself; take that when our session is unnamed.
            try
            {
                var chats = await Services.DeepSeek.DeepSeekEngine.Instance.ListChatsAsync().ConfigureAwait(false);
                var match = chats.FirstOrDefault(c => string.Equals(c.Id, reply.ChatId, StringComparison.Ordinal));

                // The web app names a chat after its first message, and ours is the protocol guide,
                // so the title it picks is "Protocol acknowledged". Adopting that made every session
                // in the sidebar read the same, which is worse than leaving our own name in place.
                bool isProtocolTitle = System.Text.RegularExpressions.Regex.IsMatch(
                    match.Title ?? string.Empty,
                    "acknowledg|protocol|understand|tool protocol",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!string.IsNullOrWhiteSpace(match.Title) && !isProtocolTitle)
                {
                    PostEventToUi("session_title", new { sessionId, title = match.Title, timestamp = Now() });
                }
            }
            catch (Exception)
            {
                // A title is a nicety; never let it fail the turn.
            }

            PostEventToUi("session_status", new { sessionId, state = "idle", timestamp = Now() });
        }
        catch (OperationCanceledException)
        {
            PostEventToUi("session_status", new { sessionId, state = "idle", timestamp = Now() });
        }
        catch (Exception ex)
        {
            PostEventToUi("session_status", new
            {
                sessionId,
                state = "error",
                error = ex.Message,
                timestamp = Now(),
            });
        }
        finally
        {
            _deepSeekRuns.TryRemove(sessionId, out _);
            cts.Dispose();

            foreach (var file in attachmentFiles)
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }
#endif

#if DEEPSEEK_UI
    /// <summary>Stops a running DeepSeek turn. The sidecar's abort path never sees these turns.</summary>
    private bool CancelDeepSeek(string sessionId)
    {
        if (!_deepSeekRuns.TryGetValue(sessionId, out var cts)) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
#endif

    private void PostOpenCodeUpdate(OpenCodeStateDto stateDto)
    {
        try
        {
            var envelope = JsonSerializer.Serialize(new
            {
                type = "OPENCODE_UPDATE",
                data = stateDto,
                @event = "opencode_status_changed",
                payload = stateDto
            }, CamelCaseJsonOptions);

            SendJsonMessageToClients(envelope);
        }
        catch { }
    }

    public OpenCodeStateDto GetOpenCodeState()
    {
        return _openCodeWatcher?.GetStateDto() ?? new OpenCodeStateDto();
    }

    public static bool FocusOpenCodeSession(string? directory)
    {
        return OpenCodeWatcherService.FocusOpenCodeSession(directory);
    }

    public async Task<OpenCodeStateDto> RefreshOpenCodeSidecarAsync()
    {
        if (_openCodeWatcher != null)
        {
            return await _openCodeWatcher.RefreshAsync();
        }
        return new OpenCodeStateDto();
    }

}