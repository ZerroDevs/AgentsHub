using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentsHub.Services.DeepSeek;

/// <summary>One DeepSeek account. The id is the profile folder name, the label is for the picker.</summary>
internal sealed record DeepSeekAccount(string Id, string Label);

/// <summary>
/// Accounts for the DeepSeek engine.
///
/// A DeepSeek login is a browser profile, so an account *is* a user data folder. Switching accounts
/// swaps the folder and rebuilds the WebView2; removing one deletes its folder, which is the only
/// real way to sign out. Kept deliberately separate from the CLI profiles: those are `agy` accounts
/// with their own VirtualHome, and the two must never share state.
/// </summary>
internal static class DeepSeekAccounts
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub");

    private static readonly string RegistryPath = Path.Combine(Root, "deepseek-accounts.json");

    private static readonly System.Threading.Lock Gate = new();
    private static List<DeepSeekAccount>? _accounts;
    private static string _active = "default";

    public static string ProfileDir(string id) => Path.Combine(Root, "deepseek-accounts", id);

    public static string CurrentProfileDir => ProfileDir(ActiveId);

    public static string ActiveId
    {
        get
        {
            Ensure();
            return _active;
        }
    }

    public static IReadOnlyList<DeepSeekAccount> All()
    {
        Ensure();
        return _accounts!;
    }

    public static DeepSeekAccount Add(string? label = null)
    {
        Ensure();

        string id = "acc" + DateTime.UtcNow.Ticks.ToString("x");
        var account = new DeepSeekAccount(id, string.IsNullOrWhiteSpace(label) ? $"Account {_accounts!.Count + 1}" : label.Trim());

        _accounts!.Add(account);
        Save();
        return account;
    }

    /// <summary>
    /// Renames an account. The id is untouched because it is the profile folder name; the label is
    /// only what the picker shows.
    /// </summary>
    public static bool Rename(string id, string label)
    {
        Ensure();

        int index = _accounts!.FindIndex(a => a.Id == id);
        if (index < 0) return false;

        string next = label.Trim();
        if (next.Length == 0) return false;

        _accounts[index] = _accounts[index] with { Label = next };
        Save();
        return true;
    }

    public static bool Switch(string id)
    {
        Ensure();
        if (!_accounts!.Any(a => a.Id == id)) return false;

        _active = id;
        Save();
        return true;
    }

    /// <summary>Deletes the account and its profile folder. The last account cannot be removed.</summary>
    public static bool Remove(string id)
    {
        Ensure();
        if (_accounts!.Count <= 1) return false;

        var account = _accounts.FirstOrDefault(a => a.Id == id);
        if (account is null) return false;

        _accounts.Remove(account);
        if (_active == id) _active = _accounts[0].Id;
        Save();

        try
        {
            string dir = ProfileDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked profile survives; the account is gone from the picker either way.
        }

        return true;
    }

    private static void Ensure()
    {
        lock (Gate)
        {
            if (_accounts is not null) return;

            try
            {
                if (File.Exists(RegistryPath))
                {
                    var root = JsonNode.Parse(File.ReadAllText(RegistryPath)) as JsonObject;
                    var list = new List<DeepSeekAccount>();

                    if (root?["accounts"] is JsonArray array)
                    {
                        foreach (var node in array)
                        {
                            if (node is not JsonObject item) continue;
                            string id = item["id"]?.GetValue<string>() ?? string.Empty;
                            if (id.Length == 0) continue;
                            list.Add(new DeepSeekAccount(id, item["label"]?.GetValue<string>() ?? id));
                        }
                    }

                    if (list.Count > 0)
                    {
                        _accounts = list;
                        string? active = root?["active"]?.GetValue<string>();
                        _active = list.Any(a => a.Id == active) ? active! : list[0].Id;
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A corrupt registry starts over rather than blocking the engine.
            }

            // A pre-accounts install has its login in the old single folder; keep using it as
            // "default" so nobody is signed out by this change.
            _accounts = [new DeepSeekAccount("default", "Account 1")];
            _active = "default";
            Save();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var payload = new JsonObject
            {
                ["active"] = _active,
                ["accounts"] = new JsonArray(_accounts!.Select(a => (JsonNode)new JsonObject
                {
                    ["id"] = a.Id,
                    ["label"] = a.Label,
                }).ToArray()),
            };

            File.WriteAllText(RegistryPath, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // The picker works for this session even if the file cannot be written.
        }
    }
}
