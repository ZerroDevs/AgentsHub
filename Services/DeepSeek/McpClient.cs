using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentsHub.Services.DeepSeek;

/// <summary>One server from `mcp_config.json`.</summary>
internal sealed record McpServerConfig(string Name, string Command, string[] Args, Dictionary<string, string> Env, string? Url);

/// <summary>One tool a server exposes, flattened for the text protocol.</summary>
internal sealed record McpToolInfo(string Server, string Name, string Description);

/// <summary>
/// A read-only MCP client over stdio.
///
/// MCP servers do not run inside DeepSeek, so we act as the client: spawn the configured command,
/// speak newline-delimited JSON-RPC, and expose the server's tools through the same text protocol as
/// the file actions. Only `tools/list` and `tools/call` are used, which is why this stays small.
///
/// Everything is fail-soft: a server that does not start inside the budget is skipped and the chat
/// carries on without it. A broken MCP config must never break the conversation.
/// </summary>
internal sealed class McpSession : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly System.Threading.Lock _gate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly StringBuilder _stderrTail = new();
    private int _nextId;
    private bool _disposed;

    private McpSession(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _stdin.AutoFlush = true;

        // stderr has to be drained or a chatty server fills the pipe buffer and blocks forever,
        // which looks like "initialize timed out" and then a dead session.
        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    string? line = process.StandardError.ReadLine();
                    if (line is null) break;
                    lock (_stderrTail)
                    {
                        _stderrTail.AppendLine(line);
                        if (_stderrTail.Length > 2000) _stderrTail.Remove(0, _stderrTail.Length - 2000);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Closing is normal.
            }
        });

        // Exactly one reader for the life of the session.
        //
        // The old code started a reader per request and abandoned it on timeout. That abandoned
        // task stayed blocked on ReadLine and swallowed the *next* response, so one slow call
        // poisoned the session and every later call timed out too.
        _ = Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                string? line = _stdout.ReadLine();
                if (line is null) break;

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue;   // server log noise on stdout
                }

                if (node?["id"] is not JsonNode idNode) continue;          // a notification
                if (!int.TryParse(idNode.ToJsonString(), out int id)) continue;
                if (!_pending.TryRemove(id, out var waiting)) continue;    // late reply, nobody wants it

                if (node["error"] is JsonObject error)
                {
                    waiting.TrySetException(new InvalidOperationException(error["message"]?.GetValue<string>() ?? "MCP error"));
                }
                else
                {
                    waiting.TrySetResult(node["result"]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed; fall through and fail anything still waiting.
        }

        string tail;
        lock (_stderrTail) tail = _stderrTail.ToString().Trim();

        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetException(new InvalidOperationException(
                tail.Length == 0
                    ? "the MCP server closed its output"
                    : $"the MCP server closed its output. It said: {tail[^Math.Min(300, tail.Length)..]}"));
        }
        _pending.Clear();
    }

    public static McpSession Start(McpServerConfig config, int startupBudgetMs = 30_000)
    {
        if (!string.IsNullOrWhiteSpace(config.Url))
        {
            throw new InvalidOperationException("remote MCP servers are not supported by this engine yet");
        }

        var info = new ProcessStartInfo
        {
            FileName = config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in config.Args) info.ArgumentList.Add(arg);
        foreach (var (key, value) in config.Env) info.Environment[key] = value;

        var process = new Process { StartInfo = info };
        process.Start();

        var session = new McpSession(process);
        try
        {
            session.Initialize(startupBudgetMs);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void Initialize(int budgetMs)
    {
        var result = Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "agents-hub", ["version"] = "1.0" },
        }, budgetMs);

        if (result is null) throw new InvalidOperationException("the server did not answer initialize");

        Notify("notifications/initialized", new JsonObject());
    }

    public List<McpToolInfo> ListTools(string server, int budgetMs = 10_000)
    {
        var tools = new List<McpToolInfo>();
        var result = Request("tools/list", new JsonObject(), budgetMs);
        if (result?["tools"] is not JsonArray array) return tools;

        foreach (var node in array)
        {
            if (node is not JsonObject tool) continue;
            string name = tool["name"]?.GetValue<string>() ?? string.Empty;
            if (name.Length == 0) continue;
            string description = tool["description"]?.GetValue<string>() ?? string.Empty;
            tools.Add(new McpToolInfo(server, name, description.Length > 140 ? description[..140] : description));
        }

        return tools;
    }

    /// <summary>Runs a tool and returns its text content, flattened.</summary>
    public string CallTool(string tool, JsonObject arguments, int budgetMs = 60_000)
    {
        var result = Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, budgetMs)
                     ?? throw new InvalidOperationException("the server did not answer tools/call");

        var text = new StringBuilder();
        if (result["content"] is JsonArray content)
        {
            foreach (var part in content)
            {
                if (part is JsonObject item && item["type"]?.GetValue<string>() == "text")
                {
                    text.AppendLine(item["text"]?.GetValue<string>() ?? string.Empty);
                }
            }
        }

        if (text.Length == 0) text.AppendLine(result.ToJsonString());
        if (result["isError"]?.GetValue<bool>() == true) throw new InvalidOperationException(text.ToString().Trim());

        return text.ToString().Trim();
    }

    private JsonNode? Request(string method, JsonObject parameters, int budgetMs)
    {
        int id;
        lock (_gate) id = ++_nextId;

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;

        try
        {
            _stdin.WriteLine(payload.ToJsonString());
        }
        catch (IOException ex)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException($"the MCP server is not accepting input: {ex.Message}");
        }

        try
        {
            if (!waiting.Task.Wait(budgetMs))
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException($"{method} timed out after {budgetMs / 1000}s");
            }
        }
        catch (AggregateException aggregate)
        {
            // Task.Wait wraps a fault, and the wrapper's message ("One or more errors occurred")
            // hid the real one all the way up to the chat.
            _pending.TryRemove(id, out _);
            throw aggregate.InnerException ?? aggregate;
        }

        return waiting.Task.GetAwaiter().GetResult();
    }

    private void Notify(string method, JsonObject parameters)
    {
        _stdin.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }.ToJsonString());
    }

    public bool IsAlive => !_disposed && !_process.HasExited;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _stdin.Close(); } catch (IOException) { }
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }
}

