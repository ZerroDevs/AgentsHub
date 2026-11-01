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
    /// <summary>
    /// A shell that runs <paramref name="command"/> inside <paramref name="directory"/>.
    ///
    /// Windows needs COMSPEC with /k to keep the console open; POSIX shells take -c and stay open
    /// only while the terminal they were started in lives. Hardcoding cmd.exe is the usual way this
    /// breaks on macOS and Linux.
    /// </summary>
    private static ProcessStartInfo ShellInDirectory(string directory, string command, bool keepOpen = true)
    {
        if (Services.Platform.PlatformHelper.IsWindows)
        {
            return new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd",
                Arguments = $"{(keepOpen ? "/k" : "/c")} \"cd /d \"{directory}\" && {command}\"",
                WorkingDirectory = directory,
                UseShellExecute = true,
            };
        }

        return new ProcessStartInfo
        {
            FileName = Services.Platform.PlatformHelper.GetDefaultShell(),
            Arguments = $"-c \"cd \"{directory}\" && {command}\"",
            WorkingDirectory = directory,
            UseShellExecute = true,
        };
    }

#if WINDOWS
    private readonly Form? _mainForm;
    private CoreWebView2? _coreWebView;
    private CliWebviewForm? _cliWebviewForm;
    private readonly System.Windows.Forms.Timer _streamFlushTimer;
#else
    private readonly object? _mainForm;
    private readonly System.Threading.Timer _streamFlushTimer;
#endif

    private readonly CliManagerService _cliManager;
    private readonly LanProxyService? _lanProxyService;
    private readonly OpenCodeWatcherService? _openCodeWatcher;

    public event Action<string>? WebMessageBroadcast;

    // Debounced terminal output buffers, keyed by session id. A CliType key collapses every
    // Antigravity profile onto CliType.AntigravityCli, so a profile session's output was either
    // shown under the built-in tab or dropped entirely.
    private readonly Lock _streamLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.StringBuilder> _terminalStreamBuffers = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _promptEnhanceCts;

    // The firewall probe spawns PowerShell, so the result is cached briefly. The LAN view can
    // open several times in a row and each probe would otherwise cost a process launch.
    private FirewallStatus? _cachedFirewall;
    private DateTime _cachedFirewallAt = DateTime.MinValue;
    private readonly SemaphoreSlim _firewallGate = new(1, 1);
    private const int FirewallCacheMs = 20000;


    // Cached so payload parsing does not allocate a new options instance on every message.
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

#if WINDOWS
    public DashboardWebBridge(Form? mainForm, CliManagerService cliManager, LanProxyService? lanProxyService, OpenCodeWatcherService? openCodeWatcher = null)
#else
    public DashboardWebBridge(object? mainForm, CliManagerService cliManager, LanProxyService? lanProxyService, OpenCodeWatcherService? openCodeWatcher = null)
#endif
    {
        _mainForm = mainForm;
        _cliManager = cliManager;
        _lanProxyService = lanProxyService;
        _openCodeWatcher = openCodeWatcher;

#if WINDOWS
        // Initialize debounced terminal stream timer (flushes every 20ms)
        _streamFlushTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _streamFlushTimer.Tick += OnStreamFlushTimerTick;
        _streamFlushTimer.Start();
#else
        _streamFlushTimer = new System.Threading.Timer(_ => OnStreamFlushTimerTick(null, EventArgs.Empty), null, 20, 20);
#endif

        // No metrics timer on purpose: hardware readings are sampled only when the Settings panel
        // asks for them, so an idle window costs nothing.

        // The sidecar's stdout is the only place its crashes and stack traces appear, and nothing
        // else forwards them to the dashboard, so the log console subscribes here.
        _cliManager.HubService.LogMessage += OnSidecarLog;

        // Per-CLI lifecycle lines (spawn failures, profile problems, refused launches) were only
        // visible in the HUD. Everything the manager reports now lands in Live Logs too.
        _cliManager.LogMessage += OnCliManagerLog;

        HookBackendEvents();
    }

    /// <summary>
    /// A plain status string carries no level, so one is inferred from the wording. The manager
    /// writes "could not", "failed" and "Warning:" for the cases worth chasing.
    /// </summary>
    private void OnCliManagerLog(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        string level = message.Contains("error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not", StringComparison.OrdinalIgnoreCase)
            ? "error"
            : message.Contains("warn", StringComparison.OrdinalIgnoreCase)
              || message.Contains("notice", StringComparison.OrdinalIgnoreCase)
              ? "warn"
              : "info";

        EmitLog(source, level, message);
    }

    /// <summary>Sidecar output, tagged by severity so a stack trace on stderr is not shown as info.</summary>
    private void OnSidecarLog(string line)
    {
        bool isError = line.Contains("[stderr]", StringComparison.OrdinalIgnoreCase);
        EmitLog("Sidecar", isError ? "error" : "info", line);
    }

#if WINDOWS
    public void AttachWebView(CoreWebView2 coreWebView)
    {
        _coreWebView = coreWebView;
        _coreWebView.WebMessageReceived += OnWebMessageReceived;

        if (_openCodeWatcher != null)
        {
            var stateDto = _openCodeWatcher.GetStateDto();
            PostOpenCodeUpdate(stateDto);
        }

        // Warm up firewall check in background at launch so cache is ready without blocking the UI
        _ = Task.Run(async () =>
        {
            try
            {
                var req = GetRequiredLanPorts();
                await GetFirewallStatusAsync(req, force: false).ConfigureAwait(false);
            }
            catch
            {
                // Background warmup failure is non-fatal
            }
        });

        // Silent background update check after 6 seconds
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(6000).ConfigureAwait(false);
                var update = await UpdateService.Instance.CheckForUpdatesAsync().ConfigureAwait(false);
                if (update.IsUpdateAvailable)
                {
                    PostEventToUi("update_available", update);
                }
            }
            catch
            {
                // Silent check failure
            }
        });
    }
