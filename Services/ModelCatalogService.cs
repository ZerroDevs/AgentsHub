#nullable enable
using System.Diagnostics;
using System.Text;

namespace AgentsHub.Services;

/// <summary>
/// One selectable model reported by the active CLI, collapsed to its base identity.
/// <para>
/// The engine lists a separate id per reasoning tier (<c>gemini-3.8-flash-high</c>,
/// <c>-medium</c>, <c>-low</c>). Showing all of them as models duplicates the list, because the
/// composer already has a dedicated effort picker — so tiers are folded into <see cref="Tiers"/> and
/// the UI presents one row per model.
/// </para>
/// </summary>
/// <param name="Id">Base identifier, used when the chosen effort has no dedicated variant.</param>
/// <param name="Label">Display name with the tier suffix removed.</param>
/// <param name="Provider">Model family, derived from the id prefix.</param>
/// <param name="Tiers">Effort level to concrete engine id, for the tiers the engine exposes.</param>
public sealed record ModelInfoDto(
    string Id,
    string Label,
    string Provider,
    IReadOnlyDictionary<string, string> Tiers);

/// <summary>The model list plus the effort levels the engine accepts.</summary>
/// <param name="Models">Every model the CLI reported, in its own order.</param>
/// <param name="Efforts">Reasoning effort values accepted by the engine.</param>
/// <param name="Source">Where the list came from: <c>cli</c> when the engine answered, <c>fallback</c> otherwise.</param>
/// <param name="Error">Diagnostic message when the engine could not be queried.</param>
public sealed record ModelCatalogDto(
    IReadOnlyList<ModelInfoDto> Models,
    IReadOnlyList<string> Efforts,
    string Source,
    string? Error);

/// <summary>
/// Reads the model list from the CLI itself (<c>agy models</c>) instead of shipping a hardcoded table.
/// <para>
/// The engine prints one <c>id\tLabel</c> pair per line and answers in a couple of seconds, so the
/// result is cached for a few minutes. If the binary is missing or the call times out the catalog
/// degrades to an empty list with an error, and the UI keeps the session's current model rather than
/// inventing alternatives.
/// </para>
/// </summary>
public sealed class ModelCatalogService
{
    public static ModelCatalogService Instance { get; } = new();

    /// <summary>
    /// Effort levels offered to the user. `agy --effort` also accepts `xhigh` and `max` on paper, but
    /// they are not usable with the models this app targets, so they are not offered.
    /// </summary>
    private static readonly string[] EffortLevels = ["low", "medium", "high"];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private readonly Lock _gate = new();
    private CacheEntry? _cache;

    private sealed record CacheEntry(DateTime AtUtc, string Key, ModelCatalogDto Catalog);

    public async Task<ModelCatalogDto> GetCatalogAsync(string? cli, CancellationToken cancellationToken = default)
    {
        string engine = NormaliseEngine(cli);

        lock (_gate)
        {
            if (_cache != null && _cache.Key == engine && DateTime.UtcNow - _cache.AtUtc < CacheTtl)
            {
                return _cache.Catalog;
            }
        }

        ModelCatalogDto catalog = engine switch
        {
            "antigravity" => await ProbeAgyAsync(cancellationToken).ConfigureAwait(false),
            _ => new ModelCatalogDto([], EffortLevels, "fallback", $"No model probe is implemented for '{engine}'.")
        };

        lock (_gate)
        {
            _cache = new CacheEntry(DateTime.UtcNow, engine, catalog);
        }

        return catalog;
    }

    /// <summary>Drops the cached catalog so the next request re-queries the engine.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    private static string NormaliseEngine(string? cli)
    {
        if (string.IsNullOrWhiteSpace(cli)) return "antigravity";

        string id = cli.Trim().ToLowerInvariant().Replace('-', '_');
        // Sessions older than the per-session engine field still report the workspace CLI, and the
        // agent chat runs agy whatever that workspace says, so Hub and agy share one catalogue.
        if (id.Contains("antigravity", StringComparison.Ordinal) || id is "agy" or "hub") return "antigravity";
        if (id.Contains("claude", StringComparison.Ordinal)) return "claude";
        if (id.Contains("codex", StringComparison.Ordinal)) return "codex";
        return "custom";
    }