/// <summary>
/// Loads MCP servers from the same `mcp_config.json` the CLI engines use, and caches their tool
/// lists for the process lifetime so priming does not pay the startup cost twice.
/// </summary>
internal static class McpRegistry
{
    private static readonly ConcurrentDictionary<string, McpSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a live session, replacing one whose process has exited.
    ///
    /// A cached dead session failed every later call with "the server closed its output", which
    /// took the whole chat down with it because priming touches MCP on every conversation.
    /// </summary>
    private static McpSession Session(McpServerConfig config)
    {
        if (Sessions.TryGetValue(config.Name, out var existing))
        {
            if (existing.IsAlive) return existing;
            Sessions.TryRemove(config.Name, out _);
            existing.Dispose();
        }

        var fresh = McpSession.Start(config);
        Sessions[config.Name] = fresh;
        return fresh;
    }
    private static readonly System.Threading.Lock Gate = new();
    private static IReadOnlyList<McpToolInfo>? _cachedTools;
    private static IReadOnlyList<McpServerConfig>? _cachedConfigs;

    private static string? _profileHome;

    /// <summary>
    /// Points the registry at a profile's VirtualHome.
    ///
    /// The CLI keeps its servers in `<VirtualHome>/.gemini/config/mcp_config.json`, not in the
    /// machine-wide `.gemini/antigravity-*` folders this used to read. Reading the wrong file made
    /// every server invisible, so the model guessed tool names that did not exist.
    /// </summary>
    public static void UseProfileHome(string? home)
    {
        if (string.Equals(_profileHome, home, StringComparison.OrdinalIgnoreCase)) return;

        lock (Gate)
        {
            _profileHome = home;
            _cachedConfigs = null;
            _cachedTools = null;

            foreach (var session in Sessions.Values) session.Dispose();
            Sessions.Clear();
        }
    }

