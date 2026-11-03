using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentsHub.Services.DeepSeek;

/// <summary>
/// File and shell tools for the DeepSeek engine, ported from `refrenceDS/deepseek/cli.py` and
/// extended.
///
/// DeepSeek's web chat has no function calling, so the protocol is text: the client teaches the
/// model a fenced `tool` block holding one JSON object, executes it, and sends the results back as
/// the next message. Actions that touch the disk or the shell are marked destructive and are held
/// for approval before they run.
///
/// It is only for surfaces without function calling: the DeepSeek and Qwen web pages
/// (<see cref="Guide"/>) and a local llama.cpp chat (<see cref="LocalGuide"/>). Agents that bring
/// their own tool loop — Claude Code, OpenCode, Antigravity, the CLI engines — are never sent it.
/// </summary>
internal static class DeepSeekTools
{
    public const int MaxRead = 60_000;
    public const int MaxWrite = 200_000;
    public const int MaxResultChars = 120_000;
    public const int MaxSearchMatches = 60;
    public const int CommandTimeoutMs = 60_000;

    public static readonly string[] Actions =
        ["list_dir", "read_file", "write_file", "apply_patch", "search_text", "run_command", "move_file", "delete_file", "read_skill", "mcp_call"];

    /// <summary>Actions that change the disk or run code, and therefore ask first.</summary>
    public static bool RequiresApproval(string action) =>
        action is "write_file" or "apply_patch" or "run_command" or "move_file" or "delete_file"
            // An MCP tool can do anything, including write. Ask until a server proves otherwise.
            or "mcp_call";

    /// <summary>Shown as the step label in the timeline. `toolKind` is what classifies the node.</summary>
    public static (string Label, string Kind) Describe(string action) => action switch
    {
        "list_dir" => ("Explored", "search"),
        "search_text" => ("Searched", "search"),
        "read_file" or "read_skill" => ("Read", "read"),
        "write_file" or "apply_patch" => ("Wrote", "write"),
        "run_command" => ("Ran", "command"),
        "move_file" or "delete_file" => ("Changed", "write"),
        "mcp_call" => ("Called", "other"),
        _ => ("Ran", "other"),
    };

    /// <summary>A short line for the approval card: what this call is about to do.</summary>
    public static string Preview(string action, JsonElement args) => action switch
    {
        "write_file" => $"Write {Str(args, "path") ?? "?"} ({(Str(args, "content") ?? "").Length} chars)",
        "apply_patch" => $"Edit {Str(args, "path") ?? "?"}: replace \"{Short(Str(args, "old_text"), 30)}\"",
        "run_command" => $"Run: {Str(args, "command") ?? "?"}",
        "move_file" => $"Move {Str(args, "from") ?? Str(args, "path") ?? "?"} to {Str(args, "to") ?? "?"}",
        "delete_file" => $"Delete {Str(args, "path") ?? "?"}",
        "mcp_call" => $"Call {Str(args, "server") ?? "?"}.{Str(args, "tool") ?? "?"}",
        _ => action,
    };

    /// <summary>
    /// Marks a conversation that already carries the protocol.
    ///
    /// Priming used to run on every new session and left an "Acknowledge protocol" chat behind each
    /// time. The engine now reads the chat's own history for this line instead of keeping local
    /// state, so the check survives restarts and profile moves. Bump the version when the protocol
    /// text changes and old chats re-prime once.
    /// </summary>
    public const string Sentinel = "[[agents-hub-tool-protocol v1]]";

