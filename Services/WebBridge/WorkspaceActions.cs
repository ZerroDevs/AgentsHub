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
    private Task HandleListDirEntriesAsync(JsonElement data, string? requestId)
    {
        {
            // `browse_directory` opens the native folder picker, so it cannot be used for
            // completion. This one only reads a directory that already exists.
            string dir = data.TryGetProperty("path", out var dirElem) ? dirElem.GetString() ?? string.Empty : string.Empty;

            var entries = new List<object>();
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(dir)
                                 .OrderBy(Directory.Exists)
                                 .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                                 .Take(200))
                    {
                        entries.Add(new
                        {
                            name = Path.GetFileName(entry),
                            path = Path.GetFileName(entry),
                            isDir = Directory.Exists(entry),
                        });
                    }
                }
                catch (Exception)
                {
                    // An unreadable directory yields an empty list rather than an error.
                }
            }

            SendResponse(requestId, true, new { entries });
        }

        return Task.CompletedTask;
    }

    private Task HandleOpenExternalAsync(JsonElement data, string? requestId)
    {
        {
            string url = data.TryGetProperty("url", out var urlElem) ? urlElem.GetString() ?? string.Empty : string.Empty;
            bool ok = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

            if (ok)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    ok = false;
                }
            }

            SendResponse(requestId, true, new { opened = ok });
        }

        return Task.CompletedTask;
    }

