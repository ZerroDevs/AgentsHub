using System.Diagnostics;
using System.Text;

namespace AgentsHub.Services;

/// <summary>Static description of a tool the hub depends on.</summary>
public sealed class DependencyDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>"runtime" (dev prerequisites) or "agent" / "utility" (AI engines and network tools).</summary>
    public string Category { get; init; } = "agent";

    /// <summary>"winget", "powershell" or "browser".</summary>
    public string InstallKind { get; init; } = "powershell";

    public string SiteUrl { get; init; } = "";

    /// <summary>Full command line for the winget / powershell install kinds.</summary>
    public string InstallCommand { get; init; } = "";

    /// <summary>Short form shown in the UI when the tool is missing.</summary>
    public string CommandPreview { get; init; } = "";

    /// <summary>Executable names looked up on PATH (without extension).</summary>
    public string[] Commands { get; init; } = [];

    /// <summary>
    /// True when this tool can drive a chat session, not just a terminal. Everything else is
    /// detected and launched but marked "Not used" in the panel, so the list never implies support
    /// the app does not have.
    /// </summary>
    public bool ChatSupported { get; init; }

    /// <summary>Absolute fallbacks; may contain %ENVIRONMENT% tokens.</summary>
    public string[] FallbackPaths { get; init; } = [];

    /// <summary>
    /// PATH hits whose directory contains any of these tokens are ignored. Guards against the
    /// Microsoft Store "python.exe" stub in WindowsApps, which is not a real interpreter.
    /// </summary>
    public string[] ExcludedPathTokens { get; init; } = [];

    /// <summary>
    /// File names that must never be probed with --version, because the executable is a GUI
    /// launcher and probing it would open the application window.
    /// </summary>
    public string[] GuiExecutableNames { get; init; } = [];
}

/// <summary>Detection result for one tool.</summary>
public sealed class DependencyStatus
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "agent";
    public bool IsInstalled { get; init; }
    public string Version { get; init; } = "";
    public string Path { get; init; } = "";
    public string InstallKind { get; init; } = "powershell";
    public string SiteUrl { get; init; } = "";
    public string CommandPreview { get; init; } = "";
}

