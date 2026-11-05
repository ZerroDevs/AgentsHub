#nullable enable

namespace AgentsHub;

partial class CliWebviewForm
{
    private System.ComponentModel.IContainer? components = null;

    private Panel topToolbar = null!;
    private HorizontalTabsPanel tabsPanel = null!;

    private Panel toolsPanel = null!;
    private Label activeStatusLabel = null!;
    private Button toggleRunButton = null!;
    private Button restartCliButton = null!;
    private Button clearTerminalButton = null!;
    private Button openBrowserButton = null!;
    private Button toggleMainWindowButton = null!;
    private Button toggleSplitButton = null!;

    private Panel contentPanel = null!;
    private SplitContainer splitWorkspaceContainer = null!;
    private Panel panelLeft = null!;
    private Panel panelRight = null!;
    private Panel paneHeaderLeft = null!;
    private Panel paneHeaderRight = null!;
    private Label paneTitleLeft = null!;
    private Label paneTitleRight = null!;
    private ComboBox paneComboLeft = null!;
    private ComboBox paneComboRight = null!;
    private Button paneCloseRightButton = null!;
    private Panel paneHostLeft = null!;
    private Panel paneHostRight = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        if (disposing)
        {
            _tabContextMenu?.Dispose();
            _mainWindowContextMenu?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        topToolbar = new Panel();
        tabsPanel = new HorizontalTabsPanel();

        toolsPanel = new Panel();
        activeStatusLabel = new Label();
        toggleRunButton = new Button();
        restartCliButton = new Button();
        clearTerminalButton = new Button();
        openBrowserButton = new Button();
        toggleSplitButton = new Button();
        toggleMainWindowButton = new Button();

        contentPanel = new Panel();
        splitWorkspaceContainer = new SplitContainer();
        panelLeft = new Panel();
        panelRight = new Panel();
        paneHeaderLeft = new Panel();
        paneHeaderRight = new Panel();
        paneTitleLeft = new Label();
        paneTitleRight = new Label();
        paneComboLeft = new ComboBox();
        paneComboRight = new ComboBox();
        paneCloseRightButton = new Button();
        paneHostLeft = new Panel();
        paneHostRight = new Panel();

        SuspendLayout();

        // 
        // Top Toolbar Panel
        // 
        topToolbar.Dock = DockStyle.Top;
        topToolbar.Height = 46;
        topToolbar.Controls.Add(toolsPanel);
        topToolbar.Controls.Add(tabsPanel);

        // 
        // Tabs Panel (docked left)
        // 
        tabsPanel.Dock = DockStyle.Left;
        tabsPanel.Width = 380;

        // 
        // Tools Panel (fills remainder of toolbar)
        // 
        toolsPanel.Dock = DockStyle.Fill;
        toolsPanel.Controls.Add(activeStatusLabel);
        toolsPanel.Controls.Add(toggleRunButton);
        toolsPanel.Controls.Add(restartCliButton);
        toolsPanel.Controls.Add(clearTerminalButton);
        toolsPanel.Controls.Add(openBrowserButton);
        toolsPanel.Controls.Add(toggleSplitButton);
        toolsPanel.Controls.Add(toggleMainWindowButton);

        activeStatusLabel.AutoSize = true;
        activeStatusLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        activeStatusLabel.Location = new Point(8, 15);
        activeStatusLabel.Text = "Status: Offline";

        toggleRunButton.Size = new Size(32, 32);
        toggleRunButton.FlatStyle = FlatStyle.Flat;
        toggleRunButton.FlatAppearance.BorderSize = 1;
        toggleRunButton.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        toggleRunButton.Text = "";
        toggleRunButton.Cursor = Cursors.Hand;

        restartCliButton.Size = new Size(32, 32);
        restartCliButton.FlatStyle = FlatStyle.Flat;
        restartCliButton.FlatAppearance.BorderSize = 1;
        restartCliButton.Font = new Font("Segoe UI", 9F);
        restartCliButton.Text = "";
        restartCliButton.Cursor = Cursors.Hand;

        clearTerminalButton.Size = new Size(32, 32);
        clearTerminalButton.FlatStyle = FlatStyle.Flat;
        clearTerminalButton.FlatAppearance.BorderSize = 1;
        clearTerminalButton.Font = new Font("Segoe UI", 9F);
        clearTerminalButton.Text = "";
        clearTerminalButton.Cursor = Cursors.Hand;

        openBrowserButton.Size = new Size(32, 32);
        openBrowserButton.FlatStyle = FlatStyle.Flat;
        openBrowserButton.FlatAppearance.BorderSize = 1;
        openBrowserButton.Font = new Font("Segoe UI", 9F);
        openBrowserButton.Text = "";
        openBrowserButton.Cursor = Cursors.Hand;

        toggleSplitButton.Size = new Size(32, 32);
        toggleSplitButton.FlatStyle = FlatStyle.Flat;
        toggleSplitButton.FlatAppearance.BorderSize = 1;
        toggleSplitButton.Font = new Font("Segoe UI", 9F);
        toggleSplitButton.Text = "";
        toggleSplitButton.Cursor = Cursors.Hand;

        toggleMainWindowButton.Size = new Size(32, 32);
        toggleMainWindowButton.FlatStyle = FlatStyle.Flat;
        toggleMainWindowButton.FlatAppearance.BorderSize = 1;
        toggleMainWindowButton.Font = new Font("Segoe UI", 9F);
        toggleMainWindowButton.Text = "";
        toggleMainWindowButton.Cursor = Cursors.Hand;

        // 
        // Content Panel (docked fill)
        // 
        contentPanel.Dock = DockStyle.Fill;
        contentPanel.Controls.Add(splitWorkspaceContainer);

        // 
        // Split Workspace Container
        // 
        splitWorkspaceContainer.Dock = DockStyle.Fill;
        splitWorkspaceContainer.Orientation = Orientation.Vertical;
        splitWorkspaceContainer.SplitterWidth = 6;
        splitWorkspaceContainer.Panel1MinSize = 25;
        splitWorkspaceContainer.Panel2MinSize = 25;
        splitWorkspaceContainer.Panel2Collapsed = true;

        // Left Pane Panel
        panelLeft.Dock = DockStyle.Fill;
        paneHeaderLeft.Dock = DockStyle.Top;
        paneHeaderLeft.Height = 32;
        paneHeaderLeft.Visible = false;
        paneTitleLeft.AutoSize = true;
        paneTitleLeft.Location = new Point(8, 7);
        paneTitleLeft.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        paneTitleLeft.Text = "Left:";
        paneComboLeft.DropDownStyle = ComboBoxStyle.DropDownList;
        paneComboLeft.FlatStyle = FlatStyle.Flat;
        paneComboLeft.Font = new Font("Segoe UI", 8.25F);
        paneComboLeft.Location = new Point(48, 4);
        paneComboLeft.Size = new Size(180, 24);
        paneComboLeft.Cursor = Cursors.Hand;
        paneHeaderLeft.Controls.Add(paneTitleLeft);
        paneHeaderLeft.Controls.Add(paneComboLeft);
        paneHostLeft.Dock = DockStyle.Fill;
        panelLeft.Controls.Add(paneHostLeft);
        panelLeft.Controls.Add(paneHeaderLeft);
        paneHeaderLeft.BringToFront();
        splitWorkspaceContainer.Panel1.Controls.Add(panelLeft);

        // Right Pane Panel
        panelRight.Dock = DockStyle.Fill;
        paneHeaderRight.Dock = DockStyle.Top;
        paneHeaderRight.Height = 32;
        paneHeaderRight.Visible = false;
        paneTitleRight.AutoSize = true;
        paneTitleRight.Location = new Point(8, 7);
        paneTitleRight.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        paneTitleRight.Text = "Right:";
        paneComboRight.DropDownStyle = ComboBoxStyle.DropDownList;
        paneComboRight.FlatStyle = FlatStyle.Flat;
        paneComboRight.Font = new Font("Segoe UI", 8.25F);
        paneComboRight.Location = new Point(56, 4);
        paneComboRight.Size = new Size(180, 24);
        paneComboRight.Cursor = Cursors.Hand;
        paneCloseRightButton.Size = new Size(24, 24);
        paneCloseRightButton.Location = new Point(244, 4);
        paneCloseRightButton.FlatStyle = FlatStyle.Flat;
        paneCloseRightButton.FlatAppearance.BorderSize = 0;
        paneCloseRightButton.Text = "✕";
        paneCloseRightButton.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
        paneCloseRightButton.Cursor = Cursors.Hand;
        paneHeaderRight.Controls.Add(paneTitleRight);
        paneHeaderRight.Controls.Add(paneComboRight);
        paneHeaderRight.Controls.Add(paneCloseRightButton);
        paneHostRight.Dock = DockStyle.Fill;
        panelRight.Controls.Add(paneHostRight);
        panelRight.Controls.Add(paneHeaderRight);
        paneHeaderRight.BringToFront();
        splitWorkspaceContainer.Panel2.Controls.Add(panelRight);

        // 
        // Form Properties
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1020, 680);
        Controls.Add(contentPanel);
        Controls.Add(topToolbar);
        topToolbar.BringToFront();
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowIcon = true;
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Agents Hub - CLI Terminal Workspace";

        ResumeLayout(false);
    }
}