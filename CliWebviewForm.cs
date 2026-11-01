using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentsHub.Helpers;
using AgentsHub.Services;
using Microsoft.Web.WebView2.Core;

namespace AgentsHub;

public partial class CliWebviewForm : Form
{
    private static bool IsInDesignMode
    {
        get
        {
            if (System.ComponentModel.LicenseManager.UsageMode == System.ComponentModel.LicenseUsageMode.Designtime)
            {
                return true;
            }

            try
            {
                using var currentProc = Process.GetCurrentProcess();
                var procName = currentProc.ProcessName;
                return procName.Contains("devenv", StringComparison.OrdinalIgnoreCase) ||
                       procName.Contains("DesignToolsServer", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    private readonly CliManagerService? _cliManager;
    private string _primaryTabId = string.Empty;
    private string _secondaryTabId = string.Empty;
    private bool _isSplitActive;
    private int _splitterDragOffset = 3;
    private WorkspacePane _focusedPane = WorkspacePane.Primary;

    public enum WorkspacePane
    {
        Primary,
        Secondary
    }

    // Pane-aware replacement for a plain tab field, so it is a property and takes the property
    // naming style. The underscore form tripped IDE1006, which the build now enforces.
    private string ActivePaneTabId
    {
        get => _isSplitActive && _focusedPane == WorkspacePane.Secondary ? _secondaryTabId : _primaryTabId;
        set
        {
            if (_isSplitActive && _focusedPane == WorkspacePane.Secondary)
            {
                _secondaryTabId = value;
            }
            else
            {
                _primaryTabId = value;
            }
        }
    }

    public string ActiveTabId => ActivePaneTabId;

    /// <summary>Raised with the new tab id whenever the active workspace tab changes.</summary>
    public event Action<string>? ActiveTabChanged;

    private bool _hubInitialized;
    private readonly string _terminalHtmlPath = string.Empty;
    private readonly string _hubOfflineHtmlPath = string.Empty;
    private Color _toggleRunNormalColor = ThemeManager.Colors.AccentGreen;
    private Color _toggleRunHoverColor = ThemeManager.Colors.AccentGreenHover;

    private Microsoft.Web.WebView2.WinForms.WebView2 hubWebView = null!;
    private Microsoft.Web.WebView2.WinForms.WebView2 claudeWebView = null!;
    private Microsoft.Web.WebView2.WinForms.WebView2 antigravityWebView = null!;
    private readonly Dictionary<string, Microsoft.Web.WebView2.WinForms.WebView2> _customWebViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Microsoft.Web.WebView2.WinForms.WebView2> _secondaryCloneWebViews = new(StringComparer.OrdinalIgnoreCase);
    private bool _isUpdatingPaneCombos;

    private sealed record PaneTabItem(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
    private CoreWebView2Environment? _webViewEnv;
    private readonly TaskCompletionSource<bool> _envInitTcs = new();

    private ComboBox? _profileComboBox;
    private Button? _addProfileButton;
    private ComboBox? _claudeEngineComboBox;
    private ComboBox? _claudeModelComboBox;
    private Button? _manageClisButton;
    private Button? _shellLauncherButton;
    private Button? _sessionConfigButton;
    private ContextMenuStrip? _sessionConfigContextMenu;
    private ContextMenuStrip? _shellContextMenu;
    private readonly List<Button> _dynamicTabButtons = [];
    private readonly ToolTip _actionToolTip = new() { ShowAlways = true };
    private bool _isUpdatingProfileCombo;
    private bool _isUpdatingClaudeControls;
    private bool _isLayoutRunning;

    // Empty-state workspace view, shown when no CLI workspace is enabled.
    private Panel? _emptyStatePanel;
    private Panel? _emptyStateCard;
    private Label? _emptyTitleLabel;
    private Label? _emptySubtitleLabel;
    private ComboBox? _emptyAddCombo;
    private Button? _emptyAddButton;
    private Label? _emptyHintLabel;

    // Rebuilt on every right-click so a stale tab id can never be actioned.
    private ContextMenuStrip? _tabContextMenu;

    private Form? _mainForm;
    private bool _didWeHideMainForm;
    private ContextMenuStrip? _mainWindowContextMenu;

    public CliWebviewForm()
    {
        InitializeComponent();
    }

    public CliWebviewForm(CliManagerService? cliManager, CliType initialTab = CliType.Hub, Form? mainForm = null) : this()
    {
        if (System.ComponentModel.LicenseManager.UsageMode != System.ComponentModel.LicenseUsageMode.Designtime && cliManager != null)
        {
            _cliManager = cliManager;
            string reqTab = AiConfigService.NormalizeCliId(CliTypeToId(initialTab));
            _primaryTabId = ResolveDefaultInitialTab(reqTab);
            _secondaryTabId = ResolveDefaultSecondaryTab(_primaryTabId);
            SetMainForm(mainForm);

            // ui/ is always copied next to the executable, so it is the only place these live.
            // The assets/web fallback that used to sit behind this check has been removed.
            var baseDir = AppContext.BaseDirectory;
            _terminalHtmlPath = Path.Combine(baseDir, "ui", "terminal.html");
            _hubOfflineHtmlPath = Path.Combine(baseDir, "ui", "hub-offline.html");

            var cliIconPath = Path.Combine(baseDir, "assets", "cli.ico");
            if (File.Exists(cliIconPath))
            {
                Icon = new Icon(cliIconPath);
            }
            else
            {
                var appIconPath = Path.Combine(baseDir, "assets", "app.ico");
                if (File.Exists(appIconPath)) Icon = new Icon(appIconPath);
            }

            SetupStyles();
            HookEvents();

            _ = InitializeWebViewsAsync();
        }
    }

    public CliType ActiveTab => IdToCliType(ActivePaneTabId);

    private static string CliTypeToId(CliType type) => type switch
    {
        CliType.ClaudeCode => "claude_code",
        CliType.AntigravityCli => "antigravity_cli",
        _ => "hub"
    };

    private static CliType IdToCliType(string id)
    {
        string norm = AiConfigService.NormalizeCliId(id);
        return norm switch
        {
            "claude_code" => CliType.ClaudeCode,
            "antigravity_cli" => CliType.AntigravityCli,
            _ => CliType.Hub
        };
    }

    private static bool IsBuiltInCli(string id)
    {
        string norm = AiConfigService.NormalizeCliId(id);
        return string.Equals(norm, "hub", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(norm, "claude_code", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(norm, "antigravity_cli", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAntigravityTab(string id)
    {
        string norm = AiConfigService.NormalizeCliId(id);
        return string.Equals(norm, "antigravity_cli", StringComparison.OrdinalIgnoreCase) ||
               CliManagerService.IsAntigravityProfileId(norm, out _);
    }

    private bool IsTabEnabled(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var workspaces = AiConfigService.Instance.GetCliWorkspaces();
        string normId = AiConfigService.NormalizeCliId(id);

        if (CliManagerService.IsAntigravityProfileId(normId, out _))
        {
            return workspaces.Any(w => w.IsEnabled && string.Equals(AiConfigService.NormalizeCliId(w.Id), "antigravity_cli", StringComparison.OrdinalIgnoreCase));
        }

        return workspaces.Any(w => w.IsEnabled && string.Equals(AiConfigService.NormalizeCliId(w.Id), normId, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveDefaultInitialTab(string? requestedId)
    {
        var workspaces = AiConfigService.Instance.GetCliWorkspaces();
        var enabled = workspaces.Where(w => w.IsEnabled).ToList();
        if (enabled.Count == 0) return string.Empty;

        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            string normReq = AiConfigService.NormalizeCliId(requestedId);

            if (CliManagerService.IsAntigravityProfileId(normReq, out _))
            {
                if (enabled.Any(w => string.Equals(AiConfigService.NormalizeCliId(w.Id), "antigravity_cli", StringComparison.OrdinalIgnoreCase)))
                {
                    return normReq;
                }
            }
            else
            {
                var match = enabled.FirstOrDefault(w => string.Equals(AiConfigService.NormalizeCliId(w.Id), normReq, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    if (string.Equals(match.Id, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
                    {
                        var prof = _cliManager?.ProfileService.ActiveProfile ?? "Default";
                        return CliManagerService.FormatAntigravityProfileId(prof);
                    }
                    return normReq;
                }
            }
        }

        var first = enabled[0];
        string firstNorm = AiConfigService.NormalizeCliId(first.Id);
        if (string.Equals(firstNorm, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
        {
            var prof = _cliManager?.ProfileService.ActiveProfile ?? "Default";
            return CliManagerService.FormatAntigravityProfileId(prof);
        }
        return firstNorm;
    }

    private string ResolveDefaultSecondaryTab(string primaryId)
    {
        var workspaces = AiConfigService.Instance.GetCliWorkspaces();
        var enabled = workspaces.Where(w => w.IsEnabled).ToList();
        if (enabled.Count == 0) return string.Empty;

        foreach (var ws in enabled)
        {
            string norm = AiConfigService.NormalizeCliId(ws.Id);
            if (string.Equals(norm, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
            {
                var allProfs = _cliManager?.ProfileService.GetProfiles() ?? [];
                foreach (var p in allProfs)
                {
                    string pId = CliManagerService.FormatAntigravityProfileId(p);
                    if (!string.Equals(pId, primaryId, StringComparison.OrdinalIgnoreCase))
                    {
                        return pId;
                    }
                }
            }
            else if (!string.Equals(norm, primaryId, StringComparison.OrdinalIgnoreCase))
            {
                return norm;
            }
        }

        return primaryId;
    }

    private void SetupStyles()
    {
        _profileComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 22,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.75F),
            Size = new Size(160, 28),
            Cursor = Cursors.Hand,
            Visible = false
        };
        _profileComboBox.DrawItem += ProfileComboBox_DrawItem;

        _addProfileButton = new Button
        {
            Text = "+ Add",
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Size = new Size(52, 32),
            Cursor = Cursors.Hand,
            Visible = false
        };
        _addProfileButton.FlatAppearance.BorderSize = 1;
        _actionToolTip.SetToolTip(_addProfileButton, "Add new Antigravity account");

        toolsPanel.Controls.Add(_profileComboBox);
        toolsPanel.Controls.Add(_addProfileButton);

        _claudeEngineComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 22,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Size = new Size(140, 28),
            Cursor = Cursors.Hand,
            Visible = false
        };
        _claudeEngineComboBox.Items.AddRange(["OpenRouter (Free)", "Official Anthropic"]);
        _claudeEngineComboBox.DrawItem += ClaudeComboBox_DrawItem;
        _actionToolTip.SetToolTip(_claudeEngineComboBox, "Select Claude Engine: OpenRouter (Free) or Official Anthropic");

        _claudeModelComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 22,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.25F),
            Size = new Size(185, 28),
            Cursor = Cursors.Hand,
            Visible = false
        };
        var cachedModels = AiConfigService.Instance.GetCachedOpenRouterFreeModels();
        foreach (var m in cachedModels)
        {
            _claudeModelComboBox.Items.Add(m);
        }
        _claudeModelComboBox.DrawItem += ClaudeComboBox_DrawItem;
        _actionToolTip.SetToolTip(_claudeModelComboBox, "Select Free OpenRouter Model for Claude Code");

        toolsPanel.Controls.Add(_claudeEngineComboBox);
        toolsPanel.Controls.Add(_claudeModelComboBox);

        UpdateClaudeControlsUi();

        _ = Task.Run(async () =>
        {
            try
            {
                var liveModels = await AiConfigService.Instance.FetchOpenRouterFreeModelsAsync(forceRefresh: false);
                if (liveModels.Count > 0)
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(() =>
                        {
                            if (_claudeModelComboBox != null && !_isUpdatingClaudeControls)
                            {
                                var currentSelected = _claudeModelComboBox.SelectedItem as string ?? AiConfigService.Instance.GetClaudeSelectedModel();
                                _claudeModelComboBox.BeginUpdate();
                                _claudeModelComboBox.Items.Clear();
                                foreach (var m in liveModels)
                                {
                                    _claudeModelComboBox.Items.Add(m);
                                }
                                if (!string.IsNullOrWhiteSpace(currentSelected) && _claudeModelComboBox.Items.Contains(currentSelected))
                                {
                                    _claudeModelComboBox.SelectedItem = currentSelected;
                                }
                                else if (_claudeModelComboBox.Items.Count > 0)
                                {
                                    _claudeModelComboBox.SelectedIndex = 0;
                                }
                                _claudeModelComboBox.EndUpdate();
                            }
                        });
                    }
                }
            }
            catch { }
        });

        SetupButtonHover(_addProfileButton);

        _shellLauncherButton = new Button
        {
            Size = new Size(32, 32),
            Text = "",
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _shellLauncherButton.FlatAppearance.BorderSize = 1;
        _shellLauncherButton.Paint += DrawShellLauncherIcon;
        _actionToolTip.SetToolTip(_shellLauncherButton, "Open Shell (CMD / PowerShell - Standard & Admin)");
        SetupButtonHover(_shellLauncherButton);
        _shellLauncherButton.Click += ShellLauncherButton_Click;
        toolsPanel.Controls.Add(_shellLauncherButton);

        _sessionConfigButton = new Button
        {
            Size = new Size(32, 32),
            Text = "",
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _sessionConfigButton.FlatAppearance.BorderSize = 1;
        _sessionConfigButton.Paint += DrawSessionConfigIcon;
        _actionToolTip.SetToolTip(_sessionConfigButton, "Session Settings (Project Folder, Resume, Auto-Approve)");
        SetupButtonHover(_sessionConfigButton);
        _sessionConfigButton.Click += (_, _) => ShowSessionConfigContextMenu();
        toolsPanel.Controls.Add(_sessionConfigButton);

        toggleRunButton.Size = new Size(32, 32);
        toggleRunButton.Text = "";
        toggleRunButton.Paint += DrawToggleRunIcon;
        toggleRunButton.MouseEnter += (_, _) =>
        {
            if (toggleRunButton.Enabled) toggleRunButton.BackColor = _toggleRunHoverColor;
        };
        toggleRunButton.MouseLeave += (_, _) =>
        {
            if (toggleRunButton.Enabled) toggleRunButton.BackColor = _toggleRunNormalColor;
        };

        restartCliButton.Size = new Size(32, 32);
        restartCliButton.Text = "";
        restartCliButton.Paint += DrawRestartIcon;
        SetupButtonHover(restartCliButton);

        clearTerminalButton.Size = new Size(32, 32);
        clearTerminalButton.Text = "";
        clearTerminalButton.Paint += DrawClearIcon;
        SetupButtonHover(clearTerminalButton);

        openBrowserButton.Size = new Size(32, 32);
        openBrowserButton.Text = "";
        openBrowserButton.Paint += DrawBrowserIcon;
        SetupButtonHover(openBrowserButton);

        toggleMainWindowButton.Size = new Size(32, 32);
        toggleMainWindowButton.Text = "";
        toggleMainWindowButton.Paint += DrawToggleMainWindowIcon;
        SetupButtonHover(toggleMainWindowButton);
        toggleMainWindowButton.Click += (_, _) => ToggleMainWindowVisibility();
        toggleMainWindowButton.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowMainWindowContextMenu();
        };

        toggleSplitButton.Size = new Size(32, 32);
        toggleSplitButton.Text = "";
        toggleSplitButton.Paint += DrawToggleSplitIcon;
        SetupButtonHover(toggleSplitButton);
        toggleSplitButton.Click += (_, _) => ToggleSplitMode();

        paneHeaderLeft.Click += (_, _) => SetFocusedPane(WorkspacePane.Primary);
        paneTitleLeft.Click += (_, _) => SetFocusedPane(WorkspacePane.Primary);
        paneHeaderRight.Click += (_, _) => SetFocusedPane(WorkspacePane.Secondary);
        paneTitleRight.Click += (_, _) => SetFocusedPane(WorkspacePane.Secondary);
        paneCloseRightButton.Click += (_, _) => ToggleSplitMode(false);
        SetupButtonHover(paneCloseRightButton);

        paneComboLeft.DrawMode = DrawMode.OwnerDrawFixed;
        paneComboLeft.ItemHeight = 22;
        paneComboLeft.DrawItem += PaneComboBox_DrawItem;
        paneComboLeft.SelectedIndexChanged += (_, _) =>
        {
            if (_isUpdatingPaneCombos) return;
            if (paneComboLeft.SelectedItem is PaneTabItem item)
            {
                _primaryTabId = item.Id;
                SetFocusedPane(WorkspacePane.Primary);
                ApplyPaneLayout();
                UpdateTabButtonsUi();
                UpdateToolbarUi();
            }
        };

        paneComboRight.DrawMode = DrawMode.OwnerDrawFixed;
        paneComboRight.ItemHeight = 22;
        paneComboRight.DrawItem += PaneComboBox_DrawItem;
        paneComboRight.SelectedIndexChanged += (_, _) =>
        {
            if (_isUpdatingPaneCombos) return;
            if (paneComboRight.SelectedItem is PaneTabItem item)
            {
                _secondaryTabId = item.Id;
                SetFocusedPane(WorkspacePane.Secondary);
                ApplyPaneLayout();
                UpdateTabButtonsUi();
                UpdateToolbarUi();
            }
        };

        paneHeaderLeft.Resize += (_, _) => LayoutPaneHeaders();
        paneHeaderRight.Resize += (_, _) => LayoutPaneHeaders();

        splitWorkspaceContainer.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                int offset = e.X - splitWorkspaceContainer.SplitterDistance;
                _splitterDragOffset = (offset >= 0 && offset <= splitWorkspaceContainer.SplitterWidth)
                    ? offset
                    : splitWorkspaceContainer.SplitterWidth / 2;
            }
        };

        splitWorkspaceContainer.SplitterMoving += (_, e) =>
        {
            var clientPos = splitWorkspaceContainer.PointToClient(Cursor.Position);
            int mouseX = clientPos.X;
            int target = mouseX - _splitterDragOffset;

            int min1 = splitWorkspaceContainer.Panel1MinSize;
            int min2 = splitWorkspaceContainer.Panel2MinSize;
            int maxDist = splitWorkspaceContainer.Width - min2 - splitWorkspaceContainer.SplitterWidth;
            if (maxDist > min1)
            {
                target = Math.Clamp(target, min1, maxDist);
                e.SplitX = target;
                try
                {
                    if (splitWorkspaceContainer.SplitterDistance != target)
                    {
                        splitWorkspaceContainer.SplitterDistance = target;
                        splitWorkspaceContainer.Update();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SplitterMoving] {ex.Message}");
                }
            }
        };
        splitWorkspaceContainer.SplitterMoved += (_, _) => TriggerTerminalFit();
        splitWorkspaceContainer.Resize += (_, _) =>
        {
            if (_isSplitActive) UpdateSplitMinSizes();
        };

        SetDoubleBuffered(this);
        SetDoubleBuffered(splitWorkspaceContainer);
        SetDoubleBuffered(panelLeft);
        SetDoubleBuffered(panelRight);
        SetDoubleBuffered(paneHostLeft);
        SetDoubleBuffered(paneHostRight);

        contentPanel.Padding = new Padding(0, 10, 0, 0);
        contentPanel.BackColor = ThemeManager.Colors.WindowBackground;

        topToolbar.Resize += (_, _) => LayoutToolbar();
        toolsPanel.Resize += (_, _) => LayoutToolbar();
        contentPanel.Resize += (_, _) => TriggerTerminalFit();
        // While the border is dragged Resize fires continuously and the
        // terminal can settle on a stale grid; refit once the drag ends.
        ResizeEnd += (_, _) => TriggerTerminalFit();
        topToolbar.Paint += (s, e) =>
        {
            using var pen = new Pen(ThemeManager.Colors.CardBorder, 1);
            e.Graphics.DrawLine(pen, 0, topToolbar.Height - 1, topToolbar.Width, topToolbar.Height - 1);
        };

        RebuildTabs();
        ApplyTheme();
    }

    private void TriggerTerminalFit()
    {
        var primaryWv = GetWebViewForId(_primaryTabId);
        if (primaryWv?.CoreWebView2 != null)
        {
            PostToWebView(primaryWv, "{\"type\":\"fit\"}");
        }

        if (_isSplitActive)
        {
            var secondaryWv = GetWebViewForPane(_secondaryTabId, WorkspacePane.Secondary);
            if (secondaryWv?.CoreWebView2 != null && secondaryWv != primaryWv)
            {
                PostToWebView(secondaryWv, "{\"type\":\"fit\"}");
            }
        }
    }

    private void DrawShellLauncherIcon(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color glyphColor = ThemeManager.Colors.TextPrimary;
        using var pen = new Pen(glyphColor, 1.8f);

        int cx = 16;
        int cy = 16;

        Point[] pts =
        [
            new Point(cx - 7, cy - 5),
            new Point(cx - 2, cy),
            new Point(cx - 7, cy + 5)
        ];
        g.DrawLines(pen, pts);

        g.DrawLine(pen, cx + 1, cy + 5, cx + 8, cy + 5);
    }

    private void ShellLauncherButton_Click(object? sender, EventArgs e)
    {
        if (_shellLauncherButton == null) return;
        BuildShellContextMenu();
        _shellContextMenu?.Show(_shellLauncherButton, new Point(0, _shellLauncherButton.Height + 2));
    }

    private void BuildShellContextMenu()
    {
        _shellContextMenu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new ShellColorTable())
        };

        var c = ThemeManager.Colors;
        _shellContextMenu.BackColor = c.CardBackground;
        _shellContextMenu.ForeColor = c.TextPrimary;

        bool embed = StartupHelper.GetPreference("EmbedShellsInTerminalWorkspace", false);

        var cmdNormal = new ToolStripMenuItem("Command Prompt (Standard)", null, (_, _) =>
        {
            if (embed)
            {
                OpenEmbeddedShell("cmd", "Command Prompt", "cmd.exe");
            }
            else
            {
                ShellLauncher.OpenCommandPrompt(false);
            }
        })
        {
            ForeColor = c.TextPrimary
        };

        var cmdAdmin = new ToolStripMenuItem("Command Prompt (Administrator)", null, (_, _) => ShellLauncher.OpenCommandPrompt(true))
        {
            ForeColor = c.DangerRed
        };

        var psNormal = new ToolStripMenuItem("PowerShell (Standard)", null, (_, _) =>
        {
            if (embed)
            {
                OpenEmbeddedShell("powershell", "PowerShell", "powershell.exe");
            }
            else
            {
                ShellLauncher.OpenPowerShell(false);
            }
        })
        {
            ForeColor = c.TextPrimary
        };

        var psAdmin = new ToolStripMenuItem("PowerShell (Administrator)", null, (_, _) => ShellLauncher.OpenPowerShell(true))
        {
            ForeColor = c.DangerRed
        };

        _shellContextMenu.Items.Add(cmdNormal);
        _shellContextMenu.Items.Add(cmdAdmin);
        _shellContextMenu.Items.Add(new ToolStripSeparator());
        _shellContextMenu.Items.Add(psNormal);
        _shellContextMenu.Items.Add(psAdmin);
    }

    private sealed class ShellColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => ThemeManager.Colors.ButtonHover;
        public override Color MenuItemSelectedGradientBegin => ThemeManager.Colors.ButtonHover;
        public override Color MenuItemSelectedGradientEnd => ThemeManager.Colors.ButtonHover;
        public override Color MenuBorder => ThemeManager.Colors.CardBorder;
        public override Color ToolStripDropDownBackground => ThemeManager.Colors.CardBackground;
        public override Color ImageMarginGradientBegin => ThemeManager.Colors.CardBackground;
        public override Color ImageMarginGradientMiddle => ThemeManager.Colors.CardBackground;
        public override Color ImageMarginGradientEnd => ThemeManager.Colors.CardBackground;
        public override Color SeparatorDark => ThemeManager.Colors.CardBorder;
        public override Color SeparatorLight => ThemeManager.Colors.CardBorder;
    }

    private void ProfileComboBox_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _profileComboBox == null) return;

        var c = ThemeManager.Colors;
        bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var bg = isSelected ? c.ButtonHover : c.WellBackground;

        using var brush = new SolidBrush(bg);
        e.Graphics.FillRectangle(brush, e.Bounds);

        var item = _profileComboBox.Items[e.Index] as ProfileItem;
        string text = item?.DisplayText ?? _profileComboBox.Items[e.Index]?.ToString() ?? "";

        var fg = item != null && item.IsRunning
            ? c.AccentGreen
            : (isSelected ? c.TextPrimary : c.TextSecondary);

        TextRenderer.DrawText(
            e.Graphics,
            text,
            e.Font ?? _profileComboBox.Font,
            new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 2, e.Bounds.Width - 8, e.Bounds.Height - 4),
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void ClaudeComboBox_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || sender is not ComboBox cb) return;

        var c = ThemeManager.Colors;
        bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var bg = isSelected ? c.ButtonHover : c.WellBackground;

        using var brush = new SolidBrush(bg);
        e.Graphics.FillRectangle(brush, e.Bounds);

        string text = cb.Items[e.Index]?.ToString() ?? "";
        var fg = isSelected ? c.TextPrimary : c.TextSecondary;

        TextRenderer.DrawText(
            e.Graphics,
            text,
            e.Font ?? cb.Font,
            new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 2, e.Bounds.Width - 8, e.Bounds.Height - 4),
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void LayoutPaneHeaders()
    {
        if (paneHeaderLeft == null || paneHeaderRight == null) return;
        paneComboLeft.Width = Math.Max(120, paneHeaderLeft.Width - 60);
        paneCloseRightButton.Left = Math.Max(0, paneHeaderRight.Width - 28);
        paneComboRight.Width = Math.Max(120, paneCloseRightButton.Left - 66);
    }

    private static void SetDoubleBuffered(Control? control)
    {
        if (control == null) return;
        try
        {
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(control, true);
        }
        catch { }
    }

    private static void PaneComboBox_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox cb || e.Index < 0 || e.Index >= cb.Items.Count) return;
        var c = ThemeManager.Colors;
        bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var bg = isSelected ? c.ButtonHover : c.WellBackground;

        using var brush = new SolidBrush(bg);
        e.Graphics.FillRectangle(brush, e.Bounds);

        string text = cb.Items[e.Index] is PaneTabItem it ? it.DisplayName : (cb.Items[e.Index]?.ToString() ?? "");
        var fg = isSelected ? c.TextPrimary : c.TextSecondary;

        TextRenderer.DrawText(
            e.Graphics,
            text,
            e.Font ?? cb.Font,
            new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 1, e.Bounds.Width - 8, e.Bounds.Height - 2),
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void UpdateClaudeControlsUi()
    {
        if (_claudeEngineComboBox == null || _claudeModelComboBox == null) return;

        try
        {
            _isUpdatingClaudeControls = true;

            string mode = AiConfigService.Instance.GetClaudeEngineMode();
            bool isOfficial = string.Equals(mode, "Official", StringComparison.OrdinalIgnoreCase);

            _claudeEngineComboBox.SelectedIndex = isOfficial ? 1 : 0;

            string selectedModel = AiConfigService.Instance.GetClaudeSelectedModel();
            int modelIdx = _claudeModelComboBox.Items.IndexOf(selectedModel);
            if (modelIdx >= 0)
            {
                _claudeModelComboBox.SelectedIndex = modelIdx;
            }
            else
            {
                _claudeModelComboBox.Items.Insert(0, selectedModel);
                _claudeModelComboBox.SelectedIndex = 0;
            }

            _claudeModelComboBox.Visible = !isOfficial && string.Equals(ActivePaneTabId, "claude_code", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _isUpdatingClaudeControls = false;
        }
    }

    private void DrawToggleRunIcon(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_cliManager == null) return;
        var info = _cliManager.GetInfo(ActivePaneTabId);

        Color glyphColor = info.State switch
        {
            CliState.Running => Color.White,
            CliState.Starting => Color.Black,
            _ => ThemeManager.Colors.AccentGreenText
        };

        using var brush = new SolidBrush(glyphColor);

        int cx = toggleRunButton.Width / 2;
        int cy = toggleRunButton.Height / 2;

        if (info.State == CliState.Running)
        {
            // Stop icon (square)
            g.FillRectangle(brush, cx - 5, cy - 5, 10, 10);
        }
        else if (info.State == CliState.Starting)
        {
            // Starting icon (pause / dual bars)
            g.FillRectangle(brush, cx - 5, cy - 5, 3, 10);
            g.FillRectangle(brush, cx + 2, cy - 5, 3, 10);
        }
        else
        {
            // Start icon (play triangle)
            Point[] pts =
            [
                new Point(cx - 4, cy - 6),
                new Point(cx - 4, cy + 6),
                new Point(cx + 6, cy)
            ];
            g.FillPolygon(brush, pts);
        }
    }

    private void DrawRestartIcon(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color glyphColor = restartCliButton.Enabled ? ThemeManager.Colors.TextPrimary : ThemeManager.Colors.TextMuted;
        using var pen = new Pen(glyphColor, 1.8f);

        int cx = restartCliButton.Width / 2;
        int cy = restartCliButton.Height / 2;
        int r = 6;

        g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, 45, 270);

        // Arrow head pointing clockwise
        using var brush = new SolidBrush(glyphColor);
        Point[] pts =
        [
            new Point(cx + 5, cy - 7),
            new Point(cx + 8, cy - 2),
            new Point(cx + 2, cy - 3)
        ];
        g.FillPolygon(brush, pts);
    }

    private void DrawClearIcon(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color glyphColor = clearTerminalButton.Enabled ? ThemeManager.Colors.TextPrimary : ThemeManager.Colors.TextMuted;
        using var pen = new Pen(glyphColor, 1.6f);

        int cx = clearTerminalButton.Width / 2;
        int cy = clearTerminalButton.Height / 2;

        // Trash lid
        g.DrawLine(pen, cx - 7, cy - 4, cx + 7, cy - 4);
        g.DrawLine(pen, cx - 3, cy - 6, cx + 3, cy - 6);

        // Trash bin body
        Point[] pts =
        [
            new Point(cx - 5, cy - 3),
            new Point(cx - 4, cy + 7),
            new Point(cx + 4, cy + 7),
            new Point(cx + 5, cy - 3)
        ];
        g.DrawLines(pen, pts);

        // Slats inside bin
        g.DrawLine(pen, cx - 2, cy - 1, cx - 2, cy + 5);
        g.DrawLine(pen, cx + 2, cy - 1, cx + 2, cy + 5);
    }

    private void DrawBrowserIcon(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color glyphColor = openBrowserButton.Enabled ? ThemeManager.Colors.TextPrimary : ThemeManager.Colors.TextMuted;
        using var pen = new Pen(glyphColor, 1.6f);

        int cx = openBrowserButton.Width / 2;
        int cy = openBrowserButton.Height / 2;
        int r = 7;

        // Globe circle
        g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        // Equator
        g.DrawLine(pen, cx - r, cy, cx + r, cy);
        // Meridian ellipse
        g.DrawEllipse(pen, cx - (r / 2), cy - r, r, 2 * r);
    }

    private void DrawManageClisIcon(object? sender, PaintEventArgs e)
    {
        if (sender is not Button btn) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color glyphColor = btn.Enabled ? ThemeManager.Colors.TextPrimary : ThemeManager.Colors.TextMuted;
        using var pen = new Pen(glyphColor, 1.6f);

        int cx = btn.Width / 2;
        int cy = btn.Height / 2;

        // Settings / Slider icon: 2 parallel slider tracks with knobs
        g.DrawLine(pen, cx - 6, cy - 3, cx + 6, cy - 3);
        g.DrawLine(pen, cx - 6, cy + 4, cx + 6, cy + 4);

        using var brush = new SolidBrush(glyphColor);
        g.FillRectangle(brush, cx - 3, cy - 5, 3, 5);
        g.FillRectangle(brush, cx + 1, cy + 2, 3, 5);
    }

    private void DrawToggleMainWindowIcon(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var main = GetMainForm();
        bool isMainVisible = main != null && !main.IsDisposed && main.Visible && main.WindowState != FormWindowState.Minimized;

        Color glyphColor = !isMainVisible
            ? ThemeManager.Colors.AccentGreen
            : (toggleMainWindowButton.Enabled ? ThemeManager.Colors.TextPrimary : ThemeManager.Colors.TextMuted);

        using var pen = new Pen(glyphColor, 1.6f);

        int cx = toggleMainWindowButton.Width / 2;
        int cy = toggleMainWindowButton.Height / 2;

        if (isMainVisible)
        {
            // Main window is currently visible: Draw window frame + downward arrow into tray line
            g.DrawRectangle(pen, cx - 7, cy - 8, 14, 10);
            g.DrawLine(pen, cx - 7, cy - 5, cx + 7, cy - 5);

            // Tray base line
            g.DrawLine(pen, cx - 7, cy + 7, cx + 7, cy + 7);

            // Downward arrow into tray
            g.DrawLine(pen, cx, cy - 2, cx, cy + 4);
            Point[] pts =
            [
                new Point(cx - 3, cy + 2),
                new Point(cx, cy + 5),
                new Point(cx + 3, cy + 2)
            ];
            g.DrawLines(pen, pts);
        }
        else
        {
            // Main window is hidden in tray: Draw tray line + restored window frame with upward arrow
            g.DrawLine(pen, cx - 7, cy + 7, cx + 7, cy + 7);

            g.DrawRectangle(pen, cx - 7, cy - 8, 14, 10);
            g.DrawLine(pen, cx - 7, cy - 5, cx + 7, cy - 5);

            // Upward arrow to restore window
            g.DrawLine(pen, cx, cy + 4, cx, cy - 2);
            Point[] pts =
            [
                new Point(cx - 3, cy),
                new Point(cx, cy - 3),
                new Point(cx + 3, cy)
            ];
            g.DrawLines(pen, pts);
        }
    }

    public void ToggleMainWindowVisibility()
    {
        var main = GetMainForm();
        if (main == null || main.IsDisposed) return;

        if (main.Visible && main.WindowState != FormWindowState.Minimized)
        {
            HideMainFormNow();
        }
        else
        {
            RestoreMainFormNow();
        }
    }

    public void HideMainFormNow()
    {
        var main = GetMainForm();
        if (main == null || main.IsDisposed) return;

        _didWeHideMainForm = true;
        main.Hide();
        UpdateToggleMainWindowButtonState();
    }

    public void RestoreMainFormNow()
    {
        var main = GetMainForm();
        if (main == null || main.IsDisposed) return;

        _didWeHideMainForm = false;
        if (main is MainForm mf)
        {
            mf.ShowAndActivate();
        }
        else
        {
            main.Show();
            if (main.WindowState == FormWindowState.Minimized)
            {
                main.WindowState = FormWindowState.Normal;
            }
            main.BringToFront();
            main.Activate();
        }
        UpdateToggleMainWindowButtonState();
    }

    private void UpdateToggleMainWindowButtonState()
    {
        if (toggleMainWindowButton == null || toggleMainWindowButton.IsDisposed) return;

        var main = GetMainForm();
        bool isMainVisible = main != null && !main.IsDisposed && main.Visible && main.WindowState != FormWindowState.Minimized;

        var c = ThemeManager.Colors;
        if (isMainVisible)
        {
            _actionToolTip.SetToolTip(toggleMainWindowButton, "Hide Main Hub to Tray (Show only this Terminal window)\nRight-click for options");
            toggleMainWindowButton.BackColor = c.ButtonBg;
            toggleMainWindowButton.FlatAppearance.BorderColor = c.ButtonBorder;
        }
        else
        {
            _actionToolTip.SetToolTip(toggleMainWindowButton, "Main Hub is in Tray (Click to Restore Dashboard)\nRight-click for options");
            toggleMainWindowButton.BackColor = c.WellBackground;
            toggleMainWindowButton.FlatAppearance.BorderColor = c.AccentGreen;
        }

        toggleMainWindowButton.Invalidate();
    }

    private void ShowMainWindowContextMenu()
    {
        _mainWindowContextMenu?.Dispose();
        _mainWindowContextMenu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new ShellColorTable())
        };

        var c = ThemeManager.Colors;
        _mainWindowContextMenu.BackColor = c.CardBackground;
        _mainWindowContextMenu.ForeColor = c.TextPrimary;

        var main = GetMainForm();
        bool isMainVisible = main != null && !main.IsDisposed && main.Visible && main.WindowState != FormWindowState.Minimized;

        var toggleItem = new ToolStripMenuItem(
            isMainVisible ? "Hide Main Hub to Tray" : "Show Main Hub Window",
            null,
            (_, _) => ToggleMainWindowVisibility()
        )
        {
            ForeColor = c.TextPrimary,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        bool autoHide = StartupHelper.GetPreference("HideMainOnCliOpen", false);
        var autoHideItem = new ToolStripMenuItem(
            "Auto-hide Main Hub when Opening Terminal",
            null,
            (_, _) =>
            {
                bool next = !StartupHelper.GetPreference("HideMainOnCliOpen", false);
                StartupHelper.SetPreference("HideMainOnCliOpen", next);
                if (next && isMainVisible)
                {
                    HideMainFormNow();
                }
            }
        )
        {
            Checked = autoHide,
            CheckOnClick = true,
            ForeColor = c.TextPrimary
        };

        _mainWindowContextMenu.Items.Add(toggleItem);
        _mainWindowContextMenu.Items.Add(new ToolStripSeparator());
        _mainWindowContextMenu.Items.Add(autoHideItem);

        _mainWindowContextMenu.Show(toggleMainWindowButton, new Point(0, toggleMainWindowButton.Height + 2));
    }

    public void SetMainForm(Form? mainForm)
    {
        if (_mainForm == mainForm && _mainForm != null) return;
        if (_mainForm != null)
        {
            _mainForm.VisibleChanged -= MainForm_VisibleChanged;
        }
        _mainForm = mainForm;
        if (_mainForm != null)
        {
            _mainForm.VisibleChanged += MainForm_VisibleChanged;
        }
        UpdateToggleMainWindowButtonState();
    }

    private Form? GetMainForm()
    {
        if (_mainForm != null && !_mainForm.IsDisposed) return _mainForm;
        var found = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (found != null && _mainForm != found)
        {
            SetMainForm(found);
        }
        return found;
    }

    private void MainForm_VisibleChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(UpdateToggleMainWindowButtonState)); } catch { }
            return;
        }
        UpdateToggleMainWindowButtonState();
    }

    private void DrawToggleSplitIcon(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color glyphColor = _isSplitActive
            ? ThemeManager.Colors.AccentGreen
            : (toggleSplitButton.Enabled ? ThemeManager.Colors.TextPrimary : ThemeManager.Colors.TextMuted);

        using var pen = new Pen(glyphColor, 1.6f);

        int cx = toggleSplitButton.Width / 2;
        int cy = toggleSplitButton.Height / 2;

        // Outer window frame
        g.DrawRectangle(pen, cx - 7, cy - 6, 14, 12);

        // Vertical split line
        g.DrawLine(pen, cx, cy - 6, cx, cy + 6);

        if (_isSplitActive)
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(50, ThemeManager.Colors.AccentGreen));
            g.FillRectangle(fillBrush, cx + 1, cy - 5, 5, 10);
        }
    }

    private void UpdateToggleSplitButtonState()
    {
        if (toggleSplitButton == null || toggleSplitButton.IsDisposed) return;
        var c = ThemeManager.Colors;

        if (_isSplitActive)
        {
            _actionToolTip.SetToolTip(toggleSplitButton, "Close Split Workspace (Return to Single Pane)");
            toggleSplitButton.BackColor = c.WellBackground;
            toggleSplitButton.FlatAppearance.BorderColor = c.AccentGreen;
        }
        else
        {
            _actionToolTip.SetToolTip(toggleSplitButton, "Split Workspace (Dual Side-by-Side Sessions)");
            toggleSplitButton.BackColor = c.ButtonBg;
            toggleSplitButton.FlatAppearance.BorderColor = c.ButtonBorder;
        }
        toggleSplitButton.Invalidate();
    }

    public void ToggleSplitMode(bool? forceState = null)
    {
        bool nextState = forceState ?? !_isSplitActive;
        if (_isSplitActive == nextState) return;

        _isSplitActive = nextState;

        if (_isSplitActive)
        {
            if (string.IsNullOrEmpty(_secondaryTabId))
            {
                var workspaces = AiConfigService.Instance.GetCliWorkspaces();
                var candidate = workspaces.FirstOrDefault(w => w.IsEnabled && !string.Equals(AiConfigService.NormalizeCliId(w.Id), _primaryTabId, StringComparison.OrdinalIgnoreCase));
                if (candidate != null)
                {
                    _secondaryTabId = AiConfigService.NormalizeCliId(candidate.Id);
                }
                else
                {
                    _secondaryTabId = ResolveDefaultSecondaryTab(_primaryTabId);
                }
            }

            if (splitWorkspaceContainer.Width > 400)
            {
                try
                {
                    splitWorkspaceContainer.SplitterDistance = splitWorkspaceContainer.Width / 2;
                }
                catch { }
            }
        }
        else
        {
            _focusedPane = WorkspacePane.Primary;
        }

        ApplyPaneLayout();
        UpdatePaneCombosItems();
    }

    public void SplitWithTab(string tabId)
    {
        _secondaryTabId = tabId;
        _isSplitActive = true;
        _focusedPane = WorkspacePane.Secondary;

        if (splitWorkspaceContainer.Width > 400)
        {
            try
            {
                splitWorkspaceContainer.SplitterDistance = splitWorkspaceContainer.Width / 2;
            }
            catch { }
        }

        ApplyPaneLayout();
        UpdatePaneCombosItems();
    }

    public void SetFocusedPane(WorkspacePane pane)
    {
        if (_focusedPane == pane && _isSplitActive) return;
        _focusedPane = pane;

        UpdatePaneHeaderUi();
        UpdateTabButtonsUi();
        UpdateToolbarUi();

        if (IsAntigravityTab(ActivePaneTabId))
        {
            RefreshProfileCombo();
        }
        else if (string.Equals(ActivePaneTabId, "claude_code", StringComparison.OrdinalIgnoreCase))
        {
            UpdateClaudeControlsUi();
        }

        UpdateSessionConfigButton();
        LayoutToolbar();

        var focusedWv = GetWebViewForPane(ActivePaneTabId, _focusedPane);
        focusedWv?.Focus();
    }

    private void UpdatePaneHeaderUi()
    {
        if (!_isSplitActive) return;
        var c = ThemeManager.Colors;

        bool isLeftFocused = _focusedPane == WorkspacePane.Primary;
        paneTitleLeft.Text = isLeftFocused ? "● Left (Active):" : "Left:";
        paneHeaderLeft.BackColor = isLeftFocused ? c.WellBackground : c.HeaderBackground;
        paneTitleLeft.ForeColor = isLeftFocused ? c.AccentGreen : c.TextSecondary;

        bool isRightFocused = _focusedPane == WorkspacePane.Secondary;
        paneTitleRight.Text = isRightFocused ? "● Right (Active):" : "Right:";
        paneHeaderRight.BackColor = isRightFocused ? c.WellBackground : c.HeaderBackground;
        paneTitleRight.ForeColor = isRightFocused ? c.AccentGreen : c.TextSecondary;

        UpdatePaneCombosSelection();
    }

    private void UpdatePaneCombosItems()
    {
        if (paneComboLeft == null || paneComboRight == null || _cliManager == null) return;

        _isUpdatingPaneCombos = true;
        try
        {
            var items = new List<PaneTabItem>();

            var workspaces = AiConfigService.Instance.GetCliWorkspaces();
            var enabledWorkspaces = workspaces.Where(w => w.IsEnabled).ToList();

            foreach (var ws in enabledWorkspaces)
            {
                string normId = AiConfigService.NormalizeCliId(ws.Id);
                if (string.Equals(normId, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
                {
                    var allProfiles = _cliManager.ProfileService.GetProfiles();
                    if (allProfiles.Count == 0) allProfiles = ["Default"];
                    foreach (var prof in allProfiles)
                    {
                        string profId = CliManagerService.FormatAntigravityProfileId(prof);
                        items.Add(new PaneTabItem(profId, $"agy: {prof}"));
                    }
                }
                else
                {
                    items.Add(new PaneTabItem(normId, ws.Name));
                }
            }

            paneComboLeft.Items.Clear();
            paneComboRight.Items.Clear();

            foreach (var it in items)
            {
                paneComboLeft.Items.Add(it);
                paneComboRight.Items.Add(it);
            }

            UpdatePaneCombosSelection();
        }
        finally
        {
            _isUpdatingPaneCombos = false;
        }
    }

    private void UpdatePaneCombosSelection()
    {
        if (paneComboLeft == null || paneComboRight == null) return;
        _isUpdatingPaneCombos = true;
        try
        {
            SelectComboItem(paneComboLeft, _primaryTabId);
            SelectComboItem(paneComboRight, _secondaryTabId);
        }
        finally
        {
            _isUpdatingPaneCombos = false;
        }
    }

    private static void SelectComboItem(ComboBox combo, string targetId)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is PaneTabItem it && string.Equals(it.Id, targetId, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0 && combo.SelectedIndex < 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void EnsureTabWebViewAsync(string tabId, WorkspacePane pane)
    {
        if (string.Equals(tabId, "hub", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tabId, "claude_code", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (CliManagerService.IsAntigravityProfileId(tabId, out _))
        {
            _ = EnsureProfileWebViewAsync(tabId);
        }
        else if (!IsBuiltInCli(tabId))
        {
            var ws = AiConfigService.Instance.GetCliWorkspaces().FirstOrDefault(w =>
                string.Equals(AiConfigService.NormalizeCliId(w.Id), tabId, StringComparison.OrdinalIgnoreCase));
            if (ws != null) _ = EnsureCustomWebViewAsync(ws);
        }

        if (pane == WorkspacePane.Secondary && string.Equals(_primaryTabId, _secondaryTabId, StringComparison.OrdinalIgnoreCase))
        {
            _ = EnsureCloneWebViewAsync(tabId);
        }
    }

    private async Task EnsureCloneWebViewAsync(string id)
    {
        if (_secondaryCloneWebViews.ContainsKey(id)) return;
        if (_webViewEnv == null)
        {
            await _envInitTcs.Task;
        }
        if (_webViewEnv == null) return;

        try
        {
            var wv = CreateHostedWebView();
            wv.Dock = DockStyle.Fill;
            paneHostRight.Controls.Add(wv);
            _secondaryCloneWebViews[id] = wv;

            await wv.EnsureCoreWebView2Async(_webViewEnv);
            ConfigureWebViewSettings(wv.CoreWebView2);
            wv.CoreWebView2.WebMessageReceived += (s, e) => TerminalWebView_WebMessageReceived(id, e);
            wv.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                NotifyWebViewsTheme();
                SendTerminalInit(id);
            };

            if (File.Exists(_terminalHtmlPath))
            {
                wv.CoreWebView2.Navigate(new Uri(_terminalHtmlPath).AbsoluteUri);
            }
            NotifyWebViewsTheme();
            ApplyPaneLayout();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CliWebviewForm] Clone WebView creation failed for '{id}': {ex.Message}");
        }
    }

    private Microsoft.Web.WebView2.WinForms.WebView2? GetWebViewForPane(string id, WorkspacePane pane)
    {
        bool isSameAsPrimary = _isSplitActive && string.Equals(_primaryTabId, _secondaryTabId, StringComparison.OrdinalIgnoreCase);
        if (pane == WorkspacePane.Secondary && isSameAsPrimary)
        {
            if (_secondaryCloneWebViews.TryGetValue(id, out var cloneWv))
            {
                return cloneWv;
            }
            _ = EnsureCloneWebViewAsync(id);
            return null;
        }

        return GetWebViewForId(id);
    }

    private void ApplyPaneLayout()
    {
        if (IsInDesignMode) return;

        EnsureTabWebViewAsync(_primaryTabId, WorkspacePane.Primary);
        if (_isSplitActive)
        {
            EnsureTabWebViewAsync(_secondaryTabId, WorkspacePane.Secondary);
        }

        var primaryWv = GetWebViewForPane(_primaryTabId, WorkspacePane.Primary);
        if (primaryWv != null)
        {
            if (primaryWv.Parent != paneHostLeft)
            {
                primaryWv.Parent = paneHostLeft;
                primaryWv.Dock = DockStyle.Fill;
            }
            primaryWv.Visible = true;
            primaryWv.BringToFront();
        }

        if (_isSplitActive)
        {
            UpdateSplitMinSizes();
            splitWorkspaceContainer.Panel2Collapsed = false;
            paneHeaderLeft.Visible = true;
            paneHeaderRight.Visible = true;
            paneHeaderLeft.BringToFront();
            paneHeaderRight.BringToFront();
            LayoutPaneHeaders();

            int availWidth = splitWorkspaceContainer.Width;
            int min1 = splitWorkspaceContainer.Panel1MinSize;
            int min2 = splitWorkspaceContainer.Panel2MinSize;
            if (availWidth > min1 + min2 && min1 >= 0 && min2 >= 0)
            {
                int curDist = splitWorkspaceContainer.SplitterDistance;
                int maxDist = availWidth - min2;
                if (curDist < min1 || curDist > maxDist || curDist < 180)
                {
                    int target = Math.Clamp(availWidth / 2, min1, maxDist);
                    try
                    {
                        splitWorkspaceContainer.SplitterDistance = target;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CliWebviewForm] SplitterDistance layout warning: {ex.Message}");
                    }
                }
            }

            var secondaryWv = GetWebViewForPane(_secondaryTabId, WorkspacePane.Secondary);
            if (secondaryWv != null)
            {
                if (secondaryWv.Parent != paneHostRight)
                {
                    secondaryWv.Parent = paneHostRight;
                    secondaryWv.Dock = DockStyle.Fill;
                }
                secondaryWv.Visible = true;
                secondaryWv.BringToFront();
            }

            UpdatePaneHeaderUi();
        }
        else
        {
            splitWorkspaceContainer.Panel2Collapsed = true;
            paneHeaderLeft.Visible = false;
            paneHeaderRight.Visible = false;
        }

        var activeSecWv = _isSplitActive ? GetWebViewForPane(_secondaryTabId, WorkspacePane.Secondary) : null;

        if (hubWebView != null && hubWebView != primaryWv && hubWebView != activeSecWv)
            hubWebView.Visible = false;
        if (claudeWebView != null && claudeWebView != primaryWv && claudeWebView != activeSecWv)
            claudeWebView.Visible = false;
        if (antigravityWebView != null && antigravityWebView != primaryWv && antigravityWebView != activeSecWv)
            antigravityWebView.Visible = false;

        foreach (var kvp in _customWebViews)
        {
            if (kvp.Value != primaryWv && kvp.Value != activeSecWv)
            {
                kvp.Value.Visible = false;
            }
        }

        foreach (var kvp in _secondaryCloneWebViews)
        {
            if (kvp.Value != primaryWv && kvp.Value != activeSecWv)
            {
                kvp.Value.Visible = false;
            }
        }

        UpdateTabButtonsUi();
        UpdateToolbarUi();
        UpdateToggleSplitButtonState();
        TriggerTerminalFit();
    }

    private void UpdateSplitMinSizes()
    {
        if (splitWorkspaceContainer == null || splitWorkspaceContainer.IsDisposed) return;
        int w = splitWorkspaceContainer.Width;
        if (w >= 560)
        {
            splitWorkspaceContainer.Panel1MinSize = 220;
            splitWorkspaceContainer.Panel2MinSize = 220;
        }
        else if (w >= 300)
        {
            splitWorkspaceContainer.Panel1MinSize = 80;
            splitWorkspaceContainer.Panel2MinSize = 80;
        }
        else
        {
            splitWorkspaceContainer.Panel1MinSize = 25;
            splitWorkspaceContainer.Panel2MinSize = 25;
        }
    }

    public void RebuildTabs()
    {
        tabsPanel.SuspendLayout();
        tabsPanel.Controls.Clear();
        _dynamicTabButtons.Clear();

        var workspaces = AiConfigService.Instance.GetCliWorkspaces();
        var enabledWorkspaces = workspaces.Where(w => w.IsEnabled).ToList();
        bool isEmptyWorkspace = enabledWorkspaces.Count == 0;

        int curX = 8;
        if (!isEmptyWorkspace)
        {
            foreach (var ws in enabledWorkspaces)
            {
                string normId = AiConfigService.NormalizeCliId(ws.Id);
                if (string.Equals(normId, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
                {
                    var allProfiles = _cliManager?.ProfileService.GetProfiles() ?? [];
                    var runningProfiles = _cliManager?.GetRunningAntigravityProfiles() ?? [];
                    var activeProfile = _cliManager?.ProfileService.ActiveProfile ?? "Default";

                    var profileTabsToShow = new List<string>();
                    foreach (var prof in runningProfiles)
                    {
                        if (!profileTabsToShow.Contains(prof, StringComparer.OrdinalIgnoreCase))
                        {
                            profileTabsToShow.Add(prof);
                        }
                    }

                    if (!string.IsNullOrEmpty(activeProfile) && !profileTabsToShow.Contains(activeProfile, StringComparer.OrdinalIgnoreCase))
                    {
                        profileTabsToShow.Add(activeProfile);
                    }

                    foreach (var prof in allProfiles)
                    {
                        if (profileTabsToShow.Count >= 5) break;
                        if (!profileTabsToShow.Contains(prof, StringComparer.OrdinalIgnoreCase))
                        {
                            profileTabsToShow.Add(prof);
                        }
                    }

                    if (profileTabsToShow.Count == 0)
                    {
                        profileTabsToShow.Add("Default");
                    }

                    foreach (var profName in profileTabsToShow)
                    {
                        string profTabId = CliManagerService.FormatAntigravityProfileId(profName);
                        string displayName = $"agy: {profName}";
                        AddTabButton(profTabId, displayName, ref curX);
                    }
                    continue;
                }

                AddTabButton(normId, ws.Name, ref curX);
            }
        }

        _manageClisButton = new Button
        {
            Size = new Size(32, 34),
            MinimumSize = new Size(32, 34),
            MaximumSize = new Size(32, 34),
            Location = new Point(curX, 6),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Text = "",
            Margin = new Padding(2, 0, 4, 0),
            Padding = Padding.Empty
        };
        _manageClisButton.FlatAppearance.BorderSize = 1;
        _manageClisButton.Paint += DrawManageClisIcon;
        _manageClisButton.Click += (_, _) => OpenManageClisDialog();
        SetupButtonHover(_manageClisButton);
        _actionToolTip.SetToolTip(_manageClisButton, "Manage CLI Workspaces (Show/Hide, Add/Remove)");

        curX += _manageClisButton.Width + 8;
        tabsPanel.Controls.Add(_manageClisButton);

        tabsPanel.Tag = curX;
        tabsPanel.AutoScroll = true;
        tabsPanel.Width = curX;
        tabsPanel.ResumeLayout();

        bool activeTabChanged = false;
        if (isEmptyWorkspace)
        {
            if (!string.IsNullOrEmpty(ActivePaneTabId))
            {
                ActivePaneTabId = string.Empty;
                activeTabChanged = true;
            }
        }
        else if (!_dynamicTabButtons.Any(b => string.Equals(b.Tag as string, ActivePaneTabId, StringComparison.OrdinalIgnoreCase)))
        {
            if (_dynamicTabButtons.Count > 0 && _dynamicTabButtons[0].Tag is string firstId)
            {
                ActivePaneTabId = firstId;
                activeTabChanged = true;
            }
        }

        SetTerminalContentVisible(!isEmptyWorkspace);
        ShowEmptyState(isEmptyWorkspace, workspaces);

        if (activeTabChanged)
        {
            ApplyPaneLayout();
        }

        UpdateTabButtonsUi();
        UpdateToolbarUi();
        UpdateSessionConfigButton();
        LayoutToolbar();
        UpdatePaneCombosItems();
    }

    private void AddTabButton(string tabId, string displayName, ref int curX)
    {
        var btn = new Button
        {
            Tag = tabId,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.75F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = Padding.Empty,
            AutoEllipsis = true
        };
        btn.FlatAppearance.BorderSize = 1;

        int measuredWidth = TextRenderer.MeasureText($"● {displayName}", btn.Font).Width + 20;
        int targetWidth;
        if (displayName.StartsWith("agy: ", StringComparison.OrdinalIgnoreCase))
        {
            targetWidth = Math.Clamp(measuredWidth, 70, 110);
        }
        else
        {
            targetWidth = Math.Clamp(measuredWidth, 75, 130);
        }

        btn.Size = new Size(targetWidth, 34);
        btn.Location = new Point(curX, 6);
        curX += targetWidth + 4;

        btn.Click += (_, _) => SwitchTab(tabId);
        SetupTabHover(btn, tabId);

        btn.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowTabContextMenu(btn, tabId, btn.PointToScreen(e.Location));
            }
        };

        _dynamicTabButtons.Add(btn);
        tabsPanel.Controls.Add(btn);
    }

    private void ShowTabContextMenu(Button tabBtn, string tabId, Point screenLocation)
    {
        var c = ThemeManager.Colors;

        _tabContextMenu?.Dispose();
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new ShellColorTable()),
            BackColor = c.CardBackground,
            ForeColor = c.TextPrimary
        };
        _tabContextMenu = menu;

        bool isAgyProfile = CliManagerService.IsAntigravityProfileId(tabId, out var profileName);
        bool isBuiltIn = IsBuiltInCli(tabId);

        string displayName = isAgyProfile
            ? $"agy: {profileName}"
            : (tabBtn.Text ?? string.Empty).TrimStart('●', '○', '◐', '▲', ' ').Trim();

        menu.Items.Add(new ToolStripMenuItem(displayName)
        {
            Enabled = false,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Open in External CMD (Real Terminal)", null, (_, _) =>
        {
            LaunchExternalCmdForTab(tabId);
        }));

        menu.Items.Add(new ToolStripSeparator());

        bool isHub = string.Equals(tabId, "hub", StringComparison.OrdinalIgnoreCase);
        if (!isHub)
        {
            string? curWorkDir = isAgyProfile
                ? _cliManager?.ProfileService.GetProfileWorkingDirectory(profileName)
                : AiConfigService.Instance.GetWorkspaceWorkingDirectory(tabId);

            bool curResume = isAgyProfile
                ? (_cliManager?.ProfileService.GetProfileAutoResume(profileName) ?? true)
                : AiConfigService.Instance.GetWorkspaceAutoResume(tabId);

            string dirSummary = (!string.IsNullOrWhiteSpace(curWorkDir) && Directory.Exists(curWorkDir))
                ? Path.GetFileName(curWorkDir.TrimEnd('\\', '/'))
                : "(Default VirtualHome)";

            menu.Items.Add(new ToolStripMenuItem($"Project Folder: {dirSummary}", null, async (_, _) =>
            {
                SwitchTab(tabId);
                await HandleSetProjectDirClickAsync();
            }));

            string resumeText = curResume
                ? "Resume Previous Session: ON (--continue)"
                : "Resume Previous Session: OFF (--continue)";
            var resumeMenuItem = new ToolStripMenuItem(resumeText, null, (_, _) =>
            {
                SwitchTab(tabId);
                HandleToggleResumeClick();
            })
            {
                Checked = curResume,
                ForeColor = curResume ? c.AccentIndigo : c.TextMuted
            };
            menu.Items.Add(resumeMenuItem);

            bool curSkipPerms = isAgyProfile
                ? (_cliManager?.ProfileService.GetProfileSkipPermissions(profileName) ?? false)
                : AiConfigService.Instance.GetWorkspaceSkipPermissions(tabId);

            string permsText = curSkipPerms
                ? "Auto-Approve Tool Permissions: ON (--dangerously-skip-permissions)"
                : "Auto-Approve Tool Permissions: OFF (--dangerously-skip-permissions)";
            var permsMenuItem = new ToolStripMenuItem(permsText, null, (_, _) =>
            {
                SwitchTab(tabId);
                HandleToggleSkipPermissionsClick();
            })
            {
                Checked = curSkipPerms,
                ForeColor = curSkipPerms ? c.WarningAmber : c.TextMuted
            };
            menu.Items.Add(permsMenuItem);

            if (!string.IsNullOrWhiteSpace(curWorkDir))
            {
                menu.Items.Add(new ToolStripMenuItem("Reset Project Folder to Default", null, async (_, _) =>
                {
                    if (isAgyProfile && _cliManager != null)
                    {
                        _cliManager.ProfileService.SetProfileWorkingDirectory(profileName, null);
                    }
                    else
                    {
                        AiConfigService.Instance.SetWorkspaceWorkingDirectory(tabId, string.Empty);
                    }
                    UpdateSessionConfigButton();
                    LayoutToolbar();

                    var info = _cliManager?.GetInfo(tabId);
                    if (info != null && (info.State == CliState.Running || info.State == CliState.Starting))
                    {
                        if (MessageBox.Show(this, $"Reset folder to default. Restart {info.ShortName} now?", "Restart CLI", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes && _cliManager != null)
                        {
                            await _cliManager.RestartAsync(tabId);
                        }
                    }
                }));
            }

            menu.Items.Add(new ToolStripSeparator());
        }

        if (!_isSplitActive)
        {
            menu.Items.Add(new ToolStripMenuItem("Split with this in Right Pane", null, (_, _) =>
            {
                SplitWithTab(tabId);
            }));
        }
        else
        {
            menu.Items.Add(new ToolStripMenuItem("Open in Left Pane (Pane 1)", null, (_, _) =>
            {
                SetFocusedPane(WorkspacePane.Primary);
                SwitchTab(tabId);
            }));
            menu.Items.Add(new ToolStripMenuItem("Open in Right Pane (Pane 2)", null, (_, _) =>
            {
                SetFocusedPane(WorkspacePane.Secondary);
                SwitchTab(tabId);
            }));
            menu.Items.Add(new ToolStripMenuItem("Close Split (Single Pane)", null, (_, _) =>
            {
                ToggleSplitMode(false);
            }));
        }

        menu.Items.Add(new ToolStripSeparator());

        // Built-in CLIs have fixed ids, so only profiles and custom workspaces can be renamed.
        menu.Items.Add(new ToolStripMenuItem("Rename", null, (_, _) =>
        {
            HandleRenameTab(tabId);
        })
        {
            Enabled = isAgyProfile || !isBuiltIn
        });

        menu.Items.Add(new ToolStripMenuItem("Remove / Close Tab", null, async (_, _) =>
        {
            await HandleDeleteTabAsync(tabId);
        })
        {
            ForeColor = c.DangerRed
        });

        menu.Show(screenLocation);
    }

    /// <summary>
    /// Opens a real, interactive Windows console running the tab's CLI, with the same
    /// environment the embedded session would have been given.
    /// </summary>
    private void LaunchExternalCmdForTab(string tabId)
    {
        if (_cliManager == null) return;

        ExternalLaunchSpec? spec;
        try
        {
            spec = _cliManager.BuildExternalLaunchSpec(tabId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not prepare the external command:\n{ex.Message}", "Open in External CMD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (spec == null || string.IsNullOrWhiteSpace(spec.Command))
        {
            MessageBox.Show(this, "This tab has no command configured.", "Open in External CMD", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string workDir = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : spec.WorkingDirectory;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k {spec.Command}",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            foreach (var kvp in spec.EnvironmentVariables)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }

            // An inherited ANTHROPIC_API_KEY would shadow the injected token.
            if (psi.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"))
            {
                psi.Environment.Remove("ANTHROPIC_API_KEY");
            }

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open an external console:\n{ex.Message}", "Open in External CMD", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HandleRenameTab(string tabId)
    {
        if (_cliManager == null) return;

        if (CliManagerService.IsAntigravityProfileId(tabId, out var profileName))
        {
            string? newName = PromptForName("Rename Antigravity Profile", "New profile name:", profileName);
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, profileName, StringComparison.Ordinal)) return;

            if (!_cliManager.ProfileService.RenameProfile(profileName, newName))
            {
                MessageBox.Show(this, $"Could not rename \"{profileName}\". The name may already be in use.", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RebuildTabs();
            SwitchTab(CliManagerService.FormatAntigravityProfileId(newName));
            return;
        }

        if (IsBuiltInCli(tabId))
        {
            MessageBox.Show(this, "Built-in CLIs cannot be renamed.", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ws = AiConfigService.Instance.GetCliWorkspaces()
            .FirstOrDefault(w => string.Equals(AiConfigService.NormalizeCliId(w.Id), tabId, StringComparison.OrdinalIgnoreCase));
        if (ws == null) return;

        string? newWorkspaceName = PromptForName("Rename Workspace", "New workspace name:", ws.Name);
        if (string.IsNullOrWhiteSpace(newWorkspaceName) || string.Equals(newWorkspaceName, ws.Name, StringComparison.Ordinal)) return;

        ws.Name = newWorkspaceName;
        AiConfigService.Instance.SaveCliWorkspace(ws);
        RebuildTabs();
    }

    private async Task HandleDeleteTabAsync(string tabId)
    {
        if (_cliManager == null) return;

        // An Antigravity tab exists only because the profile exists, so closing it removes the profile.
        if (CliManagerService.IsAntigravityProfileId(tabId, out var profileName))
        {
            var removeProfile = MessageBox.Show(
                this,
                $"Remove the Antigravity profile \"{profileName}\"?\n\nIts saved credentials and virtual home will be deleted. This cannot be undone.",
                "Remove Profile",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (removeProfile != DialogResult.Yes) return;

            try { await _cliManager.StopAsync(tabId); } catch { }

            if (!_cliManager.ProfileService.DeleteProfile(profileName))
            {
                MessageBox.Show(this, $"Could not remove profile \"{profileName}\".", "Remove Profile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ActivePaneTabId = string.Empty;
            RebuildTabs();
            return;
        }

        // Workspace tab: stop it and hide it. Workspaces are re-added from Manage CLIs,
        // so closing a tab never deletes a user's configuration outright.
        string workspaceId = AiConfigService.NormalizeCliId(tabId);
        string label = _cliManager.GetInfo(tabId).ShortName;

        var closeTab = MessageBox.Show(
            this,
            $"Close the \"{label}\" tab?\n\nIt will be stopped and hidden from the workspace. Add it back any time from Manage CLIs.",
            "Remove / Close Tab",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (closeTab != DialogResult.Yes) return;

        try { await _cliManager.StopAsync(tabId); } catch { }

        var ws = AiConfigService.Instance.GetCliWorkspaces()
            .FirstOrDefault(w => string.Equals(AiConfigService.NormalizeCliId(w.Id), workspaceId, StringComparison.OrdinalIgnoreCase));
        if (ws != null)
        {
            AiConfigService.Instance.ToggleCliWorkspace(ws.Id, false);
        }

        ActivePaneTabId = string.Empty;
        RebuildTabs();
    }

    private string? PromptForName(string title, string label, string initialValue)
    {
        var c = ThemeManager.Colors;

        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(400, 152),
            BackColor = c.WindowBackground,
            ForeColor = c.TextPrimary
        };

        var promptLabel = new Label
        {
            Text = label,
            Bounds = new Rectangle(16, 16, 368, 20),
            ForeColor = c.TextSecondary
        };

        var input = new TextBox
        {
            Text = initialValue,
            Bounds = new Rectangle(16, 42, 368, 26),
            BackColor = c.WellBackground,
            ForeColor = c.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle
        };

        var okButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(216, 92, 80, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = c.AccentGreen,
            ForeColor = c.AccentGreenText,
            Cursor = Cursors.Hand
        };
        okButton.FlatAppearance.BorderColor = c.AccentGreen;

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(304, 92, 80, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = c.ButtonBg,
            ForeColor = c.ButtonText,
            Cursor = Cursors.Hand
        };
        cancelButton.FlatAppearance.BorderColor = c.ButtonBorder;

        dialog.Controls.Add(promptLabel);
        dialog.Controls.Add(input);
        dialog.Controls.Add(okButton);
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        dialog.Shown += (_, _) =>
        {
            ThemeManager.ApplyDwmTitleBarTheme(dialog.Handle);
            input.SelectAll();
            input.Focus();
        };

        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null;
    }

    private void OpenManageClisDialog()
    {
        using var form = new ManageClisForm();
        form.ConfigurationChanged += RebuildTabs;
        form.ShowDialog(this);
        RebuildTabs();
    }

    /// <summary>
    /// Shows or hides every hosted terminal WebView. Passing false leaves the content
    /// panel to the empty-state card.
    /// </summary>
    private void SetTerminalContentVisible(bool visible)
    {
        if (hubWebView != null) hubWebView.Visible = visible && string.Equals(ActivePaneTabId, "hub", StringComparison.OrdinalIgnoreCase);
        if (claudeWebView != null) claudeWebView.Visible = visible && string.Equals(ActivePaneTabId, "claude_code", StringComparison.OrdinalIgnoreCase);
        if (antigravityWebView != null) antigravityWebView.Visible = visible && string.Equals(ActivePaneTabId, "antigravity_cli", StringComparison.OrdinalIgnoreCase);

        foreach (var kvp in _customWebViews)
        {
            kvp.Value.Visible = visible && string.Equals(kvp.Key, ActivePaneTabId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ShowEmptyState(bool show, List<CliWorkspaceConfig> workspaces)
    {
        if (!show)
        {
            if (_emptyStatePanel != null) _emptyStatePanel.Visible = false;
            return;
        }

        EnsureEmptyStatePanel();
        if (_emptyStatePanel == null) return;

        PopulateEmptyStateCombo(workspaces);
        _emptyStatePanel.Visible = true;
        _emptyStatePanel.BringToFront();
        LayoutEmptyState();
    }

    private void EnsureEmptyStatePanel()
    {
        if (_emptyStatePanel != null) return;

        _emptyStatePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ThemeManager.Colors.WindowBackground,
            Visible = false
        };

        _emptyStateCard = new Panel
        {
            Size = new Size(520, 220),
            BackColor = ThemeManager.Colors.CardBackground
        };
        _emptyStateCard.Paint += (_, e) =>
        {
            if (_emptyStateCard == null) return;
            using var pen = new Pen(ThemeManager.Colors.CardBorder, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, _emptyStateCard.Width - 1, _emptyStateCard.Height - 1);
        };

        _emptyTitleLabel = new Label
        {
            Text = "No CLI Terminals Active",
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Bounds = new Rectangle(20, 26, 480, 28)
        };

        _emptySubtitleLabel = new Label
        {
            Text = "There are currently no background terminals configured in your workspace.",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Bounds = new Rectangle(20, 58, 480, 40)
        };

        _emptyAddCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F),
            Bounds = new Rectangle(20, 120, 340, 28)
        };
        _actionToolTip.SetToolTip(_emptyAddCombo, "Select a CLI workspace or shell to add");

        _emptyAddButton = new Button
        {
            Text = "+ Add to Workspace",
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Bounds = new Rectangle(368, 119, 132, 30)
        };
        _emptyAddButton.FlatAppearance.BorderSize = 1;
        _emptyAddButton.Click += EmptyStateAddButton_Click;

        _emptyHintLabel = new Label
        {
            Text = "The selection is saved to your workspace and restored on next launch.",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Bounds = new Rectangle(20, 164, 480, 20)
        };

        _emptyStateCard.Controls.Add(_emptyTitleLabel);
        _emptyStateCard.Controls.Add(_emptySubtitleLabel);
        _emptyStateCard.Controls.Add(_emptyAddCombo);
        _emptyStateCard.Controls.Add(_emptyAddButton);
        _emptyStateCard.Controls.Add(_emptyHintLabel);

        _emptyStatePanel.Controls.Add(_emptyStateCard);
        _emptyStatePanel.Resize += (_, _) => LayoutEmptyState();

        contentPanel.Controls.Add(_emptyStatePanel);

        ApplyEmptyStateTheme();
    }

    private void LayoutEmptyState()
    {
        if (_emptyStatePanel == null || _emptyStateCard == null) return;

        int x = Math.Max(0, (_emptyStatePanel.ClientSize.Width - _emptyStateCard.Width) / 2);
        int y = Math.Max(0, (_emptyStatePanel.ClientSize.Height - _emptyStateCard.Height) / 2);
        _emptyStateCard.Location = new Point(x, y);
    }

    private void PopulateEmptyStateCombo(List<CliWorkspaceConfig> workspaces)
    {
        if (_emptyAddCombo == null) return;

        var previousId = (_emptyAddCombo.SelectedItem as EmptyStateOption)?.Id;

        _emptyAddCombo.BeginUpdate();
        _emptyAddCombo.Items.Clear();

        foreach (var ws in workspaces)
        {
            var label = string.IsNullOrWhiteSpace(ws.Name) ? ws.Id : ws.Name;
            _emptyAddCombo.Items.Add(new EmptyStateOption(ws.Id, label, false));
        }

        _emptyAddCombo.Items.Add(new EmptyStateOption("shell:cmd", "Command Prompt (embedded)", true));
        _emptyAddCombo.Items.Add(new EmptyStateOption("shell:powershell", "PowerShell (embedded)", true));

        if (_emptyAddCombo.Items.Count > 0)
        {
            int restore = previousId == null ? -1 : FindEmptyStateOption(previousId);
            _emptyAddCombo.SelectedIndex = restore >= 0 ? restore : 0;
        }

        _emptyAddCombo.EndUpdate();
    }

    private int FindEmptyStateOption(string id)
    {
        if (_emptyAddCombo == null) return -1;

        for (int i = 0; i < _emptyAddCombo.Items.Count; i++)
        {
            if (_emptyAddCombo.Items[i] is EmptyStateOption opt &&
                string.Equals(opt.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private void EmptyStateAddButton_Click(object? sender, EventArgs e)
    {
        if (_emptyAddCombo?.SelectedItem is not EmptyStateOption option) return;

        if (option.IsShell)
        {
            if (string.Equals(option.Id, "shell:powershell", StringComparison.OrdinalIgnoreCase))
            {
                OpenEmbeddedShell("powershell", "PowerShell", "powershell.exe");
            }
            else
            {
                OpenEmbeddedShell("cmd", "Command Prompt", "cmd.exe");
            }
            return;
        }

        // Persist first, then rebuild so the new tab exists before we switch to it.
        AiConfigService.Instance.ToggleCliWorkspace(option.Id, true);
        RebuildTabs();

        string workspaceId = AiConfigService.NormalizeCliId(option.Id);
        string targetTabId = string.Equals(workspaceId, "antigravity_cli", StringComparison.OrdinalIgnoreCase)
            ? CliManagerService.FormatAntigravityProfileId(_cliManager?.ProfileService.ActiveProfile ?? "Default")
            : workspaceId;

        SwitchTab(targetTabId);

        try
        {
            _cliManager?.Start(targetTabId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CliWebviewForm] Failed to start '{targetTabId}' after adding it: {ex.Message}");
        }
    }

    private void ApplyEmptyStateTheme()
    {
        if (_emptyStatePanel == null) return;

        var c = ThemeManager.Colors;

        _emptyStatePanel.BackColor = c.WindowBackground;
        if (_emptyStateCard != null) _emptyStateCard.BackColor = c.CardBackground;
        if (_emptyTitleLabel != null) _emptyTitleLabel.ForeColor = c.TextPrimary;
        if (_emptySubtitleLabel != null) _emptySubtitleLabel.ForeColor = c.TextMuted;
        if (_emptyHintLabel != null) _emptyHintLabel.ForeColor = c.TextMuted;

        if (_emptyAddCombo != null)
        {
            _emptyAddCombo.BackColor = c.WellBackground;
            _emptyAddCombo.ForeColor = c.TextPrimary;
        }

        if (_emptyAddButton != null)
        {
            _emptyAddButton.BackColor = c.AccentGreen;
            _emptyAddButton.ForeColor = c.AccentGreenText;
            _emptyAddButton.FlatAppearance.BorderColor = c.AccentGreen;
            _emptyAddButton.FlatAppearance.MouseOverBackColor = c.AccentGreenHover;
        }

        _emptyStateCard?.Invalidate();
    }

    private sealed class EmptyStateOption(string id, string label, bool isShell)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public bool IsShell { get; } = isShell;

        public override string ToString() => Label;
    }

    private void LayoutToolbar()
    {
        if (_isLayoutRunning || toolsPanel == null || topToolbar == null) return;

        try
        {
            _isLayoutRunning = true;

            bool isAntigravityActive = IsAntigravityTab(ActivePaneTabId);
            bool isClaudeActive = string.Equals(ActivePaneTabId, "claude_code", StringComparison.OrdinalIgnoreCase);
            bool isClaudeOpenRouter = isClaudeActive && string.Equals(AiConfigService.Instance.GetClaudeEngineMode(), "OpenRouter", StringComparison.OrdinalIgnoreCase);
            bool isHub = string.Equals(ActivePaneTabId, "hub", StringComparison.OrdinalIgnoreCase);
            bool showCliTools = !string.IsNullOrEmpty(ActivePaneTabId) && !isHub;

            if (_sessionConfigButton != null) _sessionConfigButton.Visible = showCliTools;

            // The profile switcher only applies to Antigravity tabs; hide it elsewhere so the
            // toolbar does not crowd.
            if (_profileComboBox != null) _profileComboBox.Visible = isAntigravityActive;

            // 1. Compute Minimum Action Bar Width
            int actionControlsWidth = 10;
            if (toggleMainWindowButton.Visible) actionControlsWidth += toggleMainWindowButton.Width + 6;
            if (toggleSplitButton.Visible) actionControlsWidth += toggleSplitButton.Width + 6;
            if (openBrowserButton.Visible) actionControlsWidth += openBrowserButton.Width + 6;
            actionControlsWidth += clearTerminalButton.Width + 6;
            actionControlsWidth += restartCliButton.Width + 6;
            actionControlsWidth += toggleRunButton.Width + 6;
            if (_sessionConfigButton != null && _sessionConfigButton.Visible) actionControlsWidth += _sessionConfigButton.Width + 6;
            if (_shellLauncherButton != null) actionControlsWidth += _shellLauncherButton.Width + 10;

            if (isClaudeActive)
            {
                actionControlsWidth += 110 + 8; // _claudeEngineComboBox (min 110px)
                if (isClaudeOpenRouter)
                {
                    actionControlsWidth += 130 + 6; // _claudeModelComboBox (min 130px)
                }
            }
            else if (isAntigravityActive)
            {
                if (_addProfileButton != null) actionControlsWidth += 52 + 8;
                // Profile switcher reserves its minimum width so the tabs panel clamps correctly.
                if (_profileComboBox != null) actionControlsWidth += 120 + 6;
            }

            // 2. Clamp tabsPanel.Width
            int preferredTabsWidth = tabsPanel.Tag is int w ? w : tabsPanel.Width;
            int maxAllowedTabsWidth = Math.Max(120, topToolbar.ClientSize.Width - actionControlsWidth - 40);
            tabsPanel.Width = Math.Min(preferredTabsWidth, maxAllowedTabsWidth);
            tabsPanel.AutoScroll = true;

            // 3. Responsive Claude Dropdowns & Controls Layout in toolsPanel
            int right = toolsPanel.ClientSize.Width - 10;

            // Action buttons laid out from right to left
            if (toggleMainWindowButton.Visible)
            {
                toggleMainWindowButton.Location = new Point(Math.Max(0, right - toggleMainWindowButton.Width), 7);
                right = toggleMainWindowButton.Left - 6;
            }

            if (toggleSplitButton.Visible)
            {
                toggleSplitButton.Location = new Point(Math.Max(0, right - toggleSplitButton.Width), 7);
                right = toggleSplitButton.Left - 6;
            }

            if (openBrowserButton.Visible)
            {
                openBrowserButton.Location = new Point(Math.Max(0, right - openBrowserButton.Width), 7);
                right = openBrowserButton.Left - 6;
            }

            clearTerminalButton.Location = new Point(Math.Max(0, right - clearTerminalButton.Width), 7);
            right = clearTerminalButton.Left - 6;

            restartCliButton.Location = new Point(Math.Max(0, right - restartCliButton.Width), 7);
            right = restartCliButton.Left - 6;

            toggleRunButton.Location = new Point(Math.Max(0, right - toggleRunButton.Width), 7);
            right = toggleRunButton.Left - 6;

            if (_sessionConfigButton != null && _sessionConfigButton.Visible)
            {
                _sessionConfigButton.Location = new Point(Math.Max(0, right - _sessionConfigButton.Width), 7);
                right = _sessionConfigButton.Left - 6;
            }

            if (_shellLauncherButton != null)
            {
                _shellLauncherButton.Location = new Point(Math.Max(0, right - _shellLauncherButton.Width), 7);
                right = _shellLauncherButton.Left - 10;
            }

            // Antigravity-only toolbar controls: profile switcher + add-account button, laid out
            // right to left so the switcher sits immediately left of the add button.
            if (isAntigravityActive)
            {
                if (_addProfileButton != null)
                {
                    _addProfileButton.Visible = true;
                    _addProfileButton.Location = new Point(Math.Max(0, right - _addProfileButton.Width), 7);
                    right = _addProfileButton.Left - 8;
                }

                if (_profileComboBox != null)
                {
                    // Responsive: takes what is left, but never squashes below 120px or grows
                    // past 160px where it would push the action buttons off the bar.
                    int comboWidth = Math.Clamp(right - 12, 120, 160);
                    _profileComboBox.Width = comboWidth;
                    _profileComboBox.Location = new Point(Math.Max(0, right - comboWidth), 9);
                    right = _profileComboBox.Left - 8;
                }
            }
            else
            {
                if (_addProfileButton != null) _addProfileButton.Visible = false;
                if (_profileComboBox != null) _profileComboBox.Visible = false;
            }

            // Claude Engine & Model controls
            if (isClaudeActive && _claudeEngineComboBox != null)
            {
                _claudeEngineComboBox.Visible = true;
                if (isClaudeOpenRouter && _claudeModelComboBox != null)
                {
                    _claudeModelComboBox.Visible = true;

                    // Dynamically scale dropdown widths based on remaining room
                    int availableForClaude = Math.Max(0, right - 12);
                    int minRequired = 105 + 120 + 8; // 233px minimum sum

                    if (availableForClaude >= minRequired)
                    {
                        int extra = availableForClaude - minRequired;
                        _claudeEngineComboBox.Width = Math.Clamp(105 + (extra * 30 / 100), 105, 135);
                        _claudeModelComboBox.Width = Math.Clamp(120 + (extra * 70 / 100), 120, 190);
                    }
                    else
                    {
                        _claudeEngineComboBox.Width = 105;
                        _claudeModelComboBox.Width = Math.Max(95, availableForClaude - 105 - 8);
                    }

                    int modelX = Math.Max(0, right - _claudeModelComboBox.Width);
                    _claudeModelComboBox.Location = new Point(modelX, 9);
                    right = modelX - 6;

                    int engineX = Math.Max(0, right - _claudeEngineComboBox.Width);
                    _claudeEngineComboBox.Location = new Point(engineX, 9);
                    right = engineX - 8;
                }
                else
                {
                    if (_claudeModelComboBox != null) _claudeModelComboBox.Visible = false;
                    _claudeEngineComboBox.Width = Math.Clamp(Math.Min(135, right - 12), 105, 135);
                    int engineX = Math.Max(0, right - _claudeEngineComboBox.Width);
                    _claudeEngineComboBox.Location = new Point(engineX, 9);
                    right = engineX - 8;
                }
            }
            else
            {
                if (_claudeEngineComboBox != null) _claudeEngineComboBox.Visible = false;
                if (_claudeModelComboBox != null) _claudeModelComboBox.Visible = false;
            }

            // 4. Status label handling: hide when narrow (< 780px) or space constrained, ellipsis otherwise
            int leftMargin = 12;
            int availableForStatus = right - leftMargin - 6;
            if (topToolbar.ClientSize.Width >= 780 && availableForStatus >= 50)
            {
                activeStatusLabel.Visible = true;
                activeStatusLabel.Location = new Point(leftMargin, 14);
                activeStatusLabel.MaximumSize = new Size(availableForStatus, 24);
                activeStatusLabel.AutoEllipsis = true;
            }
            else
            {
                activeStatusLabel.Visible = false;
            }
        }
        finally
        {
            _isLayoutRunning = false;
        }
    }

    public void ApplyTheme()
    {
        if (IsInDesignMode) return;

        var c = ThemeManager.Colors;
        if (IsHandleCreated)
        {
            ThemeManager.ApplyDwmTitleBarTheme(Handle);
        }

        BackColor = c.WindowBackground;
        topToolbar.BackColor = c.HeaderBackground;
        tabsPanel.BackColor = c.HeaderBackground;
        toolsPanel.BackColor = c.HeaderBackground;
        contentPanel.BackColor = c.WindowBackground;
        contentPanel.Padding = new Padding(0, 10, 0, 0);

        ApplyEmptyStateTheme();

        activeStatusLabel.ForeColor = c.TextSecondary;

        restartCliButton.BackColor = c.ButtonBg;
        restartCliButton.ForeColor = c.ButtonText;
        restartCliButton.FlatAppearance.BorderColor = c.ButtonBorder;

        clearTerminalButton.BackColor = c.ButtonBg;
        clearTerminalButton.ForeColor = c.ButtonText;
        clearTerminalButton.FlatAppearance.BorderColor = c.ButtonBorder;

        openBrowserButton.BackColor = c.ButtonBg;
        openBrowserButton.ForeColor = c.ButtonText;
        openBrowserButton.FlatAppearance.BorderColor = c.ButtonBorder;

        toggleMainWindowButton.BackColor = c.ButtonBg;
        toggleMainWindowButton.ForeColor = c.ButtonText;
        toggleMainWindowButton.FlatAppearance.BorderColor = c.ButtonBorder;
        UpdateToggleMainWindowButtonState();

        toggleSplitButton.BackColor = c.ButtonBg;
        toggleSplitButton.ForeColor = c.ButtonText;
        toggleSplitButton.FlatAppearance.BorderColor = c.ButtonBorder;
        UpdateToggleSplitButtonState();

        splitWorkspaceContainer.BackColor = c.CardBorder;
        panelLeft.BackColor = c.WindowBackground;
        panelRight.BackColor = c.WindowBackground;
        paneHeaderLeft.BackColor = _focusedPane == WorkspacePane.Primary ? c.WellBackground : c.HeaderBackground;
        paneHeaderRight.BackColor = _focusedPane == WorkspacePane.Secondary ? c.WellBackground : c.HeaderBackground;
        paneTitleLeft.ForeColor = _focusedPane == WorkspacePane.Primary ? c.AccentGreen : c.TextSecondary;
        paneTitleRight.ForeColor = _focusedPane == WorkspacePane.Secondary ? c.AccentGreen : c.TextSecondary;
        paneComboLeft.BackColor = c.WellBackground;
        paneComboLeft.ForeColor = c.TextPrimary;
        paneComboRight.BackColor = c.WellBackground;
        paneComboRight.ForeColor = c.TextPrimary;
        paneCloseRightButton.ForeColor = c.TextMuted;
        paneCloseRightButton.BackColor = c.ButtonBg;
        paneCloseRightButton.FlatAppearance.BorderColor = c.ButtonBorder;
        paneHostLeft.BackColor = c.WindowBackground;
        paneHostRight.BackColor = c.WindowBackground;

        if (_manageClisButton != null)
        {
            _manageClisButton.BackColor = c.ButtonBg;
            _manageClisButton.ForeColor = c.ButtonText;
            _manageClisButton.FlatAppearance.BorderColor = c.ButtonBorder;
        }

        if (_shellLauncherButton != null)
        {
            _shellLauncherButton.BackColor = c.ButtonBg;
            _shellLauncherButton.ForeColor = c.ButtonText;
            _shellLauncherButton.FlatAppearance.BorderColor = c.ButtonBorder;
            _shellLauncherButton.Invalidate();
        }

        if (_profileComboBox != null)
        {
            _profileComboBox.BackColor = c.WellBackground;
            _profileComboBox.ForeColor = c.TextPrimary;
        }

        if (_addProfileButton != null)
        {
            _addProfileButton.BackColor = c.ButtonBg;
            _addProfileButton.ForeColor = c.AccentGreen;
            _addProfileButton.FlatAppearance.BorderColor = c.AccentGreen;
        }

        if (_claudeEngineComboBox != null)
        {
            _claudeEngineComboBox.BackColor = c.WellBackground;
            _claudeEngineComboBox.ForeColor = c.TextPrimary;
            _claudeEngineComboBox.Invalidate();
        }

        if (_claudeModelComboBox != null)
        {
            _claudeModelComboBox.BackColor = c.WellBackground;
            _claudeModelComboBox.ForeColor = c.TextPrimary;
            _claudeModelComboBox.Invalidate();
        }

        if (_sessionConfigButton != null)
        {
            _sessionConfigButton.BackColor = c.ButtonBg;
            _sessionConfigButton.ForeColor = c.ButtonText;
            _sessionConfigButton.FlatAppearance.BorderColor = c.ButtonBorder;
            _sessionConfigButton.Invalidate();
        }

        UpdateSessionConfigButton();

        UpdateTabButtonsUi();
        UpdateToolbarUi();
        topToolbar.Invalidate();
        toolsPanel.Invalidate();
        contentPanel.Invalidate();

        NotifyWebViewsTheme();
    }

    public void NotifyWebViewsTheme()
    {
        var payload = new
        {
            type = "theme_changed",
            theme = ThemeManager.CurrentTheme == AppTheme.Light ? "light" : "dark",
            accentColor = ThemeManager.AccentHex
        };
        var json = JsonSerializer.Serialize(payload);

        PostToWebView(hubWebView, json);
        PostToWebView(claudeWebView, json);
        PostToWebView(antigravityWebView, json);

        foreach (var kvp in _customWebViews)
        {
            PostToWebView(kvp.Value, json);
        }
    }

    private static void PostToWebView(Microsoft.Web.WebView2.WinForms.WebView2? wv, string json)
    {
        if (wv?.CoreWebView2 != null)
        {
            try
            {
                wv.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!IsInDesignMode)
        {
            ApplyTheme();
            TriggerTerminalFit();

            // Default is split tabs closed. Do not auto-open on launch.

            if (StartupHelper.GetPreference("HideMainOnCliOpen", false))
            {
                var main = GetMainForm();
                if (main != null && !main.IsDisposed && main.Visible)
                {
                    HideMainFormNow();
                }
            }
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (IsInDesignMode) return;

        if (Visible)
        {
            UpdateToggleMainWindowButtonState();
            if (StartupHelper.GetPreference("HideMainOnCliOpen", false))
            {
                var main = GetMainForm();
                if (main != null && !main.IsDisposed && main.Visible)
                {
                    HideMainFormNow();
                }
            }
        }
        else
        {
            if (_didWeHideMainForm)
            {
                RestoreMainFormNow();
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_didWeHideMainForm)
        {
            var main = GetMainForm();
            if (main != null && !main.IsDisposed && !main.Visible)
            {
                RestoreMainFormNow();
            }
        }

        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private void HookEvents()
    {
        toggleRunButton.Click += async (_, _) => await HandleToggleRunClickAsync();
        restartCliButton.Click += async (_, _) =>
        {
            if (_cliManager != null)
            {
                await _cliManager.RestartAsync(ActivePaneTabId);
            }
        };
        clearTerminalButton.Click += (_, _) => HandleClearTerminalClick();
        openBrowserButton.Click += (_, _) => HandleOpenBrowserClick();

        if (_profileComboBox != null)
        {
            _profileComboBox.SelectedIndexChanged += async (_, _) =>
            {
                if (_isUpdatingProfileCombo || _cliManager == null) return;
                if (_profileComboBox.SelectedItem is ProfileItem item)
                {
                    string targetTabId = CliManagerService.FormatAntigravityProfileId(item.ProfileName);
                    SwitchTab(targetTabId);

                    if (!string.Equals(item.ProfileName, _cliManager.ProfileService.ActiveProfile, StringComparison.OrdinalIgnoreCase))
                    {
                        _profileComboBox.Enabled = false;
                        if (_addProfileButton != null) _addProfileButton.Enabled = false;
                        try
                        {
                            await _cliManager.SwitchAntigravityProfileAsync(item.ProfileName);
                        }
                        finally
                        {
                            _profileComboBox.Enabled = true;
                            if (_addProfileButton != null) _addProfileButton.Enabled = true;
                            RefreshProfileCombo();
                            RebuildTabs();
                        }
                    }
                }
            };
        }

        if (_addProfileButton != null)
        {
            _addProfileButton.Click += async (_, _) =>
            {
                if (_cliManager == null) return;
                var existing = _cliManager.ProfileService.GetProfiles();
                string defaultName = $"Account_{existing.Count + 1}";
                string? name = PromptForProfileName(defaultName);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (existing.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(this, $"A profile named '{name}' already exists.", "Profile Exists", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    await _cliManager.CreateAndSwitchNewProfileAsync(name);
                    string targetTabId = CliManagerService.FormatAntigravityProfileId(name);
                    RebuildTabs();
                    SwitchTab(targetTabId);
                    RefreshProfileCombo();
                }
            };
        }

        if (_claudeEngineComboBox != null)
        {
            _claudeEngineComboBox.SelectedIndexChanged += async (_, _) =>
            {
                if (_isUpdatingClaudeControls) return;
                string selected = _claudeEngineComboBox.SelectedIndex == 1 ? "Official" : "OpenRouter";
                string current = AiConfigService.Instance.GetClaudeEngineMode();
                if (!string.Equals(selected, current, StringComparison.OrdinalIgnoreCase))
                {
                    AiConfigService.Instance.SetClaudeEngineMode(selected);
                    UpdateClaudeControlsUi();
                    UpdateSessionConfigButton();
                    LayoutToolbar();

                    if (_cliManager != null)
                    {
                        var info = _cliManager.GetInfo("claude_code");
                        if (info.State == CliState.Running)
                        {
                            var res = MessageBox.Show(this,
                                $"Claude engine changed to {selected}. Restart Claude Code session to apply changes?",
                                "Restart Claude Session",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question);
                            if (res == DialogResult.Yes)
                            {
                                await _cliManager.RestartAsync("claude_code");
                            }
                        }
                    }
                }
            };
        }

        if (_claudeModelComboBox != null)
        {
            _claudeModelComboBox.SelectedIndexChanged += async (_, _) =>
            {
                if (_isUpdatingClaudeControls) return;
                if (_claudeModelComboBox.SelectedItem is string selectedModel && !string.IsNullOrWhiteSpace(selectedModel))
                {
                    string current = AiConfigService.Instance.GetClaudeSelectedModel();
                    if (!string.Equals(selectedModel, current, StringComparison.OrdinalIgnoreCase))
                    {
                        AiConfigService.Instance.SetClaudeSelectedModel(selectedModel);
                        if (string.Equals(AiConfigService.Instance.GetClaudeEngineMode(), "OpenRouter", StringComparison.OrdinalIgnoreCase))
                        {
                            CliManagerService.SyncClaudeOpenRouterSettingsJson(
                                AiConfigService.Instance.GetOpenRouterApiKey(),
                                selectedModel,
                                AiConfigService.Instance.GetClaudeCustomGatewayUrl());
                        }

                        if (_cliManager != null)
                        {
                            var info = _cliManager.GetInfo("claude_code");
                            if (info.State == CliState.Running)
                            {
                                var res = MessageBox.Show(this,
                                    $"Claude model changed to {selectedModel}. Restart Claude Code session to apply changes?",
                                    "Restart Claude Session",
                                    MessageBoxButtons.YesNo,
                                    MessageBoxIcon.Question);
                                if (res == DialogResult.Yes)
                                {
                                    await _cliManager.RestartAsync("claude_code");
                                }
                            }
                        }
                    }
                }
            };
        }

        if (_cliManager != null)
        {
            _cliManager.ProfileService.ActiveProfileChanged += (_) =>
            {
                if (InvokeRequired) BeginInvoke(RefreshProfileCombo);
                else RefreshProfileCombo();
            };

            _cliManager.ProfileService.ProfileSaved += (_) =>
            {
                if (InvokeRequired) BeginInvoke(RefreshProfileCombo);
                else RefreshProfileCombo();
            };

            _cliManager.StatusChangedId += (id, state, msg) =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke(() => OnCliStatusChanged(id, state, msg));
                }
                else
                {
                    OnCliStatusChanged(id, state, msg);
                }
            };

            _cliManager.TerminalDataReceivedId += (id, text) =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke(() => ForwardTerminalData(id, text));
                }
                else
                {
                    ForwardTerminalData(id, text);
                }
            };
        }

        ThemeManager.ThemeChanged += () =>
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(ApplyTheme);
            else ApplyTheme();
        };

        RefreshProfileCombo();
    }

    public void SwitchTab(CliType type) => SwitchTab(CliTypeToId(type));

    public void SwitchTab(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            id = ResolveDefaultInitialTab(string.Empty);
            if (string.IsNullOrEmpty(id))
            {
                ActivePaneTabId = string.Empty;
                ApplyPaneLayout();
                UpdateTabButtonsUi();
                UpdateToolbarUi();
                return;
            }
        }

        if (string.Equals(id, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
        {
            var activeProf = _cliManager?.ProfileService.ActiveProfile ?? "Default";
            id = CliManagerService.FormatAntigravityProfileId(activeProf);
        }
        else if (!CliManagerService.IsAntigravityProfileId(id, out _))
        {
            id = AiConfigService.NormalizeCliId(id);
        }

        if (!IsTabEnabled(id))
        {
            if (IsTabEnabled(ActivePaneTabId))
            {
                return;
            }

            id = ResolveDefaultInitialTab(string.Empty);
            if (string.IsNullOrEmpty(id) || !IsTabEnabled(id))
            {
                ActivePaneTabId = string.Empty;
                ApplyPaneLayout();
                UpdateTabButtonsUi();
                UpdateToolbarUi();
                return;
            }
        }

        if (_isSplitActive)
        {
            if (_focusedPane == WorkspacePane.Secondary)
            {
                _secondaryTabId = id;
            }
            else
            {
                _primaryTabId = id;
            }
        }
        else
        {
            _primaryTabId = id;
        }

        ApplyPaneLayout();
        UpdatePaneCombosSelection();

        UpdateTabButtonsUi();
        UpdateToolbarUi();

        if (IsAntigravityTab(ActivePaneTabId))
        {
            RefreshProfileCombo();
        }
        else if (string.Equals(ActivePaneTabId, "claude_code", StringComparison.OrdinalIgnoreCase))
        {
            UpdateClaudeControlsUi();
        }

        UpdateSessionConfigButton();
        LayoutToolbar();

        if (string.Equals(ActivePaneTabId, "hub", StringComparison.OrdinalIgnoreCase) && _hubInitialized)
        {
            RefreshHubNavigation();
        }
        else if (CliManagerService.IsAntigravityProfileId(ActivePaneTabId, out var activeTabProf))
        {
            _ = EnsureProfileWebViewAsync(ActivePaneTabId);
            if (_cliManager != null && !string.IsNullOrWhiteSpace(activeTabProf) &&
                !string.Equals(activeTabProf, _cliManager.ProfileService.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            {
                _ = _cliManager.SwitchAntigravityProfileAsync(activeTabProf);
            }
        }
        else if (!IsBuiltInCli(ActivePaneTabId))
        {
            var ws = AiConfigService.Instance.GetCliWorkspaces().FirstOrDefault(w => string.Equals(AiConfigService.NormalizeCliId(w.Id), ActivePaneTabId, StringComparison.OrdinalIgnoreCase));
            if (ws != null)
            {
                _ = EnsureCustomWebViewAsync(ws);
            }
        }

        TriggerTerminalFit();

        if (_mainForm is MainForm mf && mf.HudTerminal is HudTerminalForm hud && hud.IsHudOpen)
        {
            hud.SwitchCli(ActivePaneTabId, syncToWebview: false);
        }

        ActiveTabChanged?.Invoke(ActivePaneTabId);
    }

    private async Task EnsureProfileWebViewAsync(string profileId)
    {
        if (_customWebViews.ContainsKey(profileId)) return;

        if (_webViewEnv == null)
        {
            await _envInitTcs.Task;
        }

        if (_webViewEnv == null) return;

        try
        {
            var wv = CreateHostedWebView();
            wv.Visible = string.Equals(ActivePaneTabId, profileId, StringComparison.OrdinalIgnoreCase);
            paneHostLeft.Controls.Add(wv);
            _customWebViews[profileId] = wv;

            await wv.EnsureCoreWebView2Async(_webViewEnv);
            ConfigureWebViewSettings(wv.CoreWebView2);
            wv.CoreWebView2.WebMessageReceived += (s, e) => TerminalWebView_WebMessageReceived(profileId, e);
            wv.CoreWebView2.NavigationCompleted += (_, _) => NotifyWebViewsTheme();

            if (File.Exists(_terminalHtmlPath))
            {
                wv.CoreWebView2.Navigate(new Uri(_terminalHtmlPath).AbsoluteUri);
            }

            NotifyWebViewsTheme();
            ApplyPaneLayout();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CliWebviewForm] Failed to create WebView for profile '{profileId}': {ex.Message}");
        }
    }

    public void OpenEmbeddedShell(string shellId, string name, string command)
    {
        if (InvokeRequired)
        {
            Invoke(() => OpenEmbeddedShell(shellId, name, command));
            return;
        }

        shellId = AiConfigService.NormalizeCliId(shellId);

        var cfg = AiConfigService.Instance.CurrentConfig;
        var existing = cfg.CliWorkspaces.FirstOrDefault(w => string.Equals(AiConfigService.NormalizeCliId(w.Id), shellId, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            cfg.CliWorkspaces.Add(new CliWorkspaceConfig
            {
                Id = shellId,
                Name = name,
                Command = command,
                IsWebUi = false,
                IsDefault = false,
                IsEnabled = true
            });
            AiConfigService.Instance.SaveConfig(cfg);
        }
        else if (!existing.IsEnabled)
        {
            existing.IsEnabled = true;
            AiConfigService.Instance.SaveConfig(cfg);
        }

        RebuildTabs();
        SwitchTab(shellId);

        if (_cliManager != null)
        {
            var info = _cliManager.GetInfo(shellId);
            if (info.State != CliState.Running && info.State != CliState.Starting)
            {
                _cliManager.Start(shellId);
            }
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        ApplyTheme();
        Show();
        BringToFront();
        Activate();
    }

    private async Task EnsureCustomWebViewAsync(CliWorkspaceConfig ws)
    {
        if (_customWebViews.ContainsKey(ws.Id)) return;

        if (_webViewEnv == null)
        {
            await _envInitTcs.Task;
        }

        if (_webViewEnv == null) return;

        try
        {
            var wv = CreateHostedWebView();
            wv.Visible = string.Equals(ActivePaneTabId, ws.Id, StringComparison.OrdinalIgnoreCase);
            paneHostLeft.Controls.Add(wv);
            _customWebViews[ws.Id] = wv;

            await wv.EnsureCoreWebView2Async(_webViewEnv);
            ConfigureWebViewSettings(wv.CoreWebView2);
            wv.CoreWebView2.NavigationCompleted += (_, _) => NotifyWebViewsTheme();

            if (ws.IsWebUi && ws.Port.HasValue)
            {
                var targetUrl = $"http://127.0.0.1:{ws.Port.Value}/";
                wv.CoreWebView2.Navigate(targetUrl);
            }
            else
            {
                wv.CoreWebView2.WebMessageReceived += (s, e) => TerminalWebView_WebMessageReceived(ws.Id, e);
                if (File.Exists(_terminalHtmlPath))
                {
                    wv.CoreWebView2.Navigate(new Uri(_terminalHtmlPath).AbsoluteUri);
                }
            }

            NotifyWebViewsTheme();
            ApplyPaneLayout();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CliWebviewForm] Failed to create WebView for custom CLI '{ws.Name}': {ex.Message}");
        }
    }

    private static Microsoft.Web.WebView2.WinForms.WebView2 CreateHostedWebView()
    {
        // WebView2's default background is opaque white, which paints as a
        // white bar across the terminal while the control resizes with the form.
        return new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = DockStyle.Fill,
            Visible = false,
            DefaultBackgroundColor = Color.Transparent
        };
    }

    private async Task InitializeWebViewsAsync()
    {
        try
        {
            hubWebView = CreateHostedWebView();
            claudeWebView = CreateHostedWebView();
            antigravityWebView = CreateHostedWebView();

            paneHostLeft.Controls.Add(hubWebView);
            paneHostLeft.Controls.Add(claudeWebView);
            paneHostLeft.Controls.Add(antigravityWebView);

            var baseLocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userDataFolder = Path.Combine(baseLocalApp, "AgentsHub", "WebView2Data");
            _webViewEnv = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            _envInitTcs.TrySetResult(true);

            // Hub WebView
            await hubWebView.EnsureCoreWebView2Async(_webViewEnv);
            ConfigureWebViewSettings(hubWebView.CoreWebView2);
            hubWebView.CoreWebView2.WebMessageReceived += HubWebView_WebMessageReceived;
            hubWebView.CoreWebView2.NavigationCompleted += (_, _) => NotifyWebViewsTheme();
            _hubInitialized = true;
            if (IsTabEnabled("hub"))
            {
                RefreshHubNavigation();
            }

            // Claude Code WebView
            await claudeWebView.EnsureCoreWebView2Async(_webViewEnv);
            ConfigureWebViewSettings(claudeWebView.CoreWebView2);
            claudeWebView.CoreWebView2.WebMessageReceived += (s, e) => TerminalWebView_WebMessageReceived("claude_code", e);
            claudeWebView.CoreWebView2.NavigationCompleted += (_, _) => NotifyWebViewsTheme();
            if (File.Exists(_terminalHtmlPath))
            {
                claudeWebView.CoreWebView2.Navigate(new Uri(_terminalHtmlPath).AbsoluteUri);
            }

            // Antigravity CLI WebView
            await antigravityWebView.EnsureCoreWebView2Async(_webViewEnv);
            ConfigureWebViewSettings(antigravityWebView.CoreWebView2);
            antigravityWebView.CoreWebView2.WebMessageReceived += (s, e) => TerminalWebView_WebMessageReceived("antigravity_cli", e);
            antigravityWebView.CoreWebView2.NavigationCompleted += (_, _) => NotifyWebViewsTheme();
            if (File.Exists(_terminalHtmlPath))
            {
                antigravityWebView.CoreWebView2.Navigate(new Uri(_terminalHtmlPath).AbsoluteUri);
            }

            SwitchTab(ActivePaneTabId);
            NotifyWebViewsTheme();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 failed to initialize: {ex.Message}", "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Shared WebView2 hardening, applied to every hosted page in this window. DevTools stay
    /// disabled so no terminal or custom workspace can be inspected. Keep this in sync with the
    /// dashboard WebView2 in <c>MainForm</c>.
    /// </summary>
    private static void ConfigureWebViewSettings(CoreWebView2 webView)
    {
        // Terminal and workspace pages reach loopback CLIs, and each of them can type into the
        // host bridge. Keep navigation on loopback or the shipped ui folder.
        Helpers.WebViewSecurity.Apply(webView, allowLoopback: true);
        webView.Settings.AreDefaultContextMenusEnabled = true;
        webView.Settings.IsZoomControlEnabled = true;
    }

    private void RefreshHubNavigation()
    {
        if (!_hubInitialized || _cliManager == null || hubWebView?.CoreWebView2 == null)
        {
            return;
        }

        var hubInfo = _cliManager.GetInfo("hub");
        if (hubInfo.State == CliState.Running)
        {
            var targetUrl = $"http://127.0.0.1:{_cliManager.HubService.Port}/";
            if (hubWebView.Source == null || !hubWebView.Source.AbsoluteUri.StartsWith(targetUrl))
            {
                hubWebView.CoreWebView2.Navigate(targetUrl);
            }
        }
        else
        {
            if (File.Exists(_hubOfflineHtmlPath))
            {
                hubWebView.CoreWebView2.Navigate(new Uri(_hubOfflineHtmlPath).AbsoluteUri);
            }
            else
            {
                hubWebView.CoreWebView2.NavigateToString("<html><body style='background:#09090b;color:#fafafa;font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;'><h2>Hub Engine Offline</h2></body></html>");
            }
        }
    }

    private void HubWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeEl))
            {
                var typeStr = typeEl.GetString();
                if (typeStr == "start_hub")
                {
                    _cliManager?.Start("hub");
                }
            }
        }
        catch
        {
            // Suppress invalid JSON parsing errors
        }
    }

    private void TerminalWebView_WebMessageReceived(string cliId, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_cliManager == null) return;

        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeEl))
            {
                var msgType = typeEl.GetString();
                switch (msgType)
                {
                    case "ready":
                        SendTerminalInit(cliId);
                        break;

                    case "input":
                        if (doc.RootElement.TryGetProperty("data", out var dataEl))
                        {
                            var input = dataEl.GetString();
                            if (input != null)
                            {
                                _cliManager.SendInput(cliId, input);
                            }
                        }
                        break;

                    case "resize":
                        if (doc.RootElement.TryGetProperty("cols", out var colsEl) &&
                            doc.RootElement.TryGetProperty("rows", out var rowsEl))
                        {
                            short cols = colsEl.GetInt16();
                            short rows = rowsEl.GetInt16();
                            _cliManager.Resize(cliId, cols, rows);
                        }
                        break;

                    case "start":
                        _cliManager.Start(cliId);
                        break;
                }
            }
        }
        catch
        {
            // Suppress invalid JSON parsing errors
        }
    }

    private Microsoft.Web.WebView2.WinForms.WebView2? GetWebViewForId(string id)
    {
        if (string.Equals(id, "hub", StringComparison.OrdinalIgnoreCase)) return hubWebView;
        if (string.Equals(id, "claude_code", StringComparison.OrdinalIgnoreCase)) return claudeWebView;
        if (string.Equals(id, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
        {
            if (_cliManager != null)
            {
                var activeId = CliManagerService.FormatAntigravityProfileId(_cliManager.ProfileService.ActiveProfile);
                if (_customWebViews.TryGetValue(activeId, out var activeWv)) return activeWv;
            }
            return antigravityWebView;
        }
        if (_customWebViews.TryGetValue(id, out var wv)) return wv;
        return null;
    }

    private void SendTerminalInit(string cliId)
    {
        if (_cliManager == null) return;

        var targetWv = GetWebViewForId(cliId);
        if (targetWv?.CoreWebView2 == null) return;

        var info = _cliManager.GetInfo(cliId);
        var history = _cliManager.GetTerminalHistory(cliId);

        var payload = new TerminalInitPayload("init", info.ShortName, info.State == CliState.Running, info.StatusText, history);
        string json = JsonSerializer.Serialize(payload, CliJsonContext.Default.TerminalInitPayload);

        var primaryWv = GetWebViewForPane(cliId, WorkspacePane.Primary);
        if (primaryWv?.CoreWebView2 != null)
        {
            primaryWv.CoreWebView2.PostWebMessageAsJson(json);
        }

        if (_isSplitActive && string.Equals(cliId, _secondaryTabId, StringComparison.OrdinalIgnoreCase))
        {
            var secWv = GetWebViewForPane(cliId, WorkspacePane.Secondary);
            if (secWv != null && secWv != primaryWv && secWv.CoreWebView2 != null)
            {
                secWv.CoreWebView2.PostWebMessageAsJson(json);
            }
        }
    }

    private void ForwardTerminalData(string cliId, string text)
    {
        var primaryWv = GetWebViewForPane(cliId, WorkspacePane.Primary);
        if (primaryWv?.CoreWebView2 != null)
        {
            var payload = new TerminalOutputPayload("output", text);
            primaryWv.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, CliJsonContext.Default.TerminalOutputPayload));
        }

        if (_isSplitActive && string.Equals(cliId, _secondaryTabId, StringComparison.OrdinalIgnoreCase))
        {
            var secWv = GetWebViewForPane(cliId, WorkspacePane.Secondary);
            if (secWv != null && secWv != primaryWv && secWv.CoreWebView2 != null)
            {
                var payload = new TerminalOutputPayload("output", text);
                secWv.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, CliJsonContext.Default.TerminalOutputPayload));
            }
        }
    }

    private void OnCliStatusChanged(string cliId, CliState state, string message)
    {
        if (CliManagerService.IsAntigravityProfileId(cliId, out _) || string.Equals(cliId, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
        {
            RebuildTabs();
            RefreshProfileCombo();
        }
        else
        {
            UpdateTabButtonsUi();
        }

        if (string.Equals(cliId, ActivePaneTabId, StringComparison.OrdinalIgnoreCase))
        {
            UpdateToolbarUi();
        }

        if (string.Equals(cliId, "hub", StringComparison.OrdinalIgnoreCase))
        {
            RefreshHubNavigation();
        }
        else
        {
            var primaryWv = GetWebViewForPane(cliId, WorkspacePane.Primary);
            if (primaryWv?.CoreWebView2 != null)
            {
                var payload = new TerminalStatusPayload("status", state == CliState.Running, message);
                primaryWv.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, CliJsonContext.Default.TerminalStatusPayload));
            }

            if (_isSplitActive && string.Equals(cliId, _secondaryTabId, StringComparison.OrdinalIgnoreCase))
            {
                var secWv = GetWebViewForPane(cliId, WorkspacePane.Secondary);
                if (secWv != null && secWv != primaryWv && secWv.CoreWebView2 != null)
                {
                    var payload = new TerminalStatusPayload("status", state == CliState.Running, message);
                    secWv.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, CliJsonContext.Default.TerminalStatusPayload));
                }
            }
        }
    }

    private void UpdateTabButtonsUi()
    {
        if (_cliManager == null) return;
        var c = ThemeManager.Colors;

        foreach (var btn in _dynamicTabButtons)
        {
            if (btn.Tag is not string id) continue;

            var info = _cliManager.GetInfo(id);
            bool isPrimary = string.Equals(_primaryTabId, id, StringComparison.OrdinalIgnoreCase);
            bool isSecondary = _isSplitActive && string.Equals(_secondaryTabId, id, StringComparison.OrdinalIgnoreCase);
            bool isFocused = string.Equals(ActivePaneTabId, id, StringComparison.OrdinalIgnoreCase);

            var dotColor = info.State switch
            {
                CliState.Running => "●",
                CliState.Starting => "◐",
                CliState.Error => "▲",
                _ => "○"
            };

            string title = CliManagerService.IsAntigravityProfileId(id, out var prof)
                ? $"agy: {prof}"
                : info.ShortName;

            if (_isSplitActive)
            {
                if (isPrimary) title = $"{title} [L]";
                else if (isSecondary) title = $"{title} [R]";
            }

            btn.Text = $"{dotColor} {title}";

            if (isFocused)
            {
                btn.BackColor = c.ButtonHover;
                btn.ForeColor = c.TextPrimary;
                btn.FlatAppearance.BorderColor = c.AccentGreen;
            }
            else if (_isSplitActive && (isPrimary || isSecondary))
            {
                btn.BackColor = c.WellBackground;
                btn.ForeColor = c.TextPrimary;
                btn.FlatAppearance.BorderColor = c.AccentIndigo;
            }
            else
            {
                btn.BackColor = c.ButtonBg;
                btn.ForeColor = c.TextSecondary;
                btn.FlatAppearance.BorderColor = c.ButtonBorder;
            }
        }

        if (_manageClisButton != null)
        {
            _manageClisButton.BackColor = c.ButtonBg;
            _manageClisButton.FlatAppearance.BorderColor = c.ButtonBorder;
            _manageClisButton.Invalidate();
        }
    }

    private void UpdateToolbarUi()
    {
        if (_cliManager == null) return;
        var c = ThemeManager.Colors;

        // No workspace enabled: there is no active CLI, so hide its controls and say so.
        if (string.IsNullOrEmpty(ActivePaneTabId))
        {
            activeStatusLabel.Text = "No terminals active";
            activeStatusLabel.ForeColor = c.TextMuted;

            toggleRunButton.Visible = false;
            restartCliButton.Visible = false;
            clearTerminalButton.Visible = false;
            openBrowserButton.Visible = false;

            UpdateSessionConfigButton();
            LayoutToolbar();
            return;
        }

        toggleRunButton.Visible = true;
        restartCliButton.Visible = true;
        clearTerminalButton.Visible = true;

        var info = _cliManager.GetInfo(ActivePaneTabId);
        activeStatusLabel.Text = $"{info.ShortName}: {info.StatusText}";

        int browserPort = GetBrowserPortForActiveTab();

        if (info.State == CliState.Running)
        {
            activeStatusLabel.ForeColor = c.AccentGreen;
            _toggleRunNormalColor = c.DangerRed;
            _toggleRunHoverColor = Color.FromArgb(220, 38, 38);
            toggleRunButton.BackColor = _toggleRunNormalColor;
            toggleRunButton.FlatAppearance.BorderColor = c.DangerRed;
            _actionToolTip.SetToolTip(toggleRunButton, $"Stop {info.ShortName}");

            restartCliButton.Enabled = true;
            clearTerminalButton.Enabled = true;
        }
        else if (info.State == CliState.Starting)
        {
            activeStatusLabel.ForeColor = c.WarningAmber;
            _toggleRunNormalColor = c.WarningAmber;
            _toggleRunHoverColor = Color.FromArgb(217, 119, 6);
            toggleRunButton.BackColor = _toggleRunNormalColor;
            toggleRunButton.FlatAppearance.BorderColor = c.WarningAmber;
            _actionToolTip.SetToolTip(toggleRunButton, $"Starting {info.ShortName}...");

            restartCliButton.Enabled = false;
            clearTerminalButton.Enabled = true;
        }
        else
        {
            activeStatusLabel.ForeColor = c.TextMuted;
            _toggleRunNormalColor = c.AccentGreen;
            _toggleRunHoverColor = c.AccentGreenHover;
            toggleRunButton.BackColor = _toggleRunNormalColor;
            toggleRunButton.FlatAppearance.BorderColor = c.AccentGreen;
            _actionToolTip.SetToolTip(toggleRunButton, $"Start {info.ShortName}");

            restartCliButton.Enabled = false;
            clearTerminalButton.Enabled = true;
        }

        _actionToolTip.SetToolTip(restartCliButton, $"Restart {info.ShortName}");
        _actionToolTip.SetToolTip(clearTerminalButton, "Clear terminal screen");

        if (browserPort > 0)
        {
            openBrowserButton.Visible = true;
            _actionToolTip.SetToolTip(openBrowserButton, $"Open in browser (http://127.0.0.1:{browserPort}/)");
        }
        else
        {
            openBrowserButton.Visible = false;
        }

        toggleRunButton.Invalidate();
        restartCliButton.Invalidate();
        clearTerminalButton.Invalidate();
        openBrowserButton.Invalidate();

        if (string.Equals(ActivePaneTabId, "claude_code", StringComparison.OrdinalIgnoreCase))
        {
            UpdateClaudeControlsUi();
        }

        UpdateSessionConfigButton();
        LayoutToolbar();
    }

    private int GetBrowserPortForActiveTab()
    {
        if (string.Equals(ActivePaneTabId, "hub", StringComparison.OrdinalIgnoreCase))
        {
            return _cliManager?.HubService.Port ?? 8000;
        }
        if (string.Equals(ActivePaneTabId, "claude_code", StringComparison.OrdinalIgnoreCase))
        {
            return 9229;
        }
        if (CliManagerService.IsAntigravityProfileId(ActivePaneTabId, out var prof))
        {
            return _cliManager?.GetPortForProfile(prof) ?? 0;
        }
        if (string.Equals(ActivePaneTabId, "antigravity_cli", StringComparison.OrdinalIgnoreCase))
        {
            var activeProf = _cliManager?.ProfileService.ActiveProfile ?? string.Empty;
            return _cliManager?.GetPortForProfile(activeProf) ?? 0;
        }

        var ws = AiConfigService.Instance.GetCliWorkspaces().FirstOrDefault(w => string.Equals(w.Id, ActivePaneTabId, StringComparison.OrdinalIgnoreCase));
        return ws?.Port ?? 0;
    }

    private async Task HandleToggleRunClickAsync()
    {
        if (_cliManager == null) return;

        var info = _cliManager.GetInfo(ActivePaneTabId);
        if (info.State == CliState.Running || info.State == CliState.Starting)
        {
            await _cliManager.StopAsync(ActivePaneTabId);
        }
        else
        {
            _cliManager.Start(ActivePaneTabId);
        }
    }

    private async Task HandleSetProjectDirClickAsync()
    {
        string tabId = ActivePaneTabId;
        if (string.IsNullOrEmpty(tabId)) return;

        bool isAgyProfile = CliManagerService.IsAntigravityProfileId(tabId, out var profileName);
        string? currentDir = isAgyProfile
            ? _cliManager?.ProfileService.GetProfileWorkingDirectory(profileName)
            : AiConfigService.Instance.GetWorkspaceWorkingDirectory(tabId);

        using var fbd = new FolderBrowserDialog
        {
            Description = $"Select project folder for {(isAgyProfile ? $"agy: {profileName}" : tabId)}",
            UseDescriptionForTitle = true
        };
        if (!string.IsNullOrWhiteSpace(currentDir) && Directory.Exists(currentDir))
        {
            fbd.InitialDirectory = currentDir;
        }

        if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
        {
            string newPath = fbd.SelectedPath;
            if (isAgyProfile && _cliManager != null)
            {
                _cliManager.ProfileService.SetProfileWorkingDirectory(profileName, newPath);
            }
            else
            {
                AiConfigService.Instance.SetWorkspaceWorkingDirectory(tabId, newPath);
            }

            UpdateSessionConfigButton();
            LayoutToolbar();

            var info = _cliManager?.GetInfo(tabId);
            if (info != null && (info.State == CliState.Running || info.State == CliState.Starting))
            {
                var dr = MessageBox.Show(this,
                    $"Project directory updated to:\n{newPath}\n\nRestart {info.ShortName} now to apply this folder?",
                    "Restart to Apply Project Folder",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (dr == DialogResult.Yes && _cliManager != null)
                {
                    await _cliManager.RestartAsync(tabId);
                }
            }
        }
    }

    private void HandleToggleResumeClick()
    {
        string tabId = ActivePaneTabId;
        if (string.IsNullOrEmpty(tabId)) return;

        bool isAgyProfile = CliManagerService.IsAntigravityProfileId(tabId, out var profileName);
        bool current = isAgyProfile
            ? (_cliManager?.ProfileService.GetProfileAutoResume(profileName) ?? true)
            : AiConfigService.Instance.GetWorkspaceAutoResume(tabId);

        bool nextVal = !current;
        if (isAgyProfile && _cliManager != null)
        {
            _cliManager.ProfileService.SetProfileAutoResume(profileName, nextVal);
        }
        else
        {
            AiConfigService.Instance.SetWorkspaceAutoResume(tabId, nextVal);
        }

        UpdateSessionConfigButton();
        LayoutToolbar();
    }

    private void HandleToggleSkipPermissionsClick()
    {
        string tabId = ActivePaneTabId;
        if (string.IsNullOrEmpty(tabId)) return;

        bool isAgyProfile = CliManagerService.IsAntigravityProfileId(tabId, out var profileName);
        bool current = isAgyProfile
            ? (_cliManager?.ProfileService.GetProfileSkipPermissions(profileName) ?? false)
            : AiConfigService.Instance.GetWorkspaceSkipPermissions(tabId);

        bool nextVal = !current;
        if (isAgyProfile && _cliManager != null)
        {
            _cliManager.ProfileService.SetProfileSkipPermissions(profileName, nextVal);
        }
        else
        {
            AiConfigService.Instance.SetWorkspaceSkipPermissions(tabId, nextVal);
        }

        UpdateSessionConfigButton();
        LayoutToolbar();
    }

    private void UpdateSessionConfigButton()
    {
        if (_sessionConfigButton == null) return;

        string tabId = ActivePaneTabId;
        bool isHub = string.Equals(tabId, "hub", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(tabId) || isHub)
        {
            _sessionConfigButton.Visible = false;
            return;
        }

        _sessionConfigButton.Visible = true;

        bool isAgyProfile = CliManagerService.IsAntigravityProfileId(tabId, out var profileName);
        string? workDir = isAgyProfile
            ? _cliManager?.ProfileService.GetProfileWorkingDirectory(profileName)
            : AiConfigService.Instance.GetWorkspaceWorkingDirectory(tabId);

        bool autoResume = isAgyProfile
            ? (_cliManager?.ProfileService.GetProfileAutoResume(profileName) ?? true)
            : AiConfigService.Instance.GetWorkspaceAutoResume(tabId);

        bool skipPerms = isAgyProfile
            ? (_cliManager?.ProfileService.GetProfileSkipPermissions(profileName) ?? false)
            : AiConfigService.Instance.GetWorkspaceSkipPermissions(tabId);

        var c = ThemeManager.Colors;
        _sessionConfigButton.BackColor = c.ButtonBg;
        _sessionConfigButton.ForeColor = c.TextPrimary;

        if (skipPerms)
        {
            _sessionConfigButton.FlatAppearance.BorderColor = c.WarningAmber;
        }
        else if (autoResume)
        {
            _sessionConfigButton.FlatAppearance.BorderColor = c.AccentIndigo;
        }
        else if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
        {
            _sessionConfigButton.FlatAppearance.BorderColor = c.AccentGreen;
        }
        else
        {
            _sessionConfigButton.FlatAppearance.BorderColor = c.ButtonBorder;
        }

        string dirSummary = (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
            ? Path.GetFileName(workDir.TrimEnd('\\', '/'))
            : "Default (VirtualHome)";

        string resumeSummary = autoResume ? "ON (--continue)" : "OFF (New Session)";
        string permsSummary = skipPerms ? "ON (--dangerously-skip-permissions)" : "OFF (Confirm prompts)";

        _actionToolTip.SetToolTip(_sessionConfigButton,
            $"Session Settings - {(isAgyProfile ? $"agy: {profileName}" : tabId)}\n" +
            $"Project: {dirSummary}\n" +
            $"Resume: {resumeSummary}\n" +
            $"Auto-Approve: {permsSummary}\n" +
            $"Click to configure");

        _sessionConfigButton.Invalidate();
    }

    private void ShowSessionConfigContextMenu()
    {
        if (_sessionConfigButton == null) return;

        string tabId = ActivePaneTabId;
        if (string.IsNullOrEmpty(tabId) || string.Equals(tabId, "hub", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _sessionConfigContextMenu?.Dispose();

        var c = ThemeManager.Colors;
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new ShellColorTable()),
            BackColor = c.CardBackground,
            ForeColor = c.TextPrimary
        };
        _sessionConfigContextMenu = menu;

        bool isAgyProfile = CliManagerService.IsAntigravityProfileId(tabId, out var profileName);
        string displayName = isAgyProfile ? $"agy: {profileName}" : tabId;

        string? workDir = isAgyProfile
            ? _cliManager?.ProfileService.GetProfileWorkingDirectory(profileName)
            : AiConfigService.Instance.GetWorkspaceWorkingDirectory(tabId);

        bool autoResume = isAgyProfile
            ? (_cliManager?.ProfileService.GetProfileAutoResume(profileName) ?? true)
            : AiConfigService.Instance.GetWorkspaceAutoResume(tabId);

        bool skipPerms = isAgyProfile
            ? (_cliManager?.ProfileService.GetProfileSkipPermissions(profileName) ?? false)
            : AiConfigService.Instance.GetWorkspaceSkipPermissions(tabId);

        string dirSummary = (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
            ? Path.GetFileName(workDir.TrimEnd('\\', '/'))
            : "(Default VirtualHome)";

        // Title Header
        menu.Items.Add(new ToolStripMenuItem($"{displayName} Settings")
        {
            Enabled = false,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        });
        menu.Items.Add(new ToolStripSeparator());

        // Project Folder
        menu.Items.Add(new ToolStripMenuItem($"Project Folder: {dirSummary}", null, async (_, _) =>
        {
            await HandleSetProjectDirClickAsync();
        }));

        if (!string.IsNullOrWhiteSpace(workDir))
        {
            menu.Items.Add(new ToolStripMenuItem("Reset Project Folder to Default", null, async (_, _) =>
            {
                if (isAgyProfile && _cliManager != null)
                {
                    _cliManager.ProfileService.SetProfileWorkingDirectory(profileName, null);
                }
                else
                {
                    AiConfigService.Instance.SetWorkspaceWorkingDirectory(tabId, null);
                }

                UpdateSessionConfigButton();
                LayoutToolbar();

                var info = _cliManager?.GetInfo(tabId);
                if (info != null && (info.State == CliState.Running || info.State == CliState.Starting))
                {
                    var dr = MessageBox.Show(this,
                        $"Project directory reset to default (VirtualHome).\n\nRestart {info.ShortName} now to apply?",
                        "Restart to Apply Default Folder",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (dr == DialogResult.Yes && _cliManager != null)
                    {
                        await _cliManager.RestartAsync(tabId);
                    }
                }
            }));
        }

        menu.Items.Add(new ToolStripSeparator());

        // Resume Previous Session
        string resumeText = autoResume
            ? "Resume Previous Session: ON (--continue)"
            : "Resume Previous Session: OFF (--continue)";
        var resumeMenuItem = new ToolStripMenuItem(resumeText, null, (_, _) =>
        {
            HandleToggleResumeClick();
        })
        {
            Checked = autoResume,
            ForeColor = autoResume ? c.AccentIndigo : c.TextMuted
        };
        menu.Items.Add(resumeMenuItem);

        // Auto-Approve Permissions
        string permsText = skipPerms
            ? "Auto-Approve Tool Permissions: ON (--dangerously-skip-permissions)"
            : "Auto-Approve Tool Permissions: OFF (--dangerously-skip-permissions)";
        var permsMenuItem = new ToolStripMenuItem(permsText, null, (_, _) =>
        {
            HandleToggleSkipPermissionsClick();
        })
        {
            Checked = skipPerms,
            ForeColor = skipPerms ? c.WarningAmber : c.TextMuted
        };
        menu.Items.Add(permsMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        // Launch in external CMD
        menu.Items.Add(new ToolStripMenuItem("Open in External CMD (Real Terminal)", null, (_, _) =>
        {
            LaunchExternalCmdForTab(tabId);
        }));

        menu.Show(_sessionConfigButton, 0, _sessionConfigButton.Height);
    }

    private void DrawSessionConfigIcon(object? sender, PaintEventArgs e)
    {
        if (sender is not Button btn) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        string tabId = ActivePaneTabId;
        bool isAgyProfile = CliManagerService.IsAntigravityProfileId(tabId, out var profileName);
        string? workDir = isAgyProfile
            ? _cliManager?.ProfileService.GetProfileWorkingDirectory(profileName)
            : AiConfigService.Instance.GetWorkspaceWorkingDirectory(tabId);

        bool autoResume = isAgyProfile
            ? (_cliManager?.ProfileService.GetProfileAutoResume(profileName) ?? true)
            : AiConfigService.Instance.GetWorkspaceAutoResume(tabId);

        bool skipPerms = isAgyProfile
            ? (_cliManager?.ProfileService.GetProfileSkipPermissions(profileName) ?? false)
            : AiConfigService.Instance.GetWorkspaceSkipPermissions(tabId);

        var c = ThemeManager.Colors;
        Color glyphColor = btn.Enabled ? c.TextPrimary : c.TextMuted;
        using var pen = new Pen(glyphColor, 1.8f);

        int cx = btn.Width / 2;
        int cy = btn.Height / 2;

        // Gear teeth (8 radial spokes)
        float outerR = 7.0f;
        float innerR = 4.2f;
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4.0;
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            g.DrawLine(pen, cx + innerR * cos, cy + innerR * sin, cx + outerR * cos, cy + outerR * sin);
        }

        // Gear outer ring
        g.DrawEllipse(pen, cx - 4.5f, cy - 4.5f, 9.0f, 9.0f);

        // Center hub hole
        using (var bgBrush = new SolidBrush(btn.BackColor))
        {
            g.FillEllipse(bgBrush, cx - 2.0f, cy - 2.0f, 4.0f, 4.0f);
        }
        g.DrawEllipse(pen, cx - 2.0f, cy - 2.0f, 4.0f, 4.0f);

        // Status badge: Auto-Approve (amber dot badge at top-right)
        if (skipPerms)
        {
            using var amberBrush = new SolidBrush(c.WarningAmber);
            g.FillEllipse(amberBrush, cx + 4, cy - 9, 6, 6);
        }

        // Status badge: Resume Session (blue dot badge at bottom-right, matching user mockup)
        if (autoResume)
        {
            using var blueBrush = new SolidBrush(c.AccentIndigo);
            g.FillEllipse(blueBrush, cx + 4, cy + 3, 6, 6);
        }

        // Status badge: Custom Project Folder (green dot badge at bottom-left)
        if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
        {
            using var greenBrush = new SolidBrush(c.AccentGreen);
            g.FillEllipse(greenBrush, cx - 10, cy + 3, 6, 6);
        }
    }

    private void HandleClearTerminalClick()
    {
        if (_cliManager == null || string.Equals(ActivePaneTabId, "hub", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cliManager.ClearTerminalHistory(ActivePaneTabId);
        var targetWv = GetWebViewForId(ActivePaneTabId);
        if (targetWv?.CoreWebView2 != null)
        {
            var payload = new TerminalActionPayload("clear");
            targetWv.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, CliJsonContext.Default.TerminalActionPayload));
        }
    }

    internal record TerminalInitPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("isRunning")] bool IsRunning,
        [property: JsonPropertyName("statusText")] string StatusText,
        [property: JsonPropertyName("history")] string History);

    internal record TerminalOutputPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("data")] string Data);

    internal record TerminalStatusPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("isRunning")] bool IsRunning,
        [property: JsonPropertyName("statusText")] string StatusText);

    internal record TerminalActionPayload(
        [property: JsonPropertyName("type")] string Type);

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(TerminalInitPayload))]
    [JsonSerializable(typeof(TerminalOutputPayload))]
    [JsonSerializable(typeof(TerminalStatusPayload))]
    [JsonSerializable(typeof(TerminalActionPayload))]
    internal partial class CliJsonContext : JsonSerializerContext
    {
    }

    private void HandleOpenBrowserClick()
    {
        if (_cliManager == null) return;

        try
        {
            int port = GetBrowserPortForActiveTab();
            if (port > 0)
            {
                Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(
                    "This tab has no web port yet. Start the session first, then open it in the browser.",
                    "Open in Browser", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to open browser: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void SetupButtonHover(Button button)
    {
        button.MouseEnter += (_, _) =>
        {
            if (button.Enabled) button.BackColor = ThemeManager.Colors.ButtonHover;
        };
        button.MouseLeave += (_, _) =>
        {
            if (button.Enabled) button.BackColor = ThemeManager.Colors.ButtonBg;
        };
    }

    private void SetupTabHover(Button btn, string id)
    {
        btn.MouseEnter += (_, _) =>
        {
            if (!string.Equals(ActivePaneTabId, id, StringComparison.OrdinalIgnoreCase))
            {
                btn.BackColor = ThemeManager.Colors.ButtonHover;
            }
        };
        btn.MouseLeave += (_, _) =>
        {
            if (!string.Equals(ActivePaneTabId, id, StringComparison.OrdinalIgnoreCase))
            {
                btn.BackColor = ThemeManager.Colors.ButtonBg;
            }
        };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }

        if (keyData == (Keys.Control | Keys.Tab))
        {
            CycleTab(forward: true);
            return true;
        }

        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            CycleTab(forward: false);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void CycleTab(bool forward)
    {
        if (_dynamicTabButtons.Count == 0) return;

        int currentIndex = _dynamicTabButtons.FindIndex(b => string.Equals(b.Tag as string, ActivePaneTabId, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0) currentIndex = 0;

        int nextIndex = forward
            ? (currentIndex + 1) % _dynamicTabButtons.Count
            : (currentIndex - 1 + _dynamicTabButtons.Count) % _dynamicTabButtons.Count;

        if (_dynamicTabButtons[nextIndex].Tag is string nextId)
        {
            SwitchTab(nextId);
        }
    }

    private void RefreshProfileCombo()
    {
        if (IsInDesignMode || _cliManager == null || _profileComboBox == null) return;

        try
        {
            _isUpdatingProfileCombo = true;
            _profileComboBox.Items.Clear();

            var profiles = _cliManager.ProfileService.GetProfiles();
            string currentProfile = CliManagerService.IsAntigravityProfileId(ActivePaneTabId, out var tabProf)
                ? tabProf
                : (_cliManager.ProfileService.ActiveProfile ?? "Default");

            ProfileItem? selectedItem = null;

            foreach (var p in profiles)
            {
                string? email = AntigravityProfileService.GetAccountEmail(p);
                bool isRunning = _cliManager.IsProfileRunning(p);
                int? port = isRunning ? _cliManager.GetPortForProfile(p) : null;

                string statusBadge = isRunning
                    ? (port.HasValue ? $"[Running : {port.Value}]" : "[Running]")
                    : "[Stopped]";

                string emailText = !string.IsNullOrWhiteSpace(email) ? $" ({email})" : "";
                string display = $"{p}{emailText} {statusBadge}";

                var item = new ProfileItem
                {
                    ProfileName = p,
                    DisplayText = display,
                    IsRunning = isRunning,
                    Port = port
                };
                _profileComboBox.Items.Add(item);

                if (string.Equals(p, currentProfile, StringComparison.OrdinalIgnoreCase))
                {
                    selectedItem = item;
                }
            }

            if (selectedItem != null)
            {
                _profileComboBox.SelectedItem = selectedItem;
            }
            else if (_profileComboBox.Items.Count > 0)
            {
                _profileComboBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _isUpdatingProfileCombo = false;
        }
    }

    private string? PromptForProfileName(string defaultName)
    {
        using var form = new Form
        {
            Text = "New Antigravity Profile",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(330, 135),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = ThemeManager.Colors.WindowBackground,
            ForeColor = ThemeManager.Colors.TextPrimary
        };

        var lbl = new Label
        {
            Text = "Enter new profile / account name:",
            Location = new Point(16, 16),
            AutoSize = true,
            ForeColor = ThemeManager.Colors.TextSecondary
        };

        var txt = new TextBox
        {
            Text = defaultName,
            Location = new Point(16, 42),
            Width = 296,
            BackColor = ThemeManager.Colors.WellBackground,
            ForeColor = ThemeManager.Colors.TextPrimary
        };

        var btnOk = new Button
        {
            Text = "Create & Login",
            DialogResult = DialogResult.OK,
            Location = new Point(148, 82),
            Size = new Size(104, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeManager.Colors.AccentGreen,
            ForeColor = ThemeManager.Colors.AccentGreenText,
            Cursor = Cursors.Hand
        };
        btnOk.FlatAppearance.BorderSize = 0;

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(258, 82),
            Size = new Size(54, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeManager.Colors.ButtonBg,
            ForeColor = ThemeManager.Colors.TextSecondary,
            Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderColor = ThemeManager.Colors.ButtonBorder;

        form.Controls.AddRange([lbl, txt, btnOk, btnCancel]);
        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        txt.SelectAll();
        return form.ShowDialog(this) == DialogResult.OK ? txt.Text.Trim() : null;
    }

    private sealed class ProfileItem
    {
        public string ProfileName { get; set; } = "";
        public string DisplayText { get; set; } = "";
        public bool IsRunning { get; set; }
        public int? Port { get; set; }
        public override string ToString() => DisplayText;
    }
}