    private static async Task<ModelCatalogDto> ProbeAgyAsync(CancellationToken cancellationToken)
    {
        string? binary = ResolveAgyBinary();
        if (binary == null)
        {
            return new ModelCatalogDto([], EffortLevels, "fallback", "The agy CLI was not found on PATH.");
        }

        try
        {
            var startInfo = new ProcessStartInfo(binary, "models")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            ProcessSpawnLog.Record(startInfo.FileName, startInfo.Arguments, startInfo);
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new ModelCatalogDto([], EffortLevels, "fallback", "The agy model list timed out.");
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            var models = ParseModelList(stdout);
            if (models.Count == 0)
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? "No models were reported." : stderr.Trim();
                return new ModelCatalogDto([], EffortLevels, "fallback", detail);
            }

            return new ModelCatalogDto(models, EffortLevels, "cli", null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModelCatalogService] agy models failed: {ex.Message}");
            return new ModelCatalogDto([], EffortLevels, "fallback", ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // the process is already gone or cannot be signalled
        }
    }

    /// <summary>
    /// Parses `<c>id\tLabel</c>` lines and folds per-tier ids into one entry per base model. Progress
    /// chatter such as "Fetching available models…" has no tab and is skipped, which keeps the parser
    /// tolerant of extra output.
    /// </summary>
    private static List<ModelInfoDto> ParseModelList(string output)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, (string Label, string Provider, Dictionary<string, string> Tiers)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            string id = line[..tab].Trim();
            string label = line[(tab + 1)..].Trim();
            if (id.Length == 0) continue;

            string? tier = TierOf(id);
            string baseId = tier == null ? id : id[..^(tier.Length + 1)];
            string baseLabel = tier == null ? label : StripTierSuffix(label, tier);

            if (!groups.TryGetValue(baseId, out var entry))
            {
                entry = (baseLabel.Length > 0 ? baseLabel : baseId, ProviderOf(baseId), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                groups[baseId] = entry;
                order.Add(baseId);
            }

            if (tier != null) entry.Tiers[tier] = id;
        }

        return [.. order.Select(baseId =>
        {
            var entry = groups[baseId];
            return new ModelInfoDto(baseId, entry.Label, entry.Provider, entry.Tiers);
        })];
    }

    /// <summary>Removes a trailing " (High)" style suffix, which the effort picker now owns.</summary>
    private static string StripTierSuffix(string label, string tier)
    {
        string trimmed = label.TrimEnd();
        if (!trimmed.EndsWith(')')) return label;

        int open = trimmed.LastIndexOf('(');
        if (open < 0) return label;

        string inner = trimmed[(open + 1)..^1].Trim();
        bool matches = inner.Equals(tier, StringComparison.OrdinalIgnoreCase)
            || (tier == "xhigh" && inner.Equals("extra high", StringComparison.OrdinalIgnoreCase));

        return matches ? trimmed[..open].TrimEnd() : label;
    }

    private static string ProviderOf(string id)
    {
        string lowered = id.ToLowerInvariant();
        if (lowered.StartsWith("gemini", StringComparison.Ordinal)) return "Gemini";
        if (lowered.StartsWith("claude", StringComparison.Ordinal)) return "Claude";
        if (lowered.StartsWith("gpt", StringComparison.Ordinal)) return "GPT-OSS";
        if (lowered.StartsWith("deepseek", StringComparison.Ordinal)) return "DeepSeek";

        int dash = lowered.IndexOf('-');
        return dash > 0 ? lowered[..dash] : lowered;
    }

    private static string? TierOf(string id)
    {
        string lowered = id.ToLowerInvariant();
        if (lowered.EndsWith("-xhigh", StringComparison.Ordinal)) return "xhigh";
        if (lowered.EndsWith("-high", StringComparison.Ordinal)) return "high";
        if (lowered.EndsWith("-medium", StringComparison.Ordinal)) return "medium";
        if (lowered.EndsWith("-low", StringComparison.Ordinal)) return "low";
        return null;
    }

    /// <summary>
    /// Finds the CLI the same way a shell would: PATH first, then the installer's default location.
    /// </summary>
    private static string? ResolveAgyBinary()
    {
        bool windows = OperatingSystem.IsWindows();
        string exe = windows ? "agy.exe" : "agy";

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string entry in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                string dir = entry.Trim().Trim('"');
                if (dir.Length == 0) continue;

                try
                {
                    string candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // malformed PATH entry, keep looking
                }
            }
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            string known = Path.Combine(localAppData, "agy", "bin", exe);
            if (File.Exists(known)) return known;
        }

        return null;
    }
}
