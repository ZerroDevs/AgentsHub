using System.IO;
using AgentsHub.Helpers;
using AgentsHub.Services;

namespace AgentsHub;

public class ManageClisForm : Form
{
    private readonly FlowLayoutPanel _listPanel;
    private readonly Button _addCliButton;
    private readonly Button _closeButton;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Panel _bottomPanel;

    public event Action? ConfigurationChanged;

    public ManageClisForm()
    {
        Text = "Manage CLI Workspaces";
        Size = new Size(580, 520);
        MinimumSize = new Size(500, 400);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _titleLabel = new Label
        {
            Text = "CLI Workspaces",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            Location = new Point(20, 16),
            AutoSize = true
        };

        _subtitleLabel = new Label
        {
            Text = "Enable or disable workspaces. Default CLIs cannot be removed, but can be hidden.",
            Font = new Font("Segoe UI", 8.75F),
            Location = new Point(20, 42),
            AutoSize = true
        };

        _listPanel = new FlowLayoutPanel
        {
            Location = new Point(20, 72),
            Size = new Size(524, 340),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };

        _bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60
        };

        _addCliButton = new Button
        {
            Text = "+ Add New CLI",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Size = new Size(130, 34),
            Location = new Point(20, 12),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _addCliButton.FlatAppearance.BorderSize = 1;
        _addCliButton.Click += (_, _) => ShowAddEditDialog(null);

        _closeButton = new Button
        {
            Text = "Done",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Size = new Size(90, 34),
            Location = new Point(454, 12),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.OK
        };
        _closeButton.FlatAppearance.BorderSize = 1;
        _closeButton.Click += (_, _) => Close();

        _bottomPanel.Controls.Add(_addCliButton);
        _bottomPanel.Controls.Add(_closeButton);

        Controls.Add(_titleLabel);
        Controls.Add(_subtitleLabel);
        Controls.Add(_listPanel);
        Controls.Add(_bottomPanel);

        ApplyTheme();
        LoadCliList();

        ThemeManager.ThemeChanged += () =>
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(() =>
            {
                ApplyTheme();
                LoadCliList();
            });
            else
            {
                ApplyTheme();
                LoadCliList();
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ThemeManager.ApplyDwmTitleBarTheme(Handle);
    }

    public void ApplyTheme()
    {
        var c = ThemeManager.Colors;
        BackColor = c.WindowBackground;
        ForeColor = c.TextPrimary;

        _titleLabel.ForeColor = c.TextPrimary;
        _subtitleLabel.ForeColor = c.TextMuted;
        _listPanel.BackColor = c.WindowBackground;
        _bottomPanel.BackColor = c.HeaderBackground;

        _addCliButton.BackColor = c.AccentGreen;
        _addCliButton.ForeColor = c.AccentGreenText;
        _addCliButton.FlatAppearance.BorderColor = c.AccentGreen;

        _closeButton.BackColor = c.ButtonBg;
        _closeButton.ForeColor = c.TextPrimary;
        _closeButton.FlatAppearance.BorderColor = c.ButtonBorder;

        ThemeManager.ApplyDwmTitleBarTheme(Handle);
    }

    public void LoadCliList()
    {
        _listPanel.SuspendLayout();
        _listPanel.Controls.Clear();

        var c = ThemeManager.Colors;
        var workspaces = AiConfigService.Instance.GetCliWorkspaces();

        int cardWidth = _listPanel.ClientSize.Width - 28;
        if (cardWidth < 350) cardWidth = 470;

        foreach (var ws in workspaces)
        {
            var card = new Panel
            {
                Size = new Size(cardWidth, 68),
                BackColor = c.CardBackground,
                Margin = new Padding(0, 0, 0, 10)
            };

            card.Paint += (s, e) =>
            {
                using var pen = new Pen(c.CardBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            var chk = new CheckBox
            {
                Text = "",
                Checked = ws.IsEnabled,
                Location = new Point(14, 24),
                Size = new Size(20, 20),
                Cursor = Cursors.Hand
            };
            chk.CheckedChanged += (_, _) =>
            {
                AiConfigService.Instance.ToggleCliWorkspace(ws.Id, chk.Checked);
                ConfigurationChanged?.Invoke();
            };

            var nameLabel = new Label
            {
                Text = ws.Name,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = ws.IsEnabled ? c.TextPrimary : c.TextMuted,
                Location = new Point(42, 12),
                AutoSize = true
            };

            var badge = new Label
            {
                Text = ws.IsDefault ? "DEFAULT" : "CUSTOM",
                Font = new Font("Segoe UI", 7F, FontStyle.Bold),
                ForeColor = ws.IsDefault ? c.AccentIndigo : c.AccentGreen,
                BackColor = c.WellBackground,
                Location = new Point(nameLabel.Right + 10, 14),
                Size = new Size(58, 16),
                TextAlign = ContentAlignment.MiddleCenter
            };
            badge.Paint += (s, e) =>
            {
                using var p = new Pen(ws.IsDefault ? c.AccentIndigo : c.AccentGreen, 1);
                e.Graphics.DrawRectangle(p, 0, 0, badge.Width - 1, badge.Height - 1);
            };

            var cmdLabel = new Label
            {
                Text = $"Command: {ws.Command}" + (ws.Port.HasValue ? $" | Port: {ws.Port.Value}" : ""),
                Font = new Font("Segoe UI", 8F),
                ForeColor = c.TextMuted,
                Location = new Point(42, 36),
                Size = new Size(cardWidth - 180, 20),
                AutoEllipsis = true
            };

            card.Controls.Add(chk);
            card.Controls.Add(nameLabel);
            card.Controls.Add(badge);
            card.Controls.Add(cmdLabel);

            if (!ws.IsDefault)
            {
                var editBtn = new Button
                {
                    Text = "Edit",
                    Font = new Font("Segoe UI", 8F),
                    Size = new Size(46, 26),
                    Location = new Point(cardWidth - 110, 20),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = c.ButtonBg,
                    ForeColor = c.TextPrimary,
                    Cursor = Cursors.Hand
                };
                editBtn.FlatAppearance.BorderColor = c.ButtonBorder;
                editBtn.Click += (_, _) => ShowAddEditDialog(ws);

                var delBtn = new Button
                {
                    Text = "Delete",
                    Font = new Font("Segoe UI", 8F),
                    Size = new Size(54, 26),
                    Location = new Point(cardWidth - 60, 20),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = c.ButtonBg,
                    ForeColor = c.DangerRed,
                    Cursor = Cursors.Hand
                };
                delBtn.FlatAppearance.BorderColor = c.CardBorder;
                delBtn.Click += (_, _) =>
                {
                    if (MessageBox.Show($"Remove custom CLI '{ws.Name}'?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        AiConfigService.Instance.DeleteCliWorkspace(ws.Id);
                        LoadCliList();
                        ConfigurationChanged?.Invoke();
                    }
                };

                card.Controls.Add(editBtn);
                card.Controls.Add(delBtn);
            }
            else
            {
                var lockLabel = new Label
                {
                    Text = "Default (Protected)",
                    Font = new Font("Segoe UI", 8F),
                    ForeColor = c.TextMuted,
                    Location = new Point(cardWidth - 120, 24),
                    Size = new Size(110, 20),
                    TextAlign = ContentAlignment.MiddleRight
                };
                card.Controls.Add(lockLabel);
            }

            _listPanel.Controls.Add(card);
        }

        _listPanel.ResumeLayout(true);
    }

    private void ShowAddEditDialog(CliWorkspaceConfig? existing)
    {
        var c = ThemeManager.Colors;
        using var dlg = new Form
        {
            Text = existing == null ? "Add Custom CLI Workspace" : "Edit CLI Workspace",
            Size = new Size(480, 360),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = c.WindowBackground,
            ForeColor = c.TextPrimary
        };

        ThemeManager.ApplyDwmTitleBarTheme(dlg.Handle);

        var lblName = new Label { Text = "Workspace Name:", Location = new Point(24, 20), AutoSize = true, ForeColor = c.TextPrimary };
        var txtName = new TextBox { Location = new Point(24, 42), Size = new Size(416, 24), BackColor = c.WellBackground, ForeColor = c.TextPrimary, Text = existing?.Name ?? "" };

        var lblCmd = new Label { Text = "Command or Executable (e.g. aider, ollama run llama3):", Location = new Point(24, 76), AutoSize = true, ForeColor = c.TextPrimary };
        var txtCmd = new TextBox { Location = new Point(24, 98), Size = new Size(416, 24), BackColor = c.WellBackground, ForeColor = c.TextPrimary, Text = existing?.Command ?? "" };

        var lblDir = new Label { Text = "Working Directory (optional):", Location = new Point(24, 132), AutoSize = true, ForeColor = c.TextPrimary };
        var txtDir = new TextBox { Location = new Point(24, 154), Size = new Size(330, 24), BackColor = c.WellBackground, ForeColor = c.TextPrimary, Text = existing?.WorkingDirectory ?? "" };
        var btnBrowse = new Button { Text = "Browse", Location = new Point(362, 153), Size = new Size(78, 26), FlatStyle = FlatStyle.Flat, BackColor = c.ButtonBg, ForeColor = c.TextPrimary, Cursor = Cursors.Hand };
        btnBrowse.FlatAppearance.BorderColor = c.ButtonBorder;
        btnBrowse.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Select Working Directory" };
            if (fbd.ShowDialog(dlg) == DialogResult.OK)
            {
                txtDir.Text = fbd.SelectedPath;
            }
        };

        var lblPort = new Label { Text = "Browser Port (optional, if web UI):", Location = new Point(24, 188), AutoSize = true, ForeColor = c.TextPrimary };
        var txtPort = new TextBox { Location = new Point(24, 210), Size = new Size(140, 24), BackColor = c.WellBackground, ForeColor = c.TextPrimary, Text = existing?.Port?.ToString() ?? "" };

        var chkWeb = new CheckBox { Text = "Launch as Web Interface (HTTP/Localhost)", Location = new Point(180, 212), AutoSize = true, Checked = existing?.IsWebUi ?? false, ForeColor = c.TextPrimary };

        var btnSave = new Button { Text = "Save Workspace", Location = new Point(220, 265), Size = new Size(130, 32), FlatStyle = FlatStyle.Flat, BackColor = c.AccentGreen, ForeColor = c.AccentGreenText, DialogResult = DialogResult.OK, Cursor = Cursors.Hand };
        btnSave.FlatAppearance.BorderSize = 0;

        var btnCancel = new Button { Text = "Cancel", Location = new Point(360, 265), Size = new Size(80, 32), FlatStyle = FlatStyle.Flat, BackColor = c.ButtonBg, ForeColor = c.TextPrimary, DialogResult = DialogResult.Cancel, Cursor = Cursors.Hand };
        btnCancel.FlatAppearance.BorderColor = c.ButtonBorder;

        dlg.Controls.AddRange([lblName, txtName, lblCmd, txtCmd, lblDir, txtDir, btnBrowse, lblPort, txtPort, chkWeb, btnSave, btnCancel]);

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var name = txtName.Text.Trim();
            var cmd = txtCmd.Text.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cmd))
            {
                MessageBox.Show("Please specify both a name and a command.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int? port = int.TryParse(txtPort.Text.Trim(), out var p) ? p : null;

            var item = existing ?? new CliWorkspaceConfig();
            item.Name = name;
            item.Command = cmd;
            item.WorkingDirectory = txtDir.Text.Trim();
            item.Port = port;
            item.IsWebUi = chkWeb.Checked;
            item.IsEnabled = true;

            AiConfigService.Instance.SaveCliWorkspace(item);
            LoadCliList();
            ConfigurationChanged?.Invoke();
        }
    }
}
