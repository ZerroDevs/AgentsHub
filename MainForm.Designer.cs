#nullable enable

namespace AgentsHub;

partial class MainForm
{
    private System.ComponentModel.IContainer? components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            _dashboardWebBridge?.Dispose();
            Services.Sidecar.SidecarHostService.Instance.Dispose();
            _notifyIcon?.Dispose();
            _trayContextMenu?.Dispose();
            _appIcon?.Dispose();
            _trayIcon?.Dispose();
            _trayRunningIcon?.Dispose();
            _trayWarnIcon?.Dispose();
            _trayOfflineIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        SuspendLayout();

        // MainForm properties
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(960, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Agents Hub - Multi-Agent Desktop Hub and CLI Workspace";
        Name = "MainForm";
        DoubleBuffered = true;

        ResumeLayout(false);
    }
}
