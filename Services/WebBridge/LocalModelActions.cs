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

    private async Task HandleLocalModelsListAsync(string? requestId)
    {
        var library = LlamaModelLibrary.Instance;
        var budget = LocalHardware.Budget;

        // Asked here so the shelf names the backend before a model is loaded. Cached for ten minutes
        // by the service, and a missing binary answers instantly.
        if (LlamaCppService.Instance.Status().Installed)
        {
            await LlamaCppService.Instance.BackendAsync().ConfigureAwait(false);
        }

        SendResponse(requestId, true, new
        {
            directory = library.DownloadDirectory,
            defaultDirectory = library.ModelsDirectory,
            usingDefaultDirectory = string.Equals(library.DownloadDirectory, library.ModelsDirectory, StringComparison.OrdinalIgnoreCase),
            runOnRequest = LlamaCppService.Instance.RunOnRequest,
            loadedModel = LlamaCppService.Instance.LoadedModelPath,
            server = ToLlamaCppDto(LlamaCppService.Instance.Status()),
            memory = new { vramMb = budget.VramMb, ramMb = budget.RamMb },
            models = DescribeLocalModels()
        });
    }

    /// <summary>
    /// Frees the model's memory. A server started with a file serves exactly that file, so unloading
    /// is stopping the process: there is nothing left to answer with afterwards.
    /// </summary>
    private Task HandleLocalModelsUnloadAsync(string? requestId)
    {
        LlamaCppService.Instance.Stop();
        EmitLog("llama.cpp", "info", "Model unloaded and the local server stopped.");

        SendResponse(requestId, true, new
        {
            success = true,
            server = ToLlamaCppDto(LlamaCppService.Instance.Status()),
            models = DescribeLocalModels()
        });
        return Task.CompletedTask;
    }

    /// <summary>Renames a model in this app. The file on disk keeps its name, so a re-add finds it.</summary>
    private Task HandleLocalModelsRenameAsync(JsonElement data, string? requestId)
    {
        string path = data.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
        string name = data.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";

        bool renamed = LlamaModelLibrary.Instance.Rename(path, name);
        SendResponse(requestId, renamed, new { success = renamed, models = DescribeLocalModels() });
        return Task.CompletedTask;
    }

    /// <summary>Picks where new downloads land. Empty is not an answer: the default folder stays.</summary>
    private Task HandleLocalModelsPickDownloadFolderAsync(string? requestId)
    {
        string? selected = null;

#if WINDOWS
        MaybePickOnUiThread(f =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Where downloaded models are saved",
                UseDescriptionForTitle = true,
                SelectedPath = LlamaModelLibrary.Instance.DownloadDirectory
            };

            if (dialog.ShowDialog(f) == DialogResult.OK) selected = dialog.SelectedPath;
        });