#endif

    private void HookBackendEvents()
    {
        _cliManager.StatusChanged += (type, state, msg) =>
        {
            UpdateDiscordRpcAgents();
            PostEventToUi("cli_status_changed", new
            {
                cli = type.ToString(),
                state = state.ToString(),
                message = msg,
                port = GetCliPort(type),
                pid = _cliManager.GetInfo(type).ProcessId
            });
        };

        _cliManager.StatusChangedId += (id, state, msg) =>
        {
            UpdateDiscordRpcAgents();
            if (CliManagerService.IsAntigravityProfileId(id, out var profileName))
            {
                PostEventToUi("antigravity_instance_status_changed", new
                {
                    id,
                    profile = profileName,
                    state = state.ToString(),
                    message = msg,
                    port = _cliManager.GetPortForProfile(profileName),
                    isRunning = state == CliState.Running
                });

                var lanIp = NetworkHelper.GetLocalIPv4();
                var lanProxyPort = _lanProxyService?.ListenPort ?? 8080;
                var rPorts = GetRunningWebPorts(lanIp, _cliManager.HubService.Port, lanProxyPort);
                PostEventToUi("running_ports_updated", new
                {
                    runningPorts = rPorts
                });
            }
        };

        _cliManager.TerminalDataReceivedId += (id, data) =>
        {
            // Append incoming stream data into debounced buffer
            lock (_streamLock)
            {
                _terminalStreamBuffers.GetOrAdd(id, _ => new System.Text.StringBuilder()).Append(data);
            }
        };

        if (_lanProxyService != null)
        {
            // The device list was already being tracked but never reached the UI.
            _lanProxyService.ActiveClientsChanged += clients =>
            {
                PostEventToUi("lan_clients_changed", new { clients = ToClientDtos(clients) });
            };

            _lanProxyService.StateChanged += running =>
            {
                // A bind can fail, so the UI re-reads the real state rather than trusting the toggle.
                InvalidateFirewallCache();
                PostEventToUi("lan_state_changed", new
                {
                    running,
                    bindError = _lanProxyService.LastBindError ?? ""
                });
            };
        }

        FileWatchdogService.Instance.FileChanged += (fileEvent) =>
        {
            PostEventToUi("workspace_file_changed", fileEvent);
        };

        CustomCliService.Instance.CustomClisChanged += () =>
        {
            PostEventToUi("custom_clis_changed", new
            {
                clis = CustomCliService.Instance.GetCustomClis()
            });
        };

        var curWs = AiConfigService.Instance.GetActiveWorkspace();
        if (curWs != null && !string.IsNullOrWhiteSpace(curWs.WorkingDirectory))
        {
            FileWatchdogService.Instance.WatchDirectory(curWs.WorkingDirectory);
        }

        _cliManager.ProfileService.ActiveProfileChanged += (activeProfile) =>
        {
            PostEventToUi("profile_changed", new
            {
                activeProfile,
                profiles = GetProfilesWithEmail()
            });
        };

        _cliManager.ProfileService.ProfileSaved += (profile) =>
        {
            PostEventToUi("profile_saved", new
            {
                profile,
                profiles = GetProfilesWithEmail()
            });
        };

        AiConfigService.Instance.ConfigChanged += () =>
        {
            PostEventToUi("ai_config_changed", GetAiConfigDto());
        };

        if (_lanProxyService != null)
        {
            _lanProxyService.StateChanged += (isRunning) =>
            {
                var lanIp = NetworkHelper.GetLocalIPv4();
                var rPorts = GetRunningWebPorts(lanIp, _cliManager.HubService.Port, _lanProxyService.ListenPort);
                PostEventToUi("lan_proxy_state_changed", new
                {
                    lanProxyEnabled = isRunning,
                    lanProxyRunning = isRunning,
                    runningPorts = rPorts
                });
            };
        }

        if (_openCodeWatcher != null)
        {
            _openCodeWatcher.OnStatusChanged += (isOnline, port) =>
            {
                var stateDto = _openCodeWatcher.GetStateDto();
                PostOpenCodeUpdate(stateDto);
                EmitLog("OpenCode", isOnline ? "info" : "warn", isOnline ? $"Sidecar Online on port {port}" : "Sidecar Offline");
            };

            _openCodeWatcher.OnSessionsUpdated += (sessions) =>
            {
                var stateDto = _openCodeWatcher.GetStateDto();
                PostOpenCodeUpdate(stateDto);
                EmitLog("OpenCode", "info", $"Active sessions updated ({sessions.Count} session(s))");
            };

            _openCodeWatcher.OnInteractivePrompt += (prompt) =>
            {
                PostEventToUi("opencode_interactive_prompt", prompt);
                EmitLog("OpenCode", "warn", $"Interactive prompt: {prompt.Title} - {prompt.Prompt}");
            };
        }

        ProxyTelemetryService.Instance.TelemetryUpdated += () =>
        {
            PostEventToUi("token_telemetry_updated", ProxyTelemetryService.Instance.GetSummary());
        };

        // Initialize automated agent CLI completion and Discord webhook dispatcher
        AgentCompletionWatcherService.Instance.Initialize(_cliManager, (evtName, payload) =>
        {
            PostEventToUi(evtName, payload);
        });

        // Initialize Pocket Commander mobile remote bot gateway
        PocketCommanderService.Instance.Initialize(_cliManager);

        // Initialize Git Repository Manager progress and state events
        GitRepositoryManagerService.Instance.ScanProgressChanged += (progress) =>
        {
            PostEventToUi("repo_scan_progress", progress);
        };

        GitRepositoryManagerService.Instance.RepositoriesChanged += () =>
        {
            PostEventToUi("repos_updated", new
            {
                repositories = GitRepositoryManagerService.Instance.GetRepositories()
            });
        };

        DiscordRpcService.Instance.StatusChanged += (status) =>
        {
            PostEventToUi("discord_rpc_status_changed", status);
        };

        PocketCommanderService.Instance.StatusChanged += (evt, payload) =>
        {
            PostEventToUi("pocket_commander_status_changed", new { evt, payload });
        };

        PocketCommanderService.Instance.CommandReceived += (cmd, user) =>
        {
            PostEventToUi("pocket_commander_command_received", new { cmd, user, timestamp = DateTime.UtcNow });
        };

        // Wire native Bun Sidecar events directly to WebView2 UI
        // Interactive install console: stream output and exit straight to the webview.
        NpxInstallHost.OutputReceived += text => PostEventToUi("npx_install_data", new { text });
        NpxInstallHost.Exited += code => PostEventToUi("npx_install_exited", new { exitCode = code });

        Sidecar.SidecarHostService.Instance.EventDispatched += (evt, payload) =>
        {
            if (payload != null)
            {
                PostEventToUi(evt, payload);
            }
        };
    }

    private void UpdateDiscordRpcAgents()
    {
        try
        {
            int count = 0;
            string primaryName = "Claude Code";
            if (_cliManager.GetInfo(CliType.ClaudeCode).State == CliState.Running)
            {
                count++;
                primaryName = "Claude Code";
            }
            if (_cliManager.GetInfo(CliType.AntigravityCli).State == CliState.Running)
            {
                count++;
                primaryName = "Antigravity";
            }
            if (_cliManager.GetInfo(CliType.Hub).State == CliState.Running)
            {
                count++;
            }
            var runningProfiles = _cliManager.GetRunningAntigravityProfiles();
            count += runningProfiles.Count;

            DiscordRpcService.Instance.SetActiveAgents(count, primaryName);
        }
        catch { }
    }

    // The dashboard exposes one Antigravity tab for whichever profile is active, while terminal
    // sessions are keyed by profile id. Resolving here keeps history, input, and resize pointed at
    // the session that is actually running instead of at an empty "antigravity_cli" buffer.

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static object GetSystemTelemetryDto()
    {
        // Sampling runs on NetworkTelemetryService's own thread; this read is cached and cheap.
        NetworkTelemetryService.EnsureStarted();
        double netKiloBytesPerSec = NetworkTelemetryService.NetworkKiloBytesPerSec;

        long totalRamMb = 16384;
        long usedRamMb = 4096;
        long hubMemMb = 0;
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            // kernel32 exists on Windows only; the defaults above stand in on macOS and Linux, so the
            // call is gated rather than relied on to throw and be swallowed.
            if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref mem))
            {
                totalRamMb = (long)(mem.ullTotalPhys / (1024 * 1024));
                long availMb = (long)(mem.ullAvailPhys / (1024 * 1024));
                usedRamMb = totalRamMb - availMb;
            }
        }
        catch { }
        try
        {
            using var proc = Process.GetCurrentProcess();
            hubMemMb = proc.WorkingSet64 / (1024 * 1024);
        }
        catch { }

        double memUsedGb = Math.Round(usedRamMb / 1024.0, 1);
        double memTotalGb = Math.Round(totalRamMb / 1024.0, 1);

        return new
        {
            netKbps = Math.Round(netKiloBytesPerSec, 1),
            netMbps = Math.Round(netKiloBytesPerSec / 1024.0, 3),
            ramMb = usedRamMb,
            totalRamMb,
            memoryUsedGb = memUsedGb,
            memoryTotalGb = memTotalGb,
            hubMemMb
        };
    }