    /// <summary>
    /// The full protocol, for a chat surface with no function calling: DeepSeek's and Qwen's web
    /// pages. Both are primed with it as their first message, so it closes by asking for a one-line
    /// acknowledgement. A local model gets <see cref="LocalGuide"/> instead.
    /// </summary>
    public static string Guide(
        string root,
        IReadOnlyList<SkillEntry> skills,
        IReadOnlyList<McpToolInfo>? mcpTools = null)
    {
        string skillSection = SkillSection(skills);
        string mcpSection = McpSection(mcpTools);

        return $$"""
            {{Sentinel}}
            You are connected to a local desktop client that can read and write files and run commands
            on the user's computer. Work only through the protocol below.

            WORKING DIRECTORY
              {{root}}
              Every relative path resolves against it, and commands run inside it. You cannot touch
              anything outside it.

            HOW TO ASK FOR AN OPERATION
              Reply with one or more fenced blocks tagged `tool`. Each block holds exactly one JSON object:

              ```tool
              {"action": "read_file", "path": "app.py"}
              ```

              The client executes every block in the order written and sends you the results as the
              next message. Never describe, quote, or assume the contents of a file you have not read.

            EXAMPLE
              User: read app.py and tell me what it does
              You:
              ```tool
              {"action": "read_file", "path": "app.py"}
              ```
              Then the file arrives and you explain it. You do not answer from memory, and you do not
              write the file contents yourself.

            ACTIONS
              list_dir     {"action": "list_dir", "path": "."}
              read_file    {"action": "read_file", "path": "app.py"}
              search_text  {"action": "search_text", "query": "TODO", "path": "."}
              write_file   {"action": "write_file", "path": "app.py", "content": "full file text"}
              apply_patch  {"action": "apply_patch", "path": "app.py", "old_text": "exact text", "new_text": "replacement"}
              run_command  {"action": "run_command", "command": "python app.py"}
              move_file    {"action": "move_file", "from": "a.py", "to": "b.py"}
              delete_file  {"action": "delete_file", "path": "old.py"}
              read_skill   {"action": "read_skill", "name": "skill-name"}
              mcp_call     {"action": "mcp_call", "server": "name", "tool": "tool-name", "args": {} }

            RULES
              1. Read before you change: call read_file on any file you plan to modify, and base the new
                 content on what you actually received.
              2. Paths are relative to the working directory. No absolute paths and no "..".
              3. write_file replaces the entire file, so send complete content. For a small change to a
                 large file use apply_patch with old_text copied exactly from what you read.
              4. Keep explanations outside the blocks; one action per block.
              5. When the request needs no tools, or you are finished, answer normally with no `tool`
                 block. That ends the turn.
              6. Be economical: list once, read only what you need, and do not re-read what you have.
              7. Text is capped: large files come back truncated, and binary files are refused. Say so
                 instead of guessing.
              8. write_file, apply_patch, run_command, move_file and delete_file ask the user first. If
                 the user declines, do not retry the same call; explain what you need instead.
              9. After changing files, state briefly which files changed and what you did.{{skillSection}}{{mcpSection}}

            Reply with one short sentence confirming you understand, and nothing else.
            """;
    }

    /// <summary>
    /// The protocol a local model gets, for a server on 127.0.0.1 instead of a web page.
    ///
    /// Same block, same actions, a fraction of the words. The full guide spends its first half
    /// explaining itself to a webpage, which a 2B paying for every token cannot afford: it read the
    /// document instead of the request, and its answer was about the protocol. Everything here is
    /// load-bearing — the example is what gets a small model to emit a block at all, and the turn
    /// loop in <see cref="LlamaCpp.LocalChatEngine"/> enforces the rules the long version only states.
    /// </summary>
    public static string LocalGuide(
        string root,
        IReadOnlyList<SkillEntry> skills,
        IReadOnlyList<McpToolInfo>? mcpTools = null)
    {
        var actions = new List<string>
        {
            "  list_dir     {\"action\": \"list_dir\", \"path\": \".\"}",
            "  read_file    {\"action\": \"read_file\", \"path\": \"app.py\"}",
            "  search_text  {\"action\": \"search_text\", \"query\": \"TODO\", \"path\": \".\"}",
            "  write_file   {\"action\": \"write_file\", \"path\": \"app.py\", \"content\": \"whole file\"}",
            "  apply_patch  {\"action\": \"apply_patch\", \"path\": \"app.py\", \"old_text\": \"exact\", \"new_text\": \"new\"}",
            "  run_command  {\"action\": \"run_command\", \"command\": \"python app.py\"}",
            "  move_file    {\"action\": \"move_file\", \"from\": \"a.py\", \"to\": \"b.py\"}",
            "  delete_file  {\"action\": \"delete_file\", \"path\": \"old.py\"}",
        };

        // Only the actions this project can actually reach: a list of names a small model cannot use
        // is tokens spent on noise.
        if (skills.Count > 0) actions.Add("  read_skill   {\"action\": \"read_skill\", \"name\": \"skill-name\"}");
        if (mcpTools is { Count: > 0 }) actions.Add("  mcp_call     {\"action\": \"mcp_call\", \"server\": \"name\", \"tool\": \"tool\", \"args\": {}}");

        return $$"""
            You can read and write files and run commands in {{root}}.
            Paths are relative to it, and nothing outside it is reachable.

            ASKING FOR AN ACTION
              Reply with one fenced block tagged tool, holding one JSON object and nothing else:

              ```tool
              {"action": "read_file", "path": "app.py"}
              ```

              The app runs it and sends you the result. Then answer, or ask for the next action.
              Read a file before saying what is in it. When the request needs nothing from the disk,
              or you are finished, answer with no block.

            ACTIONS
              {{string.Join('\n', actions).TrimStart()}}

            RULES
              - Read a file before changing it, and build the new text from what you actually got back.
              - To change a file, send the write_file or apply_patch block itself. Never paste the file
                into the chat.
              - write_file replaces the whole file; apply_patch swaps one exact snippet copied from it.
              - write_file, apply_patch, run_command, move_file and delete_file ask the user first. If
                the user declines, do not retry the same call; explain what you need instead.
            {{SkillSection(skills)}}{{McpSection(mcpTools)}}
            """;
    }