#endif

        if (string.IsNullOrWhiteSpace(selected))
        {
            SendResponse(requestId, true, new { chosen = false, directory = LlamaModelLibrary.Instance.DownloadDirectory });
            return Task.CompletedTask;
        }

        LlamaModelLibrary.Instance.SetDownloadDirectory(selected);
        EmitLog("llama.cpp", "info", $"Downloads now go to {selected}.");

        SendResponse(requestId, true, new
        {
            chosen = true,
            directory = LlamaModelLibrary.Instance.DownloadDirectory,
            usingDefaultDirectory = false,
            models = DescribeLocalModels()
        });
        return Task.CompletedTask;
    }

    private Task HandleLocalModelsResetDownloadFolderAsync(string? requestId)
    {
        var library = LlamaModelLibrary.Instance;
        library.SetDownloadDirectory(string.Empty);

        SendResponse(requestId, true, new
        {
            directory = library.DownloadDirectory,
            usingDefaultDirectory = true,
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

    /// <summary>
    /// Saves the launch flags for one model. They only reach the process at the next start, so a
    /// running server for this file is stopped rather than left pretending to use them.
    /// </summary>
    private Task HandleLocalModelsSetFlagsAsync(JsonElement data, string? requestId)
    {
        string path = data.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
        if (!File.Exists(path))
        {
            SendResponse(requestId, false, "That model file is gone.");
            return Task.CompletedTask;
        }

        var flags = new ModelFlags(
            ContextSize: OptionalInt(data, "contextSize"),
            GpuLayers: OptionalInt(data, "gpuLayers"),
            CacheTypeK: OptionalText(data, "cacheTypeK"),
            CacheTypeV: OptionalText(data, "cacheTypeV"),
            FlashAttention: OptionalBool(data, "flashAttention"),
            ExtraArgs: OptionalText(data, "extraArgs"),
            Projector: OptionalText(data, "projector"));

        LlamaModelLibrary.Instance.SetFlags(path, flags);

        bool stopped = StopIfLoaded(path);

        EmitLog("llama.cpp", "info", flags.IsEmpty
            ? $"{Path.GetFileName(path)} uses the app-wide server flags again."
            : $"Saved launch flags for {Path.GetFileName(path)}.");

        SendResponse(requestId, true, new
        {
            success = true,
            stopped,
            message = stopped ? "Saved. The running model was unloaded so the new flags apply." : "Saved.",
            models = DescribeLocalModels()
        });

        return Task.CompletedTask;
    }

    private Task HandleLocalModelsResetFlagsAsync(JsonElement data, string? requestId)
    {
        string path = data.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";

        LlamaModelLibrary.Instance.ResetFlags(path);
        bool stopped = StopIfLoaded(path);

        SendResponse(requestId, true, new { success = true, stopped, models = DescribeLocalModels() });
        return Task.CompletedTask;
    }

    /// <summary>Picks the mmproj a vision model needs, when it is not sitting beside the model already.</summary>
    private Task HandleLocalModelsPickProjectorAsync(JsonElement data, string? requestId)
    {
        string path = data.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
        string? selected = null;

#if WINDOWS
        MaybePickOnUiThread(f =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pick the mmproj projector for this model",
                Filter = "GGUF projector (mmproj*.gguf)|*.gguf|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (ofd.ShowDialog(f) == DialogResult.OK) selected = ofd.FileName;
        });
#endif

        if (string.IsNullOrWhiteSpace(selected))
        {
            SendResponse(requestId, true, new { chosen = false, models = DescribeLocalModels() });
            return Task.CompletedTask;
        }

        var existing = LlamaModelLibrary.Instance.FlagsFor(path) ?? new ModelFlags();
        LlamaModelLibrary.Instance.SetFlags(path, existing with { Projector = selected });
        bool stopped = StopIfLoaded(path);

        EmitLog("llama.cpp", "info", $"Projector for {Path.GetFileName(path)}: {selected}");

        SendResponse(requestId, true, new { chosen = true, stopped, projector = selected, models = DescribeLocalModels() });
        return Task.CompletedTask;
    }

    /// <summary>Stops the server when it is serving this exact file, so new launch flags take effect.</summary>
    private static bool StopIfLoaded(string path)
    {
        var service = LlamaCppService.Instance;
        if (!string.Equals(service.LoadedModelPath, path, StringComparison.OrdinalIgnoreCase)) return false;
        if (!service.Status().Running) return false;

        service.Stop();
        return true;
    }

    // ---- shared shapes ----

    /// <summary>A number the page did not send stays null, so "unset" keeps meaning "use the default".</summary>
    private static int? OptionalInt(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value)) return value;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int parsed)) return parsed;

        return null;
    }

    private static bool? OptionalBool(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var el)) return null;

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? OptionalText(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;

        string value = (el.GetString() ?? string.Empty).Trim();
        return value.Length > 0 ? value : null;
    }

    private static object DescribeModel(LocalModel model)
    {
        var budget = LocalHardware.Budget;
        var service = LlamaCppService.Instance;
        var flags = LlamaModelLibrary.Instance.FlagsFor(model.Path);
        var metadata = LocalModelMetadataReader.For(model.Path);

        // The context this model would actually be loaded with, and what that costs in cache: the
        // badge used to count the weights alone, so a huge context looked free.
        int context = Math.Max(flags?.ContextSize ?? service.Runtime.ContextSize, 8192);
        string cacheK = flags?.CacheTypeK ?? service.Runtime.CacheTypeK;
        double kvPerToken = metadata.KvBytesPerToken(cacheK);

        // "Unknown" and an unprobed build are not evidence of a CPU-only binary, so they keep the
        // normal verdict; only a confirmed CPU-only build drops the GPU claim.
        bool gpuUsable = !string.Equals(service.Status().Backend, "CPU only", StringComparison.OrdinalIgnoreCase);

        var fit = budget.Fit(model.SizeBytes, context, kvPerToken, gpuUsable);

        return new
        {
            path = model.Path,
            name = model.Name,
            size = model.SizeBytes,
            origin = model.Origin,
            managed = model.Managed,
            capabilities = model.Capabilities,
            projector = model.ProjectorPath,
            visionReady = model.VisionReady,
            context,
            kvMb = kvPerToken > 0 ? (long)(kvPerToken * context / 1048576.0) : 0,
            suggestedContext = budget.SuggestContext(model.SizeBytes, kvPerToken, Math.Max(service.Runtime.ContextSize, 4096), gpuUsable),
            flags = flags is null ? null : new
            {
                contextSize = flags.ContextSize,
                gpuLayers = flags.GpuLayers,
                cacheTypeK = flags.CacheTypeK,
                cacheTypeV = flags.CacheTypeV,
                flashAttention = flags.FlashAttention,
                extraArgs = flags.ExtraArgs,
                projector = flags.Projector
            },
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
