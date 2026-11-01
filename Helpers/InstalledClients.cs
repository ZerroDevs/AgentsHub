using System.Text.Json.Serialization;
using AgentsHub.Services;

namespace AgentsHub.Helpers;

/// <summary>
/// A desktop AI or developer client found on this machine but not yet on the Dev Tools page.
/// </summary>
public sealed record InstalledAiClient(string Name, string Target, string Source, bool IsIde = false);

/// <summary>
/// Finds installed AI desktop tools and developer editors (e.g. WorkBuddy, Devin, FreeBuff).
/// Uses direct well-known candidate paths first, followed by a fast bounded BFS directory walk across user and system program folders.
/// </summary>
public static class InstalledClients
{
    private sealed record KnownClient(
        string Name,
        string[] ExeFileNames,
        string[]? ExplicitPaths = null
    );

    private static readonly KnownClient[] Known =
    [
        // AI Tools & Workspace Apps requested by user
        new("FreeBuff Desktop",
            ["Freebuff.exe", "freebuff.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\@codebufffreebuff-desktop\Freebuff.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Freebuff\Freebuff.exe"),
                @"C:\Program Files\Freebuff\Freebuff.exe",
                @"C:\Program Files (x86)\Freebuff\Freebuff.exe"
            ]),

        new("WorkBuddy",
            ["WorkBuddy.exe", "workbuddy.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\WorkBuddy\WorkBuddy.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"WorkBuddy\WorkBuddy.exe"),
                @"C:\Program Files\WorkBuddy\WorkBuddy.exe",
                @"C:\Program Files (x86)\WorkBuddy\WorkBuddy.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"WorkBuddy\WorkBuddy.exe")
            ]),

