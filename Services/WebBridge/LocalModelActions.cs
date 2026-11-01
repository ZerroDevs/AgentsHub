using System.Diagnostics;
using System.Text.Json;
using AgentsHub.Services.LlamaCpp;
#if WINDOWS
using System.Windows.Forms;
#endif

namespace AgentsHub.Services;

/// <summary>
/// The Local Models shelf: browsing Hugging Face, downloading a GGUF, adding files already on disk,
/// and running one. Downloads are the only long job here, and they report progress as they go rather
/// than holding a request open for minutes.
/// </summary>
public sealed partial class DashboardWebBridge
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _modelDownloads = new(StringComparer.OrdinalIgnoreCase);

    private Task HandleLocalModelsListAsync(string? requestId)
    {
        var library = LlamaModelLibrary.Instance;
        var budget = LocalHardware.Budget;

        SendResponse(requestId, true, new
        {
            directory = library.ModelsDirectory,
            memory = new { vramMb = budget.VramMb, ramMb = budget.RamMb },
            models = DescribeLocalModels()
        });
        return Task.CompletedTask;
    }

    private async Task HandleLocalModelsSearchAsync(JsonElement data, string? requestId)
    {
        string query = data.TryGetProperty("query", out var qEl) ? qEl.GetString() ?? "" : "";
        string bucket = data.TryGetProperty("bucket", out var bEl) ? bEl.GetString() ?? "all" : "all";
        bool uncensored = data.TryGetProperty("uncensored", out var uEl) && uEl.ValueKind == JsonValueKind.True;

        try
        {
            var results = await LlamaModelLibrary.Instance
                .SearchAsync(query, bucket, uncensored, 40)
                .ConfigureAwait(false);

            SendResponse(requestId, true, new
            {
                models = results.Select(m => new
                {
                    id = m.Id,
                    downloads = m.Downloads,
                    likes = m.Likes,
                    pipeline = m.Pipeline,
                    paramLabel = m.ParamLabel,
                    uncensored = IsUncensored(m),
                    capabilities = m.Capabilities
                })
            });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, $"Hugging Face could not be reached: {ex.Message}");
        }
    }

    private async Task HandleLocalModelsFilesAsync(JsonElement data, string? requestId)
    {
        string repo = data.TryGetProperty("repo", out var rEl) ? rEl.GetString() ?? "" : "";
        if (repo.Length == 0)
        {
            SendResponse(requestId, false, "A repository is required.");
            return;
        }

        try
        {
            var files = await LlamaModelLibrary.Instance.FilesAsync(repo).ConfigureAwait(false);
            var budget = LocalHardware.Budget;
            var library = LlamaModelLibrary.Instance;

            SendResponse(requestId, true, new
            {
                files = files.Select(f =>
                {
                    var fit = budget.Fit(f.SizeBytes);
                    return new
                    {
                        path = f.Path,
                        size = f.SizeBytes,
                        quant = f.Quant,
                        // Bytes already on disk from an interrupted run, so the row can offer Resume.
                        partial = library.PartialSize(f.Path),
                        fit = new { where = fit.Where, needMb = fit.NeedMb, haveMb = fit.HaveMb }
                    };
                })
            });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, $"Could not list {repo}: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts a download and answers at once. Progress arrives as events keyed by repository, and the
    /// finished file is described by local_model_download_done so the shelf refreshes itself.
    /// </summary>
    private Task HandleLocalModelsDownloadAsync(JsonElement data, string? requestId)
    {
        string repo = data.TryGetProperty("repo", out var rEl) ? rEl.GetString() ?? "" : "";
        string file = data.TryGetProperty("file", out var fEl) ? fEl.GetString() ?? "" : "";

        if (repo.Length == 0 || file.Length == 0)
        {
            SendResponse(requestId, false, "A repository and a file are required.");
            return Task.CompletedTask;
        }

        string key = $"{repo}/{file}";
        if (_modelDownloads.ContainsKey(key))
        {
            SendResponse(requestId, false, "That file is already downloading.");
            return Task.CompletedTask;
        }

        var cts = new CancellationTokenSource();
        _modelDownloads[key] = cts;

        SendResponse(requestId, true, new { started = true, repo, file });

        _ = Task.Run(async () =>
        {
            var lastAt = DateTime.MinValue;

            try
            {
                var model = await LlamaModelLibrary.Instance.DownloadAsync(repo, file, (received, total) =>
                {
                    // One event per second is enough to move a bar; a per-chunk event would flood the
                    // WebView with hundreds of messages a second on a fast link.
                    if (DateTime.UtcNow - lastAt < TimeSpan.FromSeconds(1) && received != total) return;
                    lastAt = DateTime.UtcNow;

                    PostEventToUi("local_model_download_progress", new
                    {
                        repo,
                        file,
                        received,
                        total,
                        percent = total > 0 ? (int)Math.Round(received * 100.0 / total) : 0
                    });
                }, cts.Token).ConfigureAwait(false);

                EmitLog("llama.cpp", "info", $"Downloaded {Path.GetFileName(model.Path)}.");
                PostEventToUi("local_model_download_done", new { repo, file, success = true, model = DescribeModel(model) });
            }
            catch (OperationCanceledException)
            {
                PostEventToUi("local_model_download_done", new { repo, file, success = false, error = "Cancelled." });
            }
            catch (Exception ex)
            {
                EmitLog("llama.cpp", "error", $"Download failed: {ex.Message}");
                PostEventToUi("local_model_download_done", new { repo, file, success = false, error = ex.Message });
            }
            finally
            {
                _modelDownloads.TryRemove(key, out _);
                cts.Dispose();
            }
        });

        return Task.CompletedTask;
    }

    private Task HandleLocalModelsCancelDownloadAsync(JsonElement data, string? requestId)
    {
        string key = data.TryGetProperty("key", out var kEl) ? kEl.GetString() ?? "" : "";
        bool cancelled = _modelDownloads.TryGetValue(key, out var cts) && cts != null;

        if (cancelled)
        {
            try { cts!.Cancel(); } catch (ObjectDisposedException) { }
        }

        SendResponse(requestId, true, new { cancelled });
        return Task.CompletedTask;
    }

    private Task HandleLocalModelsAddFileAsync(string? requestId)
    {
        string? selected = null;

#if WINDOWS
        MaybePickOnUiThread(f =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Add a GGUF model",
                Filter = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (ofd.ShowDialog(f) == DialogResult.OK) selected = ofd.FileName;
        });
#endif

        return AddPathAsync(selected, requestId);
    }

    private Task HandleLocalModelsAddFolderAsync(string? requestId)
    {
        string? selected = null;

#if WINDOWS
        MaybePickOnUiThread(f =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Pick a folder that holds .gguf files",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog(f) == DialogResult.OK) selected = dialog.SelectedPath;
        });
#endif

        return AddPathAsync(selected, requestId);
    }

    private Task AddPathAsync(string? path, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SendResponse(requestId, false, new { success = false, message = "Nothing was picked." });
            return Task.CompletedTask;
        }

        if (Directory.Exists(path))
        {
            int count = LlamaModelLibrary.Instance.AddFolder(path);
            EmitLog("llama.cpp", "info", $"Added model folder {path} ({count} file(s)).");
            SendResponse(requestId, true, new
            {
                success = true,
                message = count == 0 ? "Folder added; no .gguf files in it yet." : $"Added {count} model(s).",
                models = DescribeLocalModels()
            });
            return Task.CompletedTask;
        }

        var added = LlamaModelLibrary.Instance.AddFile(path);
        if (added == null)
        {
            SendResponse(requestId, false, new { success = false, message = "That file is not a .gguf model." });
            return Task.CompletedTask;
        }

        EmitLog("llama.cpp", "info", $"Added model {Path.GetFileName(added)}.");
        SendResponse(requestId, true, new { success = true, message = "Model added.", models = DescribeLocalModels() });
        return Task.CompletedTask;
    }

    private Task HandleLocalModelsForgetAsync(JsonElement data, string? requestId)
    {
        string path = data.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
        bool deleteFile = data.TryGetProperty("delete", out var dEl) && dEl.ValueKind == JsonValueKind.True;

        bool removed = LlamaModelLibrary.Instance.Forget(path, deleteFile);
        SendResponse(requestId, removed, new { success = removed, models = DescribeLocalModels() });
        return Task.CompletedTask;
    }

    private Task HandleLocalModelsOpenFolderAsync(string? requestId)
    {
        var directory = LlamaModelLibrary.Instance.ModelsDirectory;

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            SendResponse(requestId, true, new { opened = true, directory });
        }
        catch (Exception ex)
        {
            SendResponse(requestId, false, ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>Loads one file server-side and makes it the model new chats use.</summary>
    private async Task HandleLocalModelsRunAsync(JsonElement data, string? requestId)
    {
        string path = data.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
        if (!File.Exists(path))
        {
            SendResponse(requestId, false, "That model file is gone.");
            return;
        }

        var (status, provider) = await LlamaCppService.Instance.RunModelAsync(path).ConfigureAwait(false);

        EmitLog("llama.cpp", status.Running ? "info" : "error",
            status.Running ? $"Running {Path.GetFileName(path)}." : status.Message);

        SendResponse(requestId, status.Running, new
        {
            success = status.Running,
            message = status.Message,
            model = provider.SelectedModel,
            providerId = provider.Id,
            status = new
            {
                installed = status.Installed,
                path = status.Path,
                version = status.Version,
                running = status.Running,
                port = status.Port,
                endpoint = status.Endpoint,
                message = status.Message,
                usesCustomPath = status.UsesCustomPath
            }
        });
    }

    // ---- shared shapes ----

    private static object DescribeModel(LocalModel model)
    {
        var fit = LocalHardware.Budget.Fit(model.SizeBytes);

        return new
        {
            path = model.Path,
            name = model.Name,
            size = model.SizeBytes,
            origin = model.Origin,
            managed = model.Managed,
            capabilities = model.Capabilities,
            fit = new { where = fit.Where, needMb = fit.NeedMb, haveMb = fit.HaveMb }
        };
    }

    private static List<object> DescribeLocalModels() =>
        [.. LlamaModelLibrary.Instance.List().Select(m => DescribeModel(m))];

    private static bool IsUncensored(HfModel model) =>
        model.Tags.Any(t => t.Contains("uncensored", StringComparison.OrdinalIgnoreCase))
        || model.Id.Contains("uncensored", StringComparison.OrdinalIgnoreCase)
        || model.Id.Contains("abliterated", StringComparison.OrdinalIgnoreCase)
        || model.Id.Contains("heretic", StringComparison.OrdinalIgnoreCase);

#if WINDOWS
    /// <summary>A picker has to run on the window's thread; the callers are event handlers elsewhere.</summary>
    private void MaybePickOnUiThread(Action<Form> pick)
    {
        if (_mainForm is Form form && form.IsHandleCreated && !form.IsDisposed)
        {
            form.Invoke(() => pick(form));
        }
    }
#endif
}