    private static string SkillSection(IReadOnlyList<SkillEntry> skills) => skills.Count == 0
        ? string.Empty
        : "\n\nSKILLS\n  This project ships skills. Load one with `read_skill` before relying on it.\n"
          + string.Join('\n', skills.Select(s => $"  - {s.Name}: {s.Description}"));

    private static string McpSection(IReadOnlyList<McpToolInfo>? mcpTools) => mcpTools is null || mcpTools.Count == 0
        ? string.Empty
        : "\n\nMCP TOOLS\n  Servers expose tools you may call with `mcp_call`. Names are server.tool.\n"
          + string.Join('\n', mcpTools.Select(t => $"  - {t.Server}.{t.Name}: {t.Description}"));

    // ---------------------------------------------------------------- parsing

    // The line break after the tag is optional. A small model often writes ```tool{"action":…} on one
    // line, and a rule that required the break let that request through as ordinary text.
    private static readonly Regex ToolBlock = new(@"```[ \t]*tool[ \t]*\r?\n?(.*?)```", RegexOptions.Singleline);
    private static readonly Regex AnyBlock = new(@"```[ \t]*(?:tool|json)?[ \t]*\r?\n?(.*?)```", RegexOptions.Singleline);
    private static readonly Regex BareAction = new("\\{\\s*\"action\"\\s*:", RegexOptions.None);

    /// <summary>
    /// Pulls tool requests out of a reply. The `tool` tag is primary; any fenced JSON naming a known
    /// action is accepted as a fallback, and so is a bare object, because models drift.
    /// </summary>
    public static List<JsonElement> Extract(string answer)
    {
        var calls = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        bool TryAdd(string body)
        {
            body = body.Trim();
            if (body.Length == 0 || !seen.Add(body)) return false;

            JsonElement parsed;
            try
            {
                using var doc = JsonDocument.Parse(body);
                parsed = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return false;
            }

            if (parsed.ValueKind != JsonValueKind.Object) return false;
            if (!parsed.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String) return false;
            if (!Actions.Contains(action.GetString(), StringComparer.Ordinal)) return false;

            // Keep the whole object. Returning only the inner `args` dropped the `action` field,
            // which then arrived at Execute as "?" and failed as an unknown action. Callers read
            // flat keys first and fall back to the nested `args` object themselves.
            calls.Add(parsed);
            return true;
        }

        var fenced = new List<(int Start, int End)>();

        foreach (var match in ToolBlock.Matches(answer).Cast<Match>().Concat(AnyBlock.Matches(answer).Cast<Match>()))
        {
            fenced.Add((match.Index, match.Index + match.Length));
            TryAdd(match.Groups[1].Value);
        }

        // A small model often skips the fence and prints the object on its own. Reading that is the
        // difference between a tool step and a paragraph describing the step it meant to take.
        // Only when no block was understood: a reply that explains the protocol also quotes it, and
        // running that quote would read a file nobody asked for.
        if (calls.Count > 0) return calls;

        foreach (Match start in BareAction.Matches(answer))
        {
            if (fenced.Any(f => start.Index >= f.Start && start.Index < f.End)) continue;

            string? body = BalancedObject(answer, start.Index);
            if (body != null) TryAdd(body);
        }

        return calls;
    }