/// <summary>
/// Detects whether the external runtimes and CLI tools the hub drives are present.
///
/// Detection resolves executables from PATH plus a few standard install locations, then probes
/// each hit for a version string. Every probe spawns a short-lived process, so callers must use
/// <see cref="GetDependenciesStatusAsync"/> — it fans the probes out onto background threads and
/// never runs on the UI thread.
/// </summary>
public static class DependencyCheckerService
{
    // Some CLIs are slow to start: "hub --version" is a ~4s Python interpreter boot, so the
    // probe budget has to clear that. Probes run in parallel, so this bounds the whole scan.
    private const int VersionProbeTimeoutMs = 8000;

    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static IReadOnlyList<DependencyDefinition> Catalog { get; } =
    [
        // ---- 1. Core dev runtimes ----
        new DependencyDefinition
        {
            Id = "node",
            Name = "Node.js LTS",
            Description = "Required by the Claude Code CLI and other npm-distributed agents.",
            Category = "runtime",
            InstallKind = "winget",
            InstallCommand = "winget install OpenJS.NodeJS.LTS -e --accept-source-agreements --accept-package-agreements",
            CommandPreview = "winget install OpenJS.NodeJS.LTS",
            Commands = ["node"]
        },
        new DependencyDefinition
        {
            Id = "git",
            Name = "Git for Windows",
            Description = "Required by Claude Code and OpenCode for repository inspection and diff tracking.",
            Category = "runtime",
            InstallKind = "winget",
            InstallCommand = "winget install Git.Git -e --accept-source-agreements --accept-package-agreements",
            CommandPreview = "winget install Git.Git",
            Commands = ["git"]
        },
        new DependencyDefinition
        {
            Id = "python",
            Name = "Python 3.10+ & pip",
            Description = "Required by the Hub Agent engine.",
            Category = "runtime",
            InstallKind = "winget",
            InstallCommand = "winget install Python.Python.3.12 -e --accept-source-agreements --accept-package-agreements",
            CommandPreview = "winget install Python.Python.3.12",
            Commands = ["python", "py"],
            // The WindowsApps entry is the Store stub, not a real interpreter.
            ExcludedPathTokens = ["WindowsApps"]
        },

        // ---- 2. AI agents & network tools ----
        new DependencyDefinition
        {
            Id = "claude",
            Name = "Claude Code CLI",
            Description = "Anthropic's terminal coding agent.",
            Category = "agent",
            InstallKind = "powershell",
            SiteUrl = "https://claude.ai/",
            InstallCommand = "irm https://claude.ai/install.ps1 | iex",
            CommandPreview = "irm https://claude.ai/install.ps1 | iex",
            Commands = ["claude"],
            FallbackPaths =
            [
                @"%APPDATA%\npm\claude.cmd",
                @"%APPDATA%\npm\claude",
                @"%USERPROFILE%\.claude\local\claude.exe",
                @"~/.local/bin/claude",
                @"/usr/local/bin/claude",
                @"/opt/homebrew/bin/claude"
            ]
        },
        new DependencyDefinition
        {
            Id = "codex",
            Name = "OpenAI Codex CLI",
            Description = "OpenAI's terminal coding agent, installed through npm.",
            Category = "agent",
            InstallKind = "powershell",
            SiteUrl = "https://github.com/openai/codex",
            InstallCommand = "npm install -g @openai/codex",
            CommandPreview = "npm install -g @openai/codex",
            Commands = ["codex"],
            FallbackPaths =
            [
                @"%APPDATA%\npm\codex.cmd",
                @"%APPDATA%\npm\codex",
                @"%LOCALAPPDATA%\Programs\codex\codex.exe",
                @"~/.local/bin/codex",
                @"/usr/local/bin/codex",
                @"/opt/homebrew/bin/codex"
            ]
        },
        new DependencyDefinition
        {
            Id = "opencode",
            Name = "OpenCode CLI",
            ChatSupported = true,
            Description = "Open-source terminal agent with provider switching.",
            Category = "agent",
            InstallKind = "powershell",
            SiteUrl = "https://opencode.ai/",
            InstallCommand = "npm install -g opencode-ai",
            CommandPreview = "npm install -g opencode-ai",
            Commands = ["opencode"],
            FallbackPaths =
            [
                @"%APPDATA%\npm\opencode.cmd",
                @"%APPDATA%\npm\opencode",
                @"%LOCALAPPDATA%\Programs\opencode\opencode.exe",
                @"~/.local/bin/opencode",
                @"/usr/local/bin/opencode",
                @"/opt/homebrew/bin/opencode"
            ]
        },
        new DependencyDefinition
        {
            Id = "vyce",
            Name = "Vyce Code",
            Description = "Terminal agent with free tiers. Answers in the chat too, through a hidden console.",
            Category = "agent",
            // Chat needs a real console to type into, which only the Windows host can build. The Unix
            // route (a `script` terminal) is written but has never been run, so it is not claimed.
            ChatSupported = OperatingSystem.IsWindows(),
            InstallKind = "powershell",
            SiteUrl = "https://www.vyce-code.com/",
            InstallCommand = "npm i -g vyce-code",
            CommandPreview = "npm i -g vyce-code",
            Commands = ["vyce"],
            FallbackPaths =
            [
                @"%APPDATA%\npm\vyce.cmd",
                @"%APPDATA%\npm\vyce",
                @"~/.local/bin/vyce",
                @"/usr/local/bin/vyce",
                @"/opt/homebrew/bin/vyce"
            ]
        },
        new DependencyDefinition
        {
            Id = "qwen-code",
            Name = "Qwen Code CLI",
            Description = "Terminal agent built on Gemini CLI. Needs a Qwen Cloud, ModelStudio or third-party provider key.",
            Category = "agent",
            InstallKind = "powershell",
            SiteUrl = "https://github.com/QwenLM/qwen-code",
            InstallCommand = "npm install -g @qwen-code/qwen-code",
            CommandPreview = "npm install -g @qwen-code/qwen-code",
            Commands = ["qwen"],
            FallbackPaths =
            [
                @"%APPDATA%\npm\qwen.cmd",
                @"%APPDATA%\npm\qwen",
                @"~/.local/bin/qwen",
                @"/usr/local/bin/qwen",
                @"/opt/homebrew/bin/qwen"
            ]
        },
        new DependencyDefinition
        {
            Id = "crush",
            Name = "Crush",
            Description = "Charm's terminal agent. Model-agnostic, bring your own provider.",
            Category = "agent",
            InstallKind = "powershell",
            SiteUrl = "https://charm.land/crush",
            InstallCommand = "npm install -g @charmland/crush",
            CommandPreview = "npm install -g @charmland/crush",
            Commands = ["crush"],
            FallbackPaths =
            [
                @"%APPDATA%\npm\crush.cmd",
                @"%APPDATA%\npm\crush",
                @"%LOCALAPPDATA%\Programs\crush\crush.exe",
                @"~/.local/bin/crush",
                @"/usr/local/bin/crush",
                @"/opt/homebrew/bin/crush"
            ]
        },
        new DependencyDefinition
        {
            Id = "aider",
            Name = "Aider",
            Description = "Pair-programming agent. Bring your own model key.",
            Category = "agent",
            InstallKind = "powershell",
            SiteUrl = "https://aider.chat/",
            InstallCommand = "python -m pip install --user aider-chat",
            CommandPreview = "python -m pip install --user aider-chat",
            Commands = ["aider"],
            FallbackPaths =
            [
                @"%APPDATA%\Python\Scripts\aider.exe",
                @"%LOCALAPPDATA%\Programs\Python\Python313\Scripts\aider.exe",
                @"~/.local/bin/aider",
                @"/usr/local/bin/aider",
                @"/opt/homebrew/bin/aider"
            ]
        },
        new DependencyDefinition
        {
            Id = "antigravity",
            Name = "Antigravity CLI (agy)",
            ChatSupported = true,
            Description = "Google Antigravity terminal agent, run per isolated profile.",
            Category = "agent",
            InstallKind = "powershell",
            SiteUrl = "https://antigravity.google/",
            InstallCommand = "irm https://antigravity.google/cli/install.ps1 | iex",
            CommandPreview = "irm https://antigravity.google/cli/install.ps1 | iex",
            Commands = ["agy"],
            FallbackPaths =
            [
                @"%LOCALAPPDATA%\Programs\Antigravity CLI\agy.exe",
                @"%APPDATA%\npm\agy.cmd",
                @"~/.local/bin/agy",
                @"~/.agy/bin/agy",
                @"/usr/local/bin/agy",
                @"/opt/homebrew/bin/agy"
            ]
        },
    ];

