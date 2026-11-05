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

    private Task HandleStartShellAsync(JsonElement data, string? requestId)
    {
        {
            if (NpxInstallHost.IsRunning)
            {
                SendResponse(requestId, false, "A console session is already running.");
                return Task.CompletedTask;
            }

            string? cwd = data.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(cwd))
            {
                cwd = AiConfigService.Instance.GetActiveWorkspace()?.WorkingDirectory;
            }

            string shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
            bool started = NpxInstallHost.StartRaw(shell, string.IsNullOrWhiteSpace(cwd) ? null : cwd);

            if (!started)
            {
                SendResponse(requestId, false, "Could not start the terminal.");
                return Task.CompletedTask;
            }

            SendResponse(requestId, true, new { started = true, cwd, shell });
        }

        return Task.CompletedTask;
    }

    private Task HandleSendShellInputAsync(JsonElement data, string? requestId)
    {
        {
            string? input = data.TryGetProperty("input", out var inputProp) ? inputProp.GetString() : null;
            NpxInstallHost.Write(input ?? string.Empty);
            SendResponse(requestId, true, new { sent = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleStopShellAsync(string? requestId)
    {
        NpxInstallHost.Stop();
        SendResponse(requestId, true, new { stopped = true });

        return Task.CompletedTask;
    }

    private Task HandleOpenTerminalAtAsync(JsonElement data, string? requestId)
    {
        {
            string path = data.TryGetProperty("path", out var termPath) ? termPath.GetString() ?? string.Empty : string.Empty;
            bool opened = Directory.Exists(path);
            if (opened)
            {
                try
                {
                    // Windows Terminal when installed, otherwise a console rooted there.
                    var info = new System.Diagnostics.ProcessStartInfo { WorkingDirectory = path, UseShellExecute = true };

                    if (Services.Platform.PlatformHelper.IsWindows)
                    {
                        info.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd";
                        info.Arguments = $"/k cd /d \"{path}\"";
                    }
                    else if (Services.Platform.PlatformHelper.IsMacOS)
                    {
                        info.FileName = "open";
                        info.Arguments = $"-a Terminal \"{path}\"";
                    }
                    else
                    {
                        info.FileName = "x-terminal-emulator";
                        info.Arguments = string.Empty;
                    }

                    System.Diagnostics.Process.Start(info);
                }
                catch (Exception) { opened = false; }
            }
            SendResponse(requestId, true, new { opened });
        }

        return Task.CompletedTask;
    }

    private async Task HandleToggleCliAsync(JsonElement data, string? requestId)
    {
        // Ids rather than CliType names: the dashboard sends normalized workspace ids
        // ("claude_code"), which Enum.TryParse cannot read.
        if (data.TryGetProperty("cli", out var cliProp))
        {
            string targetCli = cliProp.GetString() ?? string.Empty;
            var info = _cliManager.GetInfo(targetCli);
            if (info.State == CliState.Running)
            {
                await _cliManager.StopAsync(targetCli);
            }
            else
            {
                _cliManager.Start(targetCli);
            }
            SendResponse(requestId, true, new { state = _cliManager.GetInfo(targetCli).State.ToString() });
        }
    }

    private async Task HandleRestartCliAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("cli", out var rCli))
        {
            await _cliManager.RestartAsync(rCli.GetString() ?? string.Empty);
            SendResponse(requestId, true, new { success = true });
        }
    }

    private Task HandleGetTerminalSessionAsync(JsonElement data, string? requestId)
    {
        // The dashboard terminal renders a CLI session, so it asks for that session's
        // scrollback and running state before it attaches. Hub is a web dashboard rather
        // than a ConPTY session, so its tab answers with the Hub port alone.
        {
            string reqCli = data.TryGetProperty("cli", out var gCli) ? (gCli.GetString() ?? "hub") : "hub";
            string normId = AiConfigService.NormalizeCliId(reqCli);

            if (string.Equals(normId, "hub", StringComparison.OrdinalIgnoreCase))
            {
                var hubInfo = _cliManager.GetInfo("hub");
                int hubPort = _cliManager.HubService.Port;
                SendResponse(requestId, true, new
                {
                    cli = "hub",
                    sessionId = "hub",
                    isRunning = hubInfo.State == CliState.Running,
                    status = hubInfo.StatusText,
                    history = string.Empty,
                    isWebUi = true,
                    webUrl = $"http://127.0.0.1:{hubPort}/",
                    port = hubPort,
                    displayName = "Hub Web"
                });
            }
            else
            {
                string sessionId = ResolveTerminalSessionId(normId);
                var info = _cliManager.GetInfo(sessionId);
                SendResponse(requestId, true, new
                {
                    cli = normId,
                    sessionId,
                    isRunning = info.State == CliState.Running,
                    status = info.StatusText,
                    history = _cliManager.GetTerminalHistory(sessionId),
                    isWebUi = false,
                    webUrl = info.WebUrl ?? (info.Port != null ? $"http://127.0.0.1:{info.Port}" : string.Empty),
                    port = info.Port,
                    displayName = info.DisplayName
                });
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleClearTerminalHistoryAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("cli", out var clrCli))
        {
            _cliManager.ClearTerminalHistory(ResolveTerminalSessionId(clrCli.GetString() ?? string.Empty));
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleSendTerminalInputAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("cli", out var inCli) && data.TryGetProperty("input", out var inData))
        {
            string targetCli = ResolveTerminalSessionId(inCli.GetString() ?? string.Empty);
            string rawInput = inData.GetString() ?? string.Empty;

            if (CliManagerService.IsAntigravityProfileId(targetCli, out _))
            {
                var cliInfo = _cliManager.GetInfo(targetCli);
                if (cliInfo.State != CliState.Running && cliInfo.State != CliState.Starting)
                {
                    _cliManager.Start(targetCli);
                }
            }

            _cliManager.SendInput(targetCli, rawInput);
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleResizeTerminalAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("cli", out var szCli) &&
            data.TryGetProperty("cols", out var cols) && data.TryGetProperty("rows", out var rows))
        {
            _cliManager.Resize(ResolveTerminalSessionId(szCli.GetString() ?? string.Empty), (short)cols.GetInt32(), (short)rows.GetInt32());
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleApplyTerminalPresetAsync(JsonElement data, string? requestId)
    {
        {
            var presetName = data.TryGetProperty("preset", out var presetProp) ? presetProp.GetString() : null;
            AiConfigService.Instance.ApplyWorkspacePreset(presetName ?? "Preset_Duo");

#if WINDOWS
            // Rebuild the standalone terminal workspace if the user has it open.
            try
            {
                if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
                {
                    f.Invoke(() =>
                    {
                        if (_cliWebviewForm != null && !_cliWebviewForm.IsDisposed)
                        {
                            _cliWebviewForm.RebuildTabs();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DashboardWebBridge] RebuildTabs after preset failed: {ex.Message}");
            }
#endif

            var workspaces = AiConfigService.Instance.GetCliWorkspaces();
            SendResponse(requestId, true, new
            {
                success = true,
                preset = AiConfigService.Instance.GetTerminalWorkspacePreset(),
                enabledClis = workspaces.Where(w => w.IsEnabled).Select(w => AiConfigService.NormalizeCliId(w.Id)).ToList()
            });
        }

        return Task.CompletedTask;
    }

    private Task HandleGetTerminalPresetAsync(string? requestId)
    {
        {
            var workspaces = AiConfigService.Instance.GetCliWorkspaces();
            SendResponse(requestId, true, new
            {
                preset = AiConfigService.Instance.GetTerminalWorkspacePreset(),
                enabledClis = workspaces.Where(w => w.IsEnabled).Select(w => AiConfigService.NormalizeCliId(w.Id)).ToList()
            });
        }

        return Task.CompletedTask;
    }

    private Task HandleOpenStandaloneTerminalsAsync(JsonElement data, string? requestId)
    {
        try
        {
#if WINDOWS
            // The standalone window opens on the CLI the dashboard is showing.
            string targetTab = data.TryGetProperty("cli", out var popCli) ? (popCli.GetString() ?? string.Empty) : string.Empty;
            if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
            {
                f.Invoke(() =>
                {
                    var form = EnsureStandaloneTerminalForm();
                    if (!string.IsNullOrWhiteSpace(targetTab))
                    {
                        var workspaces = AiConfigService.Instance.GetCliWorkspaces();
                        bool isEnabled = workspaces.Any(w => w.IsEnabled && (
                            string.Equals(AiConfigService.NormalizeCliId(w.Id), AiConfigService.NormalizeCliId(targetTab), StringComparison.OrdinalIgnoreCase) ||
                            (string.Equals(w.Id, "antigravity_cli", StringComparison.OrdinalIgnoreCase) && CliManagerService.IsAntigravityProfileId(targetTab, out _))
                        ));
                        if (isEnabled)
                        {
                            form.SwitchTab(targetTab);
                        }
                    }
                    form.ApplyTheme();
                    if (form.WindowState == FormWindowState.Minimized)
                    {
                        form.WindowState = FormWindowState.Normal;
                    }
                    form.Show();
                    form.BringToFront();
                    form.Activate();

                    if (StartupHelper.GetPreference("HideMainOnCliOpen", false))
                    {
                        form.HideMainFormNow();
                    }
                });
            }
#endif
            SendResponse(requestId, true, new { success = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DashboardWebBridge] open_standalone_terminals error: {ex}");
            SendResponse(requestId, false, ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task HandleSyncTerminalTabAsync(JsonElement data, string? requestId)
    {
        // Keeping the standalone workspace on the dashboard's CLI makes the two terminal views
        // one session rather than two windows that drift apart.
        if (data.TryGetProperty("cli", out var syncCliProp))
        {
#if WINDOWS
            string syncCli = syncCliProp.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(syncCli) && _cliWebviewForm != null && !_cliWebviewForm.IsDisposed)
            {
                try
                {
                    var workspaces = AiConfigService.Instance.GetCliWorkspaces();
                    bool isEnabled = workspaces.Any(w => w.IsEnabled && (
                        string.Equals(AiConfigService.NormalizeCliId(w.Id), AiConfigService.NormalizeCliId(syncCli), StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(w.Id, "antigravity_cli", StringComparison.OrdinalIgnoreCase) && CliManagerService.IsAntigravityProfileId(syncCli, out _))
                    ));
                    if (isEnabled && _mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
                    {
                        f.Invoke(() => _cliWebviewForm.SwitchTab(syncCli));
                    }
                }
                catch { }
            }
#endif
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleOpenSystemShellAsync(JsonElement data, string? requestId)
    {
        {
            string shellName = data.TryGetProperty("shell", out var shEl) ? shEl.GetString() ?? "cmd" : "cmd";
            bool asAdmin = data.TryGetProperty("asAdmin", out var adEl) && adEl.GetBoolean();
            bool embedShells = StartupHelper.GetPreference("EmbedShellsInTerminalWorkspace", false);

#if WINDOWS
            if (embedShells && !asAdmin && _mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
            {
                f.Invoke(() =>
                {
                    var form = EnsureStandaloneTerminalForm();
                    if (shellName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        form.OpenEmbeddedShell("powershell", "PowerShell", "powershell.exe");
                    }
                    else
                    {
                        form.OpenEmbeddedShell("cmd", "Command Prompt", "cmd.exe");
                    }

                    if (StartupHelper.GetPreference("HideMainOnCliOpen", false))
                    {
                        form.HideMainFormNow();
                    }
                });
            }
            else
            {
                if (shellName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                {
                    ShellLauncher.OpenPowerShell(asAdmin);
                }
                else
                {
                    ShellLauncher.OpenCommandPrompt(asAdmin);
                }
            }
#else
            Platform.PlatformHelper.OpenUrlInBrowser("terminal");
#endif
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private void OnStreamFlushTimerTick(object? sender, EventArgs e)
    {
#if WINDOWS
        if (_coreWebView == null && WebMessageBroadcast == null) return;
#else
        if (WebMessageBroadcast == null) return;
#endif

        lock (_streamLock)
        {
            foreach (var (sessionId, sb) in _terminalStreamBuffers)
            {
                if (sb.Length > 0)
                {
                    var chunk = sb.ToString();
                    sb.Clear();

                    PostEventToUi("terminal_data", new
                    {
                        cli = sessionId,
                        data = chunk
                    });
                }
            }
        }
    }

    private string ResolveTerminalSessionId(string cli)
    {
        return _cliManager.ResolveSessionId(cli);
    }

    private void NotifyStandaloneTabSwitched(string cliId)
    {
        PostEventToUi("terminal_tab_switched", new { cli = AiConfigService.NormalizeCliId(cliId) });
    }

#if WINDOWS
    private CliWebviewForm EnsureStandaloneTerminalForm()
    {
        if (_cliWebviewForm == null || _cliWebviewForm.IsDisposed)
        {
            var workspaces = AiConfigService.Instance.GetCliWorkspaces();
            var firstEnabled = workspaces.FirstOrDefault(w => w.IsEnabled);
            CliType initType = CliType.ClaudeCode;
            if (firstEnabled != null)
            {
                string norm = AiConfigService.NormalizeCliId(firstEnabled.Id);
                if (norm == "claude_code") initType = CliType.ClaudeCode;
                else if (norm == "antigravity_cli") initType = CliType.AntigravityCli;
            }
            _cliWebviewForm = new CliWebviewForm(_cliManager, initType, _mainForm);
            _cliWebviewForm.ActiveTabChanged += NotifyStandaloneTabSwitched;
        }
        else
        {
            _cliWebviewForm.SetMainForm(_mainForm);
        }
        return _cliWebviewForm;
    }
#endif

#if WINDOWS
    public void ToggleOrShowCliWebview()
    {
        if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
        {
            f.Invoke(() =>
            {
                try
                {
                    if (_cliWebviewForm == null || _cliWebviewForm.IsDisposed)
                    {
                        var created = EnsureStandaloneTerminalForm();
                        created.ApplyTheme();
                        created.Show();
                        created.BringToFront();
                        created.Activate();
                        return;
                    }

                    IntPtr fg = GetForegroundWindow();
                    if (_cliWebviewForm.Visible && _cliWebviewForm.WindowState != FormWindowState.Minimized && fg == _cliWebviewForm.Handle)
                    {
                        _cliWebviewForm.Hide();
                    }
                    else
                    {
                        _cliWebviewForm.ApplyTheme();
                        if (_cliWebviewForm.WindowState == FormWindowState.Minimized)
                        {
                            _cliWebviewForm.WindowState = FormWindowState.Normal;
                        }
                        _cliWebviewForm.Show();
                        _cliWebviewForm.BringToFront();
                        _cliWebviewForm.Activate();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DashboardWebBridge] ToggleOrShowCliWebview error: {ex.Message}");
                }
            });
        }
    }
#endif

    private void ToggleCliInternal(CliType cliType)
    {
        var info = _cliManager.GetInfo(cliType);
        if (info.State == CliState.Running)
        {
            _ = _cliManager.StopAsync(cliType);
        }
        else
        {
            _cliManager.Start(cliType);
        }
    }

}