    public static IReadOnlyList<McpServerConfig> Load()
    {
        if (_cachedConfigs is not null) return _cachedConfigs;

        var configs = new List<McpServerConfig>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_profileHome))
        {
            candidates.Add(Path.Combine(_profileHome, ".gemini", "config", "mcp_config.json"));
            candidates.Add(Path.Combine(_profileHome, ".gemini", "antigravity-cli", "mcp_config.json"));
            candidates.Add(Path.Combine(_profileHome, ".gemini", "mcp_config.json"));
        }
        // The shared folder wins: a server registered in the app is written to `~/.agents/mcp.json`.
        candidates.Add(Path.Combine(home, ".agents", "mcp.json"));
        candidates.Add(Path.Combine(home, ".gemini", "config", "mcp_config.json"));

        foreach (var dir in new[] { "antigravity-cli", "antigravity-ide", "antigravity" })
        {
            candidates.Add(Path.Combine(home, ".gemini", dir, "mcp_config.json"));
        }

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) continue;

                foreach (var entry in servers.EnumerateObject())
                {
                    if (configs.Any(c => string.Equals(c.Name, entry.Name, StringComparison.OrdinalIgnoreCase))) continue;

                    string command = entry.Value.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? string.Empty : string.Empty;
                    // `serverUrl` is the Antigravity spelling; `url` is what `~/.agents/mcp.json` uses.
                    string? url = entry.Value.TryGetProperty("serverUrl", out var u) ? u.GetString()
                        : entry.Value.TryGetProperty("url", out var u2) ? u2.GetString()
                        : null;

                    var args = new List<string>();
                    if (entry.Value.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in argsProp.EnumerateArray())
                        {
                            if (a.ValueKind == JsonValueKind.String) args.Add(a.GetString() ?? string.Empty);
                        }
                    }

                    var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (entry.Value.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var e in envProp.EnumerateObject())
                        {
                            if (e.Value.ValueKind == JsonValueKind.String) env[e.Name] = e.Value.GetString() ?? string.Empty;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url)) continue;
                    configs.Add(new McpServerConfig(entry.Name, command, args.ToArray(), env, url));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A malformed config is skipped, not fatal.
            }
        }

        _cachedConfigs = configs;
        return configs;
    }

    /// <summary>Every tool across every reachable server. Servers that fail to start are skipped.</summary>
    public static IReadOnlyList<McpToolInfo> Tools()
    {
        lock (Gate)
        {
            if (_cachedTools is not null) return _cachedTools;

            var tools = new List<McpToolInfo>();

            foreach (var config in Load())
            {
                try
                {
                    tools.AddRange(Session(config).ListTools(config.Name));
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception or AggregateException)
                {
                    // Unreachable server: the chat simply does not see its tools.
                }
            }

            _cachedTools = tools;
            return tools;
        }
    }

    /// <summary>Where a server would have to be written for this profile.</summary>
    public static string ConfigPath() => !string.IsNullOrWhiteSpace(_profileHome)
        ? Path.Combine(_profileHome, ".gemini", "config", "mcp_config.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "mcp_config.json");

    public static string Call(string server, string tool, JsonObject arguments, int budgetMs = 60_000)
    {
        var configs = Load();

        // An empty config is the common case, and "no server named X" would hide it.
        if (configs.Count == 0)
        {
            throw new InvalidOperationException(
                $"no MCP servers are configured. Add one to {ConfigPath()} as mcpServers.<name>, then ask again.");
        }

        var config = configs.FirstOrDefault(c => string.Equals(c.Name, server, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException(
                         $"no MCP server named '{server}'. Configured: {string.Join(", ", configs.Select(c => c.Name))}");

        return Session(config).CallTool(tool, arguments, budgetMs);
    }
}