    public static DependencyDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var definition in Catalog)
        {
            if (string.Equals(definition.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) return definition;
        }

        return null;
    }

    /// <summary>
    /// Detects every catalog entry concurrently. Safe to call from the UI thread: all process
    /// work happens on the thread pool.
    /// </summary>
    public static async Task<List<DependencyStatus>> GetDependenciesStatusAsync(CancellationToken cancellationToken = default)
    {
        var probes = Catalog
            .Select(definition => Task.Run(() => Detect(definition), cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        return [.. results];
    }

    /// <summary>Detects a single entry by id. Returns null when the id is unknown.</summary>
    public static DependencyStatus? GetStatus(string? id)
    {
        var definition = Find(id);
        return definition == null ? null : Detect(definition);
    }

    private static DependencyStatus Detect(DependencyDefinition definition)
    {
        string path = "";

        foreach (var command in definition.Commands)
        {
            var resolved = ResolveOnPath(command, definition.ExcludedPathTokens);
            if (resolved != null)
            {
                path = resolved;
                break;
            }
        }

        if (path.Length == 0)
        {
            foreach (var candidate in definition.FallbackPaths)
            {
                var resolved = ResolveFallback(candidate);
                if (resolved != null)
                {
                    path = resolved;
                    break;
                }
            }
        }

        // Never run --version against a GUI launcher; that would open the app window.
        bool probeAllowed = path.Length > 0 && !IsGuiLauncher(definition, path);
        string version = probeAllowed ? ProbeVersion(path) : "";

        // GUI installs keep their version in the folder name (…\App\v5.1.8\…), so fall back to it.
        if (version.Length == 0 && path.Length > 0)
        {
            version = ExtractVersion(path.Replace('\\', ' ').Replace('/', ' '));
        }

        return new DependencyStatus
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            Category = definition.Category,
            IsInstalled = path.Length > 0,
            Version = version,
            Path = path,
            InstallKind = definition.InstallKind,
            SiteUrl = definition.SiteUrl,
            CommandPreview = definition.CommandPreview
        };
    }

    private static bool IsGuiLauncher(DependencyDefinition definition, string path)
    {
        if (definition.GuiExecutableNames.Length == 0) return false;

        var fileName = Path.GetFileName(path);
        foreach (var guiName in definition.GuiExecutableNames)
        {
            if (string.Equals(fileName, guiName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves an executable against PATH and PATHEXT. Done in managed code rather than by
    /// shelling out to where.exe, so detection costs no extra process.
    /// </summary>
    private static string? ResolveOnPath(string command, string[] excludedTokens)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable)) return null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["", ".sh"];

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0) continue;

            bool excluded = false;
            foreach (var token in excludedTokens)
            {
                if (trimmed.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    excluded = true;
                    break;
                }
            }
            if (excluded) continue;

            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(trimmed, command + extension.ToLowerInvariant());
                    if (File.Exists(candidate)) return candidate;

                    if (!string.IsNullOrEmpty(extension))
                    {
                        candidate = Path.Combine(trimmed, command + extension.ToUpperInvariant());
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch
                {
                    // Malformed PATH entry; skip it.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves one fallback template. A single "*" directory segment is expanded against the
    /// filesystem, highest version first, for tools that install into a versioned folder such as
    /// %ProgramFiles%\Vendor\App\v*\App.exe
    /// </summary>
    private static string? ResolveFallback(string template)
    {
        var expanded = Expand(template);
        if (expanded.Length == 0) return null;

        if (!expanded.Contains('*'))
        {
            return File.Exists(expanded) ? expanded : null;
        }

        int star = expanded.IndexOf('*');
        int directoryEnd = expanded.LastIndexOf(Path.DirectorySeparatorChar, star);
        if (directoryEnd < 0) return null;

        var parent = expanded[..directoryEnd];
        var remainder = expanded[(directoryEnd + 1)..];

        int separator = remainder.IndexOf(Path.DirectorySeparatorChar);
        if (separator < 0) return null;

        var pattern = remainder[..separator];
        var tail = remainder[(separator + 1)..];

        try
        {
            if (!Directory.Exists(parent)) return null;

            // Descending so v5.1.8 wins over an older v3.x left behind by an upgrade.
            foreach (var directory in Directory.GetDirectories(parent, pattern).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(directory, tail);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch
        {
            // Unreadable directory; treat as a miss.
        }

        return null;
    }

    private static string Expand(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";

        var expanded = Environment.ExpandEnvironmentVariables(template);

        if (expanded.StartsWith("~/") || expanded.StartsWith("~\\"))
        {
            expanded = Path.Combine(UserProfile, expanded[2..]);
        }

        // ProgramFiles(x86) is not expanded by ExpandEnvironmentVariables on 64-bit hosts.
        if (template.Contains("%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase))
        {
            expanded = expanded.Replace("%ProgramFiles(x86)%", ProgramFilesX86, StringComparison.OrdinalIgnoreCase);
        }

        return expanded;
    }

    /// <summary>
    /// Runs "<tool> --version" and pulls the version out of the output. Returns an empty
    /// string when the probe times out or prints nothing useful.
    /// </summary>
    private static string ProbeVersion(string executablePath)
    {
        try
        {
            string fileName = executablePath;
            string arguments = "--version";

            var extension = Path.GetExtension(executablePath).ToLowerInvariant();
            if (OperatingSystem.IsWindows() && extension is ".cmd" or ".bat")
            {
                // cmd scripts cannot be launched directly with UseShellExecute = false.
                fileName = "cmd.exe";
                arguments = $"/c \"\"{executablePath}\" --version\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = UserProfile
            };

            using var process = Process.Start(startInfo);
            if (process == null) return "";

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(VersionProbeTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "";
            }

            string combined = stdout.GetAwaiter().GetResult() + " " + stderr.GetAwaiter().GetResult();
            return ExtractVersion(combined);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Pulls a version out of probe output without a regex. Takes the leading digits-and-dots run
    /// of the first token that starts with one, so build metadata and dates are not mistaken for
    /// the version ("v0.21.6+187.g1e0c773" yields v0.21.6, not the build date beside it).
    /// </summary>
    private static string ExtractVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        foreach (var rawToken in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            if (token.Length < 3) continue;

            int start = token[0] is 'v' or 'V' ? 1 : 0;
            if (start >= token.Length || !char.IsDigit(token[start])) continue;

            int end = start;
            while (end < token.Length && (char.IsDigit(token[end]) || token[end] == '.')) end++;

            var candidate = token[start..end].TrimEnd('.');
            if (candidate.Contains('.')) return "v" + candidate;
        }

        return "";
    }
}
