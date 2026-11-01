#nullable enable
using System.Diagnostics;
using System.Drawing;
using System.IO;
using AgentsHub.Helpers;
using AgentsHub.Services;
using AgentsHub.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentsHub;

public partial class MainForm : Form
{
    private HubProcessService? _hubService;
    private CliManagerService? _cliManager;
    private LanProxyService? _lanProxyService;
    private DashboardWebBridge? _dashboardWebBridge;
    private OpenCodeWatcherService? _openCodeWatcher;
    private WebView2? _webView;

    public DashboardWebBridge? DashboardBridge => _dashboardWebBridge;
    public HudTerminalForm? HudTerminal => _hudTerminal;

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _trayContextMenu;
    private ToolStripMenuItem? _trayOpenItem;
    private ToolStripMenuItem? _trayCliWorkspaceItem;
    private ToolStripMenuItem? _trayHudItem;
    private ToolStripMenuItem? _trayMiniBarItem;
    private ToolStripMenuItem? _trayBrowserDashboardItem;
    private ToolStripMenuItem? _trayCopyLanItem;
    private ToolStripMenuItem? _trayPresetsMenu;
    private ToolStripMenuItem? _trayPresetDuoItem;
    private ToolStripMenuItem? _trayPresetAllItem;
    private ToolStripMenuItem? _trayPresetCleanItem;
    private ToolStripMenuItem? _trayProfilesMenu;
    private ToolStripMenuItem? _trayCliControlsMenu;
    private ToolStripMenuItem? _trayStartAllItem;
    private ToolStripMenuItem? _trayStopAllItem;
    private ToolStripMenuItem? _trayRestartAllItem;
    private ToolStripMenuItem? _trayCliHubItem;
    private ToolStripMenuItem? _trayCliClaudeItem;
    private ToolStripMenuItem? _trayCliAntigravityItem;
    private ToolStripMenuItem? _trayToggleThemeItem;
    private ToolStripMenuItem? _trayCheckUpdateItem;
    private ToolStripMenuItem? _trayRestartAppItem;
    private ToolStripMenuItem? _trayExitItem;

    private bool _isExiting;
    private Icon? _appIcon;
    private MiniBarForm? _miniBarForm;
    private const int HotkeyMiniBarId = 9193;
    private bool _miniBarHotkeyRegistered;

    public MainForm()
    {
        InitializeComponent();

        if (System.ComponentModel.LicenseManager.UsageMode != System.ComponentModel.LicenseUsageMode.Designtime)
        {
            InitializeRuntime(false);
        }
    }