#endif
    private async Task HandleGetWorkspaceSnapshotsAsync(JsonElement data, string? requestId)
    {
        string? wsId = data.TryGetProperty("workspaceId", out var snapWsProp) ? snapWsProp.GetString() : null;
        var snapshots = await WorkspaceSnapshotService.Instance.GetSnapshotsAsync(wsId).ConfigureAwait(false);
        SendResponse(requestId, true, new { snapshots });
    }

    private async Task HandleCreateWorkspaceSnapshotAsync(JsonElement data, string? requestId)
    {
        string? wsId = data.TryGetProperty("workspaceId", out var crSnapWsProp) ? crSnapWsProp.GetString() : null;
        string? title = data.TryGetProperty("title", out var tProp) ? tProp.GetString() : "Manual Snapshot";
        string? trigger = data.TryGetProperty("trigger", out var trProp) ? trProp.GetString() : "Manual";

        var snapshot = await WorkspaceSnapshotService.Instance.CreateSnapshotAsync(wsId, title ?? "Manual Snapshot", trigger ?? "Manual").ConfigureAwait(false);
        if (snapshot != null)
        {
            SoundpackAudioService.Instance.PlaySoundEvent("checkpoint_created");
            SendResponse(requestId, true, new { success = true, snapshot });
        }
        else
        {
            SendResponse(requestId, false, "Failed to create snapshot");
        }
    }

    private async Task HandleRollbackWorkspaceSnapshotAsync(JsonElement data, string? requestId)
    {
        string? snapshotId = data.TryGetProperty("snapshotId", out var sIdProp) ? sIdProp.GetString() : null;
        string? wsId = data.TryGetProperty("workspaceId", out var rbSnapWsProp) ? rbSnapWsProp.GetString() : null;

        if (!string.IsNullOrEmpty(snapshotId))
        {
            var (success, restoredCount) = await WorkspaceSnapshotService.Instance.RollbackSnapshotAsync(snapshotId, wsId).ConfigureAwait(false);
            if (success)
            {
                SoundpackAudioService.Instance.PlaySoundEvent("checkpoint_created");
                SendResponse(requestId, true, new { success = true, restoredCount });
            }
            else
            {
                SendResponse(requestId, false, "Failed to rollback snapshot");
            }
        }
        else
        {
            SendResponse(requestId, false, "Snapshot ID is required");
        }
    }

    private Task HandleDeleteWorkspaceSnapshotAsync(JsonElement data, string? requestId)
    {
        {
            string? snapshotId = data.TryGetProperty("snapshotId", out var sIdProp) ? sIdProp.GetString() : null;
            string? wsId = data.TryGetProperty("workspaceId", out var delWsProp) ? delWsProp.GetString() : null;

            if (!string.IsNullOrEmpty(snapshotId))
            {
                bool deleted = WorkspaceSnapshotService.Instance.DeleteSnapshot(snapshotId, wsId);
                SendResponse(requestId, deleted, new { success = deleted });
            }
            else
            {
                SendResponse(requestId, false, "Snapshot ID is required");
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleAuditWorkspaceSecurityAsync(JsonElement data, string? requestId)
    {
        string? wsDir = data.TryGetProperty("workspaceDir", out var wsDirProp) ? wsDirProp.GetString() : null;
        var audit = await PreCommitSecurityService.Instance.AuditWorkspaceAsync(wsDir).ConfigureAwait(false);
        SendResponse(requestId, true, audit);
    }

    private Task HandleSetWorkingProjectAsync(JsonElement data, string? requestId)
    {
        {
            string? projId = data.TryGetProperty("projectId", out var pProp) ? pProp.GetString() : null;
            GitRepositoryManagerService.Instance.SetWorkingProject(projId);
            SendResponse(requestId, true, new { success = true, workingProjectId = projId });
        }

        return Task.CompletedTask;
    }

    private async Task HandleGetWorkspaceMemoriesAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rProp) ? rProp.GetString() : null;
        if (!string.IsNullOrEmpty(repoPath))
        {
            var memories = await AgentSessionMemoryService.Instance.GetMemoriesAsync(repoPath).ConfigureAwait(false);
            SendResponse(requestId, true, new { memories });
        }
        else
        {
            SendResponse(requestId, false, "repoPath is required");
        }
    }

    private async Task HandleSearchWorkspaceMemoriesAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rProp) ? rProp.GetString() : null;
        string query = data.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? "" : "";
        int topK = data.TryGetProperty("topK", out var kProp) && kProp.TryGetInt32(out var kVal) ? kVal : 10;

        if (!string.IsNullOrEmpty(repoPath))
        {
            var matches = await AgentSessionMemoryService.Instance.SearchMemoriesAsync(repoPath, query, topK).ConfigureAwait(false);
            SendResponse(requestId, true, new { memories = matches });
        }
        else
        {
            SendResponse(requestId, false, "repoPath is required");
        }
    }

    private async Task HandleDeleteWorkspaceMemoryAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rProp) ? rProp.GetString() : null;
        string? memoryId = data.TryGetProperty("memoryId", out var mProp) ? mProp.GetString() : null;

        if (!string.IsNullOrEmpty(repoPath) && !string.IsNullOrEmpty(memoryId))
        {
            var (ok, msg) = await AgentSessionMemoryService.Instance.DeleteMemoryAsync(repoPath, memoryId).ConfigureAwait(false);
            SendResponse(requestId, ok, new { success = ok, message = msg });
        }
        else
        {
            SendResponse(requestId, false, "repoPath and memoryId are required");
        }
    }

    private async Task HandleClearWorkspaceMemoriesAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rProp) ? rProp.GetString() : null;
        if (!string.IsNullOrEmpty(repoPath))
        {
            var (ok, count, msg) = await AgentSessionMemoryService.Instance.ClearAllMemoriesAsync(repoPath).ConfigureAwait(false);
            SendResponse(requestId, ok, new { success = ok, count, message = msg });
        }
        else
        {
            SendResponse(requestId, false, "repoPath is required");
        }
    }

    private async Task HandleAddWorkspaceMemoryAsync(JsonElement data, string? requestId)
    {
        string? repoPath = data.TryGetProperty("repoPath", out var rProp) ? rProp.GetString() : null;
        string? title = data.TryGetProperty("title", out var tProp) ? tProp.GetString() : null;
        string summary = data.TryGetProperty("summary", out var sProp) ? sProp.GetString() ?? "" : "";
        var tags = new List<string>();
        if (data.TryGetProperty("tags", out var tagListProp) && tagListProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in tagListProp.EnumerateArray())
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) tags.Add(s);
            }
        }

        if (!string.IsNullOrEmpty(repoPath) && !string.IsNullOrEmpty(title))
        {
            var (ok, entry, msg) = await AgentSessionMemoryService.Instance.AddManualMemoryAsync(repoPath, title, summary, tags).ConfigureAwait(false);
            SendResponse(requestId, ok, new { success = ok, entry, message = msg });
        }
        else
        {
            SendResponse(requestId, false, "repoPath and title are required");
        }
    }

    private Task HandleGetRecentFileChangesAsync(string? requestId)
    {
        {
            var changes = FileWatchdogService.Instance.GetRecentChanges();
            SendResponse(requestId, true, new { changes });
        }

        return Task.CompletedTask;
    }

    private async Task HandleGetFileDiffAsync(JsonElement data, string? requestId)
    {
        string? path = data.TryGetProperty("path", out var pProp) ? pProp.GetString() : null;
        if (!string.IsNullOrEmpty(path))
        {
            var diff = await FileWatchdogService.Instance.GetFileDiffAsync(path).ConfigureAwait(false);
            SendResponse(requestId, true, new { diff });
        }
        else
        {
            SendResponse(requestId, false, "Path is required");
        }
    }

    private async Task HandleRevertFileChangeAsync(JsonElement data, string? requestId)
    {
        string? path = data.TryGetProperty("path", out var pProp) ? pProp.GetString() : null;
        if (!string.IsNullOrEmpty(path))
        {
            bool reverted = await FileWatchdogService.Instance.RevertFileAsync(path).ConfigureAwait(false);
            SendResponse(requestId, true, new { success = reverted });
        }
        else
        {
            SendResponse(requestId, false, "Path is required");
        }
    }

    private Task HandleClearFileChangesAsync(string? requestId)
    {
        {
            FileWatchdogService.Instance.ClearRecentChanges();
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleGetSessionStateAsync(string? requestId)
    {
        {
            var stateData = SessionStateService.Instance.LoadSnapshot();
            SendResponse(requestId, true, new { state = stateData });
        }

        return Task.CompletedTask;
    }

    private Task HandleSaveSessionStateAsync(JsonElement data, string? requestId)
    {
        {
            string? activeWs = data.TryGetProperty("activeWorkspaceId", out var actWsProp) ? actWsProp.GetString() : null;
            string? activeView = data.TryGetProperty("activeView", out var vProp) ? vProp.GetString() : null;
            string? activeTab = data.TryGetProperty("activeCliTab", out var tProp) ? tProp.GetString() : null;
            SessionStateService.Instance.SaveSnapshot(activeWs ?? "", activeView ?? "dashboard", activeTab ?? "hub");
            SendResponse(requestId, true, new { success = true });
        }

        return Task.CompletedTask;
    }

    private Task HandleGetWorkspacesAsync(string? requestId)
    {
        SendResponse(requestId, true, new
        {
            workspaces = AiConfigService.Instance.GetWorkspaces(),
            activeWorkspaceId = AiConfigService.Instance.GetActiveWorkspaceId()
        });

        return Task.CompletedTask;
    }

    private Task HandleSaveWorkspaceAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("workspace", out var wsProp))
        {
            var ws = JsonSerializer.Deserialize<ProjectWorkspace>(wsProp.GetRawText(), CaseInsensitiveJsonOptions);
            if (ws != null)
            {
                AiConfigService.Instance.SaveWorkspace(ws);
                SendResponse(requestId, true, new
                {
                    success = true,
                    workspace = ws,
                    workspaces = AiConfigService.Instance.GetWorkspaces(),
                    activeWorkspaceId = AiConfigService.Instance.GetActiveWorkspaceId()
                });
                PostEventToUi("workspaces_updated", new
                {
                    workspaces = AiConfigService.Instance.GetWorkspaces(),
                    activeWorkspaceId = AiConfigService.Instance.GetActiveWorkspaceId()
                });
                return Task.CompletedTask;
            }
        }
        SendResponse(requestId, false, "Invalid workspace data");

        return Task.CompletedTask;
    }

    private Task HandleDeleteWorkspaceAsync(JsonElement data, string? requestId)
    {
        {
            string? delId = data.TryGetProperty("id", out var dIdProp) ? dIdProp.GetString() : null;
            if (!string.IsNullOrEmpty(delId))
            {
                bool deleted = AiConfigService.Instance.DeleteWorkspace(delId);
                SendResponse(requestId, true, new
                {
                    success = deleted,
                    workspaces = AiConfigService.Instance.GetWorkspaces(),
                    activeWorkspaceId = AiConfigService.Instance.GetActiveWorkspaceId()
                });
                PostEventToUi("workspaces_updated", new
                {
                    workspaces = AiConfigService.Instance.GetWorkspaces(),
                    activeWorkspaceId = AiConfigService.Instance.GetActiveWorkspaceId()
                });
            }
            else
            {
                SendResponse(requestId, false, "Workspace ID required");
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleActivateWorkspaceAsync(JsonElement data, string? requestId)
    {
        string? actId = data.TryGetProperty("id", out var aIdProp)
            ? aIdProp.GetString()
            : (data.TryGetProperty("workspaceId", out var wIdProp) ? wIdProp.GetString() : null);

        if (!string.IsNullOrEmpty(actId))
        {
            var all = AiConfigService.Instance.GetWorkspaces();
            var target = all.FirstOrDefault(w => string.Equals(w.Id, actId, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                AiConfigService.Instance.SetActiveWorkspaceId(target.Id);
                if (!string.IsNullOrWhiteSpace(target.WorkingDirectory))
                {
                    FileWatchdogService.Instance.WatchDirectory(target.WorkingDirectory);
                }
                if (!string.IsNullOrWhiteSpace(target.AntigravityProfile) &&
                    !string.Equals(target.AntigravityProfile, "default", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_cliManager.ProfileService.ActiveProfile, target.AntigravityProfile, StringComparison.OrdinalIgnoreCase))
                {
                    await _cliManager.SwitchAntigravityProfileAsync(target.AntigravityProfile);
                }
                var currentWorkspaces = AiConfigService.Instance.GetWorkspaces();
                SendResponse(requestId, true, new
                {
                    success = true,
                    activeWorkspace = target,
                    activeWorkspaceId = target.Id,
                    workspaces = currentWorkspaces
                });
                PostEventToUi("workspace_activated", new
                {
                    activeWorkspace = target,
                    activeWorkspaceId = target.Id,
                    workspaces = currentWorkspaces
                });
                return;
            }
        }
        SendResponse(requestId, false, "Workspace not found");
    }

    private Task HandleOpenInExplorerAsync(JsonElement data, string? requestId)
    {
        // The repository detail panel called four actions that never existed, so its buttons were
        // dead in exactly the way `pick_directory` was: a rejected promise inside an empty catch.
        {
            string path = data.TryGetProperty("path", out var exPath) ? exPath.GetString() ?? string.Empty : string.Empty;
            bool opened = Directory.Exists(path);
            if (opened)
            {
                try
                {
                    // Explorer, Finder and xdg-open respectively. Hardcoding explorer.exe
                    // worked on Windows and silently failed everywhere else.
                    var info = Services.Platform.PlatformHelper.IsWindows
                        ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }
                        : new System.Diagnostics.ProcessStartInfo(
                            Services.Platform.PlatformHelper.IsMacOS ? "open" : "xdg-open",
                            $"\"{path}\"") { UseShellExecute = true };
                    System.Diagnostics.Process.Start(info);
                }
                catch (Exception) { opened = false; }
            }
            SendResponse(requestId, true, new { opened });
        }

        return Task.CompletedTask;
    }

    private Task HandleGetWorkspaceFilesAsync(JsonElement data, string? requestId)
    {
        {
            string? targetDir = null;
            if (data.TryGetProperty("path", out var targetPathProp))
            {
                targetDir = targetPathProp.GetString();
            }
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                var activeWs = AiConfigService.Instance.GetActiveWorkspace();
                targetDir = activeWs?.WorkingDirectory;
            }
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                targetDir = Directory.GetCurrentDirectory();
            }

            var branch = "main";
            try
            {
                var repo = GitRepositoryManagerService.Instance.GetRepository(targetDir);
                if (repo != null && !string.IsNullOrWhiteSpace(repo.Branch))
                {
                    branch = repo.Branch;
                }
            }
            catch { }

            var fileEntries = new List<object>();
            try
            {
                var dirInfo = new DirectoryInfo(targetDir);
                var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".git", ".vs", "bin", "obj", "node_modules", ".agents", "dist", "build"
                };

                foreach (var subDir in dirInfo.GetDirectories().OrderBy(d => d.Name))
                {
                    if (ignoredNames.Contains(subDir.Name) || subDir.Name.StartsWith('.')) continue;
                    fileEntries.Add(new
                    {
                        name = subDir.Name + "/",
                        path = subDir.Name,
                        isDirectory = true,
                        extension = ""
                    });

                    try
                    {
                        foreach (var childFile in subDir.GetFiles().Take(6).OrderBy(f => f.Name))
                        {
                            fileEntries.Add(new
                            {
                                name = childFile.Name,
                                path = Path.Combine(subDir.Name, childFile.Name).Replace('\\', '/'),
                                isDirectory = false,
                                extension = childFile.Extension.ToLowerInvariant()
                            });
                        }
                    }
                    catch { }
                }

                foreach (var file in dirInfo.GetFiles().OrderBy(f => f.Name))
                {
                    if (file.Name.StartsWith('.') || file.Name.EndsWith(".user")) continue;
                    fileEntries.Add(new
                    {
                        name = file.Name,
                        path = file.Name,
                        isDirectory = false,
                        extension = file.Extension.ToLowerInvariant()
                    });
                }
            }
            catch (Exception ex)
            {
                SendResponse(requestId, false, ex.Message);
                return Task.CompletedTask;
            }

            SendResponse(requestId, true, new
            {
                rootPath = targetDir,
                branch,
                files = fileEntries
            });
        }

        return Task.CompletedTask;
    }

    private Task HandleOpenFolderAsync(JsonElement data, string? requestId)
    {
        if (data.TryGetProperty("path", out var pathProp))
        {
            var folderPath = pathProp.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                Process.Start(new ProcessStartInfo(
                    Services.Platform.PlatformHelper.IsWindows ? "explorer.exe"
                        : Services.Platform.PlatformHelper.IsMacOS ? "open" : "xdg-open",
                    folderPath) { UseShellExecute = true });
                SendResponse(requestId, true, null);
            }
            else
            {
                SendResponse(requestId, false, "Folder does not exist.");
            }
        }
        else
        {
            SendResponse(requestId, false, "Path not specified.");
        }

        return Task.CompletedTask;
    }

    private Task HandleBrowseDirectoryAsync(JsonElement data, string? requestId)
    {
        {
            string title = data.TryGetProperty("title", out var bdTitle) ? bdTitle.GetString() ?? "Select a folder" : "Select a folder";

            string? selectedFolder = null;
#if WINDOWS
            if (_mainForm is Form f && f.IsHandleCreated && !f.IsDisposed)
            {
                f.Invoke(() =>
                {
                    using var fbd = new FolderBrowserDialog { Description = title };
                    if (fbd.ShowDialog(f) == DialogResult.OK)
                    {
                        selectedFolder = fbd.SelectedPath;
                    }
                });
            }
#endif

            if (!string.IsNullOrWhiteSpace(selectedFolder))
            {
                SendResponse(requestId, true, new { path = selectedFolder });
            }
            else
            {
                SendResponse(requestId, false, "Selection cancelled");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Largest file the viewer will open, so one click cannot load a log dump into the page.</summary>
    private const int MaxViewerBytes = 1_048_576;

    private Task HandleReadWorkspaceFileAsync(JsonElement data, string? requestId)
    {
        if (!TryResolveWorkspaceFile(data, out var fullPath, out var root, out var relative, out var error))
        {
            SendResponse(requestId, false, error);
            return Task.CompletedTask;
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                SendResponse(requestId, false, $"{relative} does not exist.");
                return Task.CompletedTask;
            }

            long size = new FileInfo(fullPath).Length;
            bool truncated = size > MaxViewerBytes;

            byte[] bytes;
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int take = (int)Math.Min(size, MaxViewerBytes);
                bytes = new byte[take];
                int read = 0;

                while (read < take)
                {
                    int got = stream.Read(bytes, read, take - read);
                    if (got <= 0) break;
                    read += got;
                }

                if (read < take) Array.Resize(ref bytes, read);
            }

            // A NUL in the first few KB is the cheap, reliable signal for "this is not text".
            bool binary = Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0;

            SendResponse(requestId, true, new
            {
                path = relative,
                rootPath = root,
                extension = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant(),
                sizeBytes = size,
                truncated,
                binary,
                text = binary ? string.Empty : DecodeText(bytes)
            });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>Writes the editor's contents back to the same file, in the encoding it arrived in.</summary>
    private Task HandleSaveWorkspaceFileAsync(JsonElement data, string? requestId)
    {
        if (!TryResolveWorkspaceFile(data, out var fullPath, out var root, out var relative, out var error))
        {
            SendResponse(requestId, false, error);
            return Task.CompletedTask;
        }

        string text = data.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;

        if (Encoding.UTF8.GetByteCount(text) > MaxViewerBytes * 4)
        {
            SendResponse(requestId, false, "That edit is too large to save from the viewer.");
            return Task.CompletedTask;
        }

        try
        {
            // The viewer only ever saw the first MaxViewerBytes of a file this large, so writing the
            // editor's buffer back would silently drop the rest of it.
            if (File.Exists(fullPath) && new FileInfo(fullPath).Length > MaxViewerBytes)
            {
                SendResponse(requestId, false, $"{relative} is too large to edit in the viewer.");
                return Task.CompletedTask;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? root);

            // A UTF-8 BOM is the only marker worth preserving: rewriting a file without it changes
            // the bytes git sees, and on Windows it changes how some tools read the first line.
            bool hadBom = false;

            if (File.Exists(fullPath))
            {
                var head = new byte[3];
                using var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                hadBom = probe.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
            }

            File.WriteAllText(fullPath, text, new UTF8Encoding(hadBom));

            SendResponse(requestId, true, new
            {
                saved = true,
                path = relative,
                sizeBytes = Encoding.UTF8.GetByteCount(text)
            });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns a workspace-relative path into a real one, and refuses anything that escapes the root.
    ///
    /// The path arrives from the page, so `..\..\Users\x\.ssh\id_rsa` has to be rejected here rather
    /// than trusted: this is the only place a user-typed path becomes a file operation.
    /// </summary>
    private static bool TryResolveWorkspaceFile(
        JsonElement data,
        out string fullPath,
        out string root,
        out string relative,
        out string error)
    {
        fullPath = string.Empty;
        root = string.Empty;
        relative = string.Empty;
        error = string.Empty;

        string? requested = data.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            error = "Path is required";
            return false;
        }

        string? requestedRoot = data.TryGetProperty("root", out var rootProp) ? rootProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestedRoot))
        {
            requestedRoot = AiConfigService.Instance.GetActiveWorkspace()?.WorkingDirectory;
        }

        if (string.IsNullOrWhiteSpace(requestedRoot) || !Directory.Exists(requestedRoot))
        {
            requestedRoot = Directory.GetCurrentDirectory();
        }

        try
        {
            root = Path.GetFullPath(requestedRoot);

            string candidate = Path.IsPathRooted(requested)
                ? Path.GetFullPath(requested)
                : Path.GetFullPath(Path.Combine(root, requested.Replace('/', Path.DirectorySeparatorChar)));

            string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (!candidate.StartsWith(prefix, comparison))
            {
                error = "That path is outside the workspace.";
                return false;
            }

            if (Directory.Exists(candidate))
            {
                error = $"{requested} is a folder.";
                return false;
            }

            fullPath = candidate;
            relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>UTF-8 unless the file's own BOM says otherwise; invalid bytes become U+FFFD.</summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.UTF8.GetString(bytes);
    }

}