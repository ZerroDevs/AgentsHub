using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using AgentsHub.Helpers;
using AgentsHub.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgentsHub;

public sealed class HudTerminalForm : Form
{
    private readonly CliManagerService? _cliManager;
    private readonly Form? _mainForm;
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _animTimer;
    private bool _isSlidingDown;
    private bool _isOpen;
    private int _targetY;
    private int _hiddenY;
    private readonly Panel _headerPanel;
    private readonly Label _titleLabel;
    private readonly FlowLayoutPanel _tabsPanel;
    private readonly FlowLayoutPanel _toolbarPanel;
    private readonly Button _toggleRunButton;
    private readonly Button _restartButton;
    private readonly Button _clearButton;
    private readonly Label _hintLabel;
    private readonly Button _closeButton;
    private readonly bool _autoHideOnDeactivate = true;

    private readonly Dictionary<string, Button> _tabButtons = new(StringComparer.OrdinalIgnoreCase);
    private string _currentCliId = "hub";
    private bool _isInitialized;

    public bool IsHudOpen => _isOpen;
    public string CurrentCliId => _currentCliId;

    public HudTerminalForm(CliManagerService? cliManager = null, Form? mainForm = null)
    {
        _cliManager = cliManager;
        _mainForm = mainForm;

        Text = "AgentsHub Quake Hud";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(12, 14, 18);
        ForeColor = Color.FromArgb(240, 240, 245);

        var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        int targetWidth = (int)(screen.Width * 0.94);
        int targetHeight = (int)(screen.Height * 0.44);
        Size = new Size(targetWidth, targetHeight);
        Left = screen.Left + (screen.Width - targetWidth) / 2;

        _targetY = screen.Top;
        _hiddenY = screen.Top - targetHeight - 20;
        Top = _hiddenY;

        // Header
        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 38,
            BackColor = Color.FromArgb(18, 22, 28),
            Padding = new Padding(10, 0, 10, 0)
        };
        _headerPanel.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(40, 48, 60));
            e.Graphics.DrawLine(pen, 0, _headerPanel.Height - 1, _headerPanel.Width, _headerPanel.Height - 1);
        };

        _titleLabel = new Label
        {
            Text = "AgentsHub Quake Hud",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 185, 129),
            AutoSize = true,
            Location = new Point(12, 9)
        };

        _tabsPanel = new FlowLayoutPanel
        {
            Location = new Point(185, 5),
            Height = 28,
            AutoSize = true,
            MaximumSize = new Size(targetWidth - 520, 28),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            AutoScroll = false
        };

        _toolbarPanel = new FlowLayoutPanel
        {
            Location = new Point(targetWidth - 490, 5),
            Size = new Size(240, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent
        };

        _toggleRunButton = new Button
        {
            Text = "▶ Start",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = Color.FromArgb(52, 211, 153),
            BackColor = Color.FromArgb(24, 30, 40),
            FlatStyle = FlatStyle.Flat,
            Height = 24,
            AutoSize = true,
            Margin = new Padding(2, 0, 3, 0),
            Cursor = Cursors.Hand
        };
        _toggleRunButton.FlatAppearance.BorderSize = 1;
        _toggleRunButton.FlatAppearance.BorderColor = Color.FromArgb(16, 185, 129);
        _toggleRunButton.Click += (s, e) => OnToggleRunClick();

        _restartButton = new Button
        {
            Text = "↻ Restart",
            Font = new Font("Segoe UI", 8F),
            ForeColor = Color.FromArgb(200, 210, 225),
            BackColor = Color.FromArgb(24, 30, 40),
            FlatStyle = FlatStyle.Flat,
            Height = 24,
            AutoSize = true,
            Margin = new Padding(2, 0, 3, 0),
            Cursor = Cursors.Hand
        };
        _restartButton.FlatAppearance.BorderSize = 1;
        _restartButton.FlatAppearance.BorderColor = Color.FromArgb(50, 60, 75);
        _restartButton.Click += (s, e) => OnRestartClick();

        _clearButton = new Button
        {
            Text = "⌧ Clear",
            Font = new Font("Segoe UI", 8F),
            ForeColor = Color.FromArgb(170, 180, 195),
            BackColor = Color.FromArgb(24, 30, 40),
            FlatStyle = FlatStyle.Flat,
            Height = 24,
            AutoSize = true,
            Margin = new Padding(2, 0, 3, 0),
            Cursor = Cursors.Hand
        };
        _clearButton.FlatAppearance.BorderSize = 1;
        _clearButton.FlatAppearance.BorderColor = Color.FromArgb(50, 60, 75);
        _clearButton.Click += (s, e) => OnClearClick();

        _toolbarPanel.Controls.Add(_toggleRunButton);
        _toolbarPanel.Controls.Add(_restartButton);
        _toolbarPanel.Controls.Add(_clearButton);

        _hintLabel = new Label
        {
            Text = "Press Esc to dismiss",
            Font = new Font("Segoe UI", 8.25F, FontStyle.Regular),
            ForeColor = Color.FromArgb(130, 140, 155),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(targetWidth - 235, 10)
        };
        UpdateHintLabel();

        _closeButton = new Button
        {
            Text = "✕",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(180, 190, 205),
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(24, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(targetWidth - 34, 6),
            Cursor = Cursors.Hand
        };
        _closeButton.FlatAppearance.BorderSize = 0;
        _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 40, 40);
        _closeButton.Click += (s, e) => SlideClose();

        _headerPanel.Controls.Add(_titleLabel);
        _headerPanel.Controls.Add(_tabsPanel);
        _headerPanel.Controls.Add(_toolbarPanel);
        _headerPanel.Controls.Add(_hintLabel);
        _headerPanel.Controls.Add(_closeButton);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(10, 12, 16)
        };

        Controls.Add(_webView);
        Controls.Add(_headerPanel);

        _animTimer = new System.Windows.Forms.Timer { Interval = 12 };
        _animTimer.Tick += OnAnimTick;

        Deactivate += (s, e) =>
        {
            if (_autoHideOnDeactivate && _isOpen && !_isSlidingDown)
            {
                if (ActiveControl == _webView || ContainsFocus) return;
                SlideClose();
            }
        };

        if (_cliManager != null)
        {
            _cliManager.TerminalDataReceivedId += OnTerminalDataReceived;
            _cliManager.StatusChangedId += OnCliStatusChanged;
        }

        ThemeManager.ThemeChanged += OnThemeChanged;

        RebuildTabs();
        _ = InitializeWebViewAsync();
    }

    private void UpdateHintLabel()
    {
        try
        {
            string hotkeyName = StartupHelper.GetStringPreference("HudHotkeyKey", "F12");
            string hotkeyMod = StartupHelper.GetStringPreference("HudHotkeyModifier", "None");
            string displayHotkey = hotkeyMod != "None" ? $"{hotkeyMod}+{hotkeyName}" : hotkeyName;
            _hintLabel.Text = $"Press {displayHotkey} or Esc";
        }
        catch { }
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                string theme = ThemeManager.CurrentTheme == AppTheme.Dark ? "dark" : "light";
                string accent = ThemeManager.AccentHex;
                var payload = new { type = "theme_changed", theme, accentColor = accent };
                _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            }
        }
        catch { }
    }

    public void RebuildTabs()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RebuildTabs));
            return;
        }

        _tabsPanel.SuspendLayout();
        _tabsPanel.Controls.Clear();
        _tabButtons.Clear();

        var workspaces = AiConfigService.Instance.GetCliWorkspaces();
        var enabledWorkspaces = workspaces.Where(w => w.IsEnabled).ToList();

        var tabIds = new List<(string Id, string Name)>();

        if (enabledWorkspaces.Count == 0)
        {
            tabIds.Add(("hub", "Hub CLI"));
            tabIds.Add(("claude_code", "Claude Code"));
        }
        else
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
                            profileTabsToShow.Add(prof);
                    }
                    if (!string.IsNullOrEmpty(activeProfile) && !profileTabsToShow.Contains(activeProfile, StringComparer.OrdinalIgnoreCase))
                        profileTabsToShow.Add(activeProfile);

                    foreach (var prof in allProfiles)
                    {
                        if (profileTabsToShow.Count >= 4) break;
                        if (!profileTabsToShow.Contains(prof, StringComparer.OrdinalIgnoreCase))
                            profileTabsToShow.Add(prof);
                    }

                    if (profileTabsToShow.Count == 0)
                        profileTabsToShow.Add("Default");

                    foreach (var profName in profileTabsToShow)
                    {
                        string profTabId = CliManagerService.FormatAntigravityProfileId(profName);
                        string displayName = $"agy: {profName}";
                        tabIds.Add((profTabId, displayName));
                    }
                    continue;
                }

                tabIds.Add((normId, ws.Name));
            }
        }

        foreach (var (id, name) in tabIds)
        {
            var btn = new Button
            {
                Tag = id,
                Text = name,
                Font = new Font("Segoe UI", 8.25F, FontStyle.Regular),
                ForeColor = Color.FromArgb(210, 215, 225),
                BackColor = Color.FromArgb(30, 36, 46),
                FlatStyle = FlatStyle.Flat,
                Height = 24,
                AutoSize = true,
                Margin = new Padding(3, 1, 3, 1),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(50, 60, 75);
            btn.FlatAppearance.BorderSize = 1;
            btn.Click += (s, e) => SwitchCli(id, syncToWebview: true);

            _tabButtons[id] = btn;
            _tabsPanel.Controls.Add(btn);
        }

        _tabsPanel.ResumeLayout(true);

        if (string.IsNullOrEmpty(_currentCliId) || !_tabButtons.ContainsKey(_currentCliId))
        {
            _currentCliId = tabIds.Count > 0 ? tabIds[0].Id : "hub";
        }

        UpdateTabButtonsUi();
    }

    private void UpdateTabButtonsUi()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(UpdateTabButtonsUi));
            return;
        }

        foreach (var kvp in _tabButtons)
        {
            string id = kvp.Key;
            Button btn = kvp.Value;

            var info = _cliManager?.GetInfo(id);
            var state = info?.State ?? CliState.Offline;

            var dot = state switch
            {
                CliState.Running => "●",
                CliState.Starting => "◐",
                CliState.Error => "▲",
                _ => "○"
            };

            string name = CliManagerService.IsAntigravityProfileId(id, out var prof)
                ? $"agy: {prof}"
                : (info?.ShortName ?? id);

            btn.Text = $"{dot} {name}";

            bool isCurrent = string.Equals(_currentCliId, id, StringComparison.OrdinalIgnoreCase);
            if (isCurrent)
            {
                btn.BackColor = Color.FromArgb(42, 52, 68);
                btn.ForeColor = Color.FromArgb(255, 255, 255);
                btn.FlatAppearance.BorderColor = state == CliState.Running
                    ? Color.FromArgb(16, 185, 129)
                    : Color.FromArgb(59, 130, 246);
                btn.Font = new Font("Segoe UI", 8.25F, FontStyle.Bold);
            }
            else
            {
                btn.BackColor = Color.FromArgb(24, 28, 36);
                btn.ForeColor = state == CliState.Running
                    ? Color.FromArgb(16, 185, 129)
                    : Color.FromArgb(160, 170, 185);
                btn.FlatAppearance.BorderColor = Color.FromArgb(44, 52, 64);
                btn.Font = new Font("Segoe UI", 8.25F, FontStyle.Regular);
            }
        }

        UpdateToolbarUi();
    }

    private void UpdateToolbarUi()
    {
        if (_toggleRunButton == null) return;

        var info = _cliManager?.GetInfo(_currentCliId);
        bool isRunning = info?.State == CliState.Running;
        bool isStarting = info?.State == CliState.Starting;

        if (isRunning)
        {
            _toggleRunButton.Text = "■ Stop";
            _toggleRunButton.ForeColor = Color.FromArgb(248, 113, 113);
            _toggleRunButton.FlatAppearance.BorderColor = Color.FromArgb(239, 68, 68);
            _toggleRunButton.Enabled = true;
        }
        else if (isStarting)
        {
            _toggleRunButton.Text = "◐ Starting";
            _toggleRunButton.ForeColor = Color.FromArgb(251, 191, 36);
            _toggleRunButton.FlatAppearance.BorderColor = Color.FromArgb(245, 158, 11);
            _toggleRunButton.Enabled = false;
        }
        else
        {
            _toggleRunButton.Text = "▶ Start";
            _toggleRunButton.ForeColor = Color.FromArgb(52, 211, 153);
            _toggleRunButton.FlatAppearance.BorderColor = Color.FromArgb(16, 185, 129);
            _toggleRunButton.Enabled = true;
        }
    }

    private void OnToggleRunClick()
    {
        if (_cliManager == null || string.IsNullOrEmpty(_currentCliId)) return;
        var info = _cliManager.GetInfo(_currentCliId);
        if (info.State == CliState.Running)
        {
            _ = _cliManager.StopAsync(_currentCliId);
        }
        else
        {
            _cliManager.Start(_currentCliId);
        }
    }

    private void OnRestartClick()
    {
        if (_cliManager == null || string.IsNullOrEmpty(_currentCliId)) return;
        _ = _cliManager.RestartAsync(_currentCliId);
    }

    private void OnClearClick()
    {
        if (_cliManager == null || string.IsNullOrEmpty(_currentCliId)) return;
        _cliManager.ClearTerminalHistory(_currentCliId);
        if (_webView?.CoreWebView2 != null)
        {
            _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"clear\"}");
        }
    }

    public void SwitchCli(string cliId, bool syncToWebview = true)
    {
        if (string.IsNullOrEmpty(cliId)) return;
        _currentCliId = cliId;

        UpdateTabButtonsUi();

        if (_isInitialized && _webView?.CoreWebView2 != null)
        {
            _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"reset\"}");
            SendTerminalInit(_currentCliId);
            _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"fit\"}");
        }

        if (syncToWebview && _mainForm is MainForm mf && mf.DashboardBridge?.CliWebviewForm is CliWebviewForm cwf && !cwf.IsDisposed)
        {
            try
            {
                cwf.SwitchTab(cliId);
            }
            catch { }
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            string userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentsHub", "HudWebView");
            Directory.CreateDirectory(userDataDir);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _webView.EnsureCoreWebView2Async(env);

            // The HUD only ever renders the shipped terminal page, and that page posts keystrokes
            // to the host. Nothing else may load here.
            Helpers.WebViewSecurity.Apply(_webView.CoreWebView2, allowLoopback: false);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            string terminalHtml = Path.Combine(AppContext.BaseDirectory, "ui", "terminal.html");
            if (File.Exists(terminalHtml))
            {
                string theme = ThemeManager.CurrentTheme == AppTheme.Dark ? "dark" : "light";
                string accent = Uri.EscapeDataString(ThemeManager.AccentHex);
                string url = $"{new Uri(terminalHtml).AbsoluteUri}?theme={theme}&accent={accent}";
                _webView.CoreWebView2.Navigate(url);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HudTerminalForm] WebView2 init error: {ex.Message}");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
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
                        _isInitialized = true;
                        SendTerminalInit(_currentCliId);
                        break;

                    case "input":
                        if (doc.RootElement.TryGetProperty("data", out var dataEl))
                        {
                            var input = dataEl.GetString();
                            if (input != null && !string.IsNullOrEmpty(_currentCliId))
                            {
                                _cliManager.SendInput(_currentCliId, input);
                            }
                        }
                        break;

                    case "resize":
                        if (doc.RootElement.TryGetProperty("cols", out var colsEl) &&
                            doc.RootElement.TryGetProperty("rows", out var rowsEl))
                        {
                            short cols = colsEl.GetInt16();
                            short rows = rowsEl.GetInt16();
                            if (!string.IsNullOrEmpty(_currentCliId))
                            {
                                _cliManager.Resize(_currentCliId, cols, rows);
                            }
                        }
                        break;

                    case "start":
                        if (!string.IsNullOrEmpty(_currentCliId))
                        {
                            _cliManager.Start(_currentCliId);
                        }
                        break;
                }
            }
        }
        catch { }
    }

    private void SendTerminalInit(string cliId)
    {
        if (_cliManager == null || _webView.CoreWebView2 == null || string.IsNullOrEmpty(cliId)) return;

        try
        {
            var info = _cliManager.GetInfo(cliId);
            var history = _cliManager.GetTerminalHistory(cliId);

            string displayName = CliManagerService.IsAntigravityProfileId(cliId, out var prof)
                ? $"Antigravity ({prof})"
                : info.ShortName;

            var payload = new CliWebviewForm.TerminalInitPayload(
                "init",
                displayName,
                info.State == CliState.Running,
                info.StatusText,
                history
            );
            string json = JsonSerializer.Serialize(payload, CliWebviewForm.CliJsonContext.Default.TerminalInitPayload);
            _webView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch { }
    }

    private void OnTerminalDataReceived(string id, string text)
    {
        if (IsDisposed || string.IsNullOrEmpty(text)) return;
        if (!string.Equals(id, _currentCliId, StringComparison.OrdinalIgnoreCase)) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ForwardTerminalData(text)));
        }
        else
        {
            ForwardTerminalData(text);
        }
    }

    private void ForwardTerminalData(string text)
    {
        try
        {
            if (_webView.CoreWebView2 != null)
            {
                var payload = new CliWebviewForm.TerminalOutputPayload("output", text);
                string json = JsonSerializer.Serialize(payload, CliWebviewForm.CliJsonContext.Default.TerminalOutputPayload);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
        }
        catch { }
    }

    private void OnCliStatusChanged(string id, CliState state, string message)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => HandleStatusChanged(id, state, message)));
        }
        else
        {
            HandleStatusChanged(id, state, message);
        }
    }

    private void HandleStatusChanged(string id, CliState state, string message)
    {
        UpdateTabButtonsUi();
        if (string.Equals(id, _currentCliId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (_webView.CoreWebView2 != null)
                {
                    var payload = new CliWebviewForm.TerminalStatusPayload("status", state == CliState.Running, message);
                    string json = JsonSerializer.Serialize(payload, CliWebviewForm.CliJsonContext.Default.TerminalStatusPayload);
                    _webView.CoreWebView2.PostWebMessageAsJson(json);
                }
            }
            catch { }
        }
    }

    public void Toggle()
    {
        if (_isOpen)
        {
            SlideClose();
        }
        else
        {
            SlideOpen();
        }
    }

    public void SlideOpen()
    {
        if (_isOpen) return;

        UpdateHintLabel();

        // Sync active CLI from CliWebviewForm if available
        if (_mainForm is MainForm mf && mf.DashboardBridge?.CliWebviewForm is CliWebviewForm cwf && !cwf.IsDisposed)
        {
            string cwfActiveTab = cwf.ActiveTabId;
            if (!string.IsNullOrEmpty(cwfActiveTab))
            {
                _currentCliId = cwfActiveTab;
            }
        }

        RebuildTabs();

        if (_isInitialized && _webView?.CoreWebView2 != null)
        {
            _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"reset\"}");
            SendTerminalInit(_currentCliId);
            _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"fit\"}");
        }

        var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        Left = screen.Left + (screen.Width - Width) / 2;
        _targetY = screen.Top;
        _hiddenY = screen.Top - Height - 20;

        Top = _hiddenY;
        _isOpen = true;
        _isSlidingDown = true;
        Show();
        BringToFront();
        Activate();
        _animTimer.Start();
    }

    public void SlideClose()
    {
        if (!_isOpen) return;
        _isSlidingDown = false;
        _animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_isSlidingDown)
        {
            int diff = _targetY - Top;
            int step = Math.Max(12, diff / 3);
            if (diff <= step)
            {
                Top = _targetY;
                _animTimer.Stop();
                _webView.Focus();
            }
            else
            {
                Top += step;
            }
        }
        else
        {
            int diff = Top - _hiddenY;
            int step = Math.Max(16, diff / 3);
            if (diff <= step)
            {
                Top = _hiddenY;
                _animTimer.Stop();
                _isOpen = false;
                Hide();
            }
            else
            {
                Top -= step;
            }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape || keyData == Keys.F12)
        {
            SlideClose();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(16, 185, 129), 2);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_cliManager != null)
            {
                _cliManager.TerminalDataReceivedId -= OnTerminalDataReceived;
                _cliManager.StatusChangedId -= OnCliStatusChanged;
            }
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _animTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