    public MainForm(bool startMinimized) : this()
    {
        if (startMinimized && System.ComponentModel.LicenseManager.UsageMode != System.ComponentModel.LicenseUsageMode.Designtime)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
        }
    }

    private void InitializeRuntime(bool startMinimized)
    {
        ThemeManager.Initialize();

        // Load Ports and Config
        var savedEnginePort = StartupHelper.GetIntPreference("EnginePort", HubProcessService.DefaultHubPort);
        var savedLanPort = StartupHelper.GetIntPreference("LanPort", 9191);

        _hubService = new HubProcessService(savedEnginePort);
        _cliManager = new CliManagerService(_hubService);
        _openCodeWatcher = new OpenCodeWatcherService();
        _lanProxyService = new LanProxyService
        {
            ListenPort = savedLanPort,
            TargetPort = savedEnginePort,
            StartHubAction = () => { _cliManager.Start(CliType.Hub); return Task.CompletedTask; },
            CheckHubAliveAction = () => Task.FromResult(_cliManager.GetInfo(CliType.Hub).State == CliState.Running)
        };
        // Off unless the user turned it on: nothing listens on a LAN interface by default.
        var lanProxyEnabled = StartupHelper.GetPreference("LanProxyEnabled", false);
        if (lanProxyEnabled)
        {
            _lanProxyService.Start(
                savedLanPort,
                savedEnginePort,
                Services.LanProxyService.ReadSelectedDirectPorts());
        }

        try
        {
            ClaudeOpenRouterProxyService.Instance.Start(9230);
        }
        catch { }

        try
        {
            DiscordRpcService.Instance.Start();
        }
        catch { }

        // App Icon
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
            if (File.Exists(iconPath))
            {
                _appIcon = new Icon(iconPath);
                Icon = _appIcon;
            }
        }
        catch { }

        // Setup System Tray
        SetupSystemTray();

        ThemeManager.ThemeChanged += () =>
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() =>
                {
                    ThemeManager.ApplyDwmTitleBarTheme(Handle);
                    ApplyTrayTheme();
                }));
            }
        };

        // Setup WebView2 Workspace
        _ = InitializeWebViewAsync(startMinimized);
    }

    private async Task InitializeWebViewAsync(bool startMinimized)
    {
        try
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(8, 10, 15)
            };
            Controls.Add(_webView);

            var baseLocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userDataFolder = Path.Combine(baseLocalApp, "AgentsHub", "WebView2Data");
            if (!Directory.Exists(userDataFolder))
            {
                Directory.CreateDirectory(userDataFolder);
            }

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            var distPath = Path.Combine(AppContext.BaseDirectory, "ui", "dist");
            var webAssetsPath = Directory.Exists(distPath) ? distPath : Path.Combine(AppContext.BaseDirectory, "ui");
            if (!Directory.Exists(webAssetsPath))
            {
                Directory.CreateDirectory(webAssetsPath);
            }

            // Map https://app.agentshub.local to modern React/Vite SPA dist or ui folder. Deny keeps
            // every other origin out of the local assets; the app itself is same-origin and unaffected.
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.agentshub.local",
                webAssetsPath,
                CoreWebView2HostResourceAccessKind.Deny
            );

            // Wallpapers live outside the build output, so a new build cannot wipe the user's image.
            var themeImagePath = DashboardWebBridge.ThemeImageFolder;
            Directory.CreateDirectory(themeImagePath);
            // Wallpapers are loaded by the app origin from the theme origin, so this mapping stays
            // cross-origin readable. It exposes images only; navigation is guarded below.
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "theme.agentshub.local",
                themeImagePath,
                CoreWebView2HostResourceAccessKind.Allow
            );

            // This WebView can call every host action, so it stays on the two mapped origins and
            // DevTools stay off. A link clicked in a chat message opens in the system browser
            // instead of replacing the privileged page.
            Helpers.WebViewSecurity.Apply(
                _webView.CoreWebView2,
                allowLoopback: false,
                "https://app.agentshub.local",
                "https://theme.agentshub.local");
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            // Attach C# backend bridge
            if (_cliManager != null)
            {
                _dashboardWebBridge = new DashboardWebBridge(this, _cliManager, _lanProxyService, _openCodeWatcher);
                _dashboardWebBridge.AttachWebView(_webView.CoreWebView2);

                // The DeepSeek engine owns its own WebView2, and WebView2 has thread affinity, so it
                // has to be created here on the UI thread rather than on first use from the bridge.
                Services.DeepSeek.DeepSeekEngine.Instance.Initialize(this);

                // Same reason, same place: the Qwen window is a WebView2 host and must be created
                // here. Built on first use from the bridge, it hung forever with no message pump.
                Services.Qwen.QwenEngine.Instance.Initialize(this);
            }

            // Automatically start the compiled Bun Sidecar Engine in the background
            _ = Services.Sidecar.SidecarHostService.Instance.StartAsync();

            // Navigate to modern dashboard
            _webView.CoreWebView2.Navigate("https://app.agentshub.local/index.html");

            if (startMinimized)
            {
                Hide();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize modern workspace: {ex.Message}", "WebView2 Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetupSystemTray()
    {
        _trayContextMenu = new ContextMenuStrip();
        _trayContextMenu.Opening += TrayContextMenu_Opening;

        _trayOpenItem = new ToolStripMenuItem("Open Agents Hub", null, (_, _) => ShowAndActivate())
        {
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        _trayCliWorkspaceItem = new ToolStripMenuItem("CLI Terminal Workspace", null, (_, _) =>
        {
            _dashboardWebBridge?.ToggleOrShowCliWebview();
        });

        _trayHudItem = new ToolStripMenuItem("Quake HUD Terminal [F12]", null, (_, _) =>
        {
            ToggleHudDropdown();
        });

        _trayMiniBarItem = new ToolStripMenuItem("Desktop Mini-Bar [Ctrl+Alt+M]", null, (_, _) =>
        {
            ToggleMiniBar();
        });

        _trayBrowserDashboardItem = new ToolStripMenuItem("Open Web Dashboard in Browser", null, (_, _) =>
        {
            OpenDashboardInBrowser();
        });

        _trayCopyLanItem = new ToolStripMenuItem("Copy Mobile / LAN Link", null, (_, _) =>
        {
            CopyLanDashboardLink();
        });

        _trayPresetsMenu = new ToolStripMenuItem("Workspace Presets");
        _trayPresetDuoItem = new ToolStripMenuItem("AI Duo (Claude + agy)", null, (_, _) =>
        {
            ApplyWorkspacePresetFromTray("Preset_Duo", "AI Duo (Claude + agy)");
        });
        _trayPresetAllItem = new ToolStripMenuItem("Full Suite (All CLIs)", null, (_, _) =>
        {
            ApplyWorkspacePresetFromTray("Preset_All", "Full Suite (All CLIs)");
        });
        _trayPresetCleanItem = new ToolStripMenuItem("Clean Slate", null, (_, _) =>
        {
            ApplyWorkspacePresetFromTray("Preset_Clean", "Clean Slate");
        });
        _trayPresetsMenu.DropDownItems.Add(_trayPresetDuoItem);
        _trayPresetsMenu.DropDownItems.Add(_trayPresetAllItem);
        _trayPresetsMenu.DropDownItems.Add(_trayPresetCleanItem);

        _trayProfilesMenu = new ToolStripMenuItem("Switch Antigravity Profile");

        _trayCliControlsMenu = new ToolStripMenuItem("CLI Engines & Controls");

        _trayStartAllItem = new ToolStripMenuItem("Start All CLIs", null, (_, _) =>
        {
            if (_cliManager != null)
            {
                _cliManager.Start(CliType.Hub);
                _cliManager.Start(CliType.ClaudeCode);
                _cliManager.Start(CliType.AntigravityCli);
                ShowTrayBalloon("CLI Engines", "Started all CLIs.");
            }
        });

        _trayStopAllItem = new ToolStripMenuItem("Stop All CLIs", null, async (_, _) =>
        {
            if (_cliManager != null)
            {
                await _cliManager.StopAsync(CliType.Hub);
                await _cliManager.StopAsync(CliType.ClaudeCode);
                await _cliManager.StopAsync(CliType.AntigravityCli);
                ShowTrayBalloon("CLI Engines", "Stopped all CLIs.");
            }
        });

        _trayRestartAllItem = new ToolStripMenuItem("Restart All CLIs", null, async (_, _) =>
        {
            await RestartAllClisAsync();
        });

        _trayCliHubItem = new ToolStripMenuItem("Hub Web", null, async (_, _) =>
        {
            if (_cliManager == null) return;
            if (_cliManager.GetInfo(CliType.Hub).State == CliState.Running)
            {
                await _cliManager.StopAsync(CliType.Hub);
            }
            else
            {
                _cliManager.Start(CliType.Hub);
            }
        });

        _trayCliClaudeItem = new ToolStripMenuItem("Claude Code", null, async (_, _) =>
        {
            if (_cliManager == null) return;
            if (_cliManager.GetInfo(CliType.ClaudeCode).State == CliState.Running)
            {
                await _cliManager.StopAsync(CliType.ClaudeCode);
            }
            else
            {
                _cliManager.Start(CliType.ClaudeCode);
            }
        });

        _trayCliAntigravityItem = new ToolStripMenuItem("Antigravity CLI", null, async (_, _) =>
        {
            if (_cliManager == null) return;
            if (_cliManager.GetInfo(CliType.AntigravityCli).State == CliState.Running)
            {
                await _cliManager.StopAsync(CliType.AntigravityCli);
            }
            else
            {
                _cliManager.Start(CliType.AntigravityCli);
            }
        });

        _trayCliControlsMenu.DropDownItems.Add(_trayStartAllItem);
        _trayCliControlsMenu.DropDownItems.Add(_trayStopAllItem);
        _trayCliControlsMenu.DropDownItems.Add(_trayRestartAllItem);
        _trayCliControlsMenu.DropDownItems.Add(new ToolStripSeparator());
        _trayCliControlsMenu.DropDownItems.Add(_trayCliHubItem);
        _trayCliControlsMenu.DropDownItems.Add(_trayCliClaudeItem);
        _trayCliControlsMenu.DropDownItems.Add(_trayCliAntigravityItem);

        _trayToggleThemeItem = new ToolStripMenuItem("Switch Theme", null, (_, _) =>
        {
            ThemeManager.ToggleTheme();
        });

        _trayCheckUpdateItem = new ToolStripMenuItem("Check for Updates...", null, async (_, _) =>
        {
            await CheckForUpdatesFromTrayAsync();
        });

        _trayRestartAppItem = new ToolStripMenuItem("Restart Agents Hub", null, (_, _) => RestartApplication());
        _trayExitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication());

        _trayContextMenu.Items.Add(_trayOpenItem);
        _trayContextMenu.Items.Add(new ToolStripSeparator());
        _trayContextMenu.Items.Add(_trayCliWorkspaceItem);
        _trayContextMenu.Items.Add(_trayHudItem);
        _trayContextMenu.Items.Add(_trayMiniBarItem);
        _trayContextMenu.Items.Add(_trayBrowserDashboardItem);
        _trayContextMenu.Items.Add(_trayCopyLanItem);
        _trayContextMenu.Items.Add(new ToolStripSeparator());
        _trayContextMenu.Items.Add(_trayPresetsMenu);
        _trayContextMenu.Items.Add(_trayProfilesMenu);
        _trayContextMenu.Items.Add(_trayCliControlsMenu);
        _trayContextMenu.Items.Add(new ToolStripSeparator());
        _trayContextMenu.Items.Add(_trayToggleThemeItem);
        _trayContextMenu.Items.Add(_trayCheckUpdateItem);
        _trayContextMenu.Items.Add(_trayRestartAppItem);
        _trayContextMenu.Items.Add(_trayExitItem);

        ApplyTrayTheme();

        _notifyIcon = new NotifyIcon
        {
            Text = "Agents Hub - Multi-Agent Workspace",
            Icon = _appIcon ?? SystemIcons.Application,
            ContextMenuStrip = _trayContextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => ShowAndActivate();
    }

    private void TrayContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool winHkEnabled = StartupHelper.GetPreference("GlobalHotkey", true);
        if (_trayOpenItem != null)
        {
            if (winHkEnabled)
            {
                string winKey = StartupHelper.GetStringPreference("GlobalHotkeyKey", "H");
                string winMod = StartupHelper.GetStringPreference("GlobalHotkeyModifier", "Ctrl+Alt");
                string winHkLabel = winMod.Equals("None", StringComparison.OrdinalIgnoreCase) ? winKey : $"{winMod}+{winKey}";
                _trayOpenItem.Text = $"Open Agents Hub [{winHkLabel}]";
            }
            else
            {
                _trayOpenItem.Text = "Open Agents Hub";
            }
        }

        bool hudEnabled = StartupHelper.GetPreference("GlobalHudHotkey", true);
        if (_trayHudItem != null)
        {
            if (hudEnabled)
            {
                string key = StartupHelper.GetStringPreference("HudHotkeyKey", "F12");
                string mod = StartupHelper.GetStringPreference("HudHotkeyModifier", "None");
                string hkLabel = mod.Equals("None", StringComparison.OrdinalIgnoreCase) ? key : $"{mod}+{key}";
                _trayHudItem.Text = $"Quake HUD Terminal [{hkLabel}]";
            }
            else
            {
                _trayHudItem.Text = "Quake HUD Terminal (Disabled)";
            }
        }

        string activePreset = AiConfigService.Instance.GetTerminalWorkspacePreset();
        if (_trayPresetDuoItem != null)
            _trayPresetDuoItem.Checked = string.Equals(activePreset, "Preset_Duo", StringComparison.OrdinalIgnoreCase);
        if (_trayPresetAllItem != null)
            _trayPresetAllItem.Checked = string.Equals(activePreset, "Preset_All", StringComparison.OrdinalIgnoreCase);
        if (_trayPresetCleanItem != null)
            _trayPresetCleanItem.Checked = string.Equals(activePreset, "Preset_Clean", StringComparison.OrdinalIgnoreCase);

        if (_trayProfilesMenu != null)
        {
            _trayProfilesMenu.DropDownItems.Clear();
            var profiles = _cliManager?.ProfileService.GetProfiles() ?? [];
            string activeProf = _cliManager?.ProfileService.ActiveProfile ?? string.Empty;

            if (profiles.Count == 0)
            {
                _trayProfilesMenu.DropDownItems.Add(new ToolStripMenuItem("(No profiles configured)") { Enabled = false });
            }
            else
            {
                foreach (var prof in profiles)
                {
                    string p = prof;
                    bool isAct = string.Equals(p, activeProf, StringComparison.OrdinalIgnoreCase);
                    var pItem = new ToolStripMenuItem(p, null, async (_, _) =>
                    {
                        if (_cliManager != null)
                        {
                            await _cliManager.SwitchAntigravityProfileAsync(p);
                            ShowTrayBalloon("Antigravity Profile", $"Switched active profile to '{p}'");
                        }
                    })
                    {
                        Checked = isAct
                    };
                    _trayProfilesMenu.DropDownItems.Add(pItem);
                }
            }
        }

        if (_cliManager != null)
        {
            var hubState = _cliManager.GetInfo(CliType.Hub).State;
            bool hubRunning = hubState == CliState.Running;
            if (_trayCliHubItem != null)
                _trayCliHubItem.Text = $"Hub Web ({(hubRunning ? "● Running" : "○ Stopped")})";

            var claudeState = _cliManager.GetInfo(CliType.ClaudeCode).State;
            bool claudeRunning = claudeState == CliState.Running;
            if (_trayCliClaudeItem != null)
                _trayCliClaudeItem.Text = $"Claude Code ({(claudeRunning ? "● Running" : "○ Stopped")})";

            var agyState = _cliManager.GetInfo(CliType.AntigravityCli).State;
            bool agyRunning = agyState == CliState.Running;
            if (_trayCliAntigravityItem != null)
                _trayCliAntigravityItem.Text = $"Antigravity CLI ({(agyRunning ? "● Running" : "○ Stopped")})";
        }

        if (_trayToggleThemeItem != null)
        {
            _trayToggleThemeItem.Text = ThemeManager.CurrentTheme == AppTheme.Dark
                ? "Switch to Light Theme"
                : "Switch to Dark Theme";
        }

        ApplyTrayTheme();
    }

    private void ApplyWorkspacePresetFromTray(string presetName, string friendlyName)
    {
        AiConfigService.Instance.ApplyWorkspacePreset(presetName);
        try
        {
            if (_dashboardWebBridge?.CliWebviewForm != null && !_dashboardWebBridge.CliWebviewForm.IsDisposed)
            {
                _dashboardWebBridge.CliWebviewForm.RebuildTabs();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainForm] RebuildTabs error: {ex.Message}");
        }

        ShowTrayBalloon("Workspace Preset", $"Applied '{friendlyName}' preset");
    }

    private async Task RestartAllClisAsync()
    {
        if (_cliManager == null) return;
        ShowTrayBalloon("CLI Engines", "Restarting CLI engines...");
        await _cliManager.StopAsync(CliType.Hub);
        await _cliManager.StopAsync(CliType.ClaudeCode);
        await _cliManager.StopAsync(CliType.AntigravityCli);
        await Task.Delay(400);
        _cliManager.Start(CliType.Hub);
        _cliManager.Start(CliType.ClaudeCode);
        _cliManager.Start(CliType.AntigravityCli);
        ShowTrayBalloon("CLI Engines", "All active CLI engines restarted.");
    }

    private void OpenDashboardInBrowser()
    {
        try
        {
            int lanPort = _lanProxyService?.ListenPort ?? StartupHelper.GetIntPreference("LanPort", 9191);
            string url = $"http://127.0.0.1:{lanPort}/";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowTrayBalloon("Error", $"Could not open browser: {ex.Message}");
        }
    }

    private void CopyLanDashboardLink()
    {
        try
        {
            string lanIp = NetworkHelper.GetLocalIPv4();
            int lanPort = _lanProxyService?.ListenPort ?? StartupHelper.GetIntPreference("LanPort", 9191);
            string token = _lanProxyService is { IsRunning: true } lan && !string.IsNullOrEmpty(lan.AccessToken)
                ? $"?k={lan.AccessToken}"
                : string.Empty;
            string url = $"http://{lanIp}:{lanPort}/{token}";
            Clipboard.SetText(url);
            ShowTrayBalloon("LAN Link Copied", url);
        }
        catch (Exception ex)
        {
            ShowTrayBalloon("Copy Error", $"Could not copy link: {ex.Message}");
        }
    }

    private void RestartApplication()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainForm] RestartApplication error: {ex.Message}");
        }

        ExitApplication();
    }

    private async Task CheckForUpdatesFromTrayAsync()
    {
        ShowTrayBalloon("Updates", "Checking for updates...");
        try
        {
            var info = await UpdateService.Instance.CheckForUpdatesAsync().ConfigureAwait(true);
            if (info.IsUpdateAvailable)
            {
                var dialog = MessageBox.Show(
                    $"A new version of Agents Hub is available!\n\nCurrent: v{info.CurrentVersion}\nLatest: v{info.LatestVersion}\n\nWould you like to open Agents Hub to download and install this update?",
                    "Update Available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (dialog == DialogResult.Yes)
                {
                    ShowAndActivate();
                    if (_webView?.CoreWebView2 != null)
                    {
                        _ = _webView.CoreWebView2.ExecuteScriptAsync("if (window.triggerUpdateView) window.triggerUpdateView();");
                    }
                }
            }
            else if (info.Result == UpdateCheckResult.UpToDate)
            {
                ShowTrayBalloon("Agents Hub", $"You are up to date! (v{info.CurrentVersion})");
            }
            else if (info.Result == UpdateCheckResult.NoReleasesFound)
            {
                ShowTrayBalloon("Agents Hub", $"No published releases found yet on GitHub (v{info.CurrentVersion}).");
            }
            else
            {
                ShowTrayBalloon("Agents Hub", "Could not check for updates. Please verify your internet connection.");
            }
        }
        catch (Exception ex)
        {
            ShowTrayBalloon("Agents Hub", $"Update check failed: {ex.Message}");
        }
    }

    private void ApplyTrayTheme()
    {
        if (_trayContextMenu == null) return;

        var c = ThemeManager.Colors;
        _trayContextMenu.Renderer = new TrayMenuRenderer();
        _trayContextMenu.BackColor = c.CardBackground;
        _trayContextMenu.ForeColor = c.TextPrimary;

        ApplyThemeToMenuItems(_trayContextMenu.Items, c);
    }

    private static void ApplyThemeToMenuItems(ToolStripItemCollection items, ThemeColors c)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = c.CardBackground;
            item.ForeColor = c.TextPrimary;

            if (item is ToolStripDropDownItem dropDownItem && dropDownItem.HasDropDownItems)
            {
                dropDownItem.DropDown.BackColor = c.CardBackground;
                dropDownItem.DropDown.ForeColor = c.TextPrimary;
                ApplyThemeToMenuItems(dropDownItem.DropDownItems, c);
            }
        }
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => ThemeManager.Colors.ButtonHover;
        public override Color MenuItemSelectedGradientBegin => ThemeManager.Colors.ButtonHover;
        public override Color MenuItemSelectedGradientEnd => ThemeManager.Colors.ButtonHover;
        public override Color MenuBorder => ThemeManager.Colors.CardBorder;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color ToolStripDropDownBackground => ThemeManager.Colors.CardBackground;
        public override Color ImageMarginGradientBegin => ThemeManager.Colors.CardBackground;
        public override Color ImageMarginGradientMiddle => ThemeManager.Colors.CardBackground;
        public override Color ImageMarginGradientEnd => ThemeManager.Colors.CardBackground;
        public override Color SeparatorDark => ThemeManager.Colors.CardBorder;
        public override Color SeparatorLight => ThemeManager.Colors.CardBorder;
        public override Color CheckBackground => ThemeManager.Colors.ButtonHover;
        public override Color CheckSelectedBackground => ThemeManager.Colors.AccentGreen;
        public override Color CheckPressedBackground => ThemeManager.Colors.AccentGreen;
        public override Color ButtonSelectedHighlight => ThemeManager.Colors.ButtonHover;
        public override Color ButtonCheckedHighlight => ThemeManager.Colors.AccentGreen;
    }

    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = ThemeManager.Colors.TextPrimary;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = ThemeManager.Colors.TextSecondary;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            var r = e.ImageRectangle;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var checkRect = new Rectangle(r.X + 2, r.Y + 2, Math.Max(12, r.Width - 4), Math.Max(12, r.Height - 4));
            using var bgBrush = new SolidBrush(ThemeManager.Colors.AccentGreen);
            g.FillRectangle(bgBrush, checkRect);

            using var pen = new Pen(ThemeManager.Colors.AccentGreenText, 1.8f);
            var p1 = new Point(checkRect.Left + 3, checkRect.Top + checkRect.Height / 2);
            var p2 = new Point(checkRect.Left + checkRect.Width / 2 - 1, checkRect.Bottom - 4);
            var p3 = new Point(checkRect.Right - 3, checkRect.Top + 3);
            g.DrawLines(pen, [p1, p2, p3]);
        }
    }

    public void ShowAndActivate()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ShowAndActivate));
            return;
        }

        // Starting minimised sets ShowInTaskbar = false, and nothing ever set it back — so once the
        // window was restored from the tray it stayed invisible to the taskbar for the rest of the
        // process, even with every tray preference off.
        if (!ShowInTaskbar) ShowInTaskbar = true;

        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        BringToFront();
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        UnregisterGlobalHotkey();

        if (_cliManager != null)
        {
            _ = Task.Run(async () =>
            {
                await _cliManager.StopAsync(CliType.Hub);
                await _cliManager.StopAsync(CliType.ClaudeCode);
                await _cliManager.StopAsync(CliType.AntigravityCli);
            });
        }

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _openCodeWatcher?.Dispose();
        _dashboardWebBridge?.Dispose();
        RemoveKeyboardHook();
        UnregisterGlobalHotkey();
        UnregisterHudHotkey();
        try { ClaudeOpenRouterProxyService.Instance.Stop(); } catch { }
        try { DiscordRpcService.Instance.Dispose(); } catch { }
        // The Vyce TUI is a separate process tree in its own hidden console. Without this it keeps
        // running after the app closes, holding the session file the next launch wants.
        try { Services.Vyce.VyceEngine.Instance.Dispose(); } catch { }

        Application.Exit();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (IsHandleCreated)
        {
            ThemeManager.ApplyDwmTitleBarTheme(Handle);
        }
        ApplyTrayTheme();
    }

    public void ShowTrayBalloon(string title, string text)
    {
        if (!StartupHelper.GetPreference("WindowsNotifications", true)) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowTrayBalloon(title, text));
            return;
        }
        _notifyIcon?.ShowBalloonTip(1500, title, text, ToolTipIcon.Info);
    }

    // ---------------------------------------------------------------
    // Global hotkey: show or hide the window from anywhere.
    //
    // The app spends most of its life in the tray, and the tray menu is the only way back in.
    // A hotkey is the difference between "click the tray icon" and "press it and keep typing".
    // Ctrl+Alt+H is registered as a system-wide hotkey, so it only works while one instance is
    // running, which the single-instance mutex already guarantees.
    // ---------------------------------------------------------------
    // ---------------------------------------------------------------
    // Global hotkey: show or hide the window or Quake HUD/CLI from anywhere.
    // ---------------------------------------------------------------
    private const int WmHotKey = 0x0312;
    private const int HotkeyId = 0x4C31;
    private const int HotkeyHudId = 0x4C32;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint VkH = 0x48;
    private bool _globalHotkeyRegistered;
    private bool _hudHotkeyRegistered;
    private HudTerminalForm? _hudTerminal;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private static IntPtr _hookHandle = IntPtr.Zero;
    private static LowLevelKeyboardProc? _keyboardProc;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyGlobalHotkey();
    }

    /// <summary>
    /// Registers or unregisters the hotkeys to match stored preferences.
    /// </summary>
    public void ApplyGlobalHotkey()
    {
        bool wantEnabled = StartupHelper.GetPreference("GlobalHotkey", true);

        if (wantEnabled && IsHandleCreated)
        {
            if (_globalHotkeyRegistered) UnregisterGlobalHotkey();
            uint targetVk = GetVkCodeFromName(StartupHelper.GetStringPreference("GlobalHotkeyKey", "H"));
            uint modFlags = GetFsModifiersFromName(StartupHelper.GetStringPreference("GlobalHotkeyModifier", "Ctrl+Alt"));
            _globalHotkeyRegistered = RegisterHotKey(Handle, HotkeyId, modFlags, targetVk);
        }
        else
        {
            UnregisterGlobalHotkey();
        }

        bool wantHud = StartupHelper.GetPreference("GlobalHudHotkey", true);
        if (wantHud)
        {
            // Also register via RegisterHotKey as secondary fallback if supported
            if (IsHandleCreated)
            {
                if (_hudHotkeyRegistered) UnregisterHudHotkey();
                uint targetVk = GetVkCodeFromName(StartupHelper.GetStringPreference("HudHotkeyKey", "F12"));
                uint modFlags = GetFsModifiersFromName(StartupHelper.GetStringPreference("HudHotkeyModifier", "None"));
                _hudHotkeyRegistered = RegisterHotKey(Handle, HotkeyHudId, modFlags, targetVk);
            }
        }
        else
        {
            UnregisterHudHotkey();
        }

        if (IsHandleCreated)
        {
            if (_miniBarHotkeyRegistered) UnregisterHotKey(Handle, HotkeyMiniBarId);
            _miniBarHotkeyRegistered = RegisterHotKey(Handle, HotkeyMiniBarId, 0x0002 | 0x0001, (uint)Keys.M); // Ctrl+Alt+M
        }

        if (wantEnabled || wantHud)
        {
            SetupKeyboardHook();
        }
        else
        {
            RemoveKeyboardHook();
        }
    }

    private void SetupKeyboardHook()
    {
        if (_hookHandle != IntPtr.Zero) return;
        try
        {
            _keyboardProc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr modHandle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, modHandle, 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainForm] SetupKeyboardHook error: {ex.Message}");
        }
    }

    private void RemoveKeyboardHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            try
            {
                UnhookWindowsHookEx(_hookHandle);
            }
            catch { }
            _hookHandle = IntPtr.Zero;
            _keyboardProc = null;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            try
            {
                var kbd = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (IsWindowHotkeyMatch(kbd.vkCode))
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(ShowOrHideFromHotkey));
                    }
                    return (IntPtr)1;
                }

                if (IsQuakeHotkeyMatch(kbd.vkCode))
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(ExecuteQuakeHotkeyAction));
                    }
                    return (IntPtr)1;
                }
            }
            catch { }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool IsWindowHotkeyMatch(uint vk)
    {
        bool wantEnabled = StartupHelper.GetPreference("GlobalHotkey", true);
        if (!wantEnabled) return false;

        string configuredKey = StartupHelper.GetStringPreference("GlobalHotkeyKey", "H");
        uint targetVk = GetVkCodeFromName(configuredKey);
        if (vk != targetVk) return false;

        string mod = StartupHelper.GetStringPreference("GlobalHotkeyModifier", "Ctrl+Alt");
        return IsModifierStateMatch(mod);
    }

    private bool IsQuakeHotkeyMatch(uint vk)
    {
        bool wantHud = StartupHelper.GetPreference("GlobalHudHotkey", true);
        if (!wantHud) return false;

        string configuredKey = StartupHelper.GetStringPreference("HudHotkeyKey", "F12");
        uint targetVk = GetVkCodeFromName(configuredKey);
        if (vk != targetVk) return false;

        string mod = StartupHelper.GetStringPreference("HudHotkeyModifier", "None");
        return IsModifierStateMatch(mod);
    }

    private static bool IsModifierStateMatch(string mod)
    {
        bool ctrlDown = (GetKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
        bool altDown = (GetKeyState(0x12) & 0x8000) != 0;  // VK_MENU
        bool shiftDown = (GetKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
        bool winDown = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0; // VK_LWIN / VK_RWIN

        // Matched the same way as GetFsModifiersFromName, and for the same reason: the settings UI
        // stores "Ctrl + Alt" with spaces, which matched no case here — and the `_ => true` arm made
        // an unrecognised modifier match unconditionally, so a plain H press toggled the window.
        bool wantCtrl = false, wantAlt = false, wantShift = false, wantWin = false;

        foreach (var part in (mod ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": wantCtrl = true; break;
                case "alt": wantAlt = true; break;
                case "shift": wantShift = true; break;
                case "win" or "windows" or "super": wantWin = true; break;
            }
        }

        // Exact match: a hotkey configured as Ctrl+Alt must not fire on Ctrl alone, or on nothing.
        return ctrlDown == wantCtrl && altDown == wantAlt && shiftDown == wantShift && winDown == wantWin;
    }

    private static uint GetVkCodeFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0x7B; // VK_F12 default
        string trimmed = name.Trim().ToUpperInvariant();
        return trimmed switch
        {
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            "OEM3" or "~" or "`" => 0xC0,
            "SPACE" => 0x20,
            "TAB" => 0x09,
            _ => (trimmed.Length == 1 && char.IsLetterOrDigit(trimmed[0])) ? (uint)trimmed[0] : 0x7B
        };
    }

    /// <summary>
    /// Parses a modifier label into MOD_* flags.
    ///
    /// The old version matched whole strings ("ctrl+alt"), but the settings UI stores labels with
    /// spaces ("Ctrl + Alt"), so every combination fell through to "no modifiers" and the hotkey was
    /// registered as the bare key — pressing H anywhere toggled the window.
    /// </summary>
    private static uint GetFsModifiersFromName(string? mod)
    {
        uint flags = 0x4000; // MOD_NOREPEAT
        if (string.IsNullOrWhiteSpace(mod)) return flags;

        foreach (var part in mod.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Unsuffixed literals make the switch return int, and `uint |= int` sign-extends the
            // operand. The `u` suffixes keep the whole expression unsigned.
            flags |= part.ToLowerInvariant() switch
            {
                "alt" => 0x0001u,
                "ctrl" or "control" => 0x0002u,
                "shift" => 0x0004u,
                "win" or "windows" or "super" => 0x0008u,
                _ => 0u
            };
        }

        return flags;
    }

    private void UnregisterGlobalHotkey()
    {
        if (!_globalHotkeyRegistered || !IsHandleCreated) return;

        try
        {
            UnregisterHotKey(Handle, HotkeyId);
            if (_miniBarHotkeyRegistered)
            {
                UnregisterHotKey(Handle, HotkeyMiniBarId);
                _miniBarHotkeyRegistered = false;
            }
        }
        catch { }

        _globalHotkeyRegistered = false;
    }

    private void UnregisterHudHotkey()
    {
        if (!_hudHotkeyRegistered || !IsHandleCreated) return;

        try
        {
            UnregisterHotKey(Handle, HotkeyHudId);
        }
        catch { }

        _hudHotkeyRegistered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey)
        {
            int hotkeyId = m.WParam.ToInt32();
            if (hotkeyId == HotkeyId)
            {
                ShowOrHideFromHotkey();
                return;
            }
            if (hotkeyId == HotkeyHudId)
            {
                ExecuteQuakeHotkeyAction();
                return;
            }
            if (hotkeyId == HotkeyMiniBarId)
            {
                ToggleMiniBar();
                return;
            }
        }

        base.WndProc(ref m);
    }

    public void ExecuteQuakeHotkeyAction()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ExecuteQuakeHotkeyAction));
            return;
        }

        string target = StartupHelper.GetStringPreference("HudHotkeyTarget", "cliwebview");

        if (target.Equals("hud", StringComparison.OrdinalIgnoreCase))
        {
            ToggleHudDropdown();
        }
        else if (target.Equals("both", StringComparison.OrdinalIgnoreCase))
        {
            ToggleHudDropdown();
            _dashboardWebBridge?.ToggleOrShowCliWebview();
        }
        else // "cliwebview" - default: Standalone Terminals Window
        {
            _dashboardWebBridge?.ToggleOrShowCliWebview();
        }
    }

    public void ToggleHudDropdown()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ToggleHudDropdown));
            return;
        }

        if (_hudTerminal == null || _hudTerminal.IsDisposed)
        {
            _hudTerminal = new HudTerminalForm(_cliManager, this);
        }
        _hudTerminal.Toggle();
    }

    public void ToggleMiniBar()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ToggleMiniBar));
            return;
        }

        if (_miniBarForm == null || _miniBarForm.IsDisposed)
        {
            _miniBarForm = new MiniBarForm(
                this,
                quickPrompt =>
                {
                    _cliManager?.SendInput("hub", quickPrompt.Trim() + "\r\n");
                    ShowTrayBalloon("Prompt Sent", quickPrompt);
                },
                () => AiConfigService.Instance.GetActiveWorkspace()?.Name ?? "Default Workspace",
                () => _cliManager?.GetActiveCount() ?? 0
            );
        }

        if (_miniBarForm.Visible)
        {
            _miniBarForm.Hide();
        }
        else
        {
            _miniBarForm.RefreshState();
            _miniBarForm.Show();
            _miniBarForm.Activate();
        }
    }

    public void ShowOrHideFromHotkey()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ShowOrHideFromHotkey));
            return;
        }

        if (Visible && WindowState != FormWindowState.Minimized)
        {
            Hide();
        }
        else
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            BringToFront();
            Activate();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            var minimizeToTray = StartupHelper.GetPreference("MinimizeToTray", true);
            if (minimizeToTray)
            {
                Hide();
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
        {
            var minimizeToTray = StartupHelper.GetPreference("MinimizeToTray", true);
            if (minimizeToTray)
            {
                e.Cancel = true;
                Hide();
                ShowTrayBalloon("Agents Hub", "Agents Hub is running in the background.");
                return;
            }
            else
            {
                ExitApplication();
                return;
            }
        }

        base.OnFormClosing(e);
    }
}
