using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentsHub.Services.LlamaCpp;

/// <summary>A model file on this machine, either downloaded here or added by the person.</summary>
public sealed record LocalModel(
    string Path,
    string Name,
    long SizeBytes,
    string Origin,
    bool Managed,
    string[] Capabilities);

/// <summary>A GGUF repository as Hugging Face lists it, plus the tags a person actually filters on.</summary>
public sealed record HfModel(string Id, long Downloads, long Likes, string Pipeline, string[] Tags, string ParamLabel, string[] Capabilities);

/// <summary>One downloadable file inside a GGUF repository.</summary>
public sealed record HfFile(string Path, long SizeBytes, string Quant);

/// <summary>
/// The local model shelf: what is on disk, what Hugging Face has, and downloads between the two.
///
/// Two kinds of entry live side by side. Models downloaded here sit in the app's own folder and can
/// be deleted from the app. Models added by hand stay wherever the person keeps them; the app only
/// remembers the path, so nothing it did not create ever gets removed.
/// </summary>
public sealed class LlamaModelLibrary
{
    public static LlamaModelLibrary Instance { get; } = new();

    private const string SearchApi = "https://huggingface.co/api/models";
    private const string DownloadApi = "https://huggingface.co";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient Files = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub", "llama_models.json");

    public string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentsHub", "models");

    private sealed class Shelf
    {
        public List<string> Files { get; set; } = [];
        public List<string> Folders { get; set; } = [];
    }