#if WINDOWS
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Navigation is locked to the app origins, but a frame or a transient page must not be
        // able to drive the action set either. The sender has to be the page this bridge belongs to.
        if (!IsSelfOrigin(e.Source)) return;

        await HandleIncomingJsonAsync(e.WebMessageAsJson);
    }

    private bool IsSelfOrigin(string? source)
    {
        var page = _coreWebView?.Source;
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(page)) return false;

        return Uri.TryCreate(source, UriKind.Absolute, out var from)
            && Uri.TryCreate(page, UriKind.Absolute, out var current)
            && string.Equals(from.GetLeftPart(UriPartial.Authority), current.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }
#endif

    /// <summary>
    /// Where the wallpaper is copied. MainForm maps this folder to https://theme.agentshub.local, so
    /// the page reads it like any other asset instead of carrying a base64 payload in storage.
    /// </summary>
    internal static string ThemeImageFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub", "theme");

    public async Task HandleIncomingJsonAsync(string rawJson)
    {
        string? requestId = null;
        string action = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            action = root.TryGetProperty("action", out var actElem) ? actElem.GetString() ?? string.Empty : string.Empty;
            requestId = root.TryGetProperty("requestId", out var reqElem) ? reqElem.GetString() : null;

            await HandleActionAsync(action, root, requestId);
        }
        catch (Exception ex)
        {
            // Answer under the caller's request id. Answering with none left its promise pending, so
            // any failing action surfaced as a 25 second timeout instead of the actual reason.
            EmitLog("Bridge", "error", $"{action} failed: {ex.Message}");
            SendResponse(requestId, false, $"{action}: {ex.Message}");
        }
    }

    /// <summary>
    /// The whole web-message protocol in one table: every action names the partial method that
    /// handles it, so the surface stays readable in one place while the bodies live next to their
    /// siblings in the per-domain partials.
    /// </summary>
    private async Task HandleActionAsync(string? action, JsonElement data, string? requestId)
    {
        switch (action)
        {
            case "get_initial_state":
                await HandleGetInitialStateAsync(requestId).ConfigureAwait(false);
                break;

            case "send_chat":
                await HandleSendChatAsync(data, requestId).ConfigureAwait(false);
                break;

#if DEEPSEEK_UI
#if WINDOWS
            case "qwen_status":
                await HandleQwenStatusAsync(requestId).ConfigureAwait(false);
                break;

            case "qwen_login":
                await HandleQwenLoginAsync(requestId).ConfigureAwait(false);
                break;

            case "qwen_open":
                await HandleQwenOpenAsync(requestId).ConfigureAwait(false);
                break;

            case "open_log_bundle_location":
                await HandleOpenLogBundleLocationAsync(data, requestId).ConfigureAwait(false);
                break;

            case "export_log_bundle":
                await HandleExportLogBundleAsync(requestId).ConfigureAwait(false);
                break;

            case "engine_health":
                await HandleEngineHealthAsync(requestId).ConfigureAwait(false);
                break;

            case "qwen_models":
                await HandleQwenModelsAsync(requestId).ConfigureAwait(false);
                break;

            case "qwen_model":
                await HandleQwenModelAsync(data, requestId).ConfigureAwait(false);
                break;

            case "qwen_mode":
                await HandleQwenModeAsync(data, requestId).ConfigureAwait(false);
                break;

            case "qwen_stop":
                await HandleQwenStopAsync(requestId).ConfigureAwait(false);
                break;

            case "qwen_send":
                await HandleQwenSendAsync(data, requestId).ConfigureAwait(false);
                break;

#endif
#endif
#if DEEPSEEK_UI
            case "deepseek_status":
                await HandleDeepseekStatusAsync(requestId).ConfigureAwait(false);
                break;

            case "deepseek_login":
                await HandleDeepseekLoginAsync(requestId).ConfigureAwait(false);
                break;

            case "deepseek_accounts":
                await HandleDeepseekAccountsAsync(requestId).ConfigureAwait(false);
                break;

            case "deepseek_add_account":
                await HandleDeepseekAddAccountAsync(requestId).ConfigureAwait(false);
                break;

            case "deepseek_switch_account":
                await HandleDeepseekSwitchAccountAsync(data, requestId).ConfigureAwait(false);
                break;

            case "deepseek_rename_account":
                await HandleDeepseekRenameAccountAsync(data, requestId).ConfigureAwait(false);
                break;

            case "deepseek_remove_account":
                await HandleDeepseekRemoveAccountAsync(data, requestId).ConfigureAwait(false);
                break;

            case "list_dir_entries":
                await HandleListDirEntriesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_external":
                await HandleOpenExternalAsync(data, requestId).ConfigureAwait(false);
                break;

            case "deepseek_chats":
                await HandleDeepseekChatsAsync(requestId).ConfigureAwait(false);
                break;

            case "deepseek_approve":
                await HandleDeepseekApproveAsync(data, requestId).ConfigureAwait(false);
                break;

            case "deepseek_open_chat":
                await HandleDeepseekOpenChatAsync(data, requestId).ConfigureAwait(false);
                break;

            case "deepseek_open":
                await HandleDeepseekOpenAsync(requestId).ConfigureAwait(false);
                break;

            case "deepseek_cancel":
                await HandleDeepseekCancelAsync(data, requestId).ConfigureAwait(false);
                break;

            case "deepseek_signout":
                await HandleDeepseekSignoutAsync(requestId).ConfigureAwait(false);
                break;

#endif
            case "set_profile_skip_permissions":
                await HandleSetProfileSkipPermissionsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "grant_permission":
                await HandleGrantPermissionAsync(data, requestId).ConfigureAwait(false);
                break;

            case "revoke_permission":
                await HandleRevokePermissionAsync(data, requestId).ConfigureAwait(false);
                break;

            case "check_profile_quota":
                await HandleCheckProfileQuotaAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_model_catalog":
                await HandleGetModelCatalogAsync(data, requestId).ConfigureAwait(false);
                break;

            case "abort_task":
                await HandleAbortTaskAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_slash_commands":
                await HandleGetSlashCommandsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "switch_account":
                await HandleSwitchAccountAsync(data, requestId).ConfigureAwait(false);
                break;

            case "list_accounts":
                await HandleListAccountsAsync(requestId).ConfigureAwait(false);
                break;

            case "refresh_token":
                await HandleRefreshTokenAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_sidecar_status":
                await HandleGetSidecarStatusAsync(requestId).ConfigureAwait(false);
                break;

            case "get_system_telemetry":
                await HandleGetSystemTelemetryAsync(requestId).ConfigureAwait(false);
                break;

            case "release_system_telemetry":
                await HandleReleaseSystemTelemetryAsync(requestId).ConfigureAwait(false);
                break;

            case "check_for_updates":
                await HandleCheckForUpdatesAsync(requestId).ConfigureAwait(false);
                break;

            case "install_update":
                await HandleInstallUpdateAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_mcp_and_skills":
                await HandleGetMcpAndSkillsAsync(requestId).ConfigureAwait(false);
                break;

            case "add_mcp_folder":
                await HandleAddMcpFolderAsync(data, requestId).ConfigureAwait(false);
                break;

            case "remove_mcp_folder":
                await HandleRemoveMcpFolderAsync(data, requestId).ConfigureAwait(false);
                break;

            case "save_mcp_server":
                await HandleSaveMcpServerAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_mcp_server":
                await HandleDeleteMcpServerAsync(data, requestId).ConfigureAwait(false);
                break;

            case "toggle_skill":
                await HandleToggleSkillAsync(data, requestId).ConfigureAwait(false);
                break;

            case "save_mcp_auth":
                await HandleSaveMcpAuthAsync(data, requestId).ConfigureAwait(false);
                break;

            case "test_mcp_auth":
                await HandleTestMcpAuthAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_skill_content":
                await HandleGetSkillContentAsync(data, requestId).ConfigureAwait(false);
                break;

            case "save_skill_content":
                await HandleSaveSkillContentAsync(data, requestId).ConfigureAwait(false);
                break;

            case "create_project_skill":
                await HandleCreateProjectSkillAsync(data, requestId).ConfigureAwait(false);
                break;

            case "fork_skill_to_workspace":
                await HandleForkSkillToWorkspaceAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_project_skill":
                await HandleDeleteProjectSkillAsync(data, requestId).ConfigureAwait(false);
                break;

            case "run_npx_command":
                await HandleRunNpxCommandAsync(data, requestId).ConfigureAwait(false);
                break;

            case "send_npx_input":
                await HandleSendNpxInputAsync(data, requestId).ConfigureAwait(false);
                break;

            case "start_shell":
                await HandleStartShellAsync(data, requestId).ConfigureAwait(false);
                break;

            case "send_shell_input":
                await HandleSendShellInputAsync(data, requestId).ConfigureAwait(false);
                break;

            case "stop_shell":
                await HandleStopShellAsync(requestId).ConfigureAwait(false);
                break;

            case "stop_npx_command":
                await HandleStopNpxCommandAsync(requestId).ConfigureAwait(false);
                break;

            case "get_preference_debug":
                await HandleGetPreferenceDebugAsync(requestId).ConfigureAwait(false);
                break;

            case "open_mcp_config":
                await HandleOpenMcpConfigAsync(requestId).ConfigureAwait(false);
                break;

            case "reveal_skill_in_explorer":
                await HandleRevealSkillInExplorerAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_skill_in_editor":
                await HandleOpenSkillInEditorAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_agent_sessions":
                await HandleGetAgentSessionsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_session_steps":
            case "get_transcript_steps":
                await HandleGetSessionStepsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_agent_session":
                await HandleDeleteAgentSessionAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_workspace_snapshots":
                await HandleGetWorkspaceSnapshotsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "create_workspace_snapshot":
                await HandleCreateWorkspaceSnapshotAsync(data, requestId).ConfigureAwait(false);
                break;

            case "rollback_workspace_snapshot":
                await HandleRollbackWorkspaceSnapshotAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_workspace_snapshot":
                await HandleDeleteWorkspaceSnapshotAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_soundpack_config":
                await HandleGetSoundpackConfigAsync(requestId).ConfigureAwait(false);
                break;

            case "save_soundpack_config":
                await HandleSaveSoundpackConfigAsync(data, requestId).ConfigureAwait(false);
                break;

            case "play_sound_event":
                await HandlePlaySoundEventAsync(data, requestId).ConfigureAwait(false);
                break;

            case "inject_voice_command":
                await HandleInjectVoiceCommandAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_scheduled_automations":
                await HandleGetScheduledAutomationsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "save_scheduled_automation":
                await HandleSaveScheduledAutomationAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_scheduled_automation":
                await HandleDeleteScheduledAutomationAsync(data, requestId).ConfigureAwait(false);
                break;

            case "trigger_scheduled_automation":
                await HandleTriggerScheduledAutomationAsync(data, requestId).ConfigureAwait(false);
                break;

            case "audit_workspace_security":
                await HandleAuditWorkspaceSecurityAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_remote_webhook_config":
                await HandleGetRemoteWebhookConfigAsync(requestId).ConfigureAwait(false);
                break;

            case "save_remote_webhook_config":
                await HandleSaveRemoteWebhookConfigAsync(data, requestId).ConfigureAwait(false);
                break;

            case "test_remote_webhook":
                await HandleTestRemoteWebhookAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_pocket_commander_config":
                await HandleGetPocketCommanderConfigAsync(requestId).ConfigureAwait(false);
                break;

            case "save_pocket_commander_config":
                await HandleSavePocketCommanderConfigAsync(data, requestId).ConfigureAwait(false);
                break;

            case "test_pocket_commander_token":
                await HandleTestPocketCommanderTokenAsync(data, requestId).ConfigureAwait(false);
                break;

            case "generate_pocket_pairing_pin":
                await HandleGeneratePocketPairingPinAsync(data, requestId).ConfigureAwait(false);
                break;

            case "unpair_pocket_commander":
                await HandleUnpairPocketCommanderAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_discord_rpc_status":
                await HandleGetDiscordRpcStatusAsync(requestId).ConfigureAwait(false);
                break;

            case "save_discord_rpc_settings":
                await HandleSaveDiscordRpcSettingsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "toggle_discord_rpc":
                await HandleToggleDiscordRpcAsync(data, requestId).ConfigureAwait(false);
                break;

            case "test_discord_rpc":
                await HandleTestDiscordRpcAsync(requestId).ConfigureAwait(false);
                break;

            case "save_timelapse_clip":
                await HandleSaveTimelapseClipAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_repo_manager_state":
                await HandleGetRepoManagerStateAsync(requestId).ConfigureAwait(false);
                break;

            case "scan_repositories":
                await HandleScanRepositoriesAsync(requestId).ConfigureAwait(false);
                break;

            case "cancel_repo_scan":
                await HandleCancelRepoScanAsync(requestId).ConfigureAwait(false);
                break;

            case "save_repo_settings":
                await HandleSaveRepoSettingsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "set_working_project":
                await HandleSetWorkingProjectAsync(data, requestId).ConfigureAwait(false);
                break;

            case "update_repo_metadata":
                await HandleUpdateRepoMetadataAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_repo_notes":
                await HandleGetRepoNotesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "save_repo_notes":
                await HandleSaveRepoNotesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_repo_changes":
                await HandleGetRepoChangesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_repo_diff":
                await HandleGetRepoDiffAsync(data, requestId).ConfigureAwait(false);
                break;

            case "stage_repo_changes":
                await HandleStageRepoChangesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "create_repo_commit":
                await HandleCreateRepoCommitAsync(data, requestId).ConfigureAwait(false);
                break;

            case "repo_network_action":
                await HandleRepoNetworkActionAsync(data, requestId).ConfigureAwait(false);
                break;

            case "switch_repo_branch":
                await HandleSwitchRepoBranchAsync(data, requestId).ConfigureAwait(false);
                break;

            case "list_repo_branches":
                await HandleListRepoBranchesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "detect_repo_launchers":
                await HandleDetectRepoLaunchersAsync(data, requestId).ConfigureAwait(false);
                break;

            case "run_repo_launcher":
                await HandleRunRepoLauncherAsync(data, requestId).ConfigureAwait(false);
                break;

            case "evaluate_repo_health":
                await HandleEvaluateRepoHealthAsync(data, requestId).ConfigureAwait(false);
                break;

            case "export_repo_inventory":
                await HandleExportRepoInventoryAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_repo_in_editor":
                await HandleOpenRepoInEditorAsync(data, requestId).ConfigureAwait(false);
                break;

            case "browse_theme_image":
                await HandleBrowseThemeImageAsync(data, requestId).ConfigureAwait(false);
                break;

            case "browse_editor_path":
                await HandleBrowseEditorPathAsync(requestId).ConfigureAwait(false);
                break;

            case "get_installed_editors":
                await HandleGetInstalledEditorsAsync(requestId).ConfigureAwait(false);
                break;

            case "launch_agent_in_repo":
                await HandleLaunchAgentInRepoAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_commit_graph":
                await HandleGetCommitGraphAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_commit_diff":
                await HandleGetCommitDiffAsync(data, requestId).ConfigureAwait(false);
                break;

            case "revert_commit":
                await HandleRevertCommitAsync(data, requestId).ConfigureAwait(false);
                break;

            case "create_branch_at_commit":
                await HandleCreateBranchAtCommitAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_worktrees":
                await HandleGetWorktreesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "add_worktree":
                await HandleAddWorktreeAsync(data, requestId).ConfigureAwait(false);
                break;

            case "remove_worktree":
                await HandleRemoveWorktreeAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_stashes":
                await HandleGetStashesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_stash_diff":
                await HandleGetStashDiffAsync(data, requestId).ConfigureAwait(false);
                break;

            case "create_stash":
                await HandleCreateStashAsync(data, requestId).ConfigureAwait(false);
                break;

            case "apply_stash":
                await HandleApplyStashAsync(data, requestId).ConfigureAwait(false);
                break;

            case "drop_stash":
                await HandleDropStashAsync(data, requestId).ConfigureAwait(false);
                break;

            case "preview_git_merge":
                await HandlePreviewGitMergeAsync(data, requestId).ConfigureAwait(false);
                break;

            case "execute_git_merge":
                await HandleExecuteGitMergeAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_workspace_memories":
                await HandleGetWorkspaceMemoriesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "search_workspace_memories":
                await HandleSearchWorkspaceMemoriesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_workspace_memory":
                await HandleDeleteWorkspaceMemoryAsync(data, requestId).ConfigureAwait(false);
                break;

            case "clear_workspace_memories":
                await HandleClearWorkspaceMemoriesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "add_workspace_memory":
                await HandleAddWorkspaceMemoryAsync(data, requestId).ConfigureAwait(false);
                break;

            case "inject_session_context":
                await HandleInjectSessionContextAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_codebase_graph":
                await HandleGetCodebaseGraphAsync(data, requestId).ConfigureAwait(false);
                break;

            case "calculate_blast_radius":
                await HandleCalculateBlastRadiusAsync(data, requestId).ConfigureAwait(false);
                break;

            case "batch_repo_action":
                await HandleBatchRepoActionAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_github_auth_status":
                await HandleGetGithubAuthStatusAsync(requestId).ConfigureAwait(false);
                break;

            case "auto_detect_github_git_credentials":
                await HandleAutoDetectGithubGitCredentialsAsync(requestId).ConfigureAwait(false);
                break;

            case "save_github_pat":
                await HandleSaveGithubPatAsync(data, requestId).ConfigureAwait(false);
                break;

            case "disconnect_github":
                await HandleDisconnectGithubAsync(requestId).ConfigureAwait(false);
                break;

            case "publish_repo_to_github":
                await HandlePublishRepoToGithubAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_github_pull_requests":
                await HandleGetGithubPullRequestsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "checkout_github_pull_request":
                await HandleCheckoutGithubPullRequestAsync(data, requestId).ConfigureAwait(false);
                break;

            case "merge_github_pull_request":
                await HandleMergeGithubPullRequestAsync(data, requestId).ConfigureAwait(false);
                break;

            case "generate_ai_commit_message":
                await HandleGenerateAiCommitMessageAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_agent_rules":
                await HandleGetAgentRulesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "save_agent_rule":
                await HandleSaveAgentRuleAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_agent_rule":
                await HandleDeleteAgentRuleAsync(data, requestId).ConfigureAwait(false);
                break;

            case "toggle_workspace_rule":
                await HandleToggleWorkspaceRuleAsync(data, requestId).ConfigureAwait(false);
                break;

            case "set_workspace_rule_isolation":
                await HandleSetWorkspaceRuleIsolationAsync(data, requestId).ConfigureAwait(false);
                break;

            case "launch_interactive_npx_installer":
                await HandleLaunchInteractiveNpxInstallerAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_custom_clis":
                await HandleGetCustomClisAsync(requestId).ConfigureAwait(false);
                break;

            case "save_custom_cli":
                await HandleSaveCustomCliAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_custom_cli":
                await HandleDeleteCustomCliAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_recent_file_changes":
                await HandleGetRecentFileChangesAsync(requestId).ConfigureAwait(false);
                break;

            case "get_file_diff":
                await HandleGetFileDiffAsync(data, requestId).ConfigureAwait(false);
                break;

            case "revert_file_change":
                await HandleRevertFileChangeAsync(data, requestId).ConfigureAwait(false);
                break;

            case "clear_file_changes":
                await HandleClearFileChangesAsync(requestId).ConfigureAwait(false);
                break;

            case "run_system_diagnostics":
                await HandleRunSystemDiagnosticsAsync(requestId).ConfigureAwait(false);
                break;

            case "autofix_diagnostic_issue":
                await HandleAutofixDiagnosticIssueAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_session_state":
                await HandleGetSessionStateAsync(requestId).ConfigureAwait(false);
                break;

            case "save_session_state":
                await HandleSaveSessionStateAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_workspaces":
                await HandleGetWorkspacesAsync(requestId).ConfigureAwait(false);
                break;

            case "save_workspace":
                await HandleSaveWorkspaceAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_workspace":
                await HandleDeleteWorkspaceAsync(data, requestId).ConfigureAwait(false);
                break;

            case "activate_workspace":
                await HandleActivateWorkspaceAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_in_explorer":
                await HandleOpenInExplorerAsync(data, requestId).ConfigureAwait(false);
                break;

            case "vyce_stop":
                await HandleVyceStopAsync(requestId).ConfigureAwait(false);
                break;

            case "vyce_model_current":
                await HandleVyceModelCurrentAsync(requestId).ConfigureAwait(false);
                break;

            case "opencode_models":
                await HandleOpencodeModelsAsync(requestId).ConfigureAwait(false);
                break;

            case "opencode_auth":
                await HandleOpencodeAuthAsync(requestId).ConfigureAwait(false);
                break;

            case "opencode_login":
                await HandleOpencodeLoginAsync(requestId).ConfigureAwait(false);
                break;

            case "opencode_login_cancel":
                await HandleOpencodeLoginCancelAsync(requestId).ConfigureAwait(false);
                break;

            case "vyce_models":
                await HandleVyceModelsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "vyce_model":
                await HandleVyceModelAsync(data, requestId).ConfigureAwait(false);
                break;

            case "run_dependency_cli":
                await HandleRunDependencyCliAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_terminal_at":
                await HandleOpenTerminalAtAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_external_editor":
                await HandleOpenExternalEditorAsync(data, requestId).ConfigureAwait(false);
                break;

            case "git_branch_at":
                await HandleGitBranchAtAsync(data, requestId).ConfigureAwait(false);
                break;

            case "git_commit_graph":
                await HandleGitCommitGraphAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_git_status":
                await HandleGetGitStatusAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_workspace_files":
                await HandleGetWorkspaceFilesAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_token_telemetry":
                await HandleGetTokenTelemetryAsync(requestId).ConfigureAwait(false);
                break;

            case "reset_token_telemetry":
                await HandleResetTokenTelemetryAsync(requestId).ConfigureAwait(false);
                break;

            case "toggle_quake_hud":
                await HandleToggleQuakeHudAsync(requestId).ConfigureAwait(false);
                break;

            case "toggle_main_window":
                await HandleToggleMainWindowAsync(requestId).ConfigureAwait(false);
                break;

            case "toggle_lan_proxy":
                await HandleToggleLanProxyAsync(data, requestId).ConfigureAwait(false);
                break;

            case "set_lan_forwarders":
                await HandleSetLanForwardersAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_lan_state":
                await HandleGetLanStateAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_lan_clients":
                await HandleGetLanClientsAsync(requestId).ConfigureAwait(false);
                break;

            case "request_firewall_rule":
                await HandleRequestFirewallRuleAsync(requestId).ConfigureAwait(false);
                break;

            case "remove_firewall_rule":
                await HandleRemoveFirewallRuleAsync(requestId).ConfigureAwait(false);
                break;

            case "toggle_cli":
                await HandleToggleCliAsync(data, requestId).ConfigureAwait(false);
                break;

            case "restart_cli":
                await HandleRestartCliAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_terminal_session":
                await HandleGetTerminalSessionAsync(data, requestId).ConfigureAwait(false);
                break;

            case "clear_terminal_history":
                await HandleClearTerminalHistoryAsync(data, requestId).ConfigureAwait(false);
                break;

            case "send_terminal_input":
                await HandleSendTerminalInputAsync(data, requestId).ConfigureAwait(false);
                break;

            case "resize_terminal":
                await HandleResizeTerminalAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_active_profile_email":
                await HandleGetActiveProfileEmailAsync(requestId).ConfigureAwait(false);
                break;

            case "switch_antigravity_profile":
                await HandleSwitchAntigravityProfileAsync(data, requestId).ConfigureAwait(false);
                break;

            case "start_antigravity_instance":
                await HandleStartAntigravityInstanceAsync(data, requestId).ConfigureAwait(false);
                break;

            case "stop_antigravity_instance":
                await HandleStopAntigravityInstanceAsync(data, requestId).ConfigureAwait(false);
                break;

            case "create_antigravity_profile":
                await HandleCreateAntigravityProfileAsync(data, requestId).ConfigureAwait(false);
                break;

            case "rename_antigravity_profile":
                await HandleRenameAntigravityProfileAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_antigravity_profile":
                await HandleDeleteAntigravityProfileAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_profile_folder":
                await HandleOpenProfileFolderAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_folder":
                await HandleOpenFolderAsync(data, requestId).ConfigureAwait(false);
                break;

            case "enhance_prompt":
                await HandleEnhancePromptAsync(data, requestId).ConfigureAwait(false);
                break;

            case "fetch_models":
                await HandleFetchModelsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_claude_gateway_config":
                await HandleGetClaudeGatewayConfigAsync(requestId).ConfigureAwait(false);
                break;

            case "save_claude_gateway_config":
                await HandleSaveClaudeGatewayConfigAsync(data, requestId).ConfigureAwait(false);
                break;

            case "add_claude_gateway_provider":
                await HandleAddClaudeGatewayProviderAsync(data, requestId).ConfigureAwait(false);
                break;

            case "delete_claude_gateway_provider":
                await HandleDeleteClaudeGatewayProviderAsync(data, requestId).ConfigureAwait(false);
                break;

            case "toggle_claude_injection":
                await HandleToggleClaudeInjectionAsync(data, requestId).ConfigureAwait(false);
                break;

            case "select_claude_gateway_provider":
                await HandleSelectClaudeGatewayProviderAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_claude_settings":
                await HandleGetClaudeSettingsAsync(requestId).ConfigureAwait(false);
                break;

            case "save_claude_settings":
                await HandleSaveClaudeSettingsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_openrouter_free_models":
                await HandleGetOpenrouterFreeModelsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "refresh_openrouter_free_models":
                await HandleRefreshOpenrouterFreeModelsAsync(requestId).ConfigureAwait(false);
                break;

            case "save_openrouter_key":
                await HandleSaveOpenrouterKeyAsync(data, requestId).ConfigureAwait(false);
                break;

            case "check_openrouter_key":
                await HandleCheckOpenrouterKeyAsync(data, requestId).ConfigureAwait(false);
                break;

            case "run_claude_logout":
            case "claude_logout":
                await HandleRunClaudeLogoutAsync(requestId).ConfigureAwait(false);
                break;

            case "save_ai_config":
                await HandleSaveAiConfigAsync(data, requestId).ConfigureAwait(false);
                break;

            case "apply_terminal_preset":
                await HandleApplyTerminalPresetAsync(data, requestId).ConfigureAwait(false);
                break;

            case "get_terminal_preset":
                await HandleGetTerminalPresetAsync(requestId).ConfigureAwait(false);
                break;

            case "get_dependencies_status":
                await HandleGetDependenciesStatusAsync(requestId).ConfigureAwait(false);
                break;

            case "run_dependency_install":
                await HandleRunDependencyInstallAsync(data, requestId).ConfigureAwait(false);
                break;

            case "install_all_dependencies":
                await HandleInstallAllDependenciesAsync(requestId).ConfigureAwait(false);
                break;

            case "get_dev_tools":
                await HandleGetDevToolsAsync(requestId).ConfigureAwait(false);
                break;

            case "detect_ai_clients":
                await HandleDetectAiClientsAsync(requestId).ConfigureAwait(false);
                break;

            case "launch_tool":
                await HandleLaunchToolAsync(data, requestId).ConfigureAwait(false);
                break;

            case "browse_tool_target":
                await HandleBrowseToolTargetAsync(data, requestId).ConfigureAwait(false);
                break;

            case "browse_directory":
                await HandleBrowseDirectoryAsync(data, requestId).ConfigureAwait(false);
                break;

            case "reset_tool_target":
                await HandleResetToolTargetAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_standalone_terminals":
                await HandleOpenStandaloneTerminalsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "sync_terminal_tab":
                await HandleSyncTerminalTabAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_system_shell":
                await HandleOpenSystemShellAsync(data, requestId).ConfigureAwait(false);
                break;

            case "minimize_to_tray":
                await HandleMinimizeToTrayAsync(requestId).ConfigureAwait(false);
                break;

            case "save_general_settings":
                await HandleSaveGeneralSettingsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "save_shortcuts":
                await HandleSaveShortcutsAsync(data, requestId).ConfigureAwait(false);
                break;

            case "launch_shortcut":
                await HandleLaunchShortcutAsync(data, requestId).ConfigureAwait(false);
                break;

            case "browse_shortcut_target":
                await HandleBrowseShortcutTargetAsync(requestId).ConfigureAwait(false);
                break;

            case "get_public_ip":
                await HandleGetPublicIpAsync(requestId).ConfigureAwait(false);
                break;

            case "purge_idle_sessions":
            case "PurgeIdleSessions":
                await HandlePurgeIdleSessionsAsync(requestId).ConfigureAwait(false);
                break;

            case "get_opencode_state":
            case "GetOpenCodeState":
                await HandleGetOpencodeStateAsync(requestId).ConfigureAwait(false);
                break;

            case "focus_opencode_session":
            case "FocusOpenCodeSession":
                await HandleFocusOpencodeSessionAsync(data, requestId).ConfigureAwait(false);
                break;

            case "refresh_opencode_sidecar":
            case "RefreshOpenCodeSidecar":
                await HandleRefreshOpencodeSidecarAsync(requestId).ConfigureAwait(false);
                break;

            case "start_opencode_service":
            case "StartOpenCodeService":
                await HandleStartOpencodeServiceAsync(requestId).ConfigureAwait(false);
                break;

            case "stop_opencode_service":
            case "StopOpenCodeService":
                await HandleStopOpencodeServiceAsync(requestId).ConfigureAwait(false);
                break;

            case "recheck_opencode_service":
            case "RecheckOpenCodeService":
                await HandleRecheckOpencodeServiceAsync(requestId).ConfigureAwait(false);
                break;

            case "reply_opencode_prompt":
            case "ReplyOpenCodePrompt":
                await HandleReplyOpencodePromptAsync(data, requestId).ConfigureAwait(false);
                break;

            case "generate_qr_code":
                await HandleGenerateQrCodeAsync(data, requestId).ConfigureAwait(false);
                break;

            case "open_url_external":
                await HandleOpenUrlExternalAsync(data, requestId).ConfigureAwait(false);
                break;

            default:
                // A name the host does not own is a wiring bug on the page side, and it is silent
                // otherwise: the promise resolves with success=false and the control just does nothing.
                EmitLog("Bridge", "warn", $"Unknown action: {action}");
                SendResponse(requestId, false, $"Unknown action: {action}");
                break;
        }
    }

    private Task HandleGetInitialStateAsync(string? requestId)
    {
        SendResponse(requestId, true, GetInitialStateDto());

        return Task.CompletedTask;
    }

