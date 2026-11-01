using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentsHub.Services.ConPty;

namespace AgentsHub.Services;

public enum CliType
{
    Hub = 0,
    ClaudeCode = 1,
    AntigravityCli = 2,
    GeminiCli = AntigravityCli
}

public enum CliState
{
    Offline,
    Starting,
    Running,
    Error
}

public class CliItemInfo
{
    public string Id { get; set; } = "";
    public CliType Type { get; set; } = CliType.Hub;
    public string DisplayName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string CommandDescription { get; set; } = "";
    public CliState State { get; set; } = CliState.Offline;
    public string StatusText { get; set; } = "Offline";
    public int ProcessId { get; set; }
    public DateTime? StartTime { get; set; }
    public bool IsCustom { get; set; } = false;
    public bool IsEnabled { get; set; } = true;
    public int? Port { get; set; }
    public bool IsWebUi => !string.IsNullOrEmpty(WebUrl) || Type == CliType.Hub;
    public string? WebUrl { get; set; }
    public string? ProfileName { get; set; }
}

/// <summary>
/// How to launch a tab's CLI in a real, user-visible console window, with the same
/// environment the embedded session would receive.
/// </summary>
public sealed class ExternalLaunchSpec
{
    public string Command { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public class CliManagerService : IDisposable
{
    private readonly HubProcessService _hubService;
    private readonly CliWebTerminalServer _webTerminalServer;
    private readonly AntigravityProfileService _profileService;
    private readonly ConcurrentDictionary<string, ConPtySession> _terminalSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StringBuilder> _terminalBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CliItemInfo> _cliStates = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxBufferSize = 150_000;
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public HubProcessService HubService => _hubService;
    public CliWebTerminalServer WebTerminalServer => _webTerminalServer;
    public AntigravityProfileService ProfileService => _profileService;

    public event Action<CliType, CliState, string>? StatusChanged;
    public event Action<CliType, string>? TerminalDataReceived;
    public event Action<string, CliState, string>? StatusChangedId;
    public event Action<string, string>? TerminalDataReceivedId;
    public event Action<string, string>? InputSentId;
    public event Action<string, string>? LogMessage;

    public CliManagerService(HubProcessService hubService)
    {
        _hubService = hubService;
        _profileService = new AntigravityProfileService();

        _profileService.LogMessage += (msg) =>
        {
            LogMessage?.Invoke("Antigravity Profile", msg);
        };

        // ProfileSaved fires for every metadata write, including toggles and allow-rules, so a
        // "Saved credentials" line from here appeared after unrelated changes and, right after an
        // export, repeated the export's own message. Each caller already logs what it changed.

        _cliStates["Hub"] = new CliItemInfo
        {
            Id = "Hub",
            Type = CliType.Hub,
            DisplayName = "Hub (Web Dashboard)",
            ShortName = "Hub",
            CommandDescription = "hub dashboard --port [PORT]",
            State = CliState.Offline,
            StatusText = "Engine: Offline",
            Port = 9119,
            WebUrl = "http://127.0.0.1:9119"
        };

        _cliStates["ClaudeCode"] = new CliItemInfo
        {
            Id = "ClaudeCode",
            Type = CliType.ClaudeCode,
            DisplayName = "Claude Code (CLI)",
            ShortName = "Claude Code",
            CommandDescription = "claude",
            State = CliState.Offline,
            StatusText = "CLI: Offline",
            Port = 9229
        };

        _cliStates["AntigravityCli"] = new CliItemInfo
        {
            Id = "AntigravityCli",
            Type = CliType.AntigravityCli,
            DisplayName = "Antigravity CLI",
            ShortName = "Antigravity",
            CommandDescription = "agy",
            State = CliState.Offline,
            StatusText = "CLI: Offline",
            Port = 9339
        };

        _terminalBuffers["ClaudeCode"] = new StringBuilder();
        _terminalBuffers["AntigravityCli"] = new StringBuilder();

        // Bind Hub service events
        _hubService.StatusChanged += (hStatus, msg) =>
        {
            var cliState = hStatus switch
            {
                HubStatus.Online => CliState.Running,
                HubStatus.Starting => CliState.Starting,
                HubStatus.Error => CliState.Error,
                _ => CliState.Offline
            };

            var item = _cliStates.GetOrAdd("Hub", _ => new CliItemInfo
            {
                Id = "Hub",
                Type = CliType.Hub,
                DisplayName = "Hub (Web Dashboard)",
                ShortName = "Hub",
                CommandDescription = "hub dashboard --port [PORT]",
                Port = 9119,
                WebUrl = "http://127.0.0.1:9119"
            });
            item.State = cliState;
            item.StatusText = msg;
            StatusChanged?.Invoke(CliType.Hub, cliState, msg);
            StatusChangedId?.Invoke("Hub", cliState, msg);
        };

        _hubService.LogMessage += (msg) =>
        {
            LogMessage?.Invoke("Hub", msg);
        };

        _webTerminalServer = new CliWebTerminalServer(this);
        _webTerminalServer.Start();
    }

    public static bool IsAntigravityProfileId(string id, out string profileName)
    {
        id = id?.Trim() ?? string.Empty;
        if (id.StartsWith("antigravity_cli:", StringComparison.OrdinalIgnoreCase))
        {
            profileName = id["antigravity_cli:".Length..].Trim();
            return !string.IsNullOrEmpty(profileName);
        }
        if (id.StartsWith("antigravity_profile:", StringComparison.OrdinalIgnoreCase))
        {
            profileName = id["antigravity_profile:".Length..].Trim();
            return !string.IsNullOrEmpty(profileName);
        }
        profileName = string.Empty;
        return false;
    }

    public static string FormatAntigravityProfileId(string profileName)
    {
        return $"antigravity_cli:{profileName.Trim()}";
    }

    public string ResolveSessionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            var act = _profileService.ActiveProfile;
            if (string.IsNullOrWhiteSpace(act))
            {
                var profs = _profileService.GetProfiles();
                act = profs.Count > 0 ? profs[0] : "Default";
            }
            return FormatAntigravityProfileId(act);
        }

        id = id.Trim();

        if (IsAntigravityProfileId(id, out var profName))
        {
            return FormatAntigravityProfileId(profName);
        }

        var allProfiles = _profileService.GetProfiles();
        var matched = allProfiles.FirstOrDefault(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(matched))
        {
            return FormatAntigravityProfileId(matched);
        }

        string norm = AiConfigService.NormalizeCliId(id);
        if (string.Equals(norm, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
        {
            var act = _profileService.ActiveProfile;
            if (string.IsNullOrWhiteSpace(act))
            {
                act = allProfiles.Count > 0 ? allProfiles[0] : "Default";
            }
            return FormatAntigravityProfileId(act);
        }

        return norm;
    }

    public int AllocatePortForProfile(string profileName)
    {
        var profId = FormatAntigravityProfileId(profileName);
        if (_cliStates.TryGetValue(profId, out var existingInfo) && existingInfo.Port.HasValue && existingInfo.Port.Value >= 9339)
        {
            int preferred = existingInfo.Port.Value;
            if (IsPortAvailable(preferred) && !IsPortUsedByOtherCli(preferred, profId))
            {
                return preferred;
            }
        }

        int port = 9339;
        while (!IsPortAvailable(port) || IsPortUsedByOtherCli(port, profId))
        {
            port++;
        }
        return port;
    }

    private bool IsPortUsedByOtherCli(int port, string currentId)
    {
        foreach (var kvp in _cliStates)
        {
            if (string.Equals(kvp.Key, currentId, StringComparison.OrdinalIgnoreCase)) continue;
            if ((kvp.Value.State == CliState.Running || kvp.Value.State == CliState.Starting) && kvp.Value.Port == port)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartAntigravityProfile(string profileName) => Start(FormatAntigravityProfileId(profileName));
    public Task StopAntigravityProfileAsync(string profileName) => StopAsync(FormatAntigravityProfileId(profileName));
    public Task RestartAntigravityProfileAsync(string profileName) => RestartAsync(FormatAntigravityProfileId(profileName));

    public bool IsProfileRunning(string profileName)
    {
        var id = FormatAntigravityProfileId(profileName);
        return _cliStates.TryGetValue(id, out var info) && (info.State == CliState.Running || info.State == CliState.Starting);
    }

    public int? GetPortForProfile(string profileName)
    {
        var id = FormatAntigravityProfileId(profileName);
        if (_cliStates.TryGetValue(id, out var info) && info.Port.HasValue)
        {
            return info.Port.Value;
        }
        return null;
    }

    public IReadOnlyList<string> GetRunningAntigravityProfiles()
    {
        var list = new List<string>();
        foreach (var kvp in _cliStates)
        {
            if (IsAntigravityProfileId(kvp.Key, out var pName) && (kvp.Value.State == CliState.Running || kvp.Value.State == CliState.Starting))
            {
                list.Add(pName);
            }
        }
        return list;
    }

    public CliItemInfo GetInfo(string id)
    {
        if (IsAntigravityProfileId(id, out var profName))
        {
            string profId = FormatAntigravityProfileId(profName);
            if (_cliStates.TryGetValue(profId, out var existingProfInfo))
            {
                return existingProfInfo;
            }

            int allocPort = AllocatePortForProfile(profName);
            var createdProf = new CliItemInfo
            {
                Id = profId,
                Type = CliType.AntigravityCli,
                DisplayName = $"Antigravity ({profName})",
                ShortName = $"agy: {profName}",
                CommandDescription = $"agy (Profile: {profName})",
                State = CliState.Offline,
                StatusText = "CLI: Offline",
                Port = allocPort,
                WebUrl = $"http://127.0.0.1:{allocPort}",
                ProfileName = profName
            };
            _cliStates[profId] = createdProf;
            return createdProf;
        }

        id = AiConfigService.NormalizeCliId(id);
        if (string.Equals(id, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
        {
            var activeProf = _profileService.ActiveProfile;
            if (!string.IsNullOrWhiteSpace(activeProf))
            {
                var activeProfId = FormatAntigravityProfileId(activeProf);
                if (_cliStates.TryGetValue(activeProfId, out var activeInfo))
                {
                    return activeInfo;
                }
            }
        }

        if (_cliStates.TryGetValue(id, out var info))
        {
            return info;
        }

        if (CustomCliService.Instance.TryGetCustomCli(id, out var custom))
        {
            var createdCustom = new CliItemInfo
            {
                Id = custom.Id,
                DisplayName = custom.Name,
                ShortName = custom.ShortName,
                CommandDescription = custom.GetCommandLine(),
                IsCustom = true,
                IsEnabled = custom.IsEnabled
            };
            _cliStates[custom.Id] = createdCustom;
            return createdCustom;
        }

        var cfg = AiConfigService.Instance.CurrentConfig.CliWorkspaces.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (cfg != null)
        {
            var created = new CliItemInfo
            {
                Id = cfg.Id,
                DisplayName = cfg.Name,
                ShortName = cfg.Name,
                CommandDescription = cfg.Command,
                IsCustom = !cfg.IsDefault,
                IsEnabled = cfg.IsEnabled,
                Port = cfg.Port,
                WebUrl = cfg.IsWebUi ? $"http://127.0.0.1:{cfg.Port ?? 9119}" : null
            };
            _cliStates[cfg.Id] = created;
            return created;
        }

        if (Enum.TryParse<CliType>(id, out var ct))
        {
            return new CliItemInfo { Id = id, Type = ct, ShortName = id, DisplayName = id };
        }

        return new CliItemInfo { Id = id, ShortName = id, DisplayName = id };
    }

    private static string CliTypeToNormalizedId(CliType type) => type switch
    {
        CliType.ClaudeCode => "claude_code",
        CliType.AntigravityCli => "antigravity_cli",
        _ => "hub"
    };

    public CliItemInfo GetInfo(CliType type) => GetInfo(CliTypeToNormalizedId(type));

    public IReadOnlyList<CliItemInfo> GetAllInfos()
    {
        var workspaces = AiConfigService.Instance.CurrentConfig.CliWorkspaces;
        foreach (var ws in workspaces)
        {
            GetInfo(ws.Id);
        }
        foreach (var prof in _profileService.GetProfiles())
        {
            GetInfo(FormatAntigravityProfileId(prof));
        }
        foreach (var custom in CustomCliService.Instance.GetCustomClis())
        {
            GetInfo(custom.Id);
        }
        return [.. _cliStates.Values];
    }

    public int GetActiveCount()
    {
        return _cliStates.Values.Count(x => x.State == CliState.Running || x.State == CliState.Starting);
    }

    public void Start(CliType type) => Start(CliTypeToNormalizedId(type));

    public void Start(string id)
    {
        id = ResolveSessionId(id);
        bool isAgyProf = IsAntigravityProfileId(id, out var profileName);
        if (!isAgyProf)
        {
            if (string.Equals(id, "hub", StringComparison.OrdinalIgnoreCase))
            {
                _hubService.Start();
                return;
            }
        }

        var info = GetInfo(id);
        if (info.State == CliState.Running || info.State == CliState.Starting)
        {
            return;
        }

        info.State = CliState.Starting;
        info.StatusText = $"Starting {info.ShortName}...";
        FireStatusChanged(id, CliState.Starting, info.StatusText);
        LogMessage?.Invoke(info.ShortName, $"Launching interactive {info.ShortName} session...");

        try
        {
            if (_terminalSessions.TryRemove(id, out var oldSession))
            {
                oldSession.Dispose();
            }

            var buf = _terminalBuffers.GetOrAdd(id, _ => new StringBuilder());
            lock (buf)
            {
                buf.Clear();
            }

            var session = new ConPtySession();

            session.OutputDataReceived += (text) =>
            {
                lock (buf)
                {
                    buf.Append(text);
                    if (buf.Length > MaxBufferSize)
                    {
                        buf.Remove(0, buf.Length - MaxBufferSize);
                    }
                }

                FireTerminalDataReceived(id, text);
            };

            session.Exited += (exitCode) =>
            {
                if (IsAntigravityProfileId(id, out var exitProf))
                {
                    try
                    {
                        _profileService.StopVirtualHomeWatcher(exitProf);
                        _profileService.SyncVirtualHomeToProfile(exitProf);
                    }
                    catch { }
                }
                else if (string.Equals(id, "AntigravityCli", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(id, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _profileService.ExportCurrentSession(_profileService.ActiveProfile);
                    }
                    catch { }
                }

                info.State = CliState.Offline;
                info.StatusText = exitCode == 0 ? "Exited cleanly" : $"Exited with code {exitCode}";
                info.ProcessId = 0;
                FireStatusChanged(id, CliState.Offline, info.StatusText);
                LogMessage?.Invoke(info.ShortName, $"Process terminated with exit code {exitCode}.");
            };

            string command;
            string workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            IDictionary<string, string>? envVars = null;

            if (isAgyProf)
            {
                int port = AllocatePortForProfile(profileName);
                info.Port = port;
                info.WebUrl = $"http://127.0.0.1:{port}";

                // agy authenticates from the single, machine-wide 'gemini:antigravity'
                // credential - the per-profile VirtualHome alone is not enough. Applying the
                // profile here is what makes a session started from a tab pick up its OWN
                // account. Without it the process reads whatever credential was applied last,
                // which is why tab-started sessions showed the other profile's email while the
                // dropdown path (which already applies) was correct.
                bool applied = _profileService.ApplyProfile(profileName);
                if (!applied)
                {
                    LogMessage?.Invoke("Antigravity", $"Warning: could not apply profile '{profileName}'. The session may start with another profile's credentials.");
                }

                string virtualHomeDir = _profileService.PrepareVirtualHome(profileName);
                _profileService.StartVirtualHomeWatcher(profileName);

                // Bind this profile's allocated port to its own terminal session, so the
                // "Open in browser" action on an Antigravity tab has something to connect to.
                // Without it the port is just a number and the browser reports
                // ERR_CONNECTION_REFUSED.
                _webTerminalServer.EnsureEndpoint(id, $"agy: {profileName}", port);

                // Name the account this session will authenticate as, so cross-profile
                // contamination is visible in the log instead of only in the CLI banner.
                string accountEmail = AntigravityProfileService.GetAccountEmail(profileName) ?? string.Empty;
                LogMessage?.Invoke("Antigravity", string.IsNullOrWhiteSpace(accountEmail)
                    ? $"Launching profile '{profileName}' on port {port} (no saved account email)."
                    : $"Launching profile '{profileName}' as {accountEmail} on port {port}.");

                envVars = new Dictionary<string, string>
                {
                    ["USERPROFILE"] = virtualHomeDir,
                    ["HOME"] = virtualHomeDir,
                    ["APPDATA"] = Path.Combine(virtualHomeDir, "AppData", "Roaming"),
                    ["LOCALAPPDATA"] = Path.Combine(virtualHomeDir, "AppData", "Local"),
                    ["PORT"] = port.ToString(),
                    ["ANTIGRAVITY_PORT"] = port.ToString()
                };

                bool autoResume = _profileService.GetProfileAutoResume(profileName);
                string continueFlag = autoResume ? " --continue" : "";
                bool skipPerms = _profileService.GetProfileSkipPermissions(profileName);
                string permsFlag = skipPerms ? " --dangerously-skip-permissions" : "";
                command = $"cmd.exe /c agy{continueFlag}{permsFlag}";

                string? savedWorkDir = _profileService.GetProfileWorkingDirectory(profileName);
                if (!string.IsNullOrWhiteSpace(savedWorkDir) && Directory.Exists(savedWorkDir))
                {
                    workDir = savedWorkDir;
                }
                else
                {
                    workDir = virtualHomeDir;
                }
            }
            else if (string.Equals(id, "ClaudeCode", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(id, "claude_code", StringComparison.OrdinalIgnoreCase))
            {
                bool autoResume = AiConfigService.Instance.GetWorkspaceAutoResume("claude_code");
                string continueFlag = autoResume ? " --continue" : "";
                bool skipPerms = AiConfigService.Instance.GetWorkspaceSkipPermissions("claude_code");
                string permsFlag = skipPerms ? " --dangerously-skip-permissions" : "";
                command = $"cmd.exe /c claude{continueFlag}{permsFlag}";

                string? savedWorkDir = AiConfigService.Instance.GetWorkspaceWorkingDirectory("claude_code");
                if (!string.IsNullOrWhiteSpace(savedWorkDir) && Directory.Exists(savedWorkDir))
                {
                    workDir = savedWorkDir;
                }

                bool injectionEnabled = AiConfigService.Instance.GetClaudeInjectionEnabled();
                if (injectionEnabled)
                {
                    var activeProvider = AiConfigService.Instance.GetActiveClaudeGatewayProvider();
                    string selectedClaudeModel = AiConfigService.Instance.GetClaudeSelectedModel();
                    // The proxy now rejects callers that do not present a configured key, so the
                    // child gets the same resolved key the pool would accept.
                    string providerKey = activeProvider.GetEffectiveApiKeys().FirstOrDefault() ?? activeProvider.ApiKey;

                    // If active provider is local proxy (or port 9230), ensure local proxy is running
                    if (string.Equals(activeProvider.Id, "preset_local_proxy", StringComparison.OrdinalIgnoreCase) ||
                        activeProvider.BaseUrl.Contains(":9230"))
                    {
                        ClaudeOpenRouterProxyService.Instance.Start(9230);
                    }

                    // Pre-emptively suppress "Both ANTHROPIC_AUTH_TOKEN and /login managed key set" conflict
                    EnsureClaudeOpenRouterTokenPrecedence();

                    // Synchronize %USERPROFILE%\.claude\settings.json with provider env and strip ANTHROPIC_API_KEY
                    SyncClaudeGatewaySettingsJson(providerKey, selectedClaudeModel, activeProvider.BaseUrl);

                    // Strictly inject ONLY ANTHROPIC_AUTH_TOKEN and explicitly remove ANTHROPIC_API_KEY
                    envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ANTHROPIC_BASE_URL"] = string.IsNullOrWhiteSpace(activeProvider.BaseUrl) ? "http://127.0.0.1:9230" : activeProvider.BaseUrl,
                        ["ANTHROPIC_AUTH_TOKEN"] = providerKey,
                        ["ANTHROPIC_API_KEY"] = "", // Empty signals ConPtySession to remove from child environment
                        ["ANTHROPIC_MODEL"] = selectedClaudeModel,
                        ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "deepseek/deepseek-r1:free"
                    };

                    try { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null); } catch { }

                    if (string.IsNullOrWhiteSpace(activeProvider.ApiKey))
                    {
                        LogMessage?.Invoke(info.ShortName, $"Notice: API Key for '{activeProvider.Name}' is empty. Configure key in Gateway Engine.");
                    }
                    else
                    {
                        LogMessage?.Invoke(info.ShortName, $"Gateway active: [{activeProvider.Name}] Model: {selectedClaudeModel} via {activeProvider.BaseUrl}");
                    }
                }
                else
                {
                    RestoreClaudeOfficialLoginIfBackedUp();
                    LogMessage?.Invoke(info.ShortName, "Official Anthropic API mode active (Environment injection disabled).");
                }
            }
            else
            {
                if (CustomCliService.Instance.TryGetCustomCli(id, out var customCli))
                {
                    command = customCli.GetCommandLine();
                    if (!string.IsNullOrWhiteSpace(customCli.WorkingDirectory) && Directory.Exists(customCli.WorkingDirectory))
                    {
                        workDir = customCli.WorkingDirectory;
                    }
                    envVars = new Dictionary<string, string>(customCli.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    var cfg = AiConfigService.Instance.CurrentConfig.CliWorkspaces.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                    var rawCmd = cfg != null && !string.IsNullOrWhiteSpace(cfg.Command) ? cfg.Command.Trim() : id;
                    if (!string.IsNullOrWhiteSpace(cfg?.WorkingDirectory) && Directory.Exists(cfg.WorkingDirectory))
                    {
                        workDir = cfg.WorkingDirectory;
                    }

                    if (string.Equals(rawCmd, "cmd", StringComparison.OrdinalIgnoreCase) || rawCmd.StartsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        command = rawCmd.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rawCmd : "cmd.exe";
                    }
                    else if (rawCmd.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        command = rawCmd;
                    }
                    else
                    {
                        command = $"cmd.exe /c {rawCmd}";
                    }
                }
            }

            bool started = session.Start(command, workDir, 120, 32, envVars);
            if (started)
            {
                _terminalSessions[id] = session;
                info.State = CliState.Running;
                info.ProcessId = session.ProcessId;
                info.StartTime = DateTime.UtcNow;
                string statusDetail = string.Equals(id, "ClaudeCode", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "claude_code", StringComparison.OrdinalIgnoreCase)
                    ? (AiConfigService.Instance.GetClaudeInjectionEnabled() ? $", {AiConfigService.Instance.GetActiveClaudeGatewayProvider().Name}" : ", Official")
                    : (info.Port != null ? $", Port: {info.Port}" : "");
                info.StatusText = $"Running (PID: {session.ProcessId}{statusDetail})";
                FireStatusChanged(id, CliState.Running, info.StatusText);
                LogMessage?.Invoke(info.ShortName, $"Session active with PID {session.ProcessId}{statusDetail}.");
            }
            else
            {
                info.State = CliState.Error;
                info.StatusText = "Failed to launch session.";
                FireStatusChanged(id, CliState.Error, info.StatusText);
                LogMessage?.Invoke(info.ShortName, "Session launch failed.");
            }
        }
        catch (Exception ex)
        {
            info.State = CliState.Error;
            info.StatusText = $"Launch error: {ex.Message}";
            FireStatusChanged(id, CliState.Error, info.StatusText);
            LogMessage?.Invoke(info.ShortName, $"Exception during launch: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the command, working directory and environment needed to run a tab's CLI in an
    /// external console window. Mirrors the rules used when starting an embedded session.
    /// Returns null when the id is empty.
    /// </summary>
    public ExternalLaunchSpec? BuildExternalLaunchSpec(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Antigravity profile tab: run inside the profile's isolated virtual home.
        if (IsAntigravityProfileId(id, out var profileName))
        {
            int port = AllocatePortForProfile(profileName);
            string virtualHomeDir = _profileService.PrepareVirtualHome(profileName);

            var agyEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USERPROFILE"] = virtualHomeDir,
                ["HOME"] = virtualHomeDir,
                ["APPDATA"] = Path.Combine(virtualHomeDir, "AppData", "Roaming"),
                ["LOCALAPPDATA"] = Path.Combine(virtualHomeDir, "AppData", "Local"),
                ["PORT"] = port.ToString(),
                ["ANTIGRAVITY_PORT"] = port.ToString()
            };

            bool autoResume = _profileService.GetProfileAutoResume(profileName);
            string continueFlag = autoResume ? " --continue" : "";
            bool skipPerms = _profileService.GetProfileSkipPermissions(profileName);
            string permsFlag = skipPerms ? " --dangerously-skip-permissions" : "";
            string? savedWorkDir = _profileService.GetProfileWorkingDirectory(profileName);
            string agyWorkDir = (!string.IsNullOrWhiteSpace(savedWorkDir) && Directory.Exists(savedWorkDir))
                ? savedWorkDir
                : virtualHomeDir;

            return new ExternalLaunchSpec
            {
                Command = $"agy{continueFlag}{permsFlag}",
                WorkingDirectory = agyWorkDir,
                EnvironmentVariables = agyEnv
            };
        }

        string normalized = AiConfigService.NormalizeCliId(id);

        if (string.Equals(normalized, "claude_code", StringComparison.OrdinalIgnoreCase))
        {
            var claudeEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (AiConfigService.Instance.GetClaudeInjectionEnabled())
            {
                var activeProvider = AiConfigService.Instance.GetActiveClaudeGatewayProvider();
                string providerKey = activeProvider.GetEffectiveApiKeys().FirstOrDefault() ?? activeProvider.ApiKey;

                // The local translation proxy must be listening before claude talks to it.
                if (string.Equals(activeProvider.Id, "preset_local_proxy", StringComparison.OrdinalIgnoreCase) ||
                    activeProvider.BaseUrl.Contains(":9230"))
                {
                    ClaudeOpenRouterProxyService.Instance.Start(9230);
                }

                claudeEnv["ANTHROPIC_BASE_URL"] = string.IsNullOrWhiteSpace(activeProvider.BaseUrl)
                    ? "http://127.0.0.1:9230"
                    : activeProvider.BaseUrl;
                claudeEnv["ANTHROPIC_AUTH_TOKEN"] = providerKey;
                claudeEnv["ANTHROPIC_MODEL"] = AiConfigService.Instance.GetClaudeSelectedModel();
                claudeEnv["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "deepseek/deepseek-r1:free";
            }

            bool autoResume = AiConfigService.Instance.GetWorkspaceAutoResume("claude_code");
            string continueFlag = autoResume ? " --continue" : "";
            bool skipPerms = AiConfigService.Instance.GetWorkspaceSkipPermissions("claude_code");
            string permsFlag = skipPerms ? " --dangerously-skip-permissions" : "";
            string? savedWorkDir = AiConfigService.Instance.GetWorkspaceWorkingDirectory("claude_code");
            string claudeWorkDir = (!string.IsNullOrWhiteSpace(savedWorkDir) && Directory.Exists(savedWorkDir))
                ? savedWorkDir
                : userProfile;

            return new ExternalLaunchSpec
            {
                Command = $"claude{continueFlag}{permsFlag}",
                WorkingDirectory = claudeWorkDir,
                EnvironmentVariables = claudeEnv
            };
        }

        if (string.Equals(normalized, "hub", StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalLaunchSpec
            {
                Command = $"hub dashboard --port {_hubService.Port}",
                WorkingDirectory = userProfile,
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        // Custom workspace: use its configured command and working directory.
        var cfg = AiConfigService.Instance.CurrentConfig.CliWorkspaces
            .FirstOrDefault(c => string.Equals(AiConfigService.NormalizeCliId(c.Id), normalized, StringComparison.OrdinalIgnoreCase));

        string rawCmd = cfg != null && !string.IsNullOrWhiteSpace(cfg.Command) ? cfg.Command.Trim() : id;
        string workDir = cfg != null && !string.IsNullOrWhiteSpace(cfg.WorkingDirectory) && Directory.Exists(cfg.WorkingDirectory)
            ? cfg.WorkingDirectory
            : userProfile;

        return new ExternalLaunchSpec
        {
            Command = rawCmd,
            WorkingDirectory = workDir,
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public Task StopAsync(CliType type) => StopAsync(CliTypeToNormalizedId(type));

    public async Task StopAsync(string id)
    {
        id = ResolveSessionId(id);
        bool isAgyProf = IsAntigravityProfileId(id, out var profileName);
        if (!isAgyProf)
        {
            if (string.Equals(id, "hub", StringComparison.OrdinalIgnoreCase))
            {
                await _hubService.StopAsync();
                return;
            }
        }

        if (isAgyProf)
        {
            try
            {
                _profileService.StopVirtualHomeWatcher(profileName);
                _profileService.SyncVirtualHomeToProfile(profileName);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke("Antigravity Profile", $"Pre-stop sync notice: {ex.Message}");
            }
        }
        else if (string.Equals(id, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _profileService.ExportCurrentSession(_profileService.ActiveProfile);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke("Antigravity Profile", $"Pre-stop export error: {ex.Message}");
            }
        }

        var info = GetInfo(id);
        info.StatusText = $"Stopping {info.ShortName}...";
        FireStatusChanged(id, CliState.Offline, info.StatusText);
        LogMessage?.Invoke(info.ShortName, "Terminating interactive session...");

        await Task.Run(() =>
        {
            if (_terminalSessions.TryRemove(id, out var session))
            {
                session.Stop();
                session.Dispose();
            }

            info.State = CliState.Offline;
            info.StatusText = "CLI: Offline";
            info.ProcessId = 0;
            FireStatusChanged(id, CliState.Offline, info.StatusText);
            LogMessage?.Invoke(info.ShortName, "Session stopped.");
        });
    }

    public Task RestartAsync(CliType type) => RestartAsync(CliTypeToNormalizedId(type));

    public async Task RestartAsync(string id)
    {
        if (!IsAntigravityProfileId(id, out _))
        {
            id = AiConfigService.NormalizeCliId(id);
        }
        await StopAsync(id);
        await Task.Delay(300);
        Start(id);
    }

    public void SendInput(CliType type, string data) => SendInput(CliTypeToNormalizedId(type), data);

    public void SendInput(string id, string data)
    {
        id = ResolveSessionId(id);
        if (_terminalSessions.TryGetValue(id, out var session) && session.IsRunning)
        {
            session.Write(data);
            InputSentId?.Invoke(id, data);
        }
    }

    public void Resize(CliType type, short cols, short rows) => Resize(CliTypeToNormalizedId(type), cols, rows);

    public void Resize(string id, short cols, short rows)
    {
        id = ResolveSessionId(id);
        if (_terminalSessions.TryGetValue(id, out var session) && session.IsRunning)
        {
            session.Resize(cols, rows);
        }
    }

    public string GetTerminalHistory(CliType type) => GetTerminalHistory(CliTypeToNormalizedId(type));

    public string GetTerminalHistory(string id)
    {
        id = ResolveSessionId(id);
        if (_terminalBuffers.TryGetValue(id, out var buf))
        {
            lock (buf)
            {
                return buf.ToString();
            }
        }

        return string.Empty;
    }

    public void ClearTerminalHistory(CliType type) => ClearTerminalHistory(CliTypeToNormalizedId(type));

    public void ClearTerminalHistory(string id)
    {
        id = ResolveSessionId(id);
        if (_terminalBuffers.TryGetValue(id, out var buf))
        {
            lock (buf)
            {
                buf.Clear();
            }
        }
    }

    private void FireStatusChanged(string id, CliState state, string message)
    {
        StatusChangedId?.Invoke(id, state, message);
        var ct = id switch
        {
            "claude_code" => CliType.ClaudeCode,
            "antigravity_cli" => CliType.AntigravityCli,
            "hub" => CliType.Hub,
            _ => (CliType?)null
        };
        if (!ct.HasValue && IsAntigravityProfileId(id, out _))
        {
            ct = CliType.AntigravityCli;
        }
        if (ct.HasValue)
        {
            StatusChanged?.Invoke(ct.Value, state, message);
        }
    }

    private void FireTerminalDataReceived(string id, string data)
    {
        TerminalDataReceivedId?.Invoke(id, data);
        var ct = id switch
        {
            "claude_code" => CliType.ClaudeCode,
            "antigravity_cli" => CliType.AntigravityCli,
            "hub" => CliType.Hub,
            _ => (CliType?)null
        };
        if (!ct.HasValue && IsAntigravityProfileId(id, out _))
        {
            ct = CliType.AntigravityCli;
        }
        if (ct.HasValue)
        {
            TerminalDataReceived?.Invoke(ct.Value, data);
        }
    }

    public async Task<bool> SwitchAntigravityProfileAsync(string profileName)
    {
        LogMessage?.Invoke("Antigravity", $"Switching active profile to '{profileName}'...");

        bool applied = _profileService.ApplyProfile(profileName);
        if (applied)
        {
            _profileService.PrepareVirtualHome(profileName);
        }

        await Task.CompletedTask;
        return applied;
    }

    public async Task<bool> CreateAndSwitchNewProfileAsync(string newProfileName)
    {
        LogMessage?.Invoke("Antigravity", $"Preparing new onboarding profile '{newProfileName}'...");

        bool prepared = _profileService.PrepareNewProfile(newProfileName);
        if (prepared)
        {
            _profileService.PrepareVirtualHome(newProfileName);
            await Task.Delay(200);
            StartAntigravityProfile(newProfileName);
        }

        return prepared;
    }

    public async Task<bool> ClaudeLogoutAsync()
    {
        try
        {
            var info = GetInfo("claude_code");
            if (info.State == CliState.Running || info.State == CliState.Starting)
            {
                await StopAsync("claude_code");
            }

            try
            {
                using var p = new Process
                {
                    StartInfo = Services.Platform.PlatformHelper.IsWindows
                        ? new ProcessStartInfo
                        {
                            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd",
                            Arguments = "/c claude logout",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        }
                        : new ProcessStartInfo
                        {
                            FileName = Services.Platform.PlatformHelper.GetDefaultShell(),
                            Arguments = "-c \"claude logout\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        }
                };
                p.Start();
                await p.WaitForExitAsync();
            }
            catch { }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var claudeJsonPath = Path.Combine(userProfile, ".claude.json");
            var backupPath = Path.Combine(userProfile, ".claude.json.authbak");
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { }
            }

            if (File.Exists(claudeJsonPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(claudeJsonPath);
                    using var doc = JsonDocument.Parse(content);
                    var dict = new Dictionary<string, object?>();

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "oauthAccount", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(prop.Name, "primaryApiKey", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(prop.Name, "sessionKey", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        dict[prop.Name] = JsonElementToValue(prop.Value);
                    }

                    var newJson = JsonSerializer.Serialize(dict, IndentedJsonOptions);
                    await File.WriteAllTextAsync(claudeJsonPath, newJson);
                }
                catch { }
            }

            var credsPath = Path.Combine(userProfile, ".claude", ".credentials.json");
            if (File.Exists(credsPath))
            {
                try { await File.WriteAllTextAsync(credsPath, "{}"); } catch { }
            }

            var settingsJsonPath = Path.Combine(userProfile, ".claude", "settings.json");
            if (File.Exists(settingsJsonPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(settingsJsonPath);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            var rootDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                if (string.Equals(prop.Name, "env", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Object)
                                {
                                    var envDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var ep in prop.Value.EnumerateObject())
                                    {
                                        if (string.Equals(ep.Name, "ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase))
                                        {
                                            continue;
                                        }
                                        envDict[ep.Name] = JsonElementToValue(ep.Value);
                                    }
                                    rootDict["env"] = envDict;
                                }
                                else
                                {
                                    rootDict[prop.Name] = JsonElementToValue(prop.Value);
                                }
                            }
                            var newJson = JsonSerializer.Serialize(rootDict, IndentedJsonOptions);
                            await File.WriteAllTextAsync(settingsJsonPath, newJson);
                        }
                    }
                }
                catch { }
            }

            LogMessage?.Invoke("Claude Code", "Claude login credentials logged out. Token conflict resolved.");
            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke("Claude Code", $"Error during Claude logout: {ex.Message}");
            return false;
        }
    }

    public static void SyncClaudeGatewaySettingsJson(string apiKey, string selectedClaudeModel, string gatewayUrl)
    {
        SyncClaudeOpenRouterSettingsJson(apiKey, selectedClaudeModel, gatewayUrl);
    }

    public static void SyncClaudeOpenRouterSettingsJson(string openRouterApiKey, string selectedClaudeModel, string gatewayUrl)
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var claudeDir = Path.Combine(userProfile, ".claude");
            var settingsJsonPath = Path.Combine(claudeDir, "settings.json");

            var rootDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(settingsJsonPath))
            {
                var content = File.ReadAllText(settingsJsonPath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            rootDict[prop.Name] = JsonElementToValue(prop.Value);
                        }
                    }
                }
            }
            else if (!Directory.Exists(claudeDir))
            {
                Directory.CreateDirectory(claudeDir);
            }

            var envDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (rootDict.TryGetValue("env", out var existingEnvObj))
            {
                if (existingEnvObj is JsonElement envElem && envElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in envElem.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        envDict[prop.Name] = JsonElementToValue(prop.Value);
                    }
                }
                else if (existingEnvObj is Dictionary<string, object?> existingEnvMap)
                {
                    foreach (var kvp in existingEnvMap)
                    {
                        if (string.Equals(kvp.Key, "ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        envDict[kvp.Key] = kvp.Value;
                    }
                }
            }

            var effectiveGateway = string.IsNullOrWhiteSpace(gatewayUrl) ? "https://openrouter.ai/api/v1" : gatewayUrl;
            var effectiveModel = string.IsNullOrWhiteSpace(selectedClaudeModel) ? "qwen/qwen-2.5-coder-32b-instruct:free" : selectedClaudeModel;

            envDict["ANTHROPIC_BASE_URL"] = effectiveGateway;
            envDict["ANTHROPIC_AUTH_TOKEN"] = openRouterApiKey;
            envDict.Remove("ANTHROPIC_API_KEY");
            envDict["ANTHROPIC_MODEL"] = effectiveModel;
            envDict["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "deepseek/deepseek-r1:free";

            rootDict["env"] = envDict;

            var newJson = JsonSerializer.Serialize(rootDict, IndentedJsonOptions);
            File.WriteAllText(settingsJsonPath, newJson);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CliManagerService] Error syncing Claude settings.json: {ex.Message}");
        }
    }

    private static void EnsureClaudeOpenRouterTokenPrecedence()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var claudeJsonPath = Path.Combine(userProfile, ".claude.json");
            var backupPath = Path.Combine(userProfile, ".claude.json.authbak");

            if (File.Exists(claudeJsonPath))
            {
                var content = File.ReadAllText(claudeJsonPath);
                using var doc = JsonDocument.Parse(content);
                bool hasAuthTokens = doc.RootElement.TryGetProperty("oauthAccount", out _) ||
                                     doc.RootElement.TryGetProperty("primaryApiKey", out _) ||
                                     doc.RootElement.TryGetProperty("sessionKey", out _);

                if (hasAuthTokens)
                {
                    File.Copy(claudeJsonPath, backupPath, overwrite: true);

                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "oauthAccount", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(prop.Name, "primaryApiKey", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(prop.Name, "sessionKey", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        dict[prop.Name] = JsonElementToValue(prop.Value);
                    }

                    var newJson = JsonSerializer.Serialize(dict, IndentedJsonOptions);
                    File.WriteAllText(claudeJsonPath, newJson);
                }
            }
        }
        catch { }
    }

    public static void RestoreClaudeOfficialLoginIfBackedUp()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var claudeJsonPath = Path.Combine(userProfile, ".claude.json");
            var backupPath = Path.Combine(userProfile, ".claude.json.authbak");

            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, claudeJsonPath, overwrite: true);
                File.Delete(backupPath);
            }

            var settingsJsonPath = Path.Combine(userProfile, ".claude", "settings.json");
            if (File.Exists(settingsJsonPath))
            {
                var content = File.ReadAllText(settingsJsonPath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var rootDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (string.Equals(prop.Name, "env", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Object)
                            {
                                var envDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                                foreach (var ep in prop.Value.EnumerateObject())
                                {
                                    if (string.Equals(ep.Name, "ANTHROPIC_BASE_URL", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(ep.Name, "ANTHROPIC_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(ep.Name, "ANTHROPIC_MODEL", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(ep.Name, "ANTHROPIC_DEFAULT_OPUS_MODEL", StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }
                                    envDict[ep.Name] = JsonElementToValue(ep.Value);
                                }
                                if (envDict.Count > 0)
                                {
                                    rootDict["env"] = envDict;
                                }
                            }
                            else
                            {
                                rootDict[prop.Name] = JsonElementToValue(prop.Value);
                            }
                        }
                        var newJson = JsonSerializer.Serialize(rootDict, IndentedJsonOptions);
                        File.WriteAllText(settingsJsonPath, newJson);
                    }
                }
            }
        }
        catch { }
    }

    private static object? JsonElementToValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => JsonDocument.Parse(el.GetRawText()).RootElement.Clone()
    };

    public void Dispose()
    {
        foreach (var session in _terminalSessions.Values)
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // Suppress disposal errors on shutdown
            }
        }
        _terminalSessions.Clear();
        _webTerminalServer.Dispose();
        _hubService.Dispose();
        _profileService.Dispose();
        GC.SuppressFinalize(this);
    }
}
