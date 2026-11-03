#nullable enable
using System.Diagnostics;
using System.Text.Json;
using AgentsHub.Services.Sidecar.DTOs;

namespace AgentsHub.Services.Sidecar;

/// <summary>
/// Centralized host service managing the Bun Sidecar process lifecycle,
/// automated watchdog guarding, WebSocket IPC communication, and real-time streaming event dispatch.
/// </summary>
public sealed class SidecarHostService : IDisposable
{
    private static readonly Lazy<SidecarHostService> _lazy = new(() => new SidecarHostService());
    public static SidecarHostService Instance => _lazy.Value;

    private readonly SidecarManager _manager;
    private readonly SidecarClient _client;
    private readonly Lock _lock = new();

    private bool _isInitialized;
    private bool _isDisposed;

    /// <summary>
    /// Event fired whenever an event arrives from the Sidecar to be forwarded to the WebView2 UI.
    /// EventName: e.g. "chat_token", "session_status", "account_state", "tool_output".
    /// Payload: The parsed event payload object.
    /// </summary>
    public event Action<string, object?>? EventDispatched;

    /// <summary>
    /// Status text representing the sidecar health state ("Online", "Offline", "Starting", "Error").
    /// </summary>
    public string StatusText { get; private set; } = "Offline";
    public bool IsOnline => _client.IsConnected;
    public int BoundPort => _manager.Port;
    public int ProcessId => _manager.ProcessId;
    public int LatencyMs => _client.LastPingLatencyMs;
    public int ActiveSessionsCount => _client.ActiveSessionsCount;
    public string Version => Helpers.AppInfo.DisplayVersion;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _activeProfiles = new(StringComparer.OrdinalIgnoreCase);

    public bool IsProfileActive(string profileName) =>
        !string.IsNullOrWhiteSpace(profileName) && _activeProfiles.TryGetValue(profileName, out var active) && active;

    public IEnumerable<string> GetActiveProfiles() => _activeProfiles.Keys;

    public object GetStatusDto() => new
    {
        status = StatusText,
        port = _manager.Port,
        isOnline = IsOnline,
        pid = _manager.ProcessId,
        latencyMs = _client.LastPingLatencyMs >= 0 ? _client.LastPingLatencyMs : 1,
        activeSessions = _client.ActiveSessionsCount,
        version = Version
    };

    private SidecarHostService()
    {
        _manager = new SidecarManager();
        _client = new SidecarClient(_manager);

        _manager.OnExited += HandleSidecarExited;
        _client.OnEventReceived += HandleClientEvent;
        _client.OnStatusChanged += (connected) =>
        {
            StatusText = connected ? "Online" : "Connecting";
            EventDispatched?.Invoke("sidecar_status_changed", GetStatusDto());
        };
        _client.OnPongReceived += (_, _) =>
        {
            EventDispatched?.Invoke("sidecar_status_changed", GetStatusDto());
        };
        _client.OnSidecarStatus += (_) =>
        {
            EventDispatched?.Invoke("sidecar_status_changed", GetStatusDto());
        };
    }

