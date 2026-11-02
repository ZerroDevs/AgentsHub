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

    private Task HandleGetRepoManagerStateAsync(string? requestId)
    {
        {
            var repos = GitRepositoryManagerService.Instance.GetRepositories();
            var settings = GitRepositoryManagerService.Instance.GetSettings();
            var workingProjectId = GitRepositoryManagerService.Instance.GetWorkingProjectId();
            var isScanning = GitRepositoryManagerService.Instance.IsScanning();
            var lastProgress = GitRepositoryManagerService.Instance.GetLastProgress();
            SendResponse(requestId, true, new
            {
                repositories = repos,
                settings,
                workingProjectId,
                isScanning,
                lastProgress
            });
        }

        return Task.CompletedTask;
    }

    private async Task HandleScanRepositoriesAsync(string? requestId)
    {
        bool started = await GitRepositoryManagerService.Instance.StartScanAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { success = started });
    }

    private Task HandleCancelRepoScanAsync(string? requestId)
    {
        {
            GitRepositoryManagerService.Instance.CancelScan();
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleSaveRepoSettingsAsync(JsonElement data, string? requestId)
    {
        {
            if (data.TryGetProperty("settings", out var sProp))
            {
                var cfg = JsonSerializer.Deserialize<RepositoryManagerSettings>(sProp.GetRawText(), CaseInsensitiveJsonOptions);
                if (cfg != null)
                {
                    GitRepositoryManagerService.Instance.SaveSettings(cfg);
                    SendResponse(requestId, true, new { success = true, settings = cfg });
                    return Task.CompletedTask;
                }
            }
            SendResponse(requestId, false, "Invalid settings payload");
        }

        return Task.CompletedTask;
    }

    private Task HandleUpdateRepoMetadataAsync(JsonElement data, string? requestId)
    {
        {
            string? id = data.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (!string.IsNullOrEmpty(id))
            {
                string? status = data.TryGetProperty("status", out var repoStatusProp) ? repoStatusProp.GetString() : null;
                string? focus = data.TryGetProperty("focus", out var fcProp) ? fcProp.GetString() : null;
                bool? pinned = data.TryGetProperty("pinned", out var pinProp) ? pinProp.GetBoolean() : null;
                string? notes = data.TryGetProperty("notes", out var ntProp) ? ntProp.GetString() : null;
                List<string>? tags = data.TryGetProperty("tags", out var repoTagsProp) && repoTagsProp.ValueKind == JsonValueKind.Array
                    ? JsonSerializer.Deserialize<List<string>>(repoTagsProp.GetRawText(), CaseInsensitiveJsonOptions)
                    : null;

                GitRepositoryManagerService.Instance.UpdateRepositoryMetadata(id, status, focus, pinned, tags, notes);
                SendResponse(requestId, true, new { success = true });
            }
            else
            {
                SendResponse(requestId, false, "Repository ID is required");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleGetRepoNotesAsync(JsonElement data, string? requestId)
    {
        {
            string? repoId = data.TryGetProperty("repoId", out var rIdProp) ? rIdProp.GetString() : null;
            if (!string.IsNullOrEmpty(repoId))
            {
                var notes = GitRepositoryManagerService.Instance.GetRepoNotes(repoId);
                SendResponse(requestId, true, new { notes });
            }
            else
            {
                SendResponse(requestId, false, "Repository ID is required");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleSaveRepoNotesAsync(JsonElement data, string? requestId)
    {
        {
            string? repoId = data.TryGetProperty("repoId", out var rIdProp) ? rIdProp.GetString() : null;
            string? notes = data.TryGetProperty("notes", out var ntProp) ? ntProp.GetString() : "";
            if (!string.IsNullOrEmpty(repoId))
            {
                GitRepositoryManagerService.Instance.SaveRepoNotes(repoId, notes ?? "");
                SendResponse(requestId, true, new { success = true });
            }
            else
            {
                SendResponse(requestId, false, "Repository ID is required");
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleGetRepoChangesAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
        if (!string.IsNullOrEmpty(repoPath))
        {
            var changes = await GitRepositoryManagerService.Instance.GetWorkingTreeChangesAsync(repoPath).ConfigureAwait(false);
            SendResponse(requestId, true, new { changes });
        }
        else
        {
            SendResponse(requestId, false, "Repository path is required");
        }
    }

    private async Task HandleGetRepoDiffAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
        string? relativePath = data.TryGetProperty("relativePath", out var relProp) ? relProp.GetString() : null;
        bool staged = data.TryGetProperty("staged", out var stgProp) && stgProp.GetBoolean();

        if (!string.IsNullOrEmpty(repoPath) && !string.IsNullOrEmpty(relativePath))
        {
            var diff = await GitRepositoryManagerService.Instance.GetFileDiffAsync(repoPath, relativePath, staged).ConfigureAwait(false);
            SendResponse(requestId, true, diff);
        }
        else
        {
            SendResponse(requestId, false, "Repository path and relative file path are required");
        }
    }

    private async Task HandleStageRepoChangesAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
        bool unstage = data.TryGetProperty("unstage", out var unstgProp) && unstgProp.GetBoolean();
        var files = data.TryGetProperty("files", out var fProp) && fProp.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<List<string>>(fProp.GetRawText(), CaseInsensitiveJsonOptions) ?? []
            : [];

        if (!string.IsNullOrEmpty(repoPath))
        {
            var (success, output) = await GitRepositoryManagerService.Instance.StageChangesAsync(repoPath, files, unstage).ConfigureAwait(false);
            SendResponse(requestId, success, new { success, output });
        }
        else
        {
            SendResponse(requestId, false, "Repository path is required");
        }
    }

    private async Task HandleCreateRepoCommitAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
        string? message = data.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
        bool stageAll = data.TryGetProperty("stageAll", out var saProp) && saProp.GetBoolean();
        bool pushToRemote = data.TryGetProperty("pushToRemote", out var ptrProp) && ptrProp.GetBoolean();

        if (!string.IsNullOrEmpty(repoPath) && !string.IsNullOrEmpty(message))
        {
            var (success, output, pushed, pushOutput) = await GitRepositoryManagerService.Instance.CreateCommitAsync(repoPath, message, stageAll, pushToRemote).ConfigureAwait(false);
            SendResponse(requestId, success, new { success, output, pushed, pushOutput });
        }
        else
        {
            SendResponse(requestId, false, "Repository path and commit message are required");
        }
    }

    private async Task HandleRepoNetworkActionAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
        string? op = data.TryGetProperty("operation", out var opProp) ? opProp.GetString() : "fetch";
        string? remote = data.TryGetProperty("remote", out var remProp) ? remProp.GetString() : "origin";
        string? branch = data.TryGetProperty("branch", out var brProp) ? brProp.GetString() : null;
        bool setUpstream = data.TryGetProperty("setUpstream", out var suProp) && suProp.GetBoolean();

        if (!string.IsNullOrEmpty(repoPath))
        {
            var (success, output) = await GitRepositoryManagerService.Instance.NetworkActionAsync(repoPath, op ?? "fetch", remote ?? "origin", branch, setUpstream).ConfigureAwait(false);
            SendResponse(requestId, success, new { success, output });
        }
        else
        {
            SendResponse(requestId, false, "Repository path is required");
        }
    }

    private async Task HandleSwitchRepoBranchAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
        string? branch = data.TryGetProperty("branch", out var brProp) ? brProp.GetString() : null;

        if (!string.IsNullOrEmpty(repoPath) && !string.IsNullOrEmpty(branch))
        {
            var (success, output) = await GitRepositoryManagerService.Instance.SwitchBranchAsync(repoPath, branch).ConfigureAwait(false);
            SendResponse(requestId, success, new { success, output });
        }
        else
        {
            SendResponse(requestId, false, "Repository path and branch are required");
        }
    }

    private async Task HandleListRepoBranchesAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
        if (!string.IsNullOrEmpty(repoPath))
        {
            var branches = await GitRepositoryManagerService.Instance.ListBranchesAsync(repoPath).ConfigureAwait(false);
            SendResponse(requestId, true, new { branches });
        }
        else
        {
            SendResponse(requestId, false, "Repository path is required");
        }
    }

    private Task HandleDetectRepoLaunchersAsync(JsonElement data, string? requestId)
    {
        {
            string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
            if (!string.IsNullOrEmpty(repoPath))
            {
                var launchers = GitRepositoryManagerService.Instance.DetectLaunchers(repoPath);
                SendResponse(requestId, true, new { launchers });
            }
            else
            {
                SendResponse(requestId, false, "Repository path is required");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleRunRepoLauncherAsync(JsonElement data, string? requestId)
    {
        {
            string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
            if (!string.IsNullOrEmpty(repoPath) && data.TryGetProperty("launcher", out var lProp))
            {
                var item = JsonSerializer.Deserialize<ProjectLauncherItem>(lProp.GetRawText(), CaseInsensitiveJsonOptions);
                if (item != null)
                {
                    bool executed = GitRepositoryManagerService.Instance.ExecuteLauncher(repoPath, item);
                    SendResponse(requestId, executed, new { success = executed });
                    return Task.CompletedTask;
                }
            }
            SendResponse(requestId, false, "Invalid launcher payload");
        }

        return Task.CompletedTask;
    }

    private Task HandleEvaluateRepoHealthAsync(JsonElement data, string? requestId)
    {
        {
            string? idOrPath = data.TryGetProperty("idOrPath", out var idpProp) ? idpProp.GetString() : null;
            if (!string.IsNullOrEmpty(idOrPath))
            {
                var repo = GitRepositoryManagerService.Instance.GetRepository(idOrPath);
                if (repo != null)
                {
                    var health = GitRepositoryManagerService.Instance.EvaluateHealth(repo);
                    SendResponse(requestId, true, health);
                }
                else
                {
                    SendResponse(requestId, false, "Repository not found in registry");
                }
            }
            else
            {
                SendResponse(requestId, false, "Repository id or path is required");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleExportRepoInventoryAsync(JsonElement data, string? requestId)
    {
        {
            string? format = data.TryGetProperty("format", out var fmtProp) ? fmtProp.GetString() : "markdown";
            var exported = GitRepositoryManagerService.Instance.ExportInventory(format ?? "markdown");
            SendResponse(requestId, true, new { format = format ?? "markdown", content = exported });
        }

        return Task.CompletedTask;
    }

    private Task HandleOpenRepoInEditorAsync(JsonElement data, string? requestId)
    {
        {
            string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
            string? target = data.TryGetProperty("target", out var tgtProp) ? tgtProp.GetString() : "editor";

            if (!string.IsNullOrEmpty(repoPath) && Directory.Exists(repoPath))
            {
                try
                {
                    if (target == "explorer")
                    {
                        Process.Start(new ProcessStartInfo(
                            Services.Platform.PlatformHelper.IsWindows ? "explorer.exe"
                                : Services.Platform.PlatformHelper.IsMacOS ? "open" : "xdg-open",
                            $"\"{repoPath}\"") { UseShellExecute = true });
                        SendResponse(requestId, true, new { success = true, message = "Opened the folder" });
                    }
                    else if (target == "terminal")
                    {
                        // PowerShell ships with Windows only. The other platforms launch their own
                        // terminal in the checkout instead of a shell that is not installed there.
                        if (Services.Platform.PlatformHelper.IsWindows)
                        {
                            Process.Start(new ProcessStartInfo { FileName = "powershell.exe", WorkingDirectory = repoPath, UseShellExecute = true });
                        }
                        else if (Services.Platform.PlatformHelper.IsMacOS)
                        {
                            Process.Start(new ProcessStartInfo("open", $"-a Terminal \"{repoPath}\"") { UseShellExecute = true });
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo("x-terminal-emulator", string.Empty) { WorkingDirectory = repoPath, UseShellExecute = true });
                        }

                        SendResponse(requestId, true, new { success = true, message = "Opened in Terminal" });
                    }
                    else
                    {
                        string? specificEditor = (string.Equals(target, "editor", StringComparison.OrdinalIgnoreCase) || string.Equals(target, "ask", StringComparison.OrdinalIgnoreCase)) ? null : target;
                        var (success, msg) = GitRepositoryManagerService.Instance.OpenInEditor(repoPath, specificEditor);
                        SendResponse(requestId, success, new { success, message = msg });
                    }
                }
                catch (Exception ex)
                {
                    SendResponse(requestId, false, $"Failed to launch {target}: {ex.Message}");
                }
            }
            else
            {
                SendResponse(requestId, false, "Repository path is required or does not exist");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleBrowseThemeImageAsync(JsonElement data, string? requestId)
    {
        // Copies the picture into a folder MainForm maps to https://theme.agentshub.local, so the
        // page can paint it as a background without a data URL in browser storage.
        {
            string? chosenPath = null;
#if WINDOWS
            if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
            {
                f.Invoke(() =>
                {
                    using var ofd = new OpenFileDialog
                    {
                        Title = "Choose a background image",
                        Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|All files (*.*)|*.*",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                    };
                    if (ofd.ShowDialog(f) == DialogResult.OK)
                    {
                        chosenPath = ofd.FileName;
                    }
                });
            }
#endif

            if (string.IsNullOrWhiteSpace(chosenPath) || !File.Exists(chosenPath))
            {
                SendResponse(requestId, false, "Selection cancelled");
                return Task.CompletedTask;
            }

            try
            {
                string dir = ThemeImageFolder;
                Directory.CreateDirectory(dir);

                // One wallpaper at a time. Clearing the old files also busts the webview's
                // cache of the previous image, which a reused filename would keep showing.
                foreach (string stale in Directory.GetFiles(dir, "background.*"))
                {
                    try { File.Delete(stale); } catch (IOException) { }
                }

                string name = SaveWallpaper(chosenPath, dir);

                SendResponse(requestId, true, new
                {
                    url = $"https://theme.agentshub.local/{name}?v={DateTime.UtcNow.Ticks}",
                    name = Path.GetFileName(chosenPath),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SendResponse(requestId, false, $"Could not copy the image: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Longest side a wallpaper is allowed to keep. A window is at most a couple of megapixels.</summary>
    private const int WallpaperMaxWidth = 2560;

    /// <summary>
    /// Stores the chosen picture and returns the file name it was saved under.
    ///
    /// A 12-megapixel camera JPEG was copied in untouched and decoded on every launch to fill a window
    /// that never shows it at that size. Shrinking on import keeps the background crisp and cheap, and
    /// the person's own file is left exactly as it was. An animated GIF is copied as-is: re-encoding one
    /// frame would quietly turn an animation into a still.
    /// </summary>
    private static string SaveWallpaper(string source, string directory)
    {
        string extension = Path.GetExtension(source).ToLowerInvariant();
        string name = "background" + extension;
        string destination = Path.Combine(directory, name);

        if (extension is ".gif")
        {
            File.Copy(source, destination, overwrite: true);
            return name;
        }

#if WINDOWS
        try
        {
            using var image = Image.FromFile(source);
            bool smallEnough = image.Width <= WallpaperMaxWidth && new FileInfo(source).Length <= 3_000_000;

            if (smallEnough)
            {
                File.Copy(source, destination, overwrite: true);
                return name;
            }

            double scale = Math.Min(1.0, (double)WallpaperMaxWidth / image.Width);
            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(image.Height * scale));

            using var resized = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.DrawImage(image, 0, 0, width, height);
            }

            // JPEG, so the smaller copy is also the smaller file. A transparent background is not
            // something this layer can show anyway — the scrim sits on top of it.
            name = "background.jpg";
            resized.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Jpeg);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            // A format the decoder refuses is copied untouched: a big wallpaper beats a missing one.
        }
#endif

        File.Copy(source, destination, overwrite: true);
        return name;
    }

    private Task HandleBrowseEditorPathAsync(string? requestId)
    {
        {
            string? chosenPath = null;
#if WINDOWS
            if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
            {
                f.Invoke(() =>
                {
                    using var ofd = new OpenFileDialog
                    {
                        Title = "Select External Code Editor Executable",
                        Filter = "Executable files (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    };
                    if (ofd.ShowDialog(f) == DialogResult.OK)
                    {
                        chosenPath = ofd.FileName;
                    }
                });
            }
#endif

            if (!string.IsNullOrWhiteSpace(chosenPath))
            {
                SendResponse(requestId, true, new { path = chosenPath });
            }
            else
            {
                SendResponse(requestId, false, "Selection cancelled");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleGetInstalledEditorsAsync(string? requestId)
    {
        {
            var editors = GitRepositoryManagerService.Instance.GetInstalledEditors();
            var preferred = GitRepositoryManagerService.Instance.GetSettings().PreferredEditor;
            SendResponse(requestId, true, new { editors, preferred });
        }

        return Task.CompletedTask;
    }

    private Task HandleLaunchAgentInRepoAsync(JsonElement data, string? requestId)
    {
        {
            string? repoPath = data.TryGetProperty("repoPath", out var rPathProp) ? rPathProp.GetString() : null;
            string? cli = data.TryGetProperty("cliType", out var targetCliProp) ? targetCliProp.GetString() : "claude";

            if (!string.IsNullOrEmpty(repoPath) && Directory.Exists(repoPath))
            {
                try
                {
                    var cmd = cli?.ToLowerInvariant() switch
                    {
                        "antigravity" => "agy",
                        "claude" => "claude",
                        _ => "claude"
                    };

                    Process.Start(ShellInDirectory(repoPath, cmd));

                    SendResponse(requestId, true, new { success = true });
                }
                catch (Exception ex)
                {
                    SendResponse(requestId, false, $"Failed to start agent in repo: {ex.Message}");
                }
            }
            else
            {
                SendResponse(requestId, false, "Repository path is required");
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleGetCustomClisAsync(string? requestId)
    {
        {
            var clis = CustomCliService.Instance.GetCustomClis();
            SendResponse(requestId, true, new { clis });
        }

        return Task.CompletedTask;
    }

    private Task HandleSaveCustomCliAsync(JsonElement data, string? requestId)
    {
        {
            if (data.TryGetProperty("cli", out var cProp))
            {
                var c = JsonSerializer.Deserialize<CustomCliConfig>(cProp.GetRawText(), CaseInsensitiveJsonOptions);
                if (c != null)
                {
                    CustomCliService.Instance.SaveCustomCli(c);
                    SendResponse(requestId, true, new { success = true, cli = c, clis = CustomCliService.Instance.GetCustomClis() });
                    return Task.CompletedTask;
                }
            }
            SendResponse(requestId, false, "Invalid custom CLI payload");
        }

        return Task.CompletedTask;
    }

    private Task HandleDeleteCustomCliAsync(JsonElement data, string? requestId)
    {
        {
            string? id = data.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (!string.IsNullOrEmpty(id))
            {
                bool deleted = CustomCliService.Instance.DeleteCustomCli(id);
                SendResponse(requestId, true, new { success = deleted, clis = CustomCliService.Instance.GetCustomClis() });
            }
            else
            {
                SendResponse(requestId, false, "CLI ID is required");
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleRunSystemDiagnosticsAsync(string? requestId)
    {
        var diagnostics = await SystemDoctorService.Instance.RunDiagnosticsAsync().ConfigureAwait(false);
        SendResponse(requestId, true, new { diagnostics });
    }

    private async Task HandleAutofixDiagnosticIssueAsync(JsonElement data, string? requestId)
    {
        string? issueId = data.TryGetProperty("issueId", out var issProp) ? issProp.GetString() : null;
        if (!string.IsNullOrEmpty(issueId))
        {
            bool fixedOk = await SystemDoctorService.Instance.AutoFixIssueAsync(issueId).ConfigureAwait(false);
            var diagnostics = await SystemDoctorService.Instance.RunDiagnosticsAsync().ConfigureAwait(false);
            SendResponse(requestId, true, new { success = fixedOk, diagnostics });
        }
        else
        {
            SendResponse(requestId, false, "Issue ID is required");
        }
    }

    private Task HandleOpenExternalEditorAsync(JsonElement data, string? requestId)
    {
        {
            string path = data.TryGetProperty("path", out var edPath) ? edPath.GetString() ?? string.Empty : string.Empty;
            bool opened = Directory.Exists(path);

            if (opened)
            {
                try
                {
                    // `code` if VS Code is on PATH; the folder is the honest fallback rather
                    // than a silent no-op.
                    var start = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "code",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = true,
                    };
                    System.Diagnostics.Process.Start(start);
                }
                catch (Exception)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            Services.Platform.PlatformHelper.IsWindows ? "explorer.exe"
                                : Services.Platform.PlatformHelper.IsMacOS ? "open" : "xdg-open",
                            $"\"{path}\"") { UseShellExecute = true });
                    }
                    catch (Exception) { opened = false; }
                }
            }

            SendResponse(requestId, true, new { opened });
        }

        return Task.CompletedTask;
    }

}