#if WINDOWS
    // LibraryImport, not DllImport: no parameters and an IntPtr return need no marshalling code,
    // so the source generator can emit the call. The rest of the DllImport sites stay as they are
    // until their struct marshalling is checked against the native signatures.
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    public CliWebviewForm? CliWebviewForm => _cliWebviewForm;

#endif

    private object GetInitialStateDto()
    {
        var lanIp = NetworkHelper.GetLocalIPv4();
        int hubPort = _cliManager.HubService.Port;
        int lanProxyPort = _lanProxyService?.ListenPort ?? 9191;
        bool lanProxyRunning = _lanProxyService?.IsRunning ?? false;
        string lanToken = _lanProxyService is { IsRunning: true } lan && !string.IsNullOrEmpty(lan.AccessToken)
            ? $"?k={lan.AccessToken}"
            : string.Empty;
        string lanUrl = $"http://{lanIp}:{lanProxyPort}/{lanToken}";
        string qrBase64 = QrCodeHelper.GenerateQrCodeBase64(lanUrl);
        var runningPorts = GetRunningWebPorts(lanIp, hubPort, lanProxyPort);

        return new
        {
            version = AppInfo.DisplayVersion,
            lanIp,
            lanUrl,
            lanProxyPort,
            lanProxyRunning,
            lanProxyEnabled = lanProxyRunning,
            qrBase64,
            runningPorts,
            settings = new
            {
                autostart = StartupHelper.IsWindowsStartupEnabled(),
                startMinimized = StartupHelper.GetPreference("StartMinimized", false),
                minimizeToTray = StartupHelper.GetPreference("MinimizeToTray", true),
                notifications = StartupHelper.GetPreference("WindowsNotifications", true),
                globalHotkey = StartupHelper.GetPreference("GlobalHotkey", true),
                globalHotkeyModifier = StartupHelper.GetStringPreference("GlobalHotkeyModifier", "Ctrl+Alt"),
                globalHotkeyKey = StartupHelper.GetStringPreference("GlobalHotkeyKey", "H"),
                globalHudHotkey = StartupHelper.GetPreference("GlobalHudHotkey", true),
                hudHotkeyKey = StartupHelper.GetStringPreference("HudHotkeyKey", "F12"),
                hudHotkeyModifier = StartupHelper.GetStringPreference("HudHotkeyModifier", "None"),
                hudHotkeyTarget = StartupHelper.GetStringPreference("HudHotkeyTarget", "cliwebview"),
                maskPublicIp = StartupHelper.GetPreference("MaskPublicIp", false),
                maskShortcutPaths = StartupHelper.GetPreference("MaskShortcutPaths", false),
                embedShellsInTerminalWorkspace = StartupHelper.GetPreference("EmbedShellsInTerminalWorkspace", false),
                hideMainOnCliOpen = StartupHelper.GetPreference("HideMainOnCliOpen", false),
                enableHardwareMonitor = StartupHelper.GetPreference("EnableHardwareMonitor", false),
                enableArchitectureGraph = StartupHelper.GetPreference("EnableArchitectureGraph", false),
                theme = StartupHelper.GetStringPreference("Theme", "dark"),
                accentColor = StartupHelper.GetStringPreference("AccentColor", "#10b981")
            },
            clis = new[]
            {
                new { name = "Hub", type = CliType.Hub.ToString(), state = _cliManager.GetInfo(CliType.Hub).State.ToString(), port = hubPort, pid = _cliManager.GetInfo(CliType.Hub).ProcessId },
                new { name = "Claude Code", type = CliType.ClaudeCode.ToString(), state = _cliManager.GetInfo(CliType.ClaudeCode).State.ToString(), port = 9229, pid = _cliManager.GetInfo(CliType.ClaudeCode).ProcessId },
                new { name = "Antigravity CLI", type = CliType.AntigravityCli.ToString(), state = _cliManager.GetInfo(CliType.AntigravityCli).State.ToString(), port = 9339, pid = _cliManager.GetInfo(CliType.AntigravityCli).ProcessId }
            },
            antigravity = new
            {
                activeProfile = _cliManager.ProfileService.ActiveProfile,
                profiles = GetProfilesWithEmail(),
                model = "Gemini 3.8 Flash (High)"
            },
            opencode = GetOpenCodeState(),
            aiConfig = GetAiConfigDto(),
            workspaces = AiConfigService.Instance.GetWorkspaces(),
            activeWorkspaceId = AiConfigService.Instance.GetActiveWorkspaceId(),
            tokenTelemetry = ProxyTelemetryService.Instance.GetSummary(),
            terminalWorkspacePreset = AiConfigService.Instance.GetTerminalWorkspacePreset(),
            enabledClis = AiConfigService.Instance.GetCliWorkspaces().Where(w => w.IsEnabled).Select(w => AiConfigService.NormalizeCliId(w.Id)).ToList()
        };
    }

    private int GetCliPort(CliType type) => type switch
    {
        CliType.Hub => _cliManager.HubService.Port,
        CliType.ClaudeCode => 9229,
        CliType.AntigravityCli => 9339,
        _ => 0
    };

    private static object ToDependencyDto(DependencyStatus status) => new
    {
        id = status.Id,
        name = status.Name,
        description = status.Description,
        category = status.Category,
        isInstalled = status.IsInstalled,
        version = status.Version,
        path = status.Path,
        installKind = status.InstallKind,
        siteUrl = status.SiteUrl,
        commandPreview = status.CommandPreview,
        // Whether the tool can drive a chat session, or only a terminal. The panel marks the rest
        // "Not used" rather than implying support that does not exist.
        chatSupported = DependencyCheckerService.Find(status.Id)?.ChatSupported ?? false
    };

    private void SendJsonMessageToClients(string envelope)
    {
#if WINDOWS
        if (_coreWebView != null)
        {
            if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed && f.InvokeRequired)
            {
                f.BeginInvoke(new Action(() =>
                {
                    try { _coreWebView?.PostWebMessageAsJson(envelope); } catch { }
                }));
            }
            else
            {
                _coreWebView?.PostWebMessageAsJson(envelope);
            }
        }