    /// <summary>
    /// The complete JSON object starting at `start`, or null when the reply was cut off before it
    /// closed. Braces inside string literals are skipped, so file content cannot end it early.
    /// </summary>
    private static string? BalancedObject(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>
    /// Actions a reply asked for that this client does not have.
    ///
    /// A small model invents names — `edit_file`, `patch`, `write` — and the call then goes nowhere,
    /// leaving the reply's JSON on screen as if it were an answer. Naming the miss is what turns that
    /// into a second, usable attempt.
    /// </summary>
    public static List<string> UnknownActions(string answer)
    {
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Collect(string body)
        {
            JsonElement parsed;
            try
            {
                using var doc = JsonDocument.Parse(body.Trim());
                parsed = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return;
            }

            if (parsed.ValueKind != JsonValueKind.Object) return;
            if (!parsed.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String) return;

            string name = action.GetString() ?? string.Empty;
            if (name.Length == 0 || Actions.Contains(name, StringComparer.Ordinal)) return;
            if (seen.Add(name)) unknown.Add(name);
        }

        foreach (var match in ToolBlock.Matches(answer).Cast<Match>().Concat(AnyBlock.Matches(answer).Cast<Match>()))
        {
            Collect(match.Groups[1].Value);
        }

        foreach (Match start in BareAction.Matches(answer))
        {
            string? body = BalancedObject(answer, start.Index);
            if (body != null) Collect(body);
        }

        return unknown;
    }

    /// <summary>
    /// The settled answer: what a person should read.
    ///
    /// Routed through <see cref="Visible"/> so a block the reply never closed is dropped too. Cutting
    /// only completed fences left a model that ran out of tokens mid-block with its JSON sitting in the
    /// transcript as if it were prose.
    /// </summary>
    public static string Strip(string answer) => Visible(answer, holdIncompleteFence: false).Trim();

    /// <summary>
    /// The visible part of a partially streamed answer.
    ///
    /// Streaming sends text before the block is complete, so stripping only at the end is too late:
    /// the raw JSON is already in the message. Text from an unterminated fence is held back, and a
    /// completed fence is included only when it is ordinary code.
    /// </summary>
    public static string Visible(string partial, bool holdIncompleteFence = true)
    {
        var sb = new StringBuilder(partial.Length);
        int index = 0;

        while (true)
        {
            int open = partial.IndexOf("```", index, StringComparison.Ordinal);
            if (open < 0)
            {
                int end = partial.Length;

                // One or two backticks at the very end may be a fence that has not arrived yet; sending
                // them put a lone "`" in the reply. The settled answer turns this off, because there it
                // can only be a real trailing backtick.
                while (holdIncompleteFence && end > index && partial[end - 1] == '`' && partial.Length - end < 2) end--;

                sb.Append(partial, index, end - index);
                break;
            }

            sb.Append(partial, index, open - index);

            int close = partial.IndexOf("```", open + 3, StringComparison.Ordinal);
            if (close < 0) break;   // still streaming the block: hold everything from here

            string body = partial[(open + 3)..close];
            index = close + 3;

            // A `tool` fence is a request even when its JSON did not parse, so the tag alone keeps it
            // out of the reply.
            if (!IsToolBlock(body) && ToolTagLength(body.TrimStart()) == 0)
            {
                sb.Append(partial, open, close + 3 - open);
                continue;
            }

            // Taking the blank line the block sat in keeps the paragraphs around it together instead
            // of leaving two empty lines where the request used to be.
            index += BlankLineLength(partial, index);
        }

        return sb.ToString();
    }

    /// <summary>The length of one blank line at `index`, or 0 when there is none.</summary>
    private static int BlankLineLength(string text, int index)
    {
        int i = index;
        int newlines = 0;

        while (i < text.Length && newlines < 2)
        {
            if (text[i] == '\n') { newlines++; i++; }
            else if (text[i] == '\r') i++;
            else return 0;
        }

        return newlines == 2 ? i - index : 0;
    }

    /// <summary>The length of a leading `tool` fence tag, or 0 when the text does not start with one.</summary>
    private static int ToolTagLength(string text)
    {
        const string Tag = "tool";
        if (!text.StartsWith(Tag, StringComparison.Ordinal)) return 0;
        if (text.Length == Tag.Length) return Tag.Length;

        // `toolbox` is a language, not our tag.
        char next = text[Tag.Length];
        return char.IsLetterOrDigit(next) || next == '_' ? 0 : Tag.Length;
    }

    private static bool IsToolBlock(string body)
    {
        // A raw fence body starts with its language tag (`tool`), which the regex path strips but a
        // text slice does not. Without this, every completed block parsed as JSON failure and was
        // rendered as ordinary code.
        string text = body.Trim();
        int newline = text.IndexOf('\n');
        if (newline > 0)
        {
            string first = text[..newline].Trim();
            if (first.Length > 0 && !first.Contains('{')) text = text[(newline + 1)..].Trim();
        }

        // Tag and object on the same line, which the newline rule above cannot see.
        int tag = ToolTagLength(text);
        if (tag > 0) text = text[tag..].TrimStart();

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String
                && Actions.Contains(action.GetString(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------- execution

    public static (string Summary, string Body) Execute(string action, JsonElement args, string root)
    {
        string rel = Str(args, "path") ?? ".";
        string target = Resolve(root, rel);

        return action switch
        {
            "list_dir" => ListDir(target, root),
            "read_file" => ReadFile(target, root),
            "search_text" => SearchText(root, target, Str(args, "query") ?? string.Empty),
            "write_file" => WriteFile(target, root, Str(args, "content")),
            "apply_patch" => ApplyPatch(target, root, Str(args, "old_text"), Str(args, "new_text")),
            "run_command" => RunCommand(root, Str(args, "command")),
            "move_file" => MoveFile(target, root, Str(args, "to")),
            "delete_file" => DeleteFile(target, root),
            "read_skill" => ReadSkill(root, Str(args, "name")),
            "mcp_call" => CallMcp(args),
            _ => throw new InvalidOperationException($"unknown action '{action}'"),
        };
    }

    private static string Resolve(string root, string relative)
    {
        string raw = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim();
        if (Path.IsPathRooted(raw)) throw new InvalidOperationException("absolute paths are not allowed; use a path inside the working directory");

        string fullRoot = Path.GetFullPath(root);
        string target = Path.GetFullPath(Path.Combine(fullRoot, raw));

        if (!string.Equals(target, fullRoot, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{raw}' is outside the working directory");
        }
        return target;
    }

    private static (string, string) ListDir(string target, string root)
    {
        if (!Directory.Exists(target)) throw new InvalidOperationException($"not a directory: {Rel(target, root)}");

        var entries = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(target)
                     .OrderBy(File.Exists)
                     .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                     .Take(400))
        {
            entries.Add(Directory.Exists(entry)
                ? Path.GetFileName(entry) + "/"
                : $"{Path.GetFileName(entry)}  ({new FileInfo(entry).Length} B)");
        }

        string listing = entries.Count == 0 ? "(empty)" : string.Join('\n', entries);
        return ($"{Rel(target, root)} -> {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}",
                $"{Rel(target, root)}:\n{listing}");
    }

    private static (string, string) ReadFile(string target, string root)
    {
        if (!File.Exists(target)) throw new InvalidOperationException($"not a file: {Rel(target, root)}");

        var bytes = File.ReadAllBytes(target);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 4096)) >= 0)
        {
            throw new InvalidOperationException($"{Rel(target, root)} looks binary; not sending it");
        }

        string text = Encoding.UTF8.GetString(bytes);
        bool truncated = text.Length > MaxRead;
        if (truncated) text = text[..MaxRead] + $"\n… truncated at {MaxRead} characters …";

        string name = Rel(target, root);
        return ($"read_file {name} -> {bytes.Length} B" + (truncated ? " (truncated)" : string.Empty),
                $"--- start of {name} ---\n{text}\n--- end of {name} ---");
    }

    private static (string, string) SearchText(string root, string target, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("search_text needs a 'query'");
        if (!Directory.Exists(target)) throw new InvalidOperationException($"not a directory: {Rel(target, root)}");

        var hits = new List<string>();
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", "dist", ".venv", "__pycache__",
        };

        foreach (var file in EnumerateFiles(target, skipped))
        {
            if (hits.Count >= MaxSearchMatches) break;

            try
            {
                if (new FileInfo(file).Length > 1_000_000) continue;

                int number = 0;
                foreach (var line in File.ReadLines(file))
                {
                    number++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                    string trimmed = line.Trim();
                    hits.Add($"{Rel(file, root)}:{number}: {(trimmed.Length > 160 ? trimmed[..160] : trimmed)}");
                    if (hits.Count >= MaxSearchMatches) break;
                }
            }
            catch (IOException)
            {
                // Locked files are skipped rather than failing the whole search.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        string body = hits.Count == 0 ? "(no matches)" : string.Join('\n', hits);
        return ($"search_text \"{query}\" -> {hits.Count} match{(hits.Count == 1 ? "" : "es")}", body);
    }

    private static IEnumerable<string> EnumerateFiles(string dir, HashSet<string> skipped)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (skipped.Contains(Path.GetFileName(sub))) continue;
            foreach (var file in EnumerateFiles(sub, skipped)) yield return file;
        }
        foreach (var file in Directory.EnumerateFiles(dir)) yield return file;
    }

