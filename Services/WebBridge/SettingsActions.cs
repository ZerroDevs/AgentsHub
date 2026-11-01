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
    private Task HandleOpenLogBundleLocationAsync(JsonElement data, string? requestId)
    {
        {
            // Reveal the file in the file manager rather than launching it: a log is something
            // to look at, and a .txt opening in an editor is not what "show me where it is" means.
            string bundlePath = data.TryGetProperty("path", out var bundlePathProp)
                ? bundlePathProp.GetString() ?? string.Empty
                : string.Empty;

            bool revealed = false;

            if (!string.IsNullOrWhiteSpace(bundlePath) && File.Exists(bundlePath))
            {
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo { UseShellExecute = true };

                    if (Services.Platform.PlatformHelper.IsWindows)
                    {
                        info.FileName = "explorer.exe";
                        info.Arguments = $"/select,\"{bundlePath}\"";
                    }
                    else if (Services.Platform.PlatformHelper.IsMacOS)
                    {
                        info.FileName = "open";
                        info.Arguments = $"-R \"{bundlePath}\"";
                    }
                    else
                    {
                        info.FileName = "xdg-open";
                        info.Arguments = $"\"{Path.GetDirectoryName(bundlePath)}\"";
                    }

                    System.Diagnostics.Process.Start(info);
                    revealed = true;
                }
                catch (Exception ex)
                {
                    SendResponse(requestId, false, $"Could not open the folder: {ex.Message}");
                    return Task.CompletedTask;
                }
            }

            SendResponse(requestId, true, new { revealed });
        }

        return Task.CompletedTask;
    }

    private Task HandleExportLogBundleAsync(string? requestId)
    {
        {
            try
            {
                var (path, files, bytes) = Services.LogBundleService.Export();
                SendResponse(requestId, true, new { path, files, bytes });
            }
            catch (Exception ex)
            {
                SendResponse(requestId, false, $"Could not build the bundle: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleEngineHealthAsync(string? requestId)
    {
        {
            // Passive on purpose: nothing here navigates a page or probes a token, because a
            // health check that can hang is worse than no health check.
            var qwen = Services.Qwen.QwenEngine.Instance.Health();
            var deepSeek = Services.DeepSeek.DeepSeekEngine.Instance;

            SendResponse(requestId, true, new
            {
                engines = new object[]
                {
                    new
                    {
                        id = "deepseek",
                        name = "DeepSeek",
                        initialized = deepSeek.IsInitialized,
                        signedIn = deepSeek.IsSignedIn,
                        primedIn = (string?)null,
                        lastTurnAt = (string?)null,
                        lastError = (string?)null,
                    },
                    new
                    {
                        id = "qwen",
                        name = "Qwen",
                        initialized = qwen.Initialized,
                        signedIn = (bool?)null,
                        primedIn = qwen.PrimedIn,
                        lastTurnAt = qwen.LastTurnAt?.ToString("O"),
                        lastError = qwen.LastError,
                    },
                },
            });
        }

        return Task.CompletedTask;
    }

#endif
#endif
    private Task HandleGetSidecarStatusAsync(string? requestId)
    {
        {
            SendResponse(requestId, true, Sidecar.SidecarHostService.Instance.GetStatusDto());
        }

        return Task.CompletedTask;
    }

    private Task HandleGetSystemTelemetryAsync(string? requestId)
    {
        // One sample, on request. This is the only place hardware is read.
        SendResponse(requestId, true, GetSystemTelemetryDto());

        return Task.CompletedTask;
    }

    private Task HandleReleaseSystemTelemetryAsync(string? requestId)
    {
        // The panel closed, so the network sampler stops instead of ticking in the background.
        NetworkTelemetryService.Stop();
        SendResponse(requestId, true, new { stopped = true });

        return Task.CompletedTask;
    }

    private async Task HandleCheckForUpdatesAsync(string? requestId)
    {
        var updateInfo = await UpdateService.Instance.CheckForUpdatesAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new
        {
            result = updateInfo.Result.ToString(),
            isUpdateAvailable = updateInfo.IsUpdateAvailable,
            latestVersion = updateInfo.LatestVersion,
            currentVersion = updateInfo.CurrentVersion,
            releaseName = updateInfo.ReleaseName,
            releaseNotes = updateInfo.ReleaseNotes,
            downloadUrl = updateInfo.DownloadUrl,
            assetSizeBytes = updateInfo.AssetSizeBytes,
            publishedAtUtc = updateInfo.PublishedAtUtc
        });
    }

    // Not async on purpose: the download is handed to Task.Run and this only reports that it
    // started. Keeping the keyword would add a state machine nothing awaits.
    private Task HandleInstallUpdateAsync(JsonElement data, string? requestId)
    {
        string? dlUrl = data.TryGetProperty("downloadUrl", out var dlProp) ? dlProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(dlUrl))
        {
            SendResponse(requestId, false, "Download URL is required");
            return Task.CompletedTask;
        }

        if (!UpdateService.IsTrustedReleaseUrl(dlUrl))
        {
            SendResponse(requestId, false, "Refusing to install from an untrusted URL.");
            return Task.CompletedTask;
        }

        SendResponse(requestId, true, new { status = "downloading" });

        var progress = new Progress<double>(p =>
        {
            PostEventToUi("update_download_progress", new { progress = p });
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await UpdateService.Instance.DownloadAndInstallAsync(dlUrl, progress).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PostEventToUi("update_download_failed", new { error = ex.Message });
            }
        });

        return Task.CompletedTask;
    }

    private Task HandleGetPreferenceDebugAsync(string? requestId)
    {
        {
            // Reports what is actually in the preference store, so a UI that disagrees with
            // the registry can be diagnosed instead of guessed at.
            var keys = new[]
            {
                "StartMinimized", "MinimizeToTray", "WindowsNotifications",
                "GlobalHotkey", "GlobalHotkeyModifier", "GlobalHotkeyKey",
                "GlobalHudHotkey", "HudHotkeyModifier", "HudHotkeyKey",
                "EmbedShellsInTerminalWorkspace", "HideMainOnCliOpen"
            };

            var values = new Dictionary<string, string>();
            foreach (var key in keys)
            {
                values[key] = key is "GlobalHotkeyModifier" or "GlobalHotkeyKey"
                    or "HudHotkeyModifier" or "HudHotkeyKey"
                    ? StartupHelper.GetStringPreference(key, "(unset)")
                    : StartupHelper.GetPreference(key, false).ToString();
            }

            SendResponse(requestId, true, new { values });
        }

        return Task.CompletedTask;
    }

    private Task HandleRunDependencyCliAsync(JsonElement data, string? requestId)
    {
        {
            // Opens a console running the tool itself. The dependency catalog already knows the
            // command name, so this cannot drift from what the scanner looks for.
            string runId = data.TryGetProperty("id", out var runIdProp) ? runIdProp.GetString() ?? string.Empty : string.Empty;
            var definition = DependencyCheckerService.Find(runId);
            string command = definition != null && definition.Commands.Length > 0
                ? definition.Commands[0]
                : string.Empty;

            bool started = false;

            if (command.Length > 0)
            {
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo
                    {
                        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        UseShellExecute = true,
                    };

                    if (Services.Platform.PlatformHelper.IsWindows)
                    {
                        info.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd";
                        info.Arguments = $"/k {command}";
                    }
                    else if (Services.Platform.PlatformHelper.IsMacOS)
                    {
                        info.FileName = "open";
                        info.Arguments = $"-a Terminal --args {command}";
                    }
                    else
                    {
                        info.FileName = "x-terminal-emulator";
                        info.Arguments = $"-e {command}";
                    }

                    System.Diagnostics.Process.Start(info);
                    started = true;
                }
                catch (Exception ex)
                {
                    SendResponse(requestId, false, $"Could not start {command}: {ex.Message}");
                    return Task.CompletedTask;
                }
            }

            SendResponse(requestId, true, new { started, command });
        }

        return Task.CompletedTask;
    }

    private Task HandleGetTokenTelemetryAsync(string? requestId)
    {
        SendResponse(requestId, true, ProxyTelemetryService.Instance.GetSummary());

        return Task.CompletedTask;
    }

    private Task HandleResetTokenTelemetryAsync(string? requestId)
    {
        ProxyTelemetryService.Instance.Reset();
        SendResponse(requestId, true, ProxyTelemetryService.Instance.GetSummary());
        PostEventToUi("token_telemetry_updated", ProxyTelemetryService.Instance.GetSummary());

        return Task.CompletedTask;
    }

    private Task HandleToggleQuakeHudAsync(string? requestId)
    {
#if WINDOWS
        if (_mainForm is MainForm mf)
        {
            mf.ExecuteQuakeHotkeyAction();
            SendResponse(requestId, true, new { success = true });
        }
        else
        {
            SendResponse(requestId, false, "MainForm reference unavailable");
        }
#else
        SendResponse(requestId, false, "Quake HUD is only available on Windows desktop");
#endif

        return Task.CompletedTask;
    }

    private Task HandleToggleMainWindowAsync(string? requestId)
    {
#if WINDOWS
        if (_mainForm is MainForm mainFormToggle)
        {
            mainFormToggle.ShowOrHideFromHotkey();
            SendResponse(requestId, true, new { success = true });
        }
        else
        {
            SendResponse(requestId, false, "MainForm reference unavailable");
        }
#else
        SendResponse(requestId, false, "Main window toggle is only available on Windows desktop");
#endif

        return Task.CompletedTask;
    }

    private Task HandleGetClaudeGatewayConfigAsync(string? requestId)
    {
        {
            var cfg = AiConfigService.Instance;
            var providers = cfg.GetClaudeGatewayProviders();
            var active = cfg.GetActiveClaudeGatewayProvider();
            SendResponse(requestId, true, new
            {
                injectionEnabled = cfg.GetClaudeInjectionEnabled(),
                selectedGatewayId = cfg.GetClaudeSelectedGatewayId(),
                selectedModel = cfg.GetClaudeSelectedModel(),
                providers = providers.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    baseUrl = p.BaseUrl,
                    apiKey = p.ApiKey,
                    apiKeys = p.GetEffectiveApiKeys(),
                    isPreset = p.IsPreset
                }),
                activeProvider = new
                {
                    id = active.Id,
                    name = active.Name,
                    baseUrl = active.BaseUrl,
                    apiKey = active.ApiKey,
                    apiKeys = active.GetEffectiveApiKeys(),
                    isPreset = active.IsPreset
                }
            });
        }

        return Task.CompletedTask;
    }

    private async Task HandleSaveClaudeGatewayConfigAsync(JsonElement data, string? requestId)
    {
        string provId = data.TryGetProperty("providerId", out var pIdEl) ? (pIdEl.GetString() ?? "") : "";
        string baseUrl = data.TryGetProperty("baseUrl", out var bEl) ? (bEl.GetString() ?? "") : "";
        string apiKey = data.TryGetProperty("apiKey", out var kEl) ? (kEl.GetString()?.Trim() ?? "") : "";
        string name = data.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";

        var apiKeysList = new List<string>();
        if (data.TryGetProperty("apiKeys", out var keysArr) && keysArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in keysArr.EnumerateArray())
            {
                var s = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s) && !apiKeysList.Contains(s))
                {
                    apiKeysList.Add(s);
                }
            }
        }
        if (apiKeysList.Count == 0 && !string.IsNullOrWhiteSpace(apiKey))
        {
            apiKeysList.Add(apiKey);
        }

        string primaryKey = apiKeysList.FirstOrDefault() ?? apiKey;

        var provider = new ClaudeGatewayProvider
        {
            Id = string.IsNullOrWhiteSpace(provId) ? "preset_local_proxy" : provId,
            Name = name,
            BaseUrl = baseUrl,
            ApiKey = primaryKey,
            ApiKeys = apiKeysList,
            IsPreset = provId.StartsWith("preset_", StringComparison.OrdinalIgnoreCase)
        };
        AiConfigService.Instance.SaveClaudeGatewayProvider(provider);

        if (string.Equals(AiConfigService.Instance.GetClaudeSelectedGatewayId(), provider.Id, StringComparison.OrdinalIgnoreCase))
        {
            CliManagerService.SyncClaudeGatewaySettingsJson(primaryKey, AiConfigService.Instance.GetClaudeSelectedModel(), baseUrl);
        }

        var info = _cliManager.GetInfo("claude_code");
        if (info.State == CliState.Running || info.State == CliState.Starting)
        {
            await _cliManager.RestartAsync("claude_code");
        }

        SendResponse(requestId, true, new
        {
            success = true,
            provider = new
            {
                id = provider.Id,
                name = provider.Name,
                baseUrl = provider.BaseUrl,
                apiKey = provider.ApiKey,
                apiKeys = provider.GetEffectiveApiKeys(),
                isPreset = provider.IsPreset
            }
        });
    }

    private Task HandleAddClaudeGatewayProviderAsync(JsonElement data, string? requestId)
    {
        {
            string name = data.TryGetProperty("name", out var nEl) ? (nEl.GetString()?.Trim() ?? "Custom Provider") : "Custom Provider";
            string baseUrl = data.TryGetProperty("baseUrl", out var bEl) ? (bEl.GetString()?.Trim() ?? "https://openrouter.ai/api/v1") : "https://openrouter.ai/api/v1";
            string apiKey = data.TryGetProperty("apiKey", out var kEl) ? (kEl.GetString()?.Trim() ?? "") : "";

            var newProv = new ClaudeGatewayProvider
            {
                Id = "custom_" + Guid.NewGuid().ToString("N")[..8],
                Name = string.IsNullOrWhiteSpace(name) ? "Custom Gateway" : name,
                BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://openrouter.ai/api/v1" : baseUrl,
                ApiKey = apiKey,
                IsPreset = false
            };
            AiConfigService.Instance.SaveClaudeGatewayProvider(newProv);
            AiConfigService.Instance.SetClaudeSelectedGatewayId(newProv.Id);
            SendResponse(requestId, true, new
            {
                success = true,
                provider = new
                {
                    id = newProv.Id,
                    name = newProv.Name,
                    baseUrl = newProv.BaseUrl,
                    apiKey = newProv.ApiKey,
                    isPreset = newProv.IsPreset
                }
            });
        }

        return Task.CompletedTask;
    }

    private Task HandleDeleteClaudeGatewayProviderAsync(JsonElement data, string? requestId)
    {
        {
            string provId = data.TryGetProperty("providerId", out var pIdEl) ? (pIdEl.GetString() ?? "") : "";
            bool deleted = AiConfigService.Instance.DeleteClaudeGatewayProvider(provId);
            SendResponse(requestId, deleted, new { success = deleted, selectedGatewayId = AiConfigService.Instance.GetClaudeSelectedGatewayId() });
        }

        return Task.CompletedTask;
    }

    private async Task HandleToggleClaudeInjectionAsync(JsonElement data, string? requestId)
    {
        bool enabled = data.TryGetProperty("enabled", out var enProp) ? enProp.GetBoolean() : !AiConfigService.Instance.GetClaudeInjectionEnabled();
        AiConfigService.Instance.SetClaudeInjectionEnabled(enabled);

        if (!enabled)
        {
            CliManagerService.RestoreClaudeOfficialLoginIfBackedUp();
        }
        else
        {
            var act = AiConfigService.Instance.GetActiveClaudeGatewayProvider();
            CliManagerService.SyncClaudeGatewaySettingsJson(act.ApiKey, AiConfigService.Instance.GetClaudeSelectedModel(), act.BaseUrl);
        }

        var info = _cliManager.GetInfo("claude_code");
        if (info.State == CliState.Running || info.State == CliState.Starting)
        {
            await _cliManager.RestartAsync("claude_code");
        }

        SendResponse(requestId, true, new { enabled, success = true });
    }

    private Task HandleSelectClaudeGatewayProviderAsync(JsonElement data, string? requestId)
    {
        {
            string provId = data.TryGetProperty("providerId", out var pIdEl) ? (pIdEl.GetString() ?? "") : "";
            if (!string.IsNullOrWhiteSpace(provId))
            {
                AiConfigService.Instance.SetClaudeSelectedGatewayId(provId);
                var act = AiConfigService.Instance.GetActiveClaudeGatewayProvider();
                if (AiConfigService.Instance.GetClaudeInjectionEnabled())
                {
                    CliManagerService.SyncClaudeGatewaySettingsJson(act.ApiKey, AiConfigService.Instance.GetClaudeSelectedModel(), act.BaseUrl);
                    if (string.Equals(act.Id, "preset_local_proxy", StringComparison.OrdinalIgnoreCase) || act.BaseUrl.Contains(":9230"))
                    {
                        ClaudeOpenRouterProxyService.Instance.Start(9230);
                    }
                }
            }
            var activeAct = AiConfigService.Instance.GetActiveClaudeGatewayProvider();
            SendResponse(requestId, true, new
            {
                selectedGatewayId = AiConfigService.Instance.GetClaudeSelectedGatewayId(),
                provider = new
                {
                    id = activeAct.Id,
                    name = activeAct.Name,
                    baseUrl = activeAct.BaseUrl,
                    apiKey = activeAct.ApiKey,
                    isPreset = activeAct.IsPreset
                }
            });
        }

        return Task.CompletedTask;
    }

    private Task HandleGetClaudeSettingsAsync(string? requestId)
    {
        {
            var cfg = AiConfigService.Instance;
            var apiKey = cfg.GetOpenRouterApiKey();
            SendResponse(requestId, true, new
            {
                mode = cfg.GetClaudeEngineMode(),
                model = cfg.GetClaudeSelectedModel(),
                gatewayUrl = cfg.GetClaudeCustomGatewayUrl(),
                hasApiKey = !string.IsNullOrWhiteSpace(apiKey),
                apiKey
            });
        }

        return Task.CompletedTask;
    }

    private async Task HandleSaveClaudeSettingsAsync(JsonElement data, string? requestId)
    {
        var cfg = AiConfigService.Instance;
        if (data.TryGetProperty("mode", out var modeProp))
        {
            var m = modeProp.GetString();
            if (!string.IsNullOrWhiteSpace(m)) cfg.SetClaudeEngineMode(m);
        }
        if (data.TryGetProperty("model", out var modelProp))
        {
            var mdl = modelProp.GetString();
            if (!string.IsNullOrWhiteSpace(mdl)) cfg.SetClaudeSelectedModel(mdl);
        }
        if (data.TryGetProperty("gatewayUrl", out var gwProp))
        {
            var gw = gwProp.GetString();
            if (!string.IsNullOrWhiteSpace(gw)) cfg.SetClaudeCustomGatewayUrl(gw);
        }
        if (data.TryGetProperty("apiKey", out var keyProp))
        {
            var k = keyProp.GetString()?.Trim();
            if (k != null) cfg.SetClaudeGatewayApiKey(k);
        }
        var apiKey = cfg.GetOpenRouterApiKey();
        var mode = cfg.GetClaudeEngineMode();
        if (string.Equals(mode, "OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            CliManagerService.SyncClaudeOpenRouterSettingsJson(apiKey, cfg.GetClaudeSelectedModel(), cfg.GetClaudeCustomGatewayUrl());
        }

        var info = _cliManager.GetInfo("claude_code");
        if (info.State == CliState.Running || info.State == CliState.Starting)
        {
            await _cliManager.RestartAsync("claude_code");
        }

        SendResponse(requestId, true, new
        {
            success = true,
            mode = cfg.GetClaudeEngineMode(),
            model = cfg.GetClaudeSelectedModel(),
            gatewayUrl = cfg.GetClaudeCustomGatewayUrl(),
            hasApiKey = !string.IsNullOrWhiteSpace(apiKey),
            apiKey
        });
    }

    private async Task HandleGetOpenrouterFreeModelsAsync(JsonElement data, string? requestId)
    {
        bool force = data.TryGetProperty("force", out var fProp) && fProp.GetBoolean();
        var models = await AiConfigService.Instance.FetchOpenRouterFreeModelsAsync(force);
        SendResponse(requestId, true, new { models, count = models.Count });
    }

    private async Task HandleRefreshOpenrouterFreeModelsAsync(string? requestId)
    {
        var models = await AiConfigService.Instance.FetchOpenRouterFreeModelsAsync(true);
        SendResponse(requestId, true, new { models, count = models.Count });
    }

    private async Task HandleSaveOpenrouterKeyAsync(JsonElement data, string? requestId)
    {
        var key = data.TryGetProperty("apiKey", out var kProp) ? (kProp.GetString()?.Trim() ?? string.Empty) : string.Empty;
        AiConfigService.Instance.SetClaudeGatewayApiKey(key);

        var engineMode = AiConfigService.Instance.GetClaudeEngineMode();
        if (string.Equals(engineMode, "OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            CliManagerService.SyncClaudeOpenRouterSettingsJson(
                key,
                AiConfigService.Instance.GetClaudeSelectedModel(),
                AiConfigService.Instance.GetClaudeCustomGatewayUrl());
        }

        var info = _cliManager.GetInfo("claude_code");
        if (info.State == CliState.Running || info.State == CliState.Starting)
        {
            await _cliManager.RestartAsync("claude_code");
        }

        SendResponse(requestId, true, new { success = true, hasApiKey = !string.IsNullOrWhiteSpace(key) });
    }

    private async Task HandleCheckOpenrouterKeyAsync(JsonElement data, string? requestId)
    {
        var key = data.TryGetProperty("apiKey", out var kProp) ? kProp.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = AiConfigService.Instance.GetOpenRouterApiKey();
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            SendResponse(requestId, true, new { valid = false, message = "No API key provided" });
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
            var resp = await http.GetAsync("https://openrouter.ai/api/v1/auth/key");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                using var kDoc = JsonDocument.Parse(body);
                var dataObj = kDoc.RootElement.TryGetProperty("data", out var dEl) ? dEl : default;
                double usage = dataObj.ValueKind == JsonValueKind.Object && dataObj.TryGetProperty("usage", out var uEl) ? uEl.GetDouble() : 0;
                double? limit = dataObj.ValueKind == JsonValueKind.Object && dataObj.TryGetProperty("limit", out var lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetDouble() : null;
                var label = dataObj.ValueKind == JsonValueKind.Object && dataObj.TryGetProperty("label", out var lbEl) ? lbEl.GetString() : "OpenRouter Key";
                SendResponse(requestId, true, new
                {
                    valid = true,
                    usage = Math.Round(usage, 4),
                    limit,
                    label,
                    message = limit.HasValue ? $"Limit: ${limit.Value:F2} | Usage: ${usage:F4}" : $"Usage: ${usage:F4}"
                });
            }
            else
            {
                SendResponse(requestId, true, new { valid = false, message = $"Invalid key (HTTP {(int)resp.StatusCode})" });
            }
        }
        catch (Exception ex)
        {
            SendResponse(requestId, true, new { valid = false, message = $"Check failed: {ex.Message}" });
        }
    }

    private async Task HandleRunClaudeLogoutAsync(string? requestId)
    {
        bool ok = await _cliManager.ClaudeLogoutAsync();
        SendResponse(requestId, ok, new { success = ok, message = ok ? "Claude credentials logged out." : "Logout failed." });
    }

    private Task HandleSaveAiConfigAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("config", out var cfgProp))
        {
            // Patch the live config rather than replacing it, so anything the dashboard
            // does not send is left alone.
            ApplyConfigPatch(AiConfigService.Instance.CurrentConfig, cfgProp);
            AiConfigService.Instance.SaveConfig(AiConfigService.Instance.CurrentConfig);
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private async Task HandleGetDependenciesStatusAsync(string? requestId)
    {
        var dependencies = await DependencyCheckerService.GetDependenciesStatusAsync();
        SendResponse(requestId, true, new { dependencies = dependencies.Select(ToDependencyDto).ToList() });
    }

    private Task HandleRunDependencyInstallAsync(JsonElement data, string? requestId)
    {
        {
            var dependencyId = data.TryGetProperty("id", out var depIdProp) ? depIdProp.GetString() : null;
            var definition = DependencyCheckerService.Find(dependencyId);

            if (definition == null)
            {
                SendResponse(requestId, false, "Unknown dependency.");
                return Task.CompletedTask;
            }

            // Acknowledge immediately: the installer streams its output as events, so the
            // UI never has to wait on a long-running script.
            SendResponse(requestId, true, new { success = true, started = true, id = definition.Id, name = definition.Name });
            _ = RunDependencyInstallAsync(definition);
        }

        return Task.CompletedTask;
    }

    private async Task HandleInstallAllDependenciesAsync(string? requestId)
    {
        var statuses = await DependencyCheckerService.GetDependenciesStatusAsync();
        var missing = statuses.Where(s => !s.IsInstalled).ToList();

        if (missing.Count == 0)
        {
            SendResponse(requestId, true, new { started = false, total = 0, names = Array.Empty<string>() });
            return;
        }

        List<DependencyDefinition> queue = [];
        foreach (var status in missing)
        {
            var found = DependencyCheckerService.Find(status.Id);
            if (found != null) queue.Add(found);
        }

        SendResponse(requestId, true, new
        {
            started = true,
            total = queue.Count,
            names = queue.Select(d => d!.Name).ToArray()
        });

        // One at a time on purpose: parallel winget installs contend for the same
        // package manager and log files, and the outcome becomes unreadable.
        _ = RunAllDependencyInstallsAsync(queue);
    }

    private Task HandleGetDevToolsAsync(string? requestId)
    {
        // First run picks the dashboard shortcuts from what is actually installed on this machine.
        // Idempotent: it marks itself done, so a user who clears the shortcuts keeps them cleared.
        AiConfigService.Instance.EnsureDashboardShortcuts();

        // Local reads only: File.Exists per tool. Cheap enough to call whenever the view
        // opens, so the path health on screen is never stale.
        SendResponse(requestId, true, new
        {
            devTools = BuildDevToolDtos(),
            shortcutsVisible = AiConfigService.Instance.CurrentConfig.ShortcutsVisible
        });

        return Task.CompletedTask;
    }

    private async Task HandleDetectAiClientsAsync(string? requestId)
    {
        // A bounded directory walk, so it runs off the UI thread and reports how far
        // it got rather than pretending to be exhaustive.
        var found = await Task.Run(() => InstalledClients.FindInstalledClients());

        var configured = AiConfigService.Instance.CurrentConfig.DevTools ?? [];
        var offers = found
            .Where(f => !configured.Any(d =>
                !string.IsNullOrWhiteSpace(d.Target) &&
                (string.Equals(Path.GetFileName(f.Target), Path.GetFileName(d.Target), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(f.Target, d.Target, StringComparison.OrdinalIgnoreCase))))
            .Select(f => new
            {
                name = f.Name,
                target = f.Target,
                isIde = f.IsIde,
                alreadyConfigured = configured.Any(d =>
                    !string.IsNullOrWhiteSpace(d.Target) &&
                    (string.Equals(Path.GetFileName(f.Target), Path.GetFileName(d.Target), StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(f.Target, d.Target, StringComparison.OrdinalIgnoreCase)))
            })
            .ToList();

        SendResponse(requestId, true, new
        {
            found = offers,
            scannedFor = InstalledClients.KnownClientNames,
            alreadyConfigured = found.Count - offers.Count
        });
    }

    private Task HandleLaunchToolAsync(JsonElement data, string? requestId)
    {
        {
            string? target = null;
            string? toolName = null;
            string? toolId = null;
            if (data.TryGetProperty("target", out var targetProp))
            {
                target = targetProp.GetString();
            }
            if (data.TryGetProperty("toolName", out var toolProp))
            {
                toolName = toolProp.GetString();
            }
            if (data.TryGetProperty("toolId", out var idProp))
            {
                toolId = idProp.GetString();
                if (string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(toolId))
                {
                    var cfg = AiConfigService.Instance.CurrentConfig;
                    var item = cfg.DevTools?.FirstOrDefault(d => string.Equals(d.Id, toolId, StringComparison.OrdinalIgnoreCase));
                    target = item?.Target;
                    if (string.IsNullOrWhiteSpace(toolName)) toolName = item?.Name;
                }
            }

            if (string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(toolName))
            {
                target = AiConfigService.Instance.GetToolTarget(toolName, string.Empty);
            }

            if (string.IsNullOrWhiteSpace(target) &&
                (string.Equals(toolId, "antigravity_app", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(toolName, "Antigravity App", StringComparison.OrdinalIgnoreCase)))
            {
                target = AiConfigService.FindAntigravityAppPath();
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                SendResponse(requestId, false, "Target path is empty. Set one with Browse first.");
                return Task.CompletedTask;
            }

            string? toolArguments = data.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() : null;
            string? toolWorkingDirectory = data.TryGetProperty("workingDirectory", out var wdProp) ? wdProp.GetString() : null;

            // The UI sends the tool it launched, so fall back to the stored values.
            if (!string.IsNullOrWhiteSpace(toolId))
            {
                var stored = AiConfigService.Instance.CurrentConfig.DevTools?
                    .FirstOrDefault(d => string.Equals(d.Id, toolId, StringComparison.OrdinalIgnoreCase));
                if (stored != null)
                {
                    toolArguments ??= stored.Arguments;
                    toolWorkingDirectory ??= stored.WorkingDirectory;
                }
            }

            var (launched, launchMessage) = LaunchExecutableTarget(
                target, toolName ?? "Tool", toolArguments ?? "", toolWorkingDirectory ?? "");
            SendResponse(requestId, launched, new { success = launched, target, message = launchMessage });
        }

        return Task.CompletedTask;
    }

    private Task HandleBrowseToolTargetAsync(JsonElement data, string? requestId)
    {
        {
            string tool = string.Empty;
            string? toolId = null;
            if (data.TryGetProperty("toolName", out var brToolProp))
            {
                tool = brToolProp.GetString() ?? string.Empty;
            }
            if (data.TryGetProperty("toolId", out var brToolIdProp))
            {
                toolId = brToolIdProp.GetString();
            }

            string? selectedPath = null;
#if WINDOWS
            if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
            {
                f.Invoke(() =>
                {
                    var localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
                    var localAntigravity = Path.Combine(localPrograms, "antigravity");
                    string initDir = Directory.Exists(localAntigravity) ? localAntigravity :
                                    Directory.Exists(localPrograms) ? localPrograms :
                                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                    using var ofd = new OpenFileDialog
                    {
                        Title = string.IsNullOrWhiteSpace(tool) ? "Select Executable Target" : $"Select Target Executable for {tool}",
                        Filter = "Executables (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All Files (*.*)|*.*",
                        InitialDirectory = initDir
                    };
                    if (ofd.ShowDialog(f) == DialogResult.OK)
                    {
                        selectedPath = ofd.FileName;
                    }
                });
            }
#endif

            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                var cfg = AiConfigService.Instance.CurrentConfig;
                if (!string.IsNullOrWhiteSpace(toolId) && cfg.DevTools != null)
                {
                    var devTool = cfg.DevTools.FirstOrDefault(d => string.Equals(d.Id, toolId, StringComparison.OrdinalIgnoreCase));
                    if (devTool != null)
                    {
                        devTool.Target = selectedPath;
                        if (string.IsNullOrWhiteSpace(tool)) tool = devTool.Name;
                    }
                }
                if (!string.IsNullOrWhiteSpace(tool))
                {
                    cfg.ToolTargets[tool] = selectedPath;
                    var devTool = cfg.DevTools?.FirstOrDefault(d => string.Equals(d.Name, tool, StringComparison.OrdinalIgnoreCase));
                    if (devTool != null) devTool.Target = selectedPath;
                }
                AiConfigService.Instance.SaveConfig(cfg);
                SendResponse(requestId, true, new { toolName = tool, toolId, path = selectedPath });
            }
            else
            {
                SendResponse(requestId, false, "Selection cancelled");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleResetToolTargetAsync(JsonElement data, string? requestId)
    {
        {
            string tool = string.Empty;
            string? toolId = null;
            if (data.TryGetProperty("toolName", out var rsToolProp))
            {
                tool = rsToolProp.GetString() ?? string.Empty;
            }
            if (data.TryGetProperty("toolId", out var rsToolIdProp))
            {
                toolId = rsToolIdProp.GetString();
            }

            var defaults = AiConfigService.GetDefaultToolTargets();
            var defaultTools = AiConfigService.GetDefaultDevTools();

            string defPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(toolId))
            {
                var defItem = defaultTools.FirstOrDefault(d => string.Equals(d.Id, toolId, StringComparison.OrdinalIgnoreCase));
                if (defItem != null) defPath = defItem.Target;
            }
            if (string.IsNullOrWhiteSpace(defPath) && !string.IsNullOrWhiteSpace(tool))
            {
                if (defaults.TryGetValue(tool, out var p)) defPath = p;
            }

            var cfg = AiConfigService.Instance.CurrentConfig;
            if (!string.IsNullOrWhiteSpace(toolId) && cfg.DevTools != null)
            {
                var devTool = cfg.DevTools.FirstOrDefault(d => string.Equals(d.Id, toolId, StringComparison.OrdinalIgnoreCase));
                if (devTool != null) devTool.Target = defPath;
            }
            if (!string.IsNullOrWhiteSpace(tool))
            {
                cfg.ToolTargets[tool] = defPath;
                var devTool = cfg.DevTools?.FirstOrDefault(d => string.Equals(d.Name, tool, StringComparison.OrdinalIgnoreCase));
                if (devTool != null) devTool.Target = defPath;
            }
            AiConfigService.Instance.SaveConfig(cfg);
            SendResponse(requestId, true, new { toolName = tool, toolId, path = defPath });
        }

        return Task.CompletedTask;
    }

    private Task HandleMinimizeToTrayAsync(string? requestId)
    {
#if WINDOWS
        if (_mainForm is Form fTray && fTray.IsHandleCreated && !fTray.IsDisposed)
        {
            fTray.Invoke(() =>
            {
                var allowTray = StartupHelper.GetPreference("MinimizeToTray", true);
                if (allowTray)
                {
                    fTray.Hide();
                    (fTray as MainForm)?.ShowTrayBalloon("Agents Hub", "Agents Hub is running in the background.");
                }
                else
                {
                    fTray.WindowState = FormWindowState.Minimized;
                }
            });
        }
#endif
        SendResponse(requestId, true, new { success = true });

        return Task.CompletedTask;
    }

    private Task HandleSaveGeneralSettingsAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("settings", out var stProp))
        {
            if (stProp.TryGetProperty("autostart", out var autoEl))
            {
                StartupHelper.SetWindowsStartup(autoEl.GetBoolean());
            }
            if (stProp.TryGetProperty("startMinimized", out var minEl))
            {
                StartupHelper.SetPreference("StartMinimized", minEl.GetBoolean());
            }
            if (stProp.TryGetProperty("minimizeToTray", out var mttEl))
            {
                StartupHelper.SetPreference("MinimizeToTray", mttEl.GetBoolean());
            }
            if (stProp.TryGetProperty("notifications", out var notifEl))
            {
                StartupHelper.SetPreference("WindowsNotifications", notifEl.GetBoolean());
            }
            if (stProp.TryGetProperty("globalHotkey", out var hotkeyEl))
            {
                StartupHelper.SetPreference("GlobalHotkey", hotkeyEl.GetBoolean());
            }
            if (stProp.TryGetProperty("globalHotkeyModifier", out var ghModEl))
            {
                StartupHelper.SetStringPreference("GlobalHotkeyModifier", ghModEl.GetString() ?? "Ctrl+Alt");
            }
            if (stProp.TryGetProperty("globalHotkeyKey", out var ghKeyEl))
            {
                StartupHelper.SetStringPreference("GlobalHotkeyKey", ghKeyEl.GetString() ?? "H");
            }
#if WINDOWS
            if (_mainForm is MainForm mainForm)
            {
                try
                {
                    mainForm.Invoke(() => mainForm.ApplyGlobalHotkey());
                }
                catch
                {
                }
            }
#endif
            if (stProp.TryGetProperty("globalHudHotkey", out var hudHotkeyEl))
            {
                StartupHelper.SetPreference("GlobalHudHotkey", hudHotkeyEl.GetBoolean());
            }
            if (stProp.TryGetProperty("hudHotkeyKey", out var hudKeyEl))
            {
                StartupHelper.SetStringPreference("HudHotkeyKey", hudKeyEl.GetString() ?? "F12");
            }
            if (stProp.TryGetProperty("hudHotkeyModifier", out var hudModEl))
            {
                StartupHelper.SetStringPreference("HudHotkeyModifier", hudModEl.GetString() ?? "None");
            }
            if (stProp.TryGetProperty("hudHotkeyTarget", out var hudTargetEl))
            {
                StartupHelper.SetStringPreference("HudHotkeyTarget", hudTargetEl.GetString() ?? "cliwebview");
            }
#if WINDOWS
            if (_mainForm is MainForm mainFormHud)
            {
                try
                {
                    mainFormHud.Invoke(() => mainFormHud.ApplyGlobalHotkey());
                }
                catch
                {
                }
            }
#endif
            if (stProp.TryGetProperty("maskPublicIp", out var maskEl))
            {
                StartupHelper.SetPreference("MaskPublicIp", maskEl.GetBoolean());
            }
            if (stProp.TryGetProperty("maskShortcutPaths", out var maskPathsEl))
            {
                StartupHelper.SetPreference("MaskShortcutPaths", maskPathsEl.GetBoolean());
            }
            if (stProp.TryGetProperty("embedShellsInTerminalWorkspace", out var embedEl))
            {
                StartupHelper.SetPreference("EmbedShellsInTerminalWorkspace", embedEl.GetBoolean());
            }
            if (stProp.TryGetProperty("hideMainOnCliOpen", out var hideMainEl))
            {
                StartupHelper.SetPreference("HideMainOnCliOpen", hideMainEl.GetBoolean());
            }
            if (stProp.TryGetProperty("enableHardwareMonitor", out var hwEl))
            {
                StartupHelper.SetPreference("EnableHardwareMonitor", hwEl.GetBoolean());
            }
            if (stProp.TryGetProperty("enableArchitectureGraph", out var archEl))
            {
                StartupHelper.SetPreference("EnableArchitectureGraph", archEl.GetBoolean());
            }
            // The payload is a partial patch: most callers save one checkbox and send no
            // theme at all. Only touch the theme when a value was actually supplied. Running
            // SetTheme unconditionally defaulted the theme to Dark, so toggling any unrelated
            // preference silently reset the app and rewrote the registry to dark while the
            // dashboard kept its own light value in browser storage.
            string? themeStr = null;
            if (stProp.TryGetProperty("theme", out var themeEl))
            {
                var rawTheme = themeEl.GetString();
                if (!string.IsNullOrWhiteSpace(rawTheme))
                {
                    themeStr = rawTheme;
                    StartupHelper.SetStringPreference("Theme", rawTheme);
                }
            }
            string? accentStr = null;
            if (stProp.TryGetProperty("accentColor", out var colEl))
            {
                var rawAccent = colEl.GetString();
                if (!string.IsNullOrWhiteSpace(rawAccent))
                {
                    accentStr = rawAccent;
                    StartupHelper.SetStringPreference("AccentColor", rawAccent);
                }
            }

            if (themeStr != null || accentStr != null)
            {
                var appTheme = themeStr == null
                    ? ThemeManager.CurrentTheme
                    : (themeStr.Equals("light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark);
                ThemeManager.SetTheme(appTheme, accentStr);

#if WINDOWS
                if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
                {
                    f.Invoke(() =>
                    {
                        if (_cliWebviewForm != null && !_cliWebviewForm.IsDisposed)
                        {
                            _cliWebviewForm.ApplyTheme();
                        }
                    });
                }
#endif
            }

            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleSaveShortcutsAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("shortcuts", out var scProp))
        {
            var scList = JsonSerializer.Deserialize<List<DashboardShortcut>>(scProp.GetRawText(), CaseInsensitiveJsonOptions);
            if (scList != null)
            {
                var cfg = AiConfigService.Instance.CurrentConfig;
                cfg.Shortcuts = scList;
                AiConfigService.Instance.SaveConfig(cfg);
                SendResponse(requestId, true, new { shortcuts = cfg.Shortcuts });
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleLaunchShortcutAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("target", out var tgProp))
        {
            var target = tgProp.GetString() ?? string.Empty;
            var args = data.TryGetProperty("arguments", out var argProp) ? argProp.GetString() ?? string.Empty : string.Empty;
            LaunchShortcutApp(target, args);
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleBrowseShortcutTargetAsync(string? requestId)
    {
        string? selectedFile = null;
#if WINDOWS
        if (_mainForm is Form fShort && fShort.IsHandleCreated && !fShort.IsDisposed)
        {
            fShort.Invoke(() =>
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Select Application or Script Executable",
                    Filter = "Executable & Scripts (*.exe;*.cmd;*.bat;*.ps1)|*.exe;*.cmd;*.bat;*.ps1|All Files (*.*)|*.*",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                };
                if (ofd.ShowDialog(fShort) == DialogResult.OK)
                {
                    selectedFile = ofd.FileName;
                }
            });
        }
#endif
        if (!string.IsNullOrWhiteSpace(selectedFile))
        {
            SendResponse(requestId, true, new { path = selectedFile });
        }
        else
        {
            SendResponse(requestId, false, "Selection cancelled");
        }

        return Task.CompletedTask;
    }

    private Task HandleGenerateQrCodeAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("url", out var qrUrlEl))
        {
            var u = qrUrlEl.GetString() ?? string.Empty;
            var qr = QrCodeHelper.GenerateQrCodeBase64(u);
            SendResponse(requestId, true, new { qrBase64 = qr });
        }

        return Task.CompletedTask;
    }

    private async Task HandleOpenUrlExternalAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("url", out var urlProp))
        {
            var targetUrl = urlProp.GetString();
            if (!string.IsNullOrWhiteSpace(targetUrl)
                && (targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                if (targetUrl.Contains(":9119") || targetUrl.Contains(":9191"))
                {
                    var hubInfo = _cliManager.GetInfo(CliType.Hub);
                    if (hubInfo.State != CliState.Running)
                    {
                        if (hubInfo.State != CliState.Starting)
                        {
                            _cliManager.Start(CliType.Hub);
                        }

                        for (int i = 0; i < 35; i++)
                        {
                            await Task.Delay(100);
                            try
                            {
                                using var testClient = new System.Net.Sockets.TcpClient();
                                using var cts = new CancellationTokenSource(200);
                                await testClient.ConnectAsync(System.Net.IPAddress.Loopback, _cliManager.HubService.Port, cts.Token);
                                if (testClient.Connected) break;
                            }
                            catch { }
                        }
                    }
                }

                var localTarget = targetUrl;
                var lanIp = NetworkHelper.GetLocalIPv4();
                if (!string.IsNullOrEmpty(lanIp) && localTarget.Contains(lanIp))
                {
                    localTarget = localTarget.Replace(lanIp, "127.0.0.1");
                }

                if (!localTarget.Contains("theme=") && (localTarget.Contains(":9229") || localTarget.Contains(":9339") || localTarget.Contains(":9191") || localTarget.Contains(":9119")))
                {
                    string sep = localTarget.Contains('?') ? "&" : "?";
                    string themeParam = $"theme={(ThemeManager.CurrentTheme == AppTheme.Light ? "light" : "dark")}&accent={Uri.EscapeDataString(ThemeManager.AccentHex)}";
                    localTarget = $"{localTarget}{sep}{themeParam}";
                }

                Process.Start(new ProcessStartInfo(localTarget) { UseShellExecute = true });
                SendResponse(requestId, true, new { success = true });
            }
        }
    }

    /// <summary>
    /// Installs every missing dependency in sequence.
    ///
    /// The per-tool installer already exists and streams its own progress events, so this only
    /// has to serialise the queue and report where it is. Sequential rather than parallel because
    /// two winget installs at once fight over the same package manager and the output becomes
    /// impossible to read.
    /// </summary>
    private async Task RunAllDependencyInstallsAsync(List<DependencyDefinition> queue)
    {
        var succeeded = new List<string>();
        var failed = new List<string>();

        for (int i = 0; i < queue.Count; i++)
        {
            var definition = queue[i];
            PostEventToUi("dependency_install_all_progress", new
            {
                index = i,
                total = queue.Count,
                id = definition.Id,
                name = definition.Name
            });

            try
            {
                await RunDependencyInstallAsync(definition);

                // RunDependencyInstallAsync reports its own completion; re-reading the status is
                // the only honest way to know whether it actually took.
                var after = await DependencyCheckerService.GetDependenciesStatusAsync();
                var status = after.FirstOrDefault(s => string.Equals(s.Id, definition.Id, StringComparison.OrdinalIgnoreCase));

                if (status?.IsInstalled == true) succeeded.Add(definition.Name);
                else failed.Add(definition.Name);
            }
            catch (Exception ex)
            {
                failed.Add(definition.Name);
                PostDependencyInstallComplete(definition.Id, false, ex.Message, null);
            }
        }

        PostEventToUi("dependency_install_all_complete", new
        {
            total = queue.Count,
            succeeded,
            failed
        });
    }

    /// <summary>
    /// Starts an external target. Returns the outcome rather than showing a dialog, so the
    /// dashboard can report it through the same toast channel as everything else.
    /// </summary>
    private static (bool Launched, string Message) LaunchExecutableTarget(
        string target, string toolName, string arguments = "", string workingDirectory = "")
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return (false, $"No executable path is set for {toolName}.");
        }

        // Backstop for a quoted path that reached here without passing through config load.
        var resolved = DevToolTargets.Normalize(target);

        try
        {
            if (resolved.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                // A UWP entry point is handed to explorer, which ignores arguments and a
                // working directory of its own, so neither is applied here.
                Process.Start(new ProcessStartInfo("explorer.exe", resolved) { UseShellExecute = true });
            }
            else
            {
                var startInfo = new ProcessStartInfo(resolved) { UseShellExecute = true };

                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    startInfo.Arguments = arguments.Trim();
                }

                if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory.Trim()))
                {
                    startInfo.WorkingDirectory = workingDirectory.Trim();
                }

                Process.Start(startInfo);
            }
            return (true, $"{toolName} started.");
        }
        catch (Exception ex)
        {
            return (false, $"{toolName} could not start: {ex.Message}");
        }
    }

    private static void LaunchToolApp(string toolName)
    {
        var target = AiConfigService.Instance.GetToolTarget(toolName, string.Empty);
        _ = LaunchExecutableTarget(target, toolName);
    }

    private void LaunchShortcutApp(string target, string arguments)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            if (target.Equals("cli:hub", StringComparison.OrdinalIgnoreCase))
            {
                ToggleCliInternal(CliType.Hub);
                return;
            }
            if (target.Equals("cli:claude", StringComparison.OrdinalIgnoreCase))
            {
                ToggleCliInternal(CliType.ClaudeCode);
                return;
            }
            if (target.Equals("cli:antigravity", StringComparison.OrdinalIgnoreCase))
            {
                ToggleCliInternal(CliType.AntigravityCli);
                return;
            }

            if (target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
            }
            else
            {
                var psi = new ProcessStartInfo(target, arguments ?? string.Empty) { UseShellExecute = true };
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
#if WINDOWS
            MessageBox.Show($"Failed to launch shortcut '{target}': {ex.Message}", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
#else
            Console.WriteLine($"Failed to launch shortcut '{target}': {ex.Message}");
#endif
        }
    }

    /// <summary>
    /// Applies a dashboard config payload as a patch instead of a replacement.
    ///
    /// The dashboard sends back only the fields it renders, so a blind replace reset every field
    /// the DTO does not emit. The Claude gateway settings are the live example: they are edited
    /// through their own actions, never appear in the DTO, and were silently wiped back to their
    /// defaults by any unrelated dashboard save. Copying only the properties actually present
    /// means a field the dashboard does not own cannot be clobbered by it.
    ///
    /// Reflection is safe here because the csproj roots the whole AgentsHub assembly
    /// (TrimmerRootAssembly) and reflection-based JSON is already enabled.
    /// </summary>
    private static void ApplyConfigPatch(AiConfig target, JsonElement payload)
    {
        var incoming = JsonSerializer.Deserialize<AiConfig>(payload.GetRawText(), CaseInsensitiveJsonOptions);
        if (incoming == null) return;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in payload.EnumerateObject())
        {
            present.Add(property.Name);
        }

        foreach (var property in typeof(AiConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;

            var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            if (!present.Contains(jsonName)) continue;

            property.SetValue(target, property.GetValue(incoming));
        }
    }

    /// <summary>
    /// The dev tool list with its launch health attached, so the page can show which tools
    /// actually point at something without asking the user to find out by clicking Launch.
    /// </summary>
    private static List<object> BuildDevToolDtos()
    {
        var cfg = AiConfigService.Instance.CurrentConfig;
        var list = new List<object>();

        foreach (var d in cfg.DevTools ?? [])
        {
            var health = DevToolTargets.Check(d.Target);
            list.Add(new
            {
                id = d.Id,
                name = d.Name,
                description = d.Description,
                target = d.Target,
                arguments = d.Arguments,
                workingDirectory = d.WorkingDirectory,
                isEnabled = d.IsEnabled,
                showOnDashboard = d.ShowOnDashboard,
                iconDataUrl = d.IconDataUrl,
                health = new
                {
                    state = health.State.ToString().ToLowerInvariant(),
                    detail = health.Detail,
                    resolvedTarget = health.ResolvedTarget,
                    kind = health.Kind,
                    launchable = health.Launchable
                }
            });
        }

        return list;
    }

    private static object GetAiConfigDto()
    {
        var cfg = AiConfigService.Instance.CurrentConfig;
        return new
        {
            selectedProviderId = cfg.SelectedProviderId,
            selectedModel = cfg.SelectedModel,
            toolTargets = cfg.ToolTargets,
            devTools = BuildDevToolDtos(),
            shortcuts = cfg.Shortcuts.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                target = s.Target,
                arguments = s.Arguments,
                category = s.Category
            }).ToList(),
            providers = cfg.Providers.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                baseUrl = p.BaseUrl,
                apiKey = p.ApiKey,
                isEnabled = p.IsEnabled,
                models = p.Models
            }).ToList(),
            promptEnhancerTemperature = cfg.PromptEnhancerTemperature,
            activePromptPresetId = cfg.ActivePromptPresetId,
            promptPresets = (cfg.PromptPresets ?? []).Select(p => new
            {
                id = p.Id,
                name = p.Name,
                systemPrompt = p.SystemPrompt,
                isBuiltIn = p.IsBuiltIn
            }).ToList(),
            // Shipped defaults, so the UI can offer a true "Reset" without duplicating the text.
            builtInPromptPresets = AiConfigService.BuildDefaultPromptPresets().Select(p => new
            {
                id = p.Id,
                name = p.Name,
                systemPrompt = p.SystemPrompt
            }).ToList(),
            // Round-trips through save_ai_config so a config save cannot reset the label.
            terminalWorkspacePreset = cfg.TerminalWorkspacePreset,
            // Must round-trip too: save_ai_config replaces the whole config, so omitting these
            // would silently drop every CLI workspace (including user-added ones) on any save.
            cliWorkspaces = (cfg.CliWorkspaces ?? []).Select(w => new
            {
                id = w.Id,
                name = w.Name,
                command = w.Command,
                workingDirectory = w.WorkingDirectory,
                port = w.Port,
                isWebUi = w.IsWebUi,
                isDefault = w.IsDefault,
                isEnabled = w.IsEnabled
            }).ToList()
        };
    }

    /// <summary>
    /// Installs a dependency in the background, streaming installer output as
    /// dependency_install_progress events and finishing with dependency_install_complete.
    /// </summary>
    private async Task RunDependencyInstallAsync(DependencyDefinition definition)
    {
        var startedAt = DateTime.UtcNow;

        try
        {
            if (string.Equals(definition.InstallKind, "browser", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(definition.SiteUrl))
                {
                    PostDependencyInstallComplete(definition.Id, false, "No download page is configured for this tool.", null);
                    return;
                }

                Process.Start(new ProcessStartInfo(definition.SiteUrl) { UseShellExecute = true });
                PostDependencyInstallComplete(definition.Id, true, $"Opened {definition.SiteUrl} in your default browser.", null);
                return;
            }

            PostEventToUi("dependency_install_progress", new { id = definition.Id, line = $"Running: {definition.CommandPreview}" });

            // winget is an app-execution alias, so it has to be reached through cmd rather than
            // launched directly. PowerShell runs the downloaded install scripts, and Bypass is
            // what lets them run on machines whose execution policy would otherwise refuse them.
            bool useWinget = string.Equals(definition.InstallKind, "winget", StringComparison.OrdinalIgnoreCase);
            bool isWindows = Services.Platform.PlatformHelper.IsWindows;

            // winget and the PowerShell scripts are Windows-only paths. Elsewhere the same command
            // runs through the POSIX shell, where npm, brew and the distro package manager live.
            var startInfo = new ProcessStartInfo
            {
                FileName = !isWindows ? "/bin/bash" : useWinget ? "cmd.exe" : "powershell.exe",
                Arguments = !isWindows
                    ? $"-lc \"{definition.InstallCommand}\""
                    : useWinget
                        ? $"/c {definition.InstallCommand}"
                        : $"-NoProfile -ExecutionPolicy Bypass -Command \"{definition.InstallCommand}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            using var process = new Process { StartInfo = startInfo };
            var buffer = new StringBuilder();

            process.OutputDataReceived += (_, e) => ForwardInstallLine(definition.Id, e.Data, buffer);
            process.ErrorDataReceived += (_, e) => ForwardInstallLine(definition.Id, e.Data, buffer);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                PostDependencyInstallComplete(definition.Id, false, "Installer timed out after 5 minutes.", null);
                return;
            }

            // Re-detect rather than trusting the exit code alone: winget returns non-zero for
            // "already installed", and a tool can land on PATH only after a shell restart.
            var status = DependencyCheckerService.GetStatus(definition.Id);
            bool installed = status is { IsInstalled: true };
            bool succeeded;
            string message;

            if (installed)
            {
                succeeded = true;
                message = process.ExitCode == 0
                    ? $"Installer finished in {(DateTime.UtcNow - startedAt).TotalSeconds:F0}s."
                    : $"Installer exited with code {process.ExitCode}, but {definition.Name} is now detected.";
            }
            else if (process.ExitCode == 0)
            {
                succeeded = false;
                message = "Installer finished, but the tool is still not resolvable. A shell restart may be required.";
            }
            else
            {
                succeeded = false;
                message = $"Installer exited with code {process.ExitCode}.";
            }

            PostDependencyInstallComplete(definition.Id, succeeded, message, status);
        }
        catch (Exception ex)
        {
            PostDependencyInstallComplete(definition.Id, false, ex.Message, null);
        }
    }

    private void ForwardInstallLine(string dependencyId, string? line, StringBuilder buffer)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        if (buffer.Length < 4000)
        {
            buffer.AppendLine(line);
        }

        PostEventToUi("dependency_install_progress", new { id = dependencyId, line });
    }

    private void PostDependencyInstallComplete(string dependencyId, bool success, string message, DependencyStatus? status)
    {
        PostEventToUi("dependency_install_complete", new
        {
            id = dependencyId,
            success,
            message,
            dependency = status == null ? null : ToDependencyDto(status)
        });
    }

}