    /// <summary>
    /// Starts the sidecar process and connects WebSocket IPC in the background.
    /// Safe to call multiple times.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_isInitialized) return;
            _isInitialized = true;
        }

        try
        {
            StatusText = "Starting";
            // 1. Spawns child process with Win32 Job Object guard and negotiates dynamic port/token
            int port = await _manager.StartAsync(ct).ConfigureAwait(false);

            // 2. Connects ClientWebSocket with auto-reconnection and heartbeat
            await _client.ConnectAsync(ct).ConfigureAwait(false);

            StatusText = "Online";
            Debug.WriteLine($"[SidecarHostService] Sidecar fully online on port {port}.");

            // Query initial account state
            _ = ListAccountsAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Error";
            Debug.WriteLine($"[SidecarHostService] Failed to start sidecar: {ex.Message}");
            EventDispatched?.Invoke("sidecar_status_changed", new
            {
                status = "Error",
                error = ex.Message,
                isOnline = false
            });
        }
    }

    private void HandleSidecarExited()
    {
        StatusText = "Offline";
        EventDispatched?.Invoke("sidecar_status_changed", new
        {
            status = "Offline",
            isOnline = false
        });

        if (!_isDisposed)
        {
            // Auto-recovery: Re-initialize after brief backoff
            Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                if (!_isDisposed && !_manager.IsRunning)
                {
                    try
                    {
                        Debug.WriteLine("[SidecarHostService] Auto-recovering Sidecar process...");
                        await _manager.StartAsync().ConfigureAwait(false);
                        await _client.ConnectAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SidecarHostService] Auto-recovery failed: {ex.Message}");
                    }
                }
            });
        }
    }

    private void HandleClientEvent(BaseSidecarEvent evt)
    {
        switch (evt.Event)
        {
            case "chat_token":
                if (evt is ChatTokenEvent cte)
                {
                    EventDispatched?.Invoke("chat_token", new
                    {
                        sessionId = cte.SessionId,
                        profile = cte.Profile,
                        text = cte.Text,
                        timestamp = cte.Timestamp
                    });
                }
                break;

            case "thinking":
                if (evt is ThinkingEvent the)
                {
                    EventDispatched?.Invoke("thinking", new
                    {
                        sessionId = the.SessionId,
                        profile = the.Profile,
                        text = the.Text,
                        durationSeconds = the.DurationSeconds,
                        thinkingTokens = the.ThinkingTokens,
                        state = the.State,
                        timestamp = the.Timestamp
                    });
                }
                break;

            case "token_usage":
                if (evt is TokenUsageEvent tue)
                {
                    EventDispatched?.Invoke("token_usage", tue);
                }
                break;

            case "quota_update":
                if (evt is QuotaUpdateEvent que)
                {
                    EventDispatched?.Invoke("quota_update", que);
                }
                break;

            case "quota_check_completed":
                if (evt is QuotaCheckCompletedEvent qce)
                {
                    EventDispatched?.Invoke("quota_check_completed", new
                    {
                        profile = qce.Profile,
                        success = qce.Success,
                        quotaPercent = qce.QuotaPercent,
                        timestamp = qce.Timestamp
                    });
                }
                break;

            case "model_info":
                if (evt is ModelInfoEvent mie)
                {
                    EventDispatched?.Invoke("model_info", mie);
                }
                break;

            case "session_status":
                if (evt is SessionStatusEvent sse)
                {
                    EventDispatched?.Invoke("session_status", new
                    {
                        sessionId = sse.SessionId,
                        profile = sse.Profile,
                        state = sse.State,
                        error = sse.Error,
                        pid = sse.Pid,
                        model = sse.Model,
                        quotaPercent = sse.QuotaPercent,
                        tokens = sse.Tokens,
                        timestamp = sse.Timestamp
                    });
                }
                break;

            case "account_state":
                if (evt is AccountStateEvent ase)
                {
                    EventDispatched?.Invoke("account_state", new
                    {
                        activeAccount = ase.ActiveAccount,
                        provider = ase.Provider,
                        profiles = ase.Profiles,
                        timestamp = ase.Timestamp
                    });
                }
                break;

            case "stream_delta":
                if (evt is StreamDeltaEvent sde)
                {
                    EventDispatched?.Invoke("stream_delta", sde);
                }
                break;

            case "tool_start":
                if (evt is ToolStartEvent tse)
                {
                    EventDispatched?.Invoke("tool_start", tse);
                }
                break;

            case "tool_output":
                if (evt is ToolOutputEvent toe)
                {
                    EventDispatched?.Invoke("tool_output", toe);
                }
                break;

            case "task_completed":
                if (evt is TaskCompletedEvent tce)
                {
                    EventDispatched?.Invoke("task_completed", tce);
                }
                break;

            case "permission_denied":
                if (evt is PermissionDeniedEvent pde)
                {
                    EventDispatched?.Invoke("permission_denied", pde);
                }
                break;

            case "conversation_started":
                if (evt is ConversationStartedEvent cse)
                {
                    EventDispatched?.Invoke("conversation_started", cse);
                }
                break;

            case "terminal_data":
                if (evt is TerminalDataEvent tde)
                {
                    EventDispatched?.Invoke("terminal_data", new
                    {
                        cli = tde.SessionId,
                        data = tde.Data
                    });
                }
                break;

            default:
                EventDispatched?.Invoke(evt.Event, evt);
                break;
        }
    }

    // --- High-Level RPC APIs for Frontend ---

    public async Task StartProfileSessionAsync(string profileName, string? workingDirectory = null, string? model = null)
    {
        _activeProfiles[profileName] = true;
        await EnsureOnlineAsync().ConfigureAwait(false);
        await _client.SendAsync(new StartSessionRequest
        {
            Profile = profileName,
            WorkingDirectory = workingDirectory,
            Model = model
        }).ConfigureAwait(false);
    }

    public async Task StopProfileSessionAsync(string profileName)
    {
        _activeProfiles.TryRemove(profileName, out _);
        await EnsureOnlineAsync().ConfigureAwait(false);
        await _client.SendAsync(new StopSessionRequest
        {
            Profile = profileName
        }).ConfigureAwait(false);
    }

    public async Task SendChatAsync(string sessionId, string prompt, string? model = null, string? profile = null, string? workingDirectory = null, string? reasoningEffort = null, bool skipPermissions = true, string? conversationId = null)
    {
        await EnsureOnlineAsync().ConfigureAwait(false);
        await _client.SendAsync(new SendChatRequest
        {
            SessionId = sessionId,
            Prompt = prompt,
            Model = model,
            Profile = profile,
            ReasoningEffort = reasoningEffort,
            WorkingDirectory = workingDirectory,
            SkipPermissions = skipPermissions,
            ConversationId = conversationId
        }).ConfigureAwait(false);
    }

    public async Task SwitchAccountAsync(string profileName, string? provider = null)
    {
        await EnsureOnlineAsync().ConfigureAwait(false);
        await _client.SendAsync(new SwitchAccountRequest
        {
            Profile = profileName,
            Provider = provider
        }).ConfigureAwait(false);
    }

    public async Task ListAccountsAsync()
    {
        await EnsureOnlineAsync().ConfigureAwait(false);
        await _client.SendAsync(new ListAccountsRequest()).ConfigureAwait(false);
    }

    public async Task RefreshTokenAsync(string profileName)
    {
        await EnsureOnlineAsync().ConfigureAwait(false);
        await _client.SendAsync(new RefreshTokenRequest
        {
            Profile = profileName
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the sidecar to read one profile's quota. The percentage comes back as a
    /// <c>quota_update</c> event, and <c>quota_check_completed</c> always follows so the UI can stop
    /// its spinner even when the check fails.
    /// </summary>
    public async Task CheckProfileQuotaAsync(string? profileName)
    {
        await EnsureOnlineAsync().ConfigureAwait(false);
        await _client.SendAsync(new CheckQuotaRequest
        {
            Profile = profileName
        }).ConfigureAwait(false);
    }

    public async Task AbortTaskAsync(string taskId, string? reason = null)
    {
        await EnsureOnlineAsync().ConfigureAwait(false);
        await _client.SendAsync(new AbortTaskRequest
        {
            TaskId = taskId,
            Reason = reason
        }).ConfigureAwait(false);
    }

    private async Task EnsureOnlineAsync()
    {
        if (!_client.IsConnected)
        {
            await StartAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _client.Dispose();
        _manager.Dispose();
    }
}