    private Shelf Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new Shelf();
            return JsonSerializer.Deserialize<Shelf>(File.ReadAllText(SettingsPath)) ?? new Shelf();
        }
        catch
        {
            return new Shelf();
        }
    }

    private void Save(Shelf shelf)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(shelf));
        }
        catch
        {
            // A shelf that cannot be written still works for this session.
        }
    }

    // ---- what is on disk ----

    public IReadOnlyList<LocalModel> List()
    {
        var found = new Dictionary<string, LocalModel>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Directory.Exists(ModelsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(ModelsDirectory, "*.gguf"))
                {
                    if (IsProjector(file)) continue;
                    found[file] = Describe(file, "downloaded", true);
                }
            }
        }
        catch
        {
            // An unreadable folder is not an error worth failing the list over.
        }

        var shelf = Load();

        foreach (var file in shelf.Files)
        {
            try
            {
                if (File.Exists(file) && !IsProjector(file)) found[file] = Describe(file, "added", false);
            }
            catch
            {
            }
        }

        foreach (var folder in shelf.Folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;

                foreach (var file in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories))
                {
                    if (IsProjector(file)) continue;
                    found[file] = Describe(file, "folder", false);
                }
            }
            catch
            {
            }
        }

        return [.. found.Values.OrderByDescending(m => m.SizeBytes)];
    }

    public string? AddFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (!string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase)) return null;

        var shelf = Load();
        if (!shelf.Files.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
        {
            shelf.Files.Add(path);
            Save(shelf);
        }

        return path;
    }

    /// <summary>Remembers a folder. Its models are listed as they are, not copied anywhere.</summary>
    public int AddFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;

        var shelf = Load();
        if (!shelf.Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
        {
            shelf.Folders.Add(folder);
            Save(shelf);
        }

        try
        {
            return Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Stops tracking a model. A file the app downloaded is deleted; anything else is left alone.</summary>
    public bool Forget(string path, bool deleteFile)
    {
        bool removed = false;

        var shelf = Load();
        removed |= shelf.Files.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) > 0;
        removed |= shelf.Folders.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save(shelf);

        if (deleteFile && IsManaged(path))
        {
            try
            {
                File.Delete(path);
                removed = true;
            }
            catch
            {
                // A file locked by a running server stays; the caller sees the failure in the list.
            }
        }

        return removed;
    }

    public bool IsManaged(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(ModelsDirectory);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---- Hugging Face ----

    /// <summary>
    /// Searches GGUF repositories. Size and "uncensored" are not queryable fields on Hugging Face —
    /// parameter count is only in the repository name — so both are applied here rather than asked for.
    /// </summary>
    public async Task<List<HfModel>> SearchAsync(string query, string bucket, bool uncensoredOnly, int limit, CancellationToken ct = default)
    {
        var url = new StringBuilder($"{SearchApi}?filter=gguf&sort=downloads&direction=-1&limit=60");
        if (!string.IsNullOrWhiteSpace(query)) url.Append("&search=").Append(Uri.EscapeDataString(query.Trim()));

        using var response = await Http.GetAsync(url.ToString(), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var results = new List<HfModel>();

        foreach (var item in json.RootElement.EnumerateArray())
        {
            string id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (id.Length == 0) continue;

            var tags = new List<string>();
            if (item.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsEl.EnumerateArray())
                {
                    var value = tag.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) tags.Add(value);
                }
            }

            string haystack = id.ToLowerInvariant() + " " + string.Join(' ', tags).ToLowerInvariant();

            if (uncensoredOnly && !HaystackSaysUncensored(haystack)) continue;

            string label = ParamLabel(id);
            if (!MatchesBucket(label, bucket)) continue;

            string pipeline = item.TryGetProperty("pipeline_tag", out var pipe) ? pipe.GetString() ?? "" : "";

            results.Add(new HfModel(
                id,
                item.TryGetProperty("downloads", out var dl) ? dl.GetInt64() : 0,
                item.TryGetProperty("likes", out var lk) ? lk.GetInt64() : 0,
                pipeline,
                [.. tags],
                label,
                CapabilitiesOf(id, pipeline, tags)));

            if (results.Count >= limit) break;
        }

        return results;
    }

    /// <summary>The GGUF files in a repository, largest quant set first, split shards excluded.</summary>
    public async Task<List<HfFile>> FilesAsync(string repo, CancellationToken ct = default)
    {
        var files = new List<HfFile>();

        using var response = await Http.GetAsync($"{SearchApi}/{repo}/tree/main?recursive=true", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "file") continue;
            if (!item.TryGetProperty("path", out var pathEl)) continue;

            string path = pathEl.GetString() ?? "";
            if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;

            // A multi-part model needs every shard and the loader pointed at the first one; that is a
            // different feature, and offering half of one here would only fail after a huge download.
            if (Regex.IsMatch(path, @"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)) continue;

            files.Add(new HfFile(
                path,
                item.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0,
                QuantOf(path)));
        }

        return [.. files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Streams one file into the app's models folder. Writes to a .part beside the target so a
    /// cancelled or failed download never looks like a finished model.
    /// </summary>
    public async Task<LocalModel> DownloadAsync(string repo, string file, Action<long, long> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDirectory);

        string name = Path.GetFileName(file);
        string target = Path.Combine(ModelsDirectory, name);
        string partial = target + ".part";

        string url = $"{DownloadApi}/{repo}/resolve/main/{Uri.EscapeDataString(file).Replace("%2F", "/")}?download=true";

        // An interrupted download continues from its .part instead of starting the file over. The
        // server has to agree with a 206; a 200 means it sent the whole file and the old bytes go.
        long already = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (already > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(already, null);

        var token = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await Files.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        bool resuming = already > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resuming) already = 0;

        long remaining = response.Content.Headers.ContentLength ?? 0;
        long total = already + remaining;
        long received = already;

        progress(received, total);

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(partial, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            int read;

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress(received, total);
            }
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(partial, target);

        return Describe(target, "downloaded", true);
    }

    public void CleanPartialFiles()
    {
        try
        {
            if (!Directory.Exists(ModelsDirectory)) return;

            // A .part left by a crash is gigabytes of nothing; it is never a candidate for reuse.
            foreach (var leftover in Directory.EnumerateFiles(ModelsDirectory, "*.part"))
            {
                try { File.Delete(leftover); } catch { }
            }
        }
        catch
        {
        }
    }

    // ---- helpers ----

    private static LocalModel Describe(string path, string origin, bool managed)
    {
        return new LocalModel(
            path,
            Path.GetFileNameWithoutExtension(path),
            FileSize(path),
            origin,
            managed,
            // A local file carries no metadata, so its tags come from the name and its folder — the
            // only things a person can see anyway.
            CapabilitiesOf(path.Replace('\\', ' ').Replace('/', ' '), string.Empty, []));
    }

    /// <summary>
    /// Real byte size, following symlinks. A llama.cpp or Hugging Face cache stores models as links
    /// into a blob folder, and a link's own length is its target path, not the model.
    /// </summary>
    private static long FileSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length;
        }
        catch
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }
    }

    /// <summary>A multimodal projector is not a model: llama.cpp loads it alongside one.</summary>
    private static bool IsProjector(string path) =>
        Path.GetFileName(path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the model can do, as far as its own metadata says — for a local file that is only its
    /// name and folder. Hugging Face has no field for tool use, so those are keyword reads, labelled
    /// as such in the UI rather than claimed as fact.
    /// </summary>
    public static string[] CapabilitiesOf(string id, string pipeline, List<string> tags)
    {
        var found = new List<string>();
        string haystack = (id + " " + pipeline + " " + string.Join(' ', tags)).ToLowerInvariant();

        // A local file has no pipeline tag, so the names people actually download are recognised
        // here too: `Qwen3-VL-…`, `llava`, `gemma3`, `pixtral` and friends are vision models.
        if (haystack.Contains("image-text-to-text") || haystack.Contains("vision") || haystack.Contains("multimodal")
            || haystack.Contains("-vl") || haystack.Contains("vl-") || haystack.Contains("llava")
            || haystack.Contains("minicpm-v") || haystack.Contains("pixtral") || haystack.Contains("idefics")
            || haystack.Contains("moondream") || haystack.Contains("gemma-3") || haystack.Contains("gemma3"))
        {
            found.Add("vision");
        }

        if (haystack.Contains("text-to-image") || haystack.Contains("stable-diffusion") || haystack.Contains("sdxl")
            || haystack.Contains("flux") || haystack.Contains("sd3"))
        {
            found.Add("image");
        }

        if (haystack.Contains("function-calling") || haystack.Contains("tool-use") || haystack.Contains("tool_calls")
            || haystack.Contains("tools") || haystack.Contains("agent"))
        {
            found.Add("tools");
        }

        if (haystack.Contains("thinking") || haystack.Contains("reasoning") || haystack.Contains("deepseek-r1")
            || haystack.Contains("qwq") || haystack.Contains("reasoner") || haystack.Contains("distill"))
        {
            found.Add("thinking");
        }

        if (HaystackSaysUncensored(haystack)) found.Add("uncensored");

        // A plain chat model is a text model; saying so only when nothing more specific applies keeps
        // the row readable instead of tagging every file with everything.
        if (found.Count == 0 && !haystack.Contains("embed"))
        {
            found.Add("text");
        }

        return [.. found];
    }

    /// <summary>
    /// Bytes already on disk for a file that was interrupted. The shelf shows these as resumable
    /// instead of silently starting a multi-gigabyte download over.
    /// </summary>
    public long PartialSize(string file)
    {
        try
        {
            var partial = Path.Combine(ModelsDirectory, Path.GetFileName(file) + ".part");
            return File.Exists(partial) ? new FileInfo(partial).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool HaystackSaysUncensored(string haystack) =>
        haystack.Contains("uncensored", StringComparison.Ordinal)
        || haystack.Contains("abliterated", StringComparison.Ordinal)
        || haystack.Contains("heretic", StringComparison.Ordinal);

    /// <summary>Parameter count as the repository name states it, e.g. `Qwen3-8B-GGUF` → 8B.</summary>
    private static string ParamLabel(string id)
    {
        var match = Regex.Match(id, @"(?<!\d)(\d+(?:\.\d+)?)\s*[bB](?![a-zA-Z0-9])");
        return match.Success ? match.Groups[1].Value + "B" : "";
    }

    private static bool MatchesBucket(string label, string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket) || bucket is "all") return true;
        if (label.Length == 0) return false;

        if (!double.TryParse(label.TrimEnd('B'), out var size)) return false;

        return bucket switch
        {
            "small" => size <= 3,
            "1-7b" => size <= 7,
            "7-14b" => size > 7 && size <= 14,
            "14-32b" => size > 14 && size <= 32,
            "32b+" => size > 32,
            _ => true
        };
    }

    private static string QuantOf(string path)
    {
        string file = Path.GetFileNameWithoutExtension(path);
        var match = Regex.Match(file, @"(IQ\d+_[A-Z0-9]+|Q\d+(?:_[A-Z0-9]+)+|BF16|F16|F32)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "GGUF";
    }
}