#endif
        WebMessageBroadcast?.Invoke(envelope);
    }

#if DEEPSEEK_UI
    /// <summary>Approval cards waiting for an answer, keyed by tool call id.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _deepSeekApprovals = new();

    /// <summary>Live DeepSeek runs, so the stop button can cancel one by session id.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _deepSeekRuns = new();

#endif

    private void PostEventToUi(string eventType, object payload)
    {
        try
        {
            var envelope = JsonSerializer.Serialize(new
            {
                type = eventType == "opencode_interactive_prompt" ? "OPENCODE_INTERACTIVE_PROMPT" : eventType,
                @event = eventType,
                payload
            }, CamelCaseJsonOptions);

            SendJsonMessageToClients(envelope);
        }
        catch { }
    }

    public void EmitLog(string source, string level, string message)
    {
        PostEventToUi("live_log_entry", new
        {
            timestamp = DateTime.Now.ToString("HH:mm:ss"),
            source,
            level,
            message
        });
    }

    private void SendResponse(string? requestId, bool success, object? data)
    {
        try
        {
            var envelope = JsonSerializer.Serialize(new
            {
                responseId = requestId,
                success,
                data
            }, CamelCaseJsonOptions);

            SendJsonMessageToClients(envelope);
        }
        catch { }
    }

    public void Dispose()
    {
        _cliManager.HubService.LogMessage -= OnSidecarLog;
        _cliManager.LogMessage -= OnCliManagerLog;
#if WINDOWS
        _streamFlushTimer.Stop();
#endif
        _streamFlushTimer.Dispose();
        _promptEnhanceCts?.Dispose();
    }
}