    private static (string, string) WriteFile(string target, string root, string? content)
    {
        if (content is null) throw new InvalidOperationException("write_file needs a 'content' field");
        if (content.Length > MaxWrite) throw new InvalidOperationException($"refusing to write {content.Length} characters (limit {MaxWrite})");
        if (Directory.Exists(target)) throw new InvalidOperationException($"{Rel(target, root)} is a directory");

        string name = Rel(target, root);
        string? previous = File.Exists(target) ? File.ReadAllText(target) : null;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content, new UTF8Encoding(false));

        int written = Encoding.UTF8.GetByteCount(content);

        // A new file has nothing to compare against, so a diff would just echo the content back to
        // a model that wrote it a moment ago. Say what it is and stop there.
        if (previous is null)
        {
            return ($"wrote {name} ({written} B)", $"wrote {name}: new file, {content.Length} characters");
        }

        return ($"wrote {name} ({written} B)",
                $"wrote {name}: {content.Length} characters\n\n{Diff(previous, content)}");
    }

    private static (string, string) ApplyPatch(string target, string root, string? oldText, string? newText)
    {
        if (oldText is null || newText is null) throw new InvalidOperationException("apply_patch needs 'old_text' and 'new_text'");
        if (!File.Exists(target)) throw new InvalidOperationException($"not a file: {Rel(target, root)}");

        string text = File.ReadAllText(target);
        int at = text.IndexOf(oldText, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("old_text was not found; read the file again and copy the text exactly");
        if (text.IndexOf(oldText, at + 1, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("old_text appears more than once; include more surrounding text");
        }

        string updated = text[..at] + newText + text[(at + oldText.Length)..];
        File.WriteAllText(target, updated, new UTF8Encoding(false));

        string name = Rel(target, root);
        return ($"patched {name} (-{oldText.Length} +{newText.Length})",
                $"patched {name}: replaced {oldText.Length} characters with {newText.Length}\n\n{Diff(text, updated)}");
    }

    private static (string, string) RunCommand(string root, string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("run_command needs a 'command'");

        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = "/c " + command,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = info };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(CommandTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException($"command timed out after {CommandTimeoutMs / 1000}s");
        }

        string output = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim();
        if (output.Length > MaxRead) output = output[..MaxRead] + "\n… output truncated …";

        return ($"ran `{Short(command, 60)}` -> exit {process.ExitCode}", $"exit code {process.ExitCode}\n{output}");
    }

    private static (string, string) MoveFile(string target, string root, string? to)
    {
        if (string.IsNullOrWhiteSpace(to)) throw new InvalidOperationException("move_file needs a 'to' path");
        if (!File.Exists(target)) throw new InvalidOperationException($"not a file: {Rel(target, root)}");

        string destination = Resolve(root, to);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(target, destination, overwrite: false);

        return ($"moved {Rel(target, root)} -> {Rel(destination, root)}",
                $"moved {Rel(target, root)} to {Rel(destination, root)}");
    }

    private static (string, string) DeleteFile(string target, string root)
    {
        if (!File.Exists(target)) throw new InvalidOperationException($"not a file: {Rel(target, root)}");
        File.Delete(target);
        return ($"deleted {Rel(target, root)}", $"deleted {Rel(target, root)}");
    }

    /// <summary>Runs a tool on an MCP server. Arguments are passed through untouched.</summary>
    private static (string, string) CallMcp(JsonElement args)
    {
        string? server = Str(args, "server");
        string? tool = Str(args, "tool");
        if (string.IsNullOrWhiteSpace(server)) throw new InvalidOperationException("mcp_call needs a 'server'");
        if (string.IsNullOrWhiteSpace(tool)) throw new InvalidOperationException("mcp_call needs a 'tool'");

        var arguments = args.TryGetProperty("args", out var inner) && inner.ValueKind == JsonValueKind.Object
            ? System.Text.Json.Nodes.JsonNode.Parse(inner.GetRawText()) as System.Text.Json.Nodes.JsonObject
            : new System.Text.Json.Nodes.JsonObject();

        string output = McpRegistry.Call(server, tool, arguments ?? new System.Text.Json.Nodes.JsonObject());
        if (output.Length > MaxRead) output = output[..MaxRead] + "\n… output truncated …";

        return ($"{server}.{tool} -> {output.Length} chars", output);
    }

    private static (string, string) ReadSkill(string root, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("read_skill needs a 'name'");

        var match = SkillCatalog.Discover(root)
            .FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) throw new InvalidOperationException($"no skill named '{name}'");

        string body = File.ReadAllText(match.Path);
        if (body.Length > MaxRead) body = body[..MaxRead] + "\n… truncated …";
        return ($"read_skill {match.Name}", body);
    }

    /// <summary>
    /// A compact line diff: removed lines prefixed '-', added '+', unchanged context ' '.
    ///
    /// Enough to see what a write actually did, which a byte count never showed. Capped so a large
    /// rewrite does not flood the drawer or the model's context.
    /// </summary>
    private static string Diff(string? before, string after)
    {
        const int MaxLines = 60;

        string[] oldLines = (before ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        string[] newLines = after.Replace("\r\n", "\n").Split('\n');

        // Trim the common head and tail so the visible part is the change, not the whole file.
        int head = 0;
        while (head < oldLines.Length && head < newLines.Length && oldLines[head] == newLines[head]) head++;

        int tail = 0;
        while (tail < oldLines.Length - head && tail < newLines.Length - head
               && oldLines[^(tail + 1)] == newLines[^(tail + 1)]) tail++;

        var lines = new List<string>();
        if (head > 0) lines.Add($"  … {head} unchanged line{(head == 1 ? "" : "s")} …");

        for (int i = head; i < oldLines.Length - tail; i++) lines.Add($"- {oldLines[i]}");
        for (int i = head; i < newLines.Length - tail; i++) lines.Add($"+ {newLines[i]}");

        if (tail > 0) lines.Add($"  … {tail} unchanged line{(tail == 1 ? "" : "s")} …");
        if (lines.Count == 0) return "no textual change";

        bool capped = lines.Count > MaxLines;
        if (capped) lines = lines.Take(MaxLines).ToList();
        if (capped) lines.Add($"… diff truncated at {MaxLines} lines …");

        return string.Join('\n', lines);
    }

    private static string Rel(string path, string root)
    {
        try
        {
            string rel = Path.GetRelativePath(root, path);
            return rel == "." ? "." : rel.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>Reads a string argument from the call, flat first and then from a nested `args`.</summary>
    private static string? Str(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;

        if (args.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String) return direct.GetString();

        return args.TryGetProperty("args", out var nested)
               && nested.ValueKind == JsonValueKind.Object
               && nested.TryGetProperty(name, out var inner)
               && inner.ValueKind == JsonValueKind.String
            ? inner.GetString()
            : null;
    }

    private static string Short(string? text, int max = 40)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string flat = text.Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
