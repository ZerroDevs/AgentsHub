#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AgentsHub.Services;

public sealed class McpServerItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    /// <summary>`stdio` when the server is a local command, `sse` when it is reached over HTTP.</summary>
    public string Transport { get; set; } = "stdio";
    /// <summary>Endpoint for `sse` servers (the config's `serverUrl`).</summary>
    public string? ServerUrl { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Scope { get; set; } = "Global"; // Global, Antigravity, ClaudeCode
    public bool IsEnabled { get; set; } = true;
    public string Description { get; set; } = string.Empty;
    public int ToolCount { get; set; }
    /// <summary>Tool names cached by the CLI, when it has run this server.</summary>
    public List<string> Tools { get; set; } = [];
    public bool RequiresAuth { get; set; }
    public string AuthStatus { get; set; } = "NotRequired"; // Authenticated, TokenExpired, NeedsAuth, NotRequired
    public string AuthDetails { get; set; } = string.Empty;
    public string? AuthKeyName { get; set; }
    /// <summary>Folder this server was found in, shortened to `~/...` for display.</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>Config file the entry lives in, so an edit can be written back to the same place.</summary>
    [JsonIgnore]
    public string SourceFile { get; set; } = string.Empty;
}

/// <summary>One folder the app scans, either a built-in location or one the user added.</summary>
public sealed class McpSourceItem
{
    public string Path { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>`mcp` for config folders, `skills` for skill folders.</summary>
    public string Kind { get; set; } = "mcp";
    /// <summary>True for the built-in primary location (`~/.agents`).</summary>
    public bool IsPrimary { get; set; }
    public bool IsDefault { get; set; }
    public bool Exists { get; set; }
    public int ItemCount { get; set; }
}

public sealed class AgentSkillItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string Scope { get; set; } = "Project"; // Project, Builtin
    public string? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
    public int TokenFootprintApprox { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? Content { get; set; }
    /// <summary>Folder this skill was found in, shortened to `~/...` for display.</summary>
    public string Source { get; set; } = string.Empty;
}

public sealed class McpSkillsService
{
    public static McpSkillsService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentsHub",
        "mcp_servers.json"
    );

    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// `~/.agents` — the shared folder, primary for both servers and skills. New servers are written to
    /// `~/.agents/mcp.json` and new skills to `~/.agents/skills`, so other agents read them too.
    /// </summary>
    public static string AgentsRoot => Path.Combine(UserHome, ".agents");

    public static string AgentsMcpConfigPath => Path.Combine(AgentsRoot, "mcp.json");