        new("Devin",
            ["Devin.exe", "devin.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Devin\Devin.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Devin\Devin.exe"),
                @"C:\Program Files\Devin\Devin.exe",
                @"C:\Program Files (x86)\Devin\Devin.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Devin\Devin.exe")
            ]),

        // AI IDEs, Editors & Tools
        new("Antigravity App",
            ["Antigravity.exe"],
            [
                AiConfigService.FindAntigravityAppPath() ?? string.Empty,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\antigravity\Antigravity.exe"),
                @"C:\Program Files\antigravity\Antigravity.exe"
            ]),

        new("Antigravity IDE",
            ["Antigravity IDE.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Antigravity IDE\Antigravity IDE.exe"),
                @"C:\Program Files\Antigravity IDE\Antigravity IDE.exe"
            ]),

        new("Cursor",
            ["Cursor.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\cursor\Cursor.exe"),
                @"C:\Program Files\Cursor\Cursor.exe",
                @"C:\Program Files (x86)\Cursor\Cursor.exe"
            ]),

        new("Cline App",
            ["cline-app.exe", "Cline.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Cline\cline-app.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Cline\cline-app.exe"),
                @"C:\Program Files\Cline\cline-app.exe"
            ]),

        new("OpenCode Desktop",
            ["OpenCode.exe", "opencode-cli.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\@opencodedesktop\OpenCode.exe"),
                @"C:\Program Files\OpenCode\OpenCode.exe"
            ]),

        new("Visual Studio Code",
            ["Code.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"),
                @"C:\Program Files\Microsoft VS Code\Code.exe",
                @"C:\Program Files (x86)\Microsoft VS Code\Code.exe"
            ]),

        new("VS Code Insiders",
            ["Code - Insiders.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code Insiders\Code - Insiders.exe"),
                @"C:\Program Files\Microsoft VS Code Insiders\Code - Insiders.exe"
            ]),

        new("VSCodium",
            ["VSCodium.exe", "codium.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\VSCodium\VSCodium.exe"),
                @"C:\Program Files\VSCodium\VSCodium.exe"
            ]),

        new("Visual Studio",
            ["devenv.exe"],
            [
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\Common7\IDE\devenv.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\Common7\IDE\devenv.exe"
            ]),

        new("JetBrains Rider",
            ["rider64.exe", "rider.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Rider\bin\rider64.exe"),
                @"C:\Program Files\JetBrains\JetBrains Rider\bin\rider64.exe"
            ]),

        new("JetBrains IntelliJ IDEA",
            ["idea64.exe", "idea.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\IntelliJ IDEA\bin\idea64.exe"),
                @"C:\Program Files\JetBrains\IntelliJ IDEA\bin\idea64.exe"
            ]),

        new("JetBrains PyCharm",
            ["pycharm64.exe", "pycharm.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\PyCharm\bin\pycharm64.exe"),
                @"C:\Program Files\JetBrains\PyCharm\bin\pycharm64.exe"
            ]),

        new("JetBrains WebStorm",
            ["webstorm64.exe", "webstorm.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\WebStorm\bin\webstorm64.exe"),
                @"C:\Program Files\JetBrains\WebStorm\bin\webstorm64.exe"
            ]),

        new("Sublime Text",
            ["sublime_text.exe"],
            [
                @"C:\Program Files\Sublime Text\sublime_text.exe",
                @"C:\Program Files\Sublime Text 3\sublime_text.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Sublime Text\sublime_text.exe")
            ]),

        new("Notepad++",
            ["notepad++.exe"],
            [
                @"C:\Program Files\Notepad++\notepad++.exe",
                @"C:\Program Files (x86)\Notepad++\notepad++.exe"
            ]),

        new("Zed",
            ["Zed.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Zed\Zed.exe"),
                @"C:\Program Files\Zed\Zed.exe"
            ]),

        new("Trae",
            ["Trae.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Trae\Trae.exe"),
                @"C:\Program Files\Trae\Trae.exe"
            ]),

        new("PearAI",
            ["PearAI.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\PearAI\PearAI.exe"),
                @"C:\Program Files\PearAI\PearAI.exe"
            ]),

        new("Positron",
            ["Positron.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Positron\Positron.exe"),
                @"C:\Program Files\Positron\Positron.exe"
            ]),

        new("Void Editor",
            ["Void.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Void\Void.exe"),
                @"C:\Program Files\Void\Void.exe"
            ]),

        new("Continue",
            ["Continue.exe"]),

        new("Ollama",
            ["ollama app.exe", "ollama.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Ollama\ollama app.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Ollama\ollama.exe"),
                @"C:\Program Files\Ollama\ollama.exe"
            ]),

        new("LM Studio",
            ["LM Studio.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\LM Studio\LM Studio.exe"),
                @"C:\Program Files\LM Studio\LM Studio.exe"
            ]),

        new("Jan",
            ["Jan.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Jan\Jan.exe"),
                @"C:\Program Files\Jan\Jan.exe"
            ]),

        new("GitHub Desktop",
            ["GitHubDesktop.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"GitHubDesktop\GitHubDesktop.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\GitHub Desktop\GitHubDesktop.exe")
            ]),

        new("Aider",
            ["aider.exe"]),

        new("Shopify Magic",
            ["Magic.exe"]),

        new("Claude",
            ["Claude.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Claude\Claude.exe"),
                @"C:\Program Files\Claude\Claude.exe"
            ]),

        new("ChatGPT",
            ["ChatGPT.exe"],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ChatGPT\ChatGPT.exe"),
                @"C:\Program Files\ChatGPT\ChatGPT.exe"
            ])
    ];

    private const int MaxDepth = 3;
    private const int MaxDirsPerRoot = 800;

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".vs", "WindowsPowerShell", "Common Files", "MSBuild",
        "Microsoft.NET", "Reference Assemblies", "dotnet", "Windows Kits", "Microsoft SDKs",
        "Windows Defender", "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer",
        "WindowsInstallationAssistant", "DIFX", "Microsoft GameInput", "Microsoft Update Health Tools"
    };

    public static bool IsKnownIde(string? name, string? target)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(target)) return false;

        string n = name ?? "";
        string t = target ?? "";
        string exe = Path.GetFileName(t);

        string[] ideExeNames =
        [
            "code.exe", "cursor.exe", "antigravity ide.exe", "devin.exe", "workbuddy.exe",
            "code - insiders.exe", "vscodium.exe", "codium.exe", "zed.exe", "trae.exe",
            "positron.exe", "void.exe", "pearai.exe", "devenv.exe", "rider64.exe", "rider.exe",
            "idea64.exe", "idea.exe", "pycharm64.exe", "pycharm.exe", "webstorm64.exe", "webstorm.exe",
            "rustrover64.exe", "rustrover.exe", "fleet.exe", "clion64.exe", "clion.exe",
            "goland64.exe", "goland.exe", "sublime_text.exe", "notepad++.exe", "eclipse.exe"
        ];

        if (ideExeNames.Any(e => string.Equals(exe, e, StringComparison.OrdinalIgnoreCase)))
            return true;

        string[] ideKeywords =
        [
            "ide", "code", "cursor", "devin", "workbuddy", "studio", "rider", "pycharm",
            "intellij", "webstorm", "sublime", "editor", "vscodium", "rustrover", "fleet", "clion", "goland"
        ];

        return ideKeywords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Client names this scanner knows about, for reporting what was looked for.</summary>
    public static IReadOnlyList<string> KnownClientNames => [.. Known.Select(k => k.Name)];

    public static List<InstalledAiClient> FindInstalledClients()
    {
        var results = new List<InstalledAiClient>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Direct candidate paths first (fastest and most accurate)
        foreach (var known in Known)
        {
            if (known.ExplicitPaths == null) continue;
            foreach (var path in known.ExplicitPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    if (File.Exists(path))
                    {
                        var resolved = Path.GetFullPath(path);
                        if (!IsBuildArtifact(resolved) && seenTargets.Add(resolved))
                        {
                            seenNames.Add(known.Name);
                            results.Add(new InstalledAiClient(known.Name, resolved, "Default Location", IsKnownIde(known.Name, resolved)));
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        // 2. Build multi-lookup map for directory traversal: exeFileName -> list of matching KnownClient
        var exeMap = new Dictionary<string, List<KnownClient>>(StringComparer.OrdinalIgnoreCase);
        foreach (var known in Known)
        {
            foreach (var exe in known.ExeFileNames)
            {
                if (!exeMap.TryGetValue(exe, out var list))
                {
                    list = [];
                    exeMap[exe] = list;
                }
                list.Add(known);
            }
        }

        // 3. Single-pass bounded BFS traversal over search roots
        foreach (var root in SearchRoots())
        {
            ScanDirectory(root, exeMap, seenNames, seenTargets, results);
        }

        return [.. results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> SearchRoots()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new[]
        {
            Path.Combine(localApp, "Programs"),
            localApp,
            Path.Combine(appData, "Programs"),
            progFiles,
            progFilesX86,
            Path.Combine(localApp, "Microsoft", "WindowsApps"),
            Path.Combine(localApp, "JetBrains", "Toolbox", "apps"),
            Path.Combine(localApp, "JetBrains"),
            Path.Combine(progFiles, "JetBrains"),
            Path.Combine(progFiles, "Microsoft Visual Studio"),
            Path.Combine(progFilesX86, "Microsoft Visual Studio"),
            Path.Combine(userProfile, "scoop", "apps"),
            Path.Combine(userProfile, ".cargo", "bin"),
            Path.Combine(userProfile, ".antigravity"),
            Path.Combine(progFiles, "Sublime Text"),
            Path.Combine(progFiles, "Notepad++"),
            Path.Combine(progFilesX86, "Notepad++")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static void ScanDirectory(
        string root,
        Dictionary<string, List<KnownClient>> exeMap,
        HashSet<string> seenNames,
        HashSet<string> seenTargets,
        List<InstalledAiClient> results)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;

        while (queue.Count > 0 && visited < MaxDirsPerRoot)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;

            try
            {
                var files = Directory.GetFiles(dir, "*.exe");
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (exeMap.TryGetValue(fileName, out var matchedClients))
                    {
                        var resolved = Path.GetFullPath(file);
                        if (IsBuildArtifact(resolved)) continue;

                        foreach (var client in matchedClients)
                        {
                            if (!seenNames.Contains(client.Name) && seenTargets.Add(resolved))
                            {
                                seenNames.Add(client.Name);
                                results.Add(new InstalledAiClient(client.Name, resolved, root, IsKnownIde(client.Name, resolved)));
                            }
                        }
                    }
                }
            }
            catch { }

            if (depth >= MaxDepth) continue;

            try
            {
                var subDirs = Directory.GetDirectories(dir);
                foreach (var sub in subDirs)
                {
                    var subName = Path.GetFileName(sub);
                    if (SkipFolders.Contains(subName)) continue;
                    if (subName.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                        subName.StartsWith("crash", StringComparison.OrdinalIgnoreCase)) continue;

                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch { }
        }
    }

    private static bool IsBuildArtifact(string path) =>
        path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\obj\Debug\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\obj\Release\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\unins", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\uninstall", StringComparison.OrdinalIgnoreCase);
}
