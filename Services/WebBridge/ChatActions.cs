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

    private async Task HandleSendChatAsync(JsonElement data, string? requestId)
    {
        string sessionId = data.TryGetProperty("sessionId", out var sElem) ? sElem.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        string prompt = data.TryGetProperty("prompt", out var pElem) ? pElem.GetString() ?? string.Empty : string.Empty;
        string? model = data.TryGetProperty("model", out var mElem) ? mElem.GetString() : null;
        string? profile = data.TryGetProperty("profile", out var prElem) ? prElem.GetString() : null;
        string? cwd = data.TryGetProperty("workingDirectory", out var wdElem) ? wdElem.GetString() : null;
        string? effort = data.TryGetProperty("reasoningEffort", out var efElem) ? efElem.GetString() : null;

        string activeFallback = _cliManager.ProfileService.ActiveProfile ?? "default";
        if (string.IsNullOrWhiteSpace(profile) || string.Equals(profile, "default", StringComparison.OrdinalIgnoreCase))
        {
            profile = activeFallback;
        }

        // The toggle is persisted per profile, so an omitted flag falls back to the
        // stored preference instead of silently granting full access.
        bool skipPermissions = data.TryGetProperty("skipPermissions", out var spElem) && spElem.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? spElem.GetBoolean()
            : _cliManager.ProfileService.GetProfileSkipPermissions(profile);

        // The session's own conversation, so a chat never answers with another chat's history.
        string? conversationId = data.TryGetProperty("conversationId", out var cvElem) ? cvElem.GetString() : null;

#if DEEPSEEK_UI
        // A DeepSeek session never touches agy: its turn runs in our own WebView2 and
        // reports through the same events, so the chat UI needs no special case.
        string? engine = data.TryGetProperty("engine", out var enElem) ? enElem.GetString() : null;

        // Vyce has no headless mode, so its turn runs in a hidden pseudo-console and the
        // answer is read from the CLI's own session file. Nothing here opens a window.
        if (string.Equals(engine, "vyce", StringComparison.OrdinalIgnoreCase))
        {
            string vyceRoot = string.IsNullOrWhiteSpace(cwd)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : cwd;

            PostEventToUi("session_status", new { sessionId, state = "running", timestamp = DateTime.UtcNow.ToString("O") });

            var vyceProgress = new Progress<string>(text =>
                PostEventToUi("chat_token", new { sessionId, text, timestamp = DateTime.UtcNow.ToString("O") }));

            // Vyce publishes no event stream: its TUI draws reasoning and tool calls on a screen the
            // app never reads, so "the turn is under way" is the only signal available. The row is
            // live while it runs and is pruned on settle if nothing streamed into it.
            PostEventToUi("thinking", new
            {
                sessionId,
                text = string.Empty,
                timestamp = DateTime.UtcNow.ToString("O"),
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var reply = await Services.Vyce.VyceEngine.Instance
                        .SendAsync(prompt, vyceRoot, vyceProgress)
                        .ConfigureAwait(false);

                    PostEventToUi("chat_final", new
                    {
                        sessionId,
                        text = reply.Text,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    PostEventToUi("thinking", new
                    {
                        sessionId,
                        text = string.Empty,
                        state = "done",
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    PostEventToUi("session_status", new
                    {
                        sessionId,
                        state = "idle",
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });
                }
                catch (Exception ex)
                {
                    PostEventToUi("chat_final", new
                    {
                        sessionId,
                        text = $"Vyce could not answer: {ex.Message}",
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    // A failed turn must not leave a Thought row spinning.
                    PostEventToUi("thinking", new
                    {
                        sessionId,
                        text = string.Empty,
                        state = "done",
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    PostEventToUi("session_status", new
                    {
                        sessionId,
                        state = "error",
                        error = ex.Message,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });
                }
            });

            SendResponse(requestId, true, new { started = true, engine = "vyce" });
            return;
        }

        // OpenCode needs none of that: it runs headless, prints one JSON event per line,
        // and every event carries the session id that continues the conversation.
        if (string.Equals(engine, "opencode", StringComparison.OrdinalIgnoreCase))
        {
            string openRoot = string.IsNullOrWhiteSpace(cwd)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : cwd;

            var openFiles = MaterialiseAttachments(data);

            // A headless run rejects every permission request it cannot ask about, so the
            // profile's full-access switch is what decides whether tools may run at all.
            bool openAuto = skipPermissions;

            // A turn with no `--model` uses the CLI's default, which here is routed through a
            // local proxy that rejects an unkeyed call. A free model is what always runs.
            string? openModel = string.IsNullOrWhiteSpace(model)
                ? await Services.OpenCode.OpenCodeEngine.Instance.DefaultModelAsync().ConfigureAwait(false)
                : model;

            PostEventToUi("session_status", new { sessionId, state = "running", timestamp = DateTime.UtcNow.ToString("O") });

            var openProgress = new Progress<string>(text =>
                PostEventToUi("chat_token", new { sessionId, text, timestamp = DateTime.UtcNow.ToString("O") }));

            // Reasoning parts are not the answer. Passing them as `thinking` is what gives the turn
            // a Thought row instead of either hiding the work or pasting it into the reply.
            IProgress<string> openThinking = new Progress<string>(text =>
                PostEventToUi("thinking", new { sessionId, text, timestamp = DateTime.UtcNow.ToString("O") }));

            _ = Task.Run(async () =>
            {
                try
                {
                    var turn = await Services.OpenCode.OpenCodeEngine.Instance
                        .SendAsync(
                            prompt,
                            openRoot,
                            conversationId,
                            openModel,
                            openAuto,
                            openFiles.Count > 0 ? openFiles : null,
                            openProgress,
                            tool => PostOpenCodeTool(sessionId, tool),
                            text => openThinking.Report(text))
                        .ConfigureAwait(false);

                    // Close the Thought row. A step that streamed no reasoning is pruned by the
                    // store on settle, so the transcript never keeps an empty one.
                    PostEventToUi("thinking", new
                    {
                        sessionId,
                        text = string.Empty,
                        state = "done",
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    // The CLI's own id, stored on the session, so the next turn continues
                    // this conversation instead of starting another one.
                    if (turn.SessionId is not null)
                    {
                        PostEventToUi("conversation_started", new
                        {
                            sessionId,
                            conversationId = turn.SessionId,
                            timestamp = DateTime.UtcNow.ToString("O"),
                        });
                    }

                    PostEventToUi("chat_final", new
                    {
                        sessionId,
                        text = turn.Text,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    PostEventToUi("session_status", new
                    {
                        sessionId,
                        state = "idle",
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });
                }
                catch (Exception ex)
                {
                    PostEventToUi("chat_final", new
                    {
                        sessionId,
                        text = $"OpenCode could not answer: {ex.Message}",
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    // A failed turn must not leave a Thought row spinning.
                    PostEventToUi("thinking", new
                    {
                        sessionId,
                        text = string.Empty,
                        state = "done",
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    PostEventToUi("session_status", new
                    {
                        sessionId,
                        state = "error",
                        error = ex.Message,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });
                }
            });

            SendResponse(requestId, true, new { started = true, engine = "opencode" });
            return;
        }
        if (string.Equals(engine, "zai", StringComparison.OrdinalIgnoreCase))
        {
            // Fire and forget for the same reason DeepSeek is: a turn can prime, run several tool
            // rounds and take minutes, and awaiting it here would trip the UI's request timeout.
            bool zaiTools = !data.TryGetProperty("toolsEnabled", out var ztElem) || ztElem.ValueKind != JsonValueKind.False;
            string? zaiModel = data.TryGetProperty("model", out var zmElem) ? zmElem.GetString() : null;

            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchZaiAsync(sessionId, prompt, conversationId, cwd, skipPermissions, profile, zaiTools, zaiModel)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PostEventToUi("session_status", new
                    {
                        sessionId,
                        state = "error",
                        error = ex.Message,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });
                }
            });

            SendResponse(requestId, true, new { sent = true, sessionId, engine = "zai" });
            return;
        }

        if (string.Equals(engine, "deepseek", StringComparison.OrdinalIgnoreCase))
        {
            // Fire and forget, exactly like the sidecar path. A DeepSeek turn can run for
            // minutes (priming, several tool rounds), and awaiting it here made the UI's
            // 25s request timeout fire while the turn was still going, painting a red
            // error into a chat that then answered normally.
            // Read the payload here, not inside the task. `data` is a JsonElement over a
            // document the handler disposes on return, so touching it from a task that
            // outlives the handler throws "Cannot access a disposed object".
            bool toolsAllowed = !data.TryGetProperty("toolsEnabled", out var teElem) || teElem.ValueKind != JsonValueKind.False;
            // DeepThink defaults on, Search defaults off: those are the two states a fresh
            // web chat starts in, so an older session keeps behaving as it did.
            bool deepThink = !data.TryGetProperty("deepThink", out var dtElem) || dtElem.ValueKind != JsonValueKind.False;
            bool webSearch = data.TryGetProperty("webSearch", out var wsElem) && wsElem.ValueKind == JsonValueKind.True;
            var attachmentFiles = MaterialiseAttachments(data);

            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchDeepSeekAsync(sessionId, prompt, conversationId, cwd, skipPermissions, profile, toolsAllowed, attachmentFiles, deepThink, webSearch).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PostEventToUi("session_status", new
                    {
                        sessionId,
                        state = "error",
                        error = ex.Message,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });
                }
            });

            SendResponse(requestId, true, new { sent = true, sessionId, engine = "deepseek" });
            return;
        }

        // Qwen: same contract as DeepSeek. Fire and forget, report through chat_token,
        // so the transcript needs no special case for a third engine.
        if (string.Equals(engine, "qwen", StringComparison.OrdinalIgnoreCase))
        {
            // Read from the payload here: `data` is disposed when the handler returns and
            // the task outlives it.
            string? qRoot = data.TryGetProperty("workingDirectory", out var qwd) ? qwd.GetString() : null;
            bool dataQwenTools = data.TryGetProperty("toolsEnabled", out var qte) && qte.ValueKind == JsonValueKind.False;
            bool qSkip = skipPermissions;

            // The same approval card DeepSeek uses, resolved by the same response action,
            // so a Qwen turn can write a file once the user approves it.
            Services.Qwen.QwenEngine.Instance.ApprovalRequested = qSkip
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
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    bool approved = await pending.Task.ConfigureAwait(false);
                    _deepSeekApprovals.TryRemove(callId, out _);
                    return approved;
                };

            // The same nodes DeepSeek draws, from the same event names, so a Qwen turn
            // shows Ran / Wrote / Explored rows instead of one bare paragraph.
            Services.Qwen.QwenEngine.Instance.ToolStarted = (callId, action, path) =>
            {
                var (label, kind) = Services.DeepSeek.DeepSeekTools.Describe(action);

                PostEventToUi("tool_start", new
                {
                    sessionId,
                    callId,
                    tool = label,
                    @params = new Dictionary<string, object?>
                    {
                        ["toolKind"] = kind,
                        ["path"] = path,
                        ["action"] = action,
                        ["commandLine"] = action == "run_command" ? path : null,
                    },
                    timestamp = DateTime.UtcNow.ToString("O"),
                });
            };

            Services.Qwen.QwenEngine.Instance.ToolFinished = (callId, action, success, summary) =>
            {
                var (label, _) = Services.DeepSeek.DeepSeekTools.Describe(action);

                PostEventToUi("tool_output", new
                {
                    sessionId,
                    callId,
                    tool = label,
                    success,
                    result = summary,
                    error = (string?)null,
                    durationMs = 0,
                    timestamp = DateTime.UtcNow.ToString("O"),
                });
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<string>(text =>
                        PostEventToUi("chat_token", new { sessionId, text, timestamp = DateTime.UtcNow.ToString("O") }));

                    // Qwen renders "Thinking completed" above the answer. Passing it on
                    // as `thinking` gives the transcript the same reasoning block DeepSeek
                    // shows, instead of hiding the work behind a spinner.
                    var thinking = new Progress<string>(text =>
                        PostEventToUi("thinking", new { sessionId, text, timestamp = DateTime.UtcNow.ToString("O") }));

                    bool qTools = !dataQwenTools;
                    var reply = await Services.Qwen.QwenEngine.Instance
                        .SendAsync(
                            prompt,
                            qRoot,
                            qTools,
                            qSkip,
                            progress,
                            thinking,
                            conversationId,
                            id => PostEventToUi("conversation_started", new
                            {
                                sessionId,
                                conversationId = id,
                                timestamp = DateTime.UtcNow.ToString("O"),
                            }))
                        .ConfigureAwait(false);

                    // Settle the transcript. Streaming appends, so whatever it showed
                    // mid-turn cannot be taken back; this replaces the answer with the
                    // text the turn actually produced.
                    PostEventToUi("chat_final", new
                    {
                        sessionId,
                        text = reply.Text,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });

                    if (reply.Text.Length == 0)
                    {
                        // Extraction found nothing. Say so with the selector list, rather
                        // than leaving a chat that looks like it is still thinking.
                        PostEventToUi("session_status", new
                        {
                            sessionId,
                            state = "error",
                            error = "Qwen answered but no text could be read from the page.",
                            timestamp = DateTime.UtcNow.ToString("O"),
                        });
                    }
                    else
                    {
                        // Without this the session stayed on "Streaming" forever and the
                        // composer refused every later message.
                        PostEventToUi("session_status", new
                        {
                            sessionId,
                            state = "idle",
                            timestamp = DateTime.UtcNow.ToString("O"),
                        });
                    }
                }
                catch (Exception ex)
                {
                    PostEventToUi("session_status", new
                    {
                        sessionId,
                        state = "error",
                        error = ex.Message,
                        timestamp = DateTime.UtcNow.ToString("O"),
                    });
                }
            });

            SendResponse(requestId, true, new { sent = true, sessionId, engine = "qwen" });
            return;
        }
#endif

        await Sidecar.SidecarHostService.Instance.SendChatAsync(sessionId, prompt, model, profile, cwd, effort, skipPermissions, conversationId).ConfigureAwait(false);
        SendResponse(requestId, true, new { sent = true, sessionId });
    }

    private async Task HandleGetModelCatalogAsync(JsonElement data, string? requestId)
    {
        // Model list for the composer's selector. The engine reports its own catalogue; nothing
        // here is a static table.
        {
            var activeWs = AiConfigService.Instance.GetActiveWorkspace();

            string? cli = data.TryGetProperty("cli", out var mcElem) ? mcElem.GetString() : null;
            if (string.IsNullOrWhiteSpace(cli)) cli = activeWs?.PrimaryCli;

            // The refresh button has to beat the catalog's own five-minute cache, not just the
            // renderer's, or pressing it looks like nothing happened.
            bool force = data.TryGetProperty("force", out var forceElem) && forceElem.ValueKind == JsonValueKind.True;
            if (force) ModelCatalogService.Instance.Invalidate();

            var catalog = await ModelCatalogService.Instance.GetCatalogAsync(cli).ConfigureAwait(false);
            SendResponse(requestId, true, catalog);
        }
    }

    private async Task HandleAbortTaskAsync(JsonElement data, string? requestId)
    {
        // Stops the in-flight turn. The sidecar aborts the child process and the session's
        // AbortController; the UI then settles its own state from the session_status event.
        {
            string? taskId = data.TryGetProperty("taskId", out var tElem) ? tElem.GetString() : null;
            if (string.IsNullOrWhiteSpace(taskId) && data.TryGetProperty("sessionId", out var sElem2))
            {
                taskId = sElem2.GetString();
            }

            if (string.IsNullOrWhiteSpace(taskId))
            {
                SendResponse(requestId, false, "taskId or sessionId is required");
                return;
            }

            string? reason = data.TryGetProperty("reason", out var rElem) ? rElem.GetString() : null;

#if DEEPSEEK_UI
            // The Stop button is one button for every engine, and it has always spoken to the
            // sidecar. The web engines run inside this process, so they are stopped here as
            // well; each call is a no-op when that engine has no turn in flight.
            Services.DeepSeek.DeepSeekEngine.Instance.Stop();

            // Z.ai runs on its own page; its stop cancels the read loop and the turn token.
            Services.Zai.ZaiEngine.Instance.Stop();

            // Qwen's stop has to click its page on the UI thread, so it is fired and forgotten
            // rather than awaited: the abort response should not wait on a click.
            _ = Services.Qwen.QwenEngine.Instance.StopAsync();

            // Vyce cancels with Esc in its hidden console, which is a write to that console's
            // input buffer and returns immediately.
            Services.Vyce.VyceEngine.Instance.Stop();

            // OpenCode is a process per turn; stopping means killing its tree.
            Services.OpenCode.OpenCodeEngine.Instance.Stop();
#endif
            await Sidecar.SidecarHostService.Instance.AbortTaskAsync(taskId, reason).ConfigureAwait(false);
            SendResponse(requestId, true, new { aborted = true, taskId });
        }
    }

    private async Task HandleGetSlashCommandsAsync(JsonElement data, string? requestId)
    {
        // Slash command discovery for the composer's palette. The CLI and directory come from the
        // request when the UI knows them, otherwise from the active workspace.
        {
            var activeWs = AiConfigService.Instance.GetActiveWorkspace();

            string? cli = data.TryGetProperty("cli", out var cElem) ? cElem.GetString() : null;
            if (string.IsNullOrWhiteSpace(cli)) cli = activeWs?.PrimaryCli;

            string? cwd = data.TryGetProperty("cwd", out var dElem) ? dElem.GetString() : null;
            if (string.IsNullOrWhiteSpace(cwd)) cwd = activeWs?.WorkingDirectory;

            var profiles = _cliManager.ProfileService.GetProfiles();
            var commands = await SlashCommandService.Instance.GetCommandsAsync(cli, cwd, profiles).ConfigureAwait(false);

            SendResponse(requestId, true, new { commands, engine = cli });
        }
    }

    private async Task HandleGetAgentSessionsAsync(JsonElement data, string? requestId)
    {
        string? wsDir = data.TryGetProperty("workspaceDir", out var wdProp) ? wdProp.GetString() : null;
        var sessions = await TranscriptInspectorService.Instance.GetSessionsAsync(wsDir).ConfigureAwait(false);
        SendResponse(requestId, true, new { sessions });
    }

    private async Task HandleGetSessionStepsAsync(JsonElement data, string? requestId)
    {
        string? transcriptPath = data.TryGetProperty("transcriptPath", out var tpProp) ? tpProp.GetString() : null;
        if (!string.IsNullOrEmpty(transcriptPath))
        {
            var steps = await TranscriptInspectorService.Instance.GetSessionStepsAsync(transcriptPath).ConfigureAwait(false);
            SendResponse(requestId, true, new { steps });
        }
        else
        {
            SendResponse(requestId, false, "Transcript path is required");
        }
    }

    private Task HandleDeleteAgentSessionAsync(JsonElement data, string? requestId)
    {
        {
            string? transcriptPath = data.TryGetProperty("transcriptPath", out var delTpProp) ? delTpProp.GetString() : null;
            if (!string.IsNullOrEmpty(transcriptPath))
            {
                bool deleted = TranscriptInspectorService.Instance.DeleteSession(transcriptPath);
                SendResponse(requestId, deleted, new { success = deleted });
            }
            else
            {
                SendResponse(requestId, false, "Transcript path is required");
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleInjectSessionContextAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rProp) ? rProp.GetString() : null;
        string prompt = data.TryGetProperty("prompt", out var pProp) ? pProp.GetString() ?? "" : "";

        if (!string.IsNullOrEmpty(repoPath))
        {
            var snippet = await AgentSessionMemoryService.Instance.GetContextInjectionSnippetAsync(repoPath, prompt).ConfigureAwait(false);
            SendResponse(requestId, true, new { snippet });
        }
        else
        {
            SendResponse(requestId, false, "repoPath is required");
        }
    }

    private async Task HandlePurgeIdleSessionsAsync(string? requestId)
    {
        if (_openCodeWatcher != null)
        {
            var deleted = await _openCodeWatcher.PurgeIdleSessionsAsync();
            SendResponse(requestId, true, new { deletedCount = deleted, success = true });
        }
        else
        {
            SendResponse(requestId, false, "OpenCode watcher service not initialized");
        }
    }

    private async Task HandleEnhancePromptAsync(JsonElement data, string? requestId)
    {
        var providerId = data.TryGetProperty("providerId", out var pId) ? pId.GetString() : null;
        var modelName = data.TryGetProperty("model", out var mName) ? mName.GetString() : null;
        var rawPrompt = data.TryGetProperty("prompt", out var rPrompt) ? rPrompt.GetString() : string.Empty;
        var style = data.TryGetProperty("style", out var sProp) ? sProp.GetString() : "General & Balanced";
        var systemPrompt = data.TryGetProperty("systemPrompt", out var spProp) ? spProp.GetString() : null;

        double? temperature = null;
        if (data.TryGetProperty("temperature", out var tProp) &&
            tProp.ValueKind == JsonValueKind.Number &&
            tProp.TryGetDouble(out var tVal))
        {
            temperature = tVal;
        }

        if (string.IsNullOrWhiteSpace(rawPrompt))
        {
            SendResponse(requestId, false, "Please enter a prompt to enhance.");
            return;
        }

        var config = AiConfigService.Instance.CurrentConfig;
        var provider = config.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            SendResponse(requestId, false, "Provider not found.");
            return;
        }

        _promptEnhanceCts?.Cancel();
        _promptEnhanceCts = new CancellationTokenSource();

        try
        {
            var enhanced = await PromptEnhancerService.Instance.EnhancePromptAsync(
                provider,
                modelName ?? provider.Models.FirstOrDefault() ?? "gpt-4o-mini",
                rawPrompt,
                style ?? "General & Balanced",
                systemPrompt,
                temperature,
                _promptEnhanceCts.Token
            );

            SendResponse(requestId, true, new { enhancedText = enhanced });
        }
        catch (OperationCanceledException)
        {
            SendResponse(requestId, false, "Enhancement cancelled.");
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, ex.Message);
        }
    }

    private async Task HandleFetchModelsAsync(JsonElement data, string? requestId)
    {
        var providerId = data.TryGetProperty("providerId", out var pId) ? pId.GetString() : null;
        var config = AiConfigService.Instance.CurrentConfig;
        var provider = config.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

        if (provider == null)
        {
            SendResponse(requestId, false, "Provider not found.");
            return;
        }

        try
        {
            var models = await PromptEnhancerService.Instance.FetchAvailableModelsAsync(provider);
            int addedCount = 0;
            foreach (var m in models)
            {
                if (!provider.Models.Contains(m, StringComparer.OrdinalIgnoreCase))
                {
                    provider.Models.Add(m);
                    addedCount++;
                }
            }
            AiConfigService.Instance.SaveConfig(config);
            SendResponse(requestId, true, new { models = provider.Models, total = models.Count, added = addedCount });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, ex.Message);
        }
    }

#if DEEPSEEK_UI
    /// <summary>A readable label for a tool id: `write` shows as Write, `bash` as Bash.</summary>
    private static string ToolLabel(string name)
    {
        return name.Length == 0
            ? "Tool"
            : char.ToUpperInvariant(name[0]) + name[1..];
    }
#endif

#if DEEPSEEK_UI
    private static List<string> MaterialiseAttachments(JsonElement data)
    {
        var files = new List<string>();
        if (!data.TryGetProperty("attachments", out var items) || items.ValueKind != JsonValueKind.Array) return files;

        foreach (var item in items.EnumerateArray())
        {
            string? dataUrl = item.TryGetProperty("dataUrl", out var du) ? du.GetString() : null;
            string name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "attachment" : "attachment";
            if (string.IsNullOrWhiteSpace(dataUrl)) continue;

            int comma = dataUrl.IndexOf(',');
            if (comma < 0) continue;

            try
            {
                var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
                string safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
                string temp = Path.Combine(Path.GetTempPath(), $"agentshub-attach-{Guid.NewGuid():N}-{safe}");
                File.WriteAllBytes(temp, bytes);
                files.Add(temp);
            }
            catch (FormatException)
            {
                // A malformed data URL is skipped; the turn still runs without it.
            }
        }

        return files;
    }
#endif

}