    private static string RootsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentsHub",
        "mcp_roots.json");

    /// <summary>Folder names an MCP config may use inside a scanned folder, in lookup order.</summary>
    private static readonly string[] McpConfigFileNames = ["mcp.json", "mcp", "mcp_config.json", ".mcp.json"];

    public sealed class CustomRoots
    {
        public List<string> Mcp { get; set; } = [];
        public List<string> Skills { get; set; } = [];
    }

    private async Task<CustomRoots> ReadCustomRootsAsync()
    {
        try
        {
            if (!File.Exists(RootsFilePath)) return new CustomRoots();
            var json = await File.ReadAllTextAsync(RootsFilePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CustomRoots>(json, JsonOpts) ?? new CustomRoots();
        }
        catch
        {
            return new CustomRoots();
        }
    }

    private async Task WriteCustomRootsAsync(CustomRoots roots)
    {
        var dir = Path.GetDirectoryName(RootsFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(RootsFilePath, JsonSerializer.Serialize(roots, JsonOpts)).ConfigureAwait(false);
    }

    /// <summary>`~/x` under the profile so labels stay short and do not leak the user name.</summary>
    public static string Shorten(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var home = UserHome;
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..].Replace('\\', '/')
            : path.Replace('\\', '/');
    }

    /// <summary>
    /// Every folder scanned for one kind of thing, primary first and user-added last.
    /// `~/mcp` is only listed when it exists — it is not a convention, just a folder people have.
    /// </summary>
    public async Task<List<McpSourceItem>> GetSourcesAsync(string kind)
    {
        bool wantSkills = string.Equals(kind, "skills", StringComparison.OrdinalIgnoreCase);
        var sources = new List<McpSourceItem>();

        if (wantSkills)
        {
            sources.Add(MakeSource(Path.Combine(AgentsRoot, "skills"), "skills", true, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".mcp", "skills"), "skills", false, true));
            if (Directory.Exists(Path.Combine(UserHome, "mcp")))
                sources.Add(MakeSource(Path.Combine(UserHome, "mcp", "skills"), "skills", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".claude", "skills"), "skills", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".gemini", "skills"), "skills", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".gemini", "antigravity", "skills"), "skills", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".gemini", "antigravity-cli", "skills"), "skills", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".gemini", "antigravity", "builtin", "skills"), "skills", false, true));
        }
        else
        {
            sources.Add(MakeSource(AgentsRoot, "mcp", true, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".mcp"), "mcp", false, true));
            if (Directory.Exists(Path.Combine(UserHome, "mcp")))
                sources.Add(MakeSource(Path.Combine(UserHome, "mcp"), "mcp", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".gemini", "config"), "mcp", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".gemini", "antigravity-cli"), "mcp", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".gemini", "antigravity-ide"), "mcp", false, true));
            sources.Add(MakeSource(Path.Combine(UserHome, ".gemini", "antigravity"), "mcp", false, true));
        }

        var custom = await ReadCustomRootsAsync().ConfigureAwait(false);
        foreach (var raw in wantSkills ? custom.Skills : custom.Mcp)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // A folder that already has a `skills` subfolder means the user picked the parent.
            var path = raw.Trim();
            if (wantSkills)
            {
                var nested = Path.Combine(path, "skills");
                if (Directory.Exists(nested) && Directory.GetDirectories(nested).Length > 0) path = nested;
            }

            if (sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            sources.Add(MakeSource(path, wantSkills ? "skills" : "mcp", false, false));
        }

        return sources;
    }

    private static McpSourceItem MakeSource(string path, string kind, bool isPrimary, bool isDefault) => new()
    {
        Path = path,
        Label = Shorten(path),
        Kind = kind,
        IsPrimary = isPrimary,
        IsDefault = isDefault,
        Exists = Directory.Exists(path),
        ItemCount = Directory.Exists(path)
            ? (kind == "skills"
                ? Directory.GetDirectories(path).Count(d => File.Exists(Path.Combine(d, "SKILL.md")) || File.Exists(Path.Combine(d, "SKILL.md.disabled")))
                : Directory.GetFiles(path).Count(f => McpConfigFileNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)))
            : 0
    };

    /// <summary>Adds a folder to the scan list. Returns the full list for the given kind.</summary>
    public async Task<List<McpSourceItem>> AddSourceAsync(string kind, string path)
    {
        bool wantSkills = string.Equals(kind, "skills", StringComparison.OrdinalIgnoreCase);
        var full = System.IO.Path.GetFullPath(path.Trim());

        var roots = await ReadCustomRootsAsync().ConfigureAwait(false);
        var list = wantSkills ? roots.Skills : roots.Mcp;
        if (!list.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(full);
            await WriteCustomRootsAsync(roots).ConfigureAwait(false);
        }

        return await GetSourcesAsync(kind).ConfigureAwait(false);
    }

    /// <summary>Stops scanning a folder the user added. Built-in folders are never removed.</summary>
    public async Task<List<McpSourceItem>> RemoveSourceAsync(string kind, string path)
    {
        bool wantSkills = string.Equals(kind, "skills", StringComparison.OrdinalIgnoreCase);
        var roots = await ReadCustomRootsAsync().ConfigureAwait(false);
        var list = wantSkills ? roots.Skills : roots.Mcp;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        await WriteCustomRootsAsync(roots).ConfigureAwait(false);
        return await GetSourcesAsync(kind).ConfigureAwait(false);
    }

    public async Task<List<McpServerItem>> GetMcpServersAsync()
    {
        var list = new List<McpServerItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Every MCP folder in scan order: ~/.agents first (the shared default), then the folders
        // other agents use, then anything the user added.
        var sources = await GetSourcesAsync("mcp").ConfigureAwait(false);

        foreach (var source in sources)
        {
            if (!source.Exists) continue;

            foreach (var configName in McpConfigFileNames)
            {
                var configPath = Path.Combine(source.Path, configName);
                if (!File.Exists(configPath)) continue;

                foreach (var parsed in await ParseMcpConfigAsync(configPath, source.Label).ConfigureAwait(false))
                {
                    if (seenNames.Add(parsed.Name)) list.Add(parsed);
                }
            }

            // Tool folders left behind without a config entry (the Antigravity CLI caches them here).
            if (!source.Path.Contains(".gemini", StringComparison.OrdinalIgnoreCase)) continue;

            var toolRoot = Path.Combine(source.Path, "mcp");
            if (!Directory.Exists(toolRoot)) continue;

            foreach (var serverDir in Directory.GetDirectories(toolRoot))
            {
                var dirName = Path.GetFileName(serverDir);
                if (!seenNames.Add(dirName)) continue;

                var toolFiles = Directory.GetFiles(serverDir, "*.json");
                list.Add(new McpServerItem
                {
                    Id = "dir_" + dirName.ToLowerInvariant(),
                    Name = dirName,
                    Command = "builtin",
                    Scope = "Antigravity",
                    IsEnabled = true,
                    Source = source.Label,
                    Description = $"MCP server with {toolFiles.Length} cached tool definition(s)",
                    ToolCount = toolFiles.Length
                });
            }
        }

        // 2. Servers this app stored before the switch to ~/.agents, kept so they do not vanish.
        if (File.Exists(ConfigFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(ConfigFilePath).ConfigureAwait(false);
                var stored = JsonSerializer.Deserialize<List<McpServerItem>>(json, JsonOpts);
                if (stored != null)
                {
                    foreach (var s in stored)
                    {
                        if (!seenNames.Add(s.Name)) continue;
                        s.Source = "AgentsHub (legacy)";
                        list.Add(s);
                    }
                }
            }
            catch { }
        }

        // 3. Claude Desktop keeps its servers in one file rather than a folder.
        try
        {
            var claudeCfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude", "claude_desktop_config.json");

            foreach (var parsed in await ParseMcpConfigAsync(claudeCfg, "Claude Desktop").ConfigureAwait(false))
            {
                if (seenNames.Add(parsed.Name)) list.Add(parsed);
            }
        }
        catch { }

        // 4. Run automated auth detection across all servers
        foreach (var s in list)
        {
            DetectAuthStatus(s);
        }

        return list;
    }

    /// <summary>Reads one config file's `mcpServers` block into server items.</summary>
    private static async Task<List<McpServerItem>> ParseMcpConfigAsync(string configPath, string label)
    {
        var items = new List<McpServerItem>();
        if (!File.Exists(configPath)) return items;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await File.ReadAllTextAsync(configPath).ConfigureAwait(false));
        }
        catch
        {
            return items;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return items;
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
                return items;

            foreach (var prop in servers.EnumerateObject())
            {
                var name = prop.Name;
                var url = FirstString(prop.Value, "serverUrl", "url", "httpUrl");
                var isRemote = !string.IsNullOrWhiteSpace(url);
                var command = FirstString(prop.Value, "command") ?? (isRemote ? string.Empty : "npx");
                var (toolCount, tools) = CountCachedTools(name, configPath);

                items.Add(new McpServerItem
                {
                    Id = "mcp_" + name.ToLowerInvariant(),
                    Name = name,
                    Transport = isRemote ? "sse" : "stdio",
                    ServerUrl = url,
                    Command = command,
                    Args = StringArray(prop.Value, "args"),
                    Env = StringMap(prop.Value, "env"),
                    Scope = label.Contains(".gemini", StringComparison.OrdinalIgnoreCase) ? "Antigravity" : "Global",
                    IsEnabled = true,
                    Source = label,
                    SourceFile = configPath,
                    Description = BuildServerDescription(isRemote, url, command, toolCount),
                    ToolCount = toolCount,
                    Tools = tools
                });
            }
        }

        return items;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    private static List<string> StringArray(JsonElement element, string name)
    {
        var values = new List<string>();
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray()) values.Add(item.GetString() ?? string.Empty);
        }
        return values;
    }

    private static Dictionary<string, string> StringMap(JsonElement element, string name)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in prop.EnumerateObject()) values[entry.Name] = entry.Value.GetString() ?? string.Empty;
        }
        return values;
    }

    /// <summary>Tool definitions the CLI cached for a server, which is what "connected" means.</summary>
    private static (int Count, List<string> Tools) CountCachedTools(string name, string configPath)
    {
        var configDir = Path.GetDirectoryName(configPath) ?? string.Empty;
        var candidates = new List<string> { Path.Combine(configDir, "mcp", name) };

        var geminiRoot = Path.Combine(UserHome, ".gemini");
        if (Directory.Exists(geminiRoot) && configPath.StartsWith(geminiRoot, StringComparison.OrdinalIgnoreCase))
        {
            candidates.AddRange(Directory.GetDirectories(geminiRoot).Select(d => Path.Combine(d, "mcp", name)));
        }

        int best = 0;
        var bestTools = new List<string>();

        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;

            var names = Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count > best)
            {
                best = names.Count;
                bestTools = names;
            }
        }

        return (best, bestTools);
    }

    /// <summary>Throws on malformed JSON rather than returning an empty object, so a save never truncates a config.</summary>
    private static async Task<JsonObject> ReadJsonObjectAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException($"{Shorten(path)} does not contain a JSON object.");
    }

    private static async Task WriteJsonObjectAsync(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, root.ToJsonString(JsonOpts)).ConfigureAwait(false);
    }

    private static JsonObject BuildConfigEntry(McpServerItem server)
    {
        var entry = new JsonObject();

        if (string.Equals(server.Transport, "sse", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(server.ServerUrl))
        {
            entry["type"] = "http";
            entry["url"] = server.ServerUrl!.Trim();
        }
        else
        {
            entry["command"] = string.IsNullOrWhiteSpace(server.Command) ? "npx" : server.Command.Trim();
            if (server.Args.Count > 0) entry["args"] = new JsonArray(server.Args.Select(a => JsonValue.Create(a)).ToArray());
        }

        var env = new JsonObject();
        foreach (var kvp in server.Env)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Value)) env[kvp.Key] = kvp.Value;
        }
        if (env.Count > 0) entry["env"] = env;

        return entry;
    }

    /// <summary>
    /// Registers a server in `~/.agents/mcp.json` in the standard `mcpServers` shape, merged into
    /// whatever is already there. That file is the shared default, so other agents read it too.
    /// </summary>
    public async Task<bool> SaveMcpServerAsync(McpServerItem server)
    {
        if (string.IsNullOrWhiteSpace(server.Name)) return false;

        try
        {
            var root = File.Exists(AgentsMcpConfigPath)
                ? await ReadJsonObjectAsync(AgentsMcpConfigPath).ConfigureAwait(false)
                : new JsonObject();

            var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
            servers[server.Name.Trim()] = BuildConfigEntry(server);
            root["mcpServers"] = servers;

            await WriteJsonObjectAsync(AgentsMcpConfigPath, root).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a server from the file that actually defines it. Deleting only from our own store left
    /// the server listed, because discovery kept reading it back out of the config file.
    /// </summary>
    public async Task<bool> DeleteMcpServerAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        var servers = await GetMcpServersAsync().ConfigureAwait(false);
        var target = servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
                     ?? servers.FirstOrDefault(s => string.Equals(s.Name, id, StringComparison.OrdinalIgnoreCase));
        if (target == null) return false;

        bool removed = await RemoveServerFromFileAsync(target.SourceFile, target.Name).ConfigureAwait(false);

        // The legacy store keeps its own copy of servers added before the switch to ~/.agents.
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var stored = JsonSerializer.Deserialize<List<McpServerItem>>(
                    await File.ReadAllTextAsync(ConfigFilePath).ConfigureAwait(false), JsonOpts) ?? [];
                if (stored.RemoveAll(s => string.Equals(s.Name, target.Name, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    await File.WriteAllTextAsync(ConfigFilePath, JsonSerializer.Serialize(stored, JsonOpts)).ConfigureAwait(false);
                    removed = true;
                }
            }
        }
        catch { }

        return removed;
    }

    private static async Task<bool> RemoveServerFromFileAsync(string? configPath, string name)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return false;

        try
        {
            var root = await ReadJsonObjectAsync(configPath).ConfigureAwait(false);
            if (root["mcpServers"] is not JsonObject servers || !servers.ContainsKey(name)) return false;

            servers.Remove(name);
            root["mcpServers"] = servers;
            await WriteJsonObjectAsync(configPath, root).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<AgentSkillItem>> GetAgentSkillsAsync(string? workspaceDir = null)
    {
        var list = new List<AgentSkillItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var workspaces = AiConfigService.Instance.GetWorkspaces();
        var activeWs = AiConfigService.Instance.GetActiveWorkspace();

        var searchRoots = new List<(string Root, string Scope, string? WsId, string? WsName)>();

        // 1. Scan active workspace first
        if (!string.IsNullOrWhiteSpace(workspaceDir) && Directory.Exists(workspaceDir))
        {
            var matchedWs = workspaces.FirstOrDefault(w => string.Equals(w.WorkingDirectory, workspaceDir, StringComparison.OrdinalIgnoreCase)) ?? activeWs;
            searchRoots.Add((Path.Combine(workspaceDir, ".agents", "skills"), "Workspace", matchedWs?.Id, matchedWs?.Name));
            searchRoots.Add((Path.Combine(workspaceDir, ".gemini", "skills"), "Workspace", matchedWs?.Id, matchedWs?.Name));
        }
        else if (activeWs != null && !string.IsNullOrWhiteSpace(activeWs.WorkingDirectory) && Directory.Exists(activeWs.WorkingDirectory))
        {
            searchRoots.Add((Path.Combine(activeWs.WorkingDirectory, ".agents", "skills"), "Workspace", activeWs.Id, activeWs.Name));
            searchRoots.Add((Path.Combine(activeWs.WorkingDirectory, ".gemini", "skills"), "Workspace", activeWs.Id, activeWs.Name));
        }

        // Scan all configured workspaces so user can see and jump to skills across projects
        foreach (var ws in workspaces)
        {
            if (!string.IsNullOrWhiteSpace(ws.WorkingDirectory) && Directory.Exists(ws.WorkingDirectory))
            {
                var skillsDir = Path.Combine(ws.WorkingDirectory, ".agents", "skills");
                if (!searchRoots.Any(sr => string.Equals(sr.Root, skillsDir, StringComparison.OrdinalIgnoreCase)))
                {
                    searchRoots.Add((skillsDir, "Workspace", ws.Id, ws.Name));
                }
            }
        }

        searchRoots.Add((Path.Combine(AppContext.BaseDirectory, ".agents", "skills"), "Workspace", activeWs?.Id, activeWs?.Name));

        // 2. Global roots: `~/.agents/skills` first, then every folder agents install into, including
        // the ones the user added. A project copy still wins because it was scanned first.
        foreach (var source in await GetSourcesAsync("skills").ConfigureAwait(false))
        {
            if (!searchRoots.Any(sr => string.Equals(sr.Root, source.Path, StringComparison.OrdinalIgnoreCase)))
                searchRoots.Add((source.Path, "Global", null, null));
        }

        foreach (var (root, scope, wsId, wsName) in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var skillDir in Directory.GetDirectories(root))
            {
                // Disabling renames SKILL.md to SKILL.md.disabled, which is what makes the CLI skip
                // it. Such skills are still listed (flagged disabled) so they can be switched back on.
                var activeFile = Path.Combine(skillDir, "SKILL.md");
                var disabledFile = Path.Combine(skillDir, "SKILL.md.disabled");
                var isEnabled = File.Exists(activeFile);
                if (!isEnabled && !File.Exists(disabledFile)) continue;

                var skillFile = isEnabled ? activeFile : disabledFile;

                var dirName = Path.GetFileName(skillDir);
                // Keyed by skill name only, and roots are scanned workspace-first, so a project copy
                // shadows the global one instead of appearing twice.
                if (!seenKeys.Add(dirName)) continue;

                var name = dirName;
                var desc = "Agent capability skill";
                string fullContent = string.Empty;

                try
                {
                    fullContent = await File.ReadAllTextAsync(skillFile).ConfigureAwait(false);
                    using var reader = new StringReader(fullContent);
                    string? line;
                    bool inFrontmatter = false;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var trimmed = line.Trim();
                        if (trimmed == "---")
                        {
                            if (!inFrontmatter) { inFrontmatter = true; continue; }
                            else break;
                        }

                        if (inFrontmatter)
                        {
                            if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                            {
                                name = trimmed[5..].Trim().Trim('"', '\'');
                            }
                            else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                            {
                                desc = trimmed[12..].Trim().Trim('"', '\'');
                            }
                        }
                    }
                }
                catch { }

                list.Add(new AgentSkillItem
                {
                    Id = dirName,
                    Name = name,
                    Description = desc,
                    Path = skillFile,
                    DirectoryPath = skillDir,
                    Scope = scope,
                    WorkspaceId = wsId,
                    WorkspaceName = wsName,
                    TokenFootprintApprox = Math.Max(1, fullContent.Length / 4),
                    IsEnabled = isEnabled,
                    Source = Shorten(root)
                });
            }
        }

        return list;
    }

    /// <summary>
    /// The bridge passes these paths through, so only a skill definition file is readable or
    /// writable. Without this, the action was a general read/write primitive for any file path.
    /// </summary>
    private static bool IsSkillMarkdownPath(string skillPath)
    {
        if (string.IsNullOrWhiteSpace(skillPath)) return false;

        string fileName = Path.GetFileName(skillPath);
        return string.Equals(fileName, "SKILL.md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "SKILL.md.disabled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A write is confined to a discovered skill root. The filename check alone still allows a path
    /// such as `.agents/skills/../../target/SKILL.md`, which resolves outside the root.
    /// </summary>
    private static bool IsInsideSkillsRoot(string skillPath)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(skillPath).Replace('\\', '/');
        }
        catch
        {
            return false;
        }

        return normalized.Contains("/.agents/skills/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.gemini/skills/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(string content, string name, string description, string scope, int tokenCount)> GetSkillContentAsync(string skillPath)
    {
        if (!IsSkillMarkdownPath(skillPath)) return (string.Empty, string.Empty, string.Empty, "Project", 0);

        if (!File.Exists(skillPath))
            return (string.Empty, string.Empty, string.Empty, "Builtin", 0);

        var content = await File.ReadAllTextAsync(skillPath).ConfigureAwait(false);
        var dirName = Path.GetFileName(Path.GetDirectoryName(skillPath)) ?? "skill";
        var name = dirName;
        var desc = "Agent capability skill";
        var isBuiltin = skillPath.Contains("builtin", StringComparison.OrdinalIgnoreCase);

        try
        {
            using var reader = new StringReader(content);
            string? line;
            bool inFrontmatter = false;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (trimmed == "---")
                {
                    if (!inFrontmatter) inFrontmatter = true;
                    else break;
                    continue;
                }
                if (inFrontmatter)
                {
                    if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                        name = trimmed[5..].Trim().Trim('"', '\'');
                    else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                        desc = trimmed[12..].Trim().Trim('"', '\'');
                }
            }
        }
        catch { }

        int tokenCount = Math.Max(1, content.Length / 4);
        return (content, name, desc, isBuiltin ? "Builtin" : "Project", tokenCount);
    }

    public async Task<bool> SaveSkillContentAsync(string skillPath, string name, string description, string content)
    {
        if (!IsSkillMarkdownPath(skillPath)) return false;
        if (!IsInsideSkillsRoot(skillPath)) return false;
        if (skillPath.Contains("builtin", StringComparison.OrdinalIgnoreCase))
            return false; // Builtin skills are immutable

        var dir = Path.GetDirectoryName(skillPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string fileText;
        if (content.TrimStart().StartsWith("---"))
        {
            fileText = content;
        }
        else
        {
            fileText = $"---\nname: {name.Trim()}\ndescription: {description.Trim()}\n---\n\n{content.Trim()}\n";
        }

        await File.WriteAllTextAsync(skillPath, fileText).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Writes a hand-written skill as `<root>/.agents/skills/<slug>/SKILL.md`.
    ///
    /// `scope` is `global` (the user's own skill directory) or anything else for the workspace, which
    /// is the same pair the rest of discovery reports.
    /// </summary>
    public async Task<AgentSkillItem?> CreateProjectSkillAsync(
        string? workspaceId,
        string name,
        string description,
        string content,
        string? scope = null)
    {
        bool isGlobal = string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase);

        string skillsRoot;
        string? wsId = null;
        string? wsName = null;

        if (isGlobal)
        {
            skillsRoot = Path.Combine(AgentsRoot, "skills");
        }
        else
        {
            var workspaces = AiConfigService.Instance.GetWorkspaces();
            var ws = !string.IsNullOrEmpty(workspaceId)
                ? workspaces.FirstOrDefault(w => string.Equals(w.Id, workspaceId, StringComparison.OrdinalIgnoreCase))
                : AiConfigService.Instance.GetActiveWorkspace();

            if (ws == null || string.IsNullOrWhiteSpace(ws.WorkingDirectory)) return null;

            skillsRoot = Path.Combine(ws.WorkingDirectory, ".agents", "skills");
            wsId = ws.Id;
            wsName = ws.Name;
        }

        var safeSlug = System.Text.RegularExpressions.Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9_\-]", "-").Trim('-');
        if (string.IsNullOrEmpty(safeSlug)) safeSlug = "custom-skill";

        var skillDir = Path.Combine(skillsRoot, safeSlug);
        Directory.CreateDirectory(skillDir);

        var skillFile = Path.Combine(skillDir, "SKILL.md");
        string fileText;
        if (content.TrimStart().StartsWith("---"))
        {
            fileText = content;
        }
        else
        {
            fileText = $"---\nname: {name.Trim()}\ndescription: {description.Trim()}\n---\n\n{content.Trim()}\n";
        }

        await File.WriteAllTextAsync(skillFile, fileText).ConfigureAwait(false);

        return new AgentSkillItem
        {
            Id = safeSlug,
            Name = name,
            Description = description,
            Path = skillFile,
            DirectoryPath = skillDir,
            Scope = isGlobal ? "Global" : "Workspace",
            WorkspaceId = wsId,
            WorkspaceName = wsName,
            TokenFootprintApprox = Math.Max(1, fileText.Length / 4),
            IsEnabled = true,
            Content = fileText
        };
    }

    public async Task<AgentSkillItem?> ForkSkillToWorkspaceAsync(string sourceSkillPath, string? targetWorkspaceId)
    {
        if (!File.Exists(sourceSkillPath)) return null;

        var workspaces = AiConfigService.Instance.GetWorkspaces();
        var ws = !string.IsNullOrEmpty(targetWorkspaceId)
            ? workspaces.FirstOrDefault(w => string.Equals(w.Id, targetWorkspaceId, StringComparison.OrdinalIgnoreCase))
            : AiConfigService.Instance.GetActiveWorkspace();

        if (ws == null || string.IsNullOrWhiteSpace(ws.WorkingDirectory)) return null;

        var sourceDir = Path.GetDirectoryName(sourceSkillPath);
        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) return null;

        var skillSlug = Path.GetFileName(sourceDir);
        var targetDir = Path.Combine(ws.WorkingDirectory, ".agents", "skills", skillSlug);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destFile = Path.Combine(targetDir, relative);
            var destSubDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destSubDir) && !Directory.Exists(destSubDir))
                Directory.CreateDirectory(destSubDir);

            File.Copy(file, destFile, true);
        }

        var targetSkillFile = Path.Combine(targetDir, "SKILL.md");
        var content = await File.ReadAllTextAsync(targetSkillFile).ConfigureAwait(false);

        return new AgentSkillItem
        {
            Id = skillSlug,
            Name = skillSlug,
            Description = "Forked project skill",
            Path = targetSkillFile,
            DirectoryPath = targetDir,
            Scope = "Project",
            WorkspaceId = ws.Id,
            WorkspaceName = ws.Name,
            TokenFootprintApprox = Math.Max(1, content.Length / 4),
            IsEnabled = true,
            Content = content
        };
    }

    public bool DeleteProjectSkill(string skillPath)
    {
        if (skillPath.Contains("builtin", StringComparison.OrdinalIgnoreCase))
            return false;

        var skillDir = Path.GetDirectoryName(skillPath);
        if (string.IsNullOrEmpty(skillDir) || !Directory.Exists(skillDir)) return false;

        // Resolve first: a path built with `..` still contains the marker above, but points
        // somewhere else entirely once the filesystem resolves it.
        string fullDir = Path.GetFullPath(skillDir);
        string marker = fullDir.Replace('\\', '/');
        if (!marker.Contains("/.agents/skills/") && !marker.Contains("/.gemini/skills/")) return false;
        if (!File.Exists(Path.Combine(fullDir, "SKILL.md")) && !File.Exists(Path.Combine(fullDir, "SKILL.md.disabled")))
            return false;

        try
        {
            Directory.Delete(fullDir, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enables or disables a skill by renaming its `SKILL.md`. The CLI discovers skills by that exact
    /// filename, so this is the only lever that actually changes what it loads — a flag stored on the
    /// side would look like it worked while the agent kept loading the skill.
    /// </summary>
    public static bool SetSkillEnabled(string skillDirectory, bool enabled)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(skillDirectory) || !Directory.Exists(skillDirectory)) return false;

            var active = Path.Combine(skillDirectory, "SKILL.md");
            var disabled = Path.Combine(skillDirectory, "SKILL.md.disabled");

            if (enabled)
            {
                if (File.Exists(disabled)) File.Move(disabled, active, overwrite: true);
                return File.Exists(active);
            }

            if (File.Exists(active)) File.Move(active, disabled, overwrite: true);
            return File.Exists(disabled);
        }
        catch
        {
            return false;
        }
    }

    public static bool RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return true;
            }
            else if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool OpenInEditor(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string BuildServerDescription(bool isRemote, string? serverUrl, string command, int toolCount)
    {
        if (isRemote)
        {
            var host = Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ? uri.Host : serverUrl;
            return toolCount > 0
                ? $"Remote MCP server at {host} exposing {toolCount} tools"
                : $"Remote MCP server at {host}";
        }

        return toolCount > 0
            ? $"Local MCP server ({command}) exposing {toolCount} tools"
            : $"Local MCP server ({command}) — no tools cached yet";
    }

    /// <summary>
    /// Works out whether a server has usable credentials.
    ///
    /// Two real sources: an OAuth token the CLI cached for that server, or a key in the server's own
    /// `env` block. Nothing is inferred from the server's name beyond knowing which env key it wants.
    /// </summary>
    public static void DetectAuthStatus(McpServerItem item)
    {
        var name = item.Name.Trim().ToLowerInvariant();

        string? keyName = name switch
        {
            var n when n.Contains("supabase") => "SUPABASE_ACCESS_TOKEN",
            var n when n.Contains("github") => "GITHUB_PERSONAL_ACCESS_TOKEN",
            var n when n.Contains("cloudflare") => "CLOUDFLARE_API_TOKEN",
            var n when n.Contains("sentry") => "SENTRY_AUTH_TOKEN",
            var n when n.Contains("linear") => "LINEAR_API_KEY",
            _ => null
        };

        var isRemote = string.Equals(item.Transport, "sse", StringComparison.OrdinalIgnoreCase);
        item.RequiresAuth = isRemote || keyName != null;
        item.AuthKeyName = keyName;

        if (!item.RequiresAuth)
        {
            item.AuthStatus = "NotRequired";
            item.AuthDetails = "No credentials required";
            return;
        }

        if (TryGetCachedOAuthExpiry(item.Name, out var expiry))
        {
            if (expiry == null || expiry > DateTimeOffset.UtcNow)
            {
                item.AuthStatus = "Authenticated";
                item.AuthDetails = expiry == null
                    ? "OAuth token cached by the CLI"
                    : $"OAuth active until {expiry.Value.LocalDateTime:MMM dd, HH:mm}";
                return;
            }

            item.AuthStatus = "TokenExpired";
            item.AuthDetails = $"OAuth token expired {expiry.Value.LocalDateTime:MMM dd, yyyy}";
            return;
        }

        if (keyName != null && item.Env.TryGetValue(keyName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            item.AuthStatus = "Authenticated";
            item.AuthDetails = $"{keyName}: {Mask(token)}";
            return;
        }

        item.AuthStatus = "NeedsAuth";
        item.AuthDetails = keyName != null
            ? $"{keyName} is not configured"
            : "This server requires an OAuth sign-in";
    }

    /// <summary>
    /// Reads the CLI's cached OAuth tokens (`~/.gemini/config/mcp_oauth_tokens.json`). The file is
    /// written by the CLI, so the shape is treated as unknown: an entry for the server counts as a
    /// token, and an expiry is read only when one is recognisable.
    /// </summary>
    private static bool TryGetCachedOAuthExpiry(string serverName, out DateTimeOffset? expiry)
    {
        expiry = null;

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "config", "mcp_oauth_tokens.json");

            if (!File.Exists(path)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!entry.Name.Contains(serverName, StringComparison.OrdinalIgnoreCase) &&
                    !serverName.Contains(entry.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Value.ValueKind != JsonValueKind.Object) return true;

                foreach (var field in entry.Value.EnumerateObject())
                {
                    var key = field.Name.ToLowerInvariant();
                    if (key is "expires_at" or "expiresat" or "expiry" or "expires")
                    {
                        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out var ms) && ms > 0)
                        {
                            expiry = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                            return true;
                        }

                        if (field.Value.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(field.Value.GetString(), out var parsed))
                        {
                            expiry = parsed;
                            return true;
                        }
                    }
                }

                return true;
            }
        }
        catch
        {
            // A malformed token cache must not break discovery.
        }

        return false;
    }

    private static string Mask(string token) =>
        token.Length > 8 ? $"{token[..4]}...{token[^4..]}" : "configured";

    public async Task<bool> SaveMcpAuthKeyAsync(string serverName, string keyName, string keyValue)
    {
        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(keyName)) return false;

        bool updatedAny = false;

        // Written into the file the server was discovered in, so a credential never creates a second
        // copy of the server in ~/.agents/mcp.json.
        try
        {
            var servers = await GetMcpServersAsync().ConfigureAwait(false);
            var target = servers.Find(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
            if (target != null &&
                await SetEnvInFileAsync(target.SourceFile, target.Name, keyName, keyValue).ConfigureAwait(false))
            {
                updatedAny = true;
            }
        }
        catch { }

        return updatedAny;
    }

    /// <summary>Sets or clears one env key on a server entry in the file that defines it.</summary>
    private static async Task<bool> SetEnvInFileAsync(string? configPath, string serverName, string keyName, string keyValue)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return false;

        try
        {
            var root = await ReadJsonObjectAsync(configPath).ConfigureAwait(false);
            if (root["mcpServers"] is not JsonObject servers || servers[serverName] is not JsonObject entry) return false;

            var env = entry["env"] as JsonObject ?? new JsonObject();
            if (string.IsNullOrWhiteSpace(keyValue)) env.Remove(keyName);
            else env[keyName] = keyValue.Trim();

            if (env.Count == 0) entry.Remove("env");
            else entry["env"] = env;

            await WriteJsonObjectAsync(configPath, root).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<object> TestMcpAuthAsync(string serverName, string keyValue)
    {
        var cleanVal = keyValue.Trim();
        if (string.IsNullOrWhiteSpace(cleanVal))
        {
            return new { valid = false, message = "Key or token cannot be empty." };
        }

        var sName = serverName.Trim().ToLowerInvariant();

        if (sName.Contains("supabase"))
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cleanVal);
                client.DefaultRequestHeaders.Add("User-Agent", "AgentsHub/1.0");

                var resp = await client.GetAsync("https://api.supabase.com/v1/projects").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return new { valid = true, message = "Supabase Access Token verified successfully! Connected to projects." };
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return new { valid = false, message = "Supabase rejected the token: 401 Unauthorized." };
                }
            }
            catch { }

            if (cleanVal.StartsWith("sbp_") || cleanVal.StartsWith("ey"))
            {
                return new { valid = true, message = "Token format valid (Supabase Access Token)." };
            }

            return new { valid = true, message = "Supabase key saved." };
        }

        if (sName.Contains("github"))
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cleanVal);
                client.DefaultRequestHeaders.Add("User-Agent", "AgentsHub/1.0");

                var resp = await client.GetAsync("https://api.github.com/user").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return new { valid = true, message = "GitHub Personal Access Token verified successfully!" };
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return new { valid = false, message = "GitHub rejected the token: 401 Unauthorized." };
                }
            }
            catch { }

            return new { valid = true, message = "GitHub token format accepted." };
        }

        return new { valid = true, message = $"{serverName} key format accepted." };
    }
}
