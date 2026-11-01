using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentsHub.Helpers;

namespace AgentsHub.Services;

/// <summary>
/// Manages isolated multi-account profiles for Antigravity CLI without touching antigravity-ide.
/// Profiles are saved under %LOCALAPPDATA%\AgentsHub\Profiles\Antigravity\{ProfileName}.
/// </summary>
public sealed class AntigravityProfileService : IDisposable
{
    private static readonly string UserProfileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string GeminiDir = Path.Combine(UserProfileDir, ".gemini");
    private static readonly string GeminiGoogleAccountsFile = Path.Combine(GeminiDir, "google_accounts.json");
    private static readonly string GeminiOAuthCredsFile = Path.Combine(GeminiDir, "oauth_creds.json");
    private static readonly string GeminiCliCacheDir = Path.Combine(GeminiDir, "antigravity-cli", "cache");

    private const string CredentialName = "gemini:antigravity";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const string PrefActiveProfileKey = "AntigravityActiveProfile";

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int reservedFlag);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    private readonly Lock _lock = new();
    private FileSystemWatcher? _fileWatcher;
    private System.Threading.Timer? _debounceSyncTimer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileSystemWatcher> _virtualHomeWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Timer> _virtualHomeDebounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private string _activeProfile = string.Empty;
    private volatile bool _isApplyingProfile = false;

    public string ActiveProfile
    {
        get
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(_activeProfile) || string.Equals(_activeProfile, "default", StringComparison.OrdinalIgnoreCase))
                {
                    // The type has to be explicit: a collection expression has no natural type, so
                    // `var` cannot be used across this conditional.
                    List<string> profs = Directory.Exists(ProfilesBaseDir)
                        ? [.. Directory.GetDirectories(ProfilesBaseDir)
                            .Select(Path.GetFileName)
                            .Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, "default", StringComparison.OrdinalIgnoreCase))
                            .Select(n => n!)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]
                        : [];

                    if (profs.Count > 0)
                    {
                        _activeProfile = profs[0];
                    }
                }
                return _activeProfile;
            }
        }
        private set
        {
            lock (_lock)
            {
                _activeProfile = value;
            }
            StartupHelper.SetStringPreference(PrefActiveProfileKey, value);
            ActiveProfileChanged?.Invoke(value);
        }
    }

    public static string ProfilesBaseDir => Helpers.AppPaths.AntigravityProfilesDir;

    public event Action<string>? ActiveProfileChanged;
    public event Action<string>? ProfileSaved;
    public event Action<string, string>? ProfileRenamed;
    public event Action<string /*profileName*/, string /*email*/>? ProfileSessionCaptured;
    public event Action<string>? LogMessage;

    private readonly Dictionary<string, string> _lastCapturedSignatures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fingerprint of the live session as it stood at the last successful export, per profile.
    /// Reading it is what lets a rewrite of identical credentials cost nothing.
    /// </summary>
    private readonly Dictionary<string, string> _lastExportedSignatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _isInitialized = false;

    public AntigravityProfileService()
    {
        Helpers.AppPaths.MigrateLegacyAntigravityProfiles();
        EnsureProfilesDirectoryExists();

        var saved = StartupHelper.GetStringPreference(PrefActiveProfileKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            _activeProfile = saved;
        }

        // Auto-detect existing session if no profiles exist yet
        AutoDetectExistingSession();

        SeedExistingProfileSignatures();
        _isInitialized = true;

        InitializeFileWatcher();
    }

    private void EnsureProfilesDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(ProfilesBaseDir))
            {
                Directory.CreateDirectory(ProfilesBaseDir);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to create profiles base directory: {ex.Message}");
        }
    }

    /// <summary>
    /// If user already has active login in %USERPROFILE%\.gemini and no profile exists,
    /// captures it cleanly into a named profile to prevent data loss.
    /// Does NOT create any 'Default' placeholder profile.
    /// </summary>
    private void AutoDetectExistingSession()
    {
        try
        {
            RemoveUnauthenticatedDefaultProfile();

            var profiles = GetProfiles();
            if (profiles.Count == 0 && HasValidActiveCredentials())
            {
                string? acctFile = ResolveGoogleAccountsPath(GeminiDir);
                var detectedEmail = acctFile != null ? TryExtractAccountEmail(acctFile) : null;
                string profileName = !string.IsNullOrWhiteSpace(detectedEmail) ? SanitizeProfileName(detectedEmail) : "Account_1";

                Log($"Detected existing Antigravity CLI session. Automatically exporting to initial profile '{profileName}'...");
                ExportCurrentSession(profileName);
                ActiveProfile = profileName;
            }
            else if (profiles.Count > 0 && !profiles.Contains(_activeProfile, StringComparer.OrdinalIgnoreCase))
            {
                ActiveProfile = profiles[0];
            }
            else if (profiles.Count == 0)
            {
                ActiveProfile = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log($"Auto-detect session error: {ex.Message}");
        }
    }

    private void RemoveUnauthenticatedDefaultProfile()
    {
        try
        {
            foreach (var name in new[] { "Default", "default" })
            {
                string defaultDir = Path.Combine(ProfilesBaseDir, name);
                if (Directory.Exists(defaultDir) && !HasValidSavedCredentials(name))
                {
                    Directory.Delete(defaultDir, true);
                    Log($"Removed unauthenticated {name} profile directory.");
                }
            }
        }
        catch
        {
            // Non-fatal
        }
    }

    public IReadOnlyList<string> GetProfiles()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(ProfilesBaseDir))
                {
                    return [];
                }

                RemoveUnauthenticatedDefaultProfile();

                var dirs = Directory.GetDirectories(ProfilesBaseDir)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .Where(n => !string.Equals(n, "default", StringComparison.OrdinalIgnoreCase) || HasValidSavedCredentials(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return dirs;
            }
            catch (Exception ex)
            {
                Log($"Error listing profiles: {ex.Message}");
                return [];
            }
        }
    }

    public static string? ResolveOAuthCredsPath(string parentDir)
    {
        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir)) return null;
        string p1 = Path.Combine(parentDir, "oauth_creds.json");
        if (File.Exists(p1)) return p1;
        string p2 = Path.Combine(parentDir, "oauth_creds");
        if (File.Exists(p2)) return p2;
        return null;
    }

    public static string? ResolveGoogleAccountsPath(string parentDir)
    {
        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir)) return null;
        string p1 = Path.Combine(parentDir, "google_accounts.json");
        if (File.Exists(p1)) return p1;
        string p2 = Path.Combine(parentDir, "google_accounts");
        if (File.Exists(p2)) return p2;
        return null;
    }

    /// <summary>
    /// Reads the 'gemini:antigravity' Generic Credential from Windows Credential Manager.
    /// Antigravity CLI on Windows stores its authentication tokens here.
    /// </summary>
    public static string? ReadWindowsCredential(string target = CredentialName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ReadUnixCredential(target);
        }

        try
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr ptr))
                return null;

            try
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize == 0) return null;
                byte[] bytes = new byte[c.CredentialBlobSize];
                Marshal.Copy(c.CredentialBlob, bytes, 0, (int)c.CredentialBlobSize);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CredFree(ptr);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the 'gemini:antigravity' Generic Credential to Windows Credential Manager.
    /// </summary>
    public static bool WriteWindowsCredential(string secret, string target = CredentialName, string userName = "antigravity")
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;

        if (!OperatingSystem.IsWindows())
        {
            return WriteUnixCredential(secret, target);
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(secret);
        IntPtr blobPtr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = userName,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blobPtr
            };
            return CredWrite(ref c, 0);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    private static string GetUnixCredentialFilePath()
    {
        string dir = Platform.PlatformHelper.GetConfigDirectory();
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return Path.Combine(dir, "credentials.json");
    }

    private static string? ReadUnixCredential(string target)
    {
        try
        {
            string file = GetUnixCredentialFilePath();
            if (!File.Exists(file)) return null;
            string json = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(target, out var prop))
            {
                return prop.GetString();
            }
        }
        catch { }
        return null;
    }

    private static bool WriteUnixCredential(string secret, string target)
    {
        try
        {
            string file = GetUnixCredentialFilePath();
            Dictionary<string, string> store = [];
            if (File.Exists(file))
            {
                try
                {
                    string existing = File.ReadAllText(file);
                    store = JsonSerializer.Deserialize<Dictionary<string, string>>(existing) ?? [];
                }
                catch { }
            }
            store[target] = secret;
            File.WriteAllText(file, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PurgeUnixCredential(string target)
    {
        try
        {
            string file = GetUnixCredentialFilePath();
            if (!File.Exists(file)) return;
            string existing = File.ReadAllText(file);
            var store = JsonSerializer.Deserialize<Dictionary<string, string>>(existing);
            if (store != null && store.Remove(target))
            {
                File.WriteAllText(file, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }

    /// <summary>
    /// Extracts account email from token JSON or JWT id_token payload.
    /// </summary>
    public static string? ExtractEmailFromTokenBlob(string secretJson)
    {
        if (string.IsNullOrWhiteSpace(secretJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(secretJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String)
            {
                var s = e.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }

            if (root.TryGetProperty("id_token", out var idTokenProp) && idTokenProp.ValueKind == JsonValueKind.String)
            {
                var jwt = idTokenProp.GetString();
                if (!string.IsNullOrWhiteSpace(jwt))
                {
                    var parts = jwt.Split('.');
                    if (parts.Length >= 2)
                    {
                        var payloadJson = DecodeBase64Url(parts[1]);
                        if (!string.IsNullOrWhiteSpace(payloadJson))
                        {
                            using var payloadDoc = JsonDocument.Parse(payloadJson);
                            if (payloadDoc.RootElement.TryGetProperty("email", out var jwtEmail) && jwtEmail.ValueKind == JsonValueKind.String)
                            {
                                var em = jwtEmail.GetString();
                                if (!string.IsNullOrWhiteSpace(em)) return em;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static string? DecodeBase64Url(string base64Url)
    {
        try
        {
            string padded = base64Url.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            byte[] bytes = Convert.FromBase64String(padded);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks whether active credentials exist either in Windows Credential Manager or disk.
    /// </summary>
    public static bool HasValidActiveCredentials()
    {
        try
        {
            // Primary check: Windows Credential Manager 'gemini:antigravity'
            string? cred = ReadWindowsCredential();
            if (!string.IsNullOrWhiteSpace(cred) && cred.Length > 10)
            {
                return true;
            }

            // Fallback check: disk files
            string? oauth = ResolveOAuthCredsPath(GeminiDir);
            if (oauth != null && CheckFileHasContentSafe(oauth, 10))
            {
                return true;
            }

            string? accounts = ResolveGoogleAccountsPath(GeminiDir);
            if (accounts != null && CheckFileHasContentSafe(accounts, 5))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Alias for HasValidActiveCredentials.
    /// </summary>
    public static bool HasValidLiveCredentials() => HasValidActiveCredentials();

    /// <summary>
    /// Checks whether a saved profile directory contains valid credentials.
    /// </summary>
    public static bool HasValidSavedCredentials(string profileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileName)) return false;

            string dir = Path.Combine(ProfilesBaseDir, profileName);
            if (!Directory.Exists(dir)) return false;

            string credFile = Path.Combine(dir, "gemini_credential.json");
            if (File.Exists(credFile) && CheckFileHasContentSafe(credFile, 10))
            {
                return true;
            }

            string? oauth = ResolveOAuthCredsPath(dir);
            if (oauth != null && CheckFileHasContentSafe(oauth, 10))
            {
                return true;
            }

            string? accounts = ResolveGoogleAccountsPath(dir);
            if (accounts != null && CheckFileHasContentSafe(accounts, 5))
            {
                return true;
            }

            // Check VirtualHome\.gemini as well
            string virtGemini = Path.Combine(dir, "VirtualHome", ".gemini");
            if (Directory.Exists(virtGemini))
            {
                string vCred = Path.Combine(virtGemini, "gemini_credential.json");
                if (File.Exists(vCred) && CheckFileHasContentSafe(vCred, 10)) return true;

                string? vOAuth = ResolveOAuthCredsPath(virtGemini);
                if (vOAuth != null && CheckFileHasContentSafe(vOAuth, 10)) return true;

                string? vAcct = ResolveGoogleAccountsPath(virtGemini);
                if (vAcct != null && CheckFileHasContentSafe(vAcct, 5)) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckFileHasContentSafe(string filePath, long minBytes)
    {
        try
        {
            if (!File.Exists(filePath)) return false;

            try
            {
                var fi = new FileInfo(filePath);
                fi.Refresh();
                if (fi.Length > minBytes) return true;
            }
            catch { }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length > minBytes;
        }
        catch
        {
            try
            {
                var fi = new FileInfo(filePath);
                return fi.Length > minBytes;
            }
            catch
            {
                return File.Exists(filePath);
            }
        }
    }

    /// <summary>
    /// Captures credentials and cache into {profileName}.
    /// Strictly aborts and refuses to overwrite if active credentials are empty or invalid.
    /// </summary>
    public bool ExportCurrentSession(string profileName)
    {
        if (!IsSafeProfileName(profileName)) return false;

        lock (_lock)
        {
            try
            {
                if (!HasValidActiveCredentials())
                {
                    Log($"Skipping session export for '{profileName}': active .gemini does not contain valid credentials.");
                    return false;
                }

                string targetDir = Path.Combine(ProfilesBaseDir, profileName);
                // Whether this run creates the profile or overwrites one that is already there. The
                // log used to say "exported" either way, which read like a duplicate was created
                // every time the live session was captured into an existing profile.
                bool profileExisted = Directory.Exists(targetDir);

                // Same credentials as the last export: nothing to write and nothing worth logging.
                // The CLI rewrites these files far more often than it changes them, and every write
                // used to land a full export plus a "Refreshed credentials" line, which is what made
                // an idle chat log the same sentence every few seconds.
                string liveSignature = ComputeLiveSessionSignature();

                if (profileExisted
                    && _lastExportedSignatures.TryGetValue(profileName, out var lastSignature)
                    && string.Equals(lastSignature, liveSignature, StringComparison.Ordinal))
                {
                    return true;
                }
                Directory.CreateDirectory(targetDir);

                // Isolation Guard: ensure live credential matches target profile's expected identity!
                string? expectedEmail = GetAccountEmail(profileName);
                string? credSecret = ReadWindowsCredential();
                string? credEmail = !string.IsNullOrWhiteSpace(credSecret) ? ExtractEmailFromTokenBlob(credSecret) : null;
                string? liveAccountsPath = ResolveGoogleAccountsPath(GeminiDir);
                string? diskEmail = liveAccountsPath != null && File.Exists(liveAccountsPath) ? TryExtractAccountEmail(liveAccountsPath) : null;
                string resolvedLiveEmail = credEmail ?? diskEmail ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(expectedEmail) && !string.IsNullOrWhiteSpace(resolvedLiveEmail))
                {
                    if (!string.Equals(expectedEmail, resolvedLiveEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"[Isolation Guard] Blocked session export for '{profileName}': Live credential belongs to '{resolvedLiveEmail}', but profile '{profileName}' is registered to '{expectedEmail}'. Overwrite aborted to prevent cross-contamination.");
                        return false;
                    }
                }

                string email = string.Empty;

                // 1. Export Windows Credential 'gemini:antigravity'
                if (!string.IsNullOrWhiteSpace(credSecret))
                {
                    // Guard against saving foreign secret into gemini_credential.json
                    if (string.IsNullOrWhiteSpace(credEmail) || string.IsNullOrWhiteSpace(expectedEmail) || string.Equals(expectedEmail, credEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        string credDest = Path.Combine(targetDir, "gemini_credential.json");
                        File.WriteAllText(credDest, credSecret);
                        if (!string.IsNullOrWhiteSpace(credEmail))
                        {
                            email = credEmail;
                        }

                        try
                        {
                            using var doc = JsonDocument.Parse(credSecret);
                            if (doc.RootElement.TryGetProperty("token", out var tokenProp))
                            {
                                File.WriteAllText(Path.Combine(targetDir, "oauth_creds.json"), tokenProp.GetRawText());
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        Log($"[Isolation Guard] Refused to write mismatched Windows Credential ({credEmail}) to '{profileName}' ({expectedEmail}).");
                    }
                }

                // 2. Export disk oauth_creds if present
                string? liveOAuth = ResolveOAuthCredsPath(GeminiDir);
                if (liveOAuth != null && File.Exists(liveOAuth))
                {
                    if (string.IsNullOrWhiteSpace(expectedEmail) || string.IsNullOrWhiteSpace(resolvedLiveEmail) || string.Equals(expectedEmail, resolvedLiveEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        string destOAuth = Path.Combine(targetDir, "oauth_creds.json");
                        RobustCopyFile(liveOAuth, destOAuth);
                    }
                }

                // 3. Export disk google_accounts if present
                if (liveAccountsPath != null && File.Exists(liveAccountsPath))
                {
                    if (string.IsNullOrWhiteSpace(expectedEmail) || string.IsNullOrWhiteSpace(diskEmail) || string.Equals(expectedEmail, diskEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        string destAccounts = Path.Combine(targetDir, "google_accounts.json");
                        RobustCopyFile(liveAccountsPath, destAccounts);
                        if (!string.IsNullOrWhiteSpace(diskEmail))
                        {
                            email = diskEmail;
                        }
                    }
                }

                // If email was found but no google_accounts.json exists, synthesize one
                string destGoogleAccounts = Path.Combine(targetDir, "google_accounts.json");
                if (!File.Exists(destGoogleAccounts) && !string.IsNullOrWhiteSpace(email))
                {
                    var acctsObj = new
                    {
                        active = email,
                        accounts = new[] { new { email } }
                    };
                    File.WriteAllText(destGoogleAccounts, JsonSerializer.Serialize(acctsObj, new JsonSerializerOptions { WriteIndented = true }));
                }

                // 4. Export antigravity-cli\cache\ folder
                if (Directory.Exists(GeminiCliCacheDir))
                {
                    string destCacheDir = Path.Combine(targetDir, "cache");
                    CopyDirectoryRecursive(GeminiCliCacheDir, destCacheDir);
                }

                string cliSettings = Path.Combine(GeminiDir, "antigravity-cli", "settings.json");
                if (File.Exists(cliSettings))
                {
                    RobustCopyFile(cliSettings, Path.Combine(targetDir, "cli_settings.json"));
                }

                // 5. Save metadata
                var meta = LoadProfileMetadata(profileName);
                meta.ProfileName = profileName;
                meta.AccountEmail = email;
                meta.LastExportUtc = DateTime.UtcNow;
                SaveProfileMetadata(profileName, meta);

                string identity = !string.IsNullOrWhiteSpace(email) ? email : "tokens secured";
                Log(profileExisted
                    ? $"Refreshed credentials of existing profile '{profileName}' ({identity})."
                    : $"Created profile '{profileName}' from the current session ({identity}).");
                ProfileSaved?.Invoke(profileName);

                string? capturedProfile = null;
                string? capturedEmail = null;

                if (_isInitialized)
                {
                    string sig = ComputeSessionSignature(targetDir, email);
                    if (!_lastCapturedSignatures.TryGetValue(profileName, out var prevSig) || prevSig != sig)
                    {
                        _lastCapturedSignatures[profileName] = sig;
                        capturedProfile = profileName;
                        capturedEmail = email;
                    }
                }

                if (capturedProfile != null)
                {
                    ProfileSessionCaptured?.Invoke(capturedProfile, capturedEmail ?? string.Empty);
                }

                _lastExportedSignatures[profileName] = liveSignature;

                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to export session to '{profileName}': {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Safely applies targetProfile using an atomic switch.
    /// Step 1: Flushes source profile if active and valid.
    /// Step 2: If target has saved credentials (Case A), restores Windows Credential and CLI cache;
    /// if empty (Case B), purges Windows Credential and clears live CLI state.
    /// Step 3: Updates ActiveProfile.
    /// NEVER touches antigravity-ide.
    /// </summary>
    public bool ApplyProfile(string profileName)
    {
        if (!IsSafeProfileName(profileName)) return false;

        lock (_lock)
        {
            _isApplyingProfile = true;
            try
            {
                // Step 1: Capture the live session into the outgoing profile, but ONLY when that
                // profile has nothing saved yet.
                //
                // ExportCurrentSession reads the single, machine-wide 'gemini:antigravity'
                // credential and writes it into the profile's saved files. Running it on every
                // switch therefore overwrites a profile's stored account with whatever credential
                // was applied last - which is exactly how two profiles end up resolving to the
                // same Google account. A profile that already has valid saved credentials must
                // never be overwritten here; its own VirtualHome is the source of truth and the
                // VirtualHome watcher syncs any refresh back.
                if (!string.IsNullOrWhiteSpace(_activeProfile) &&
                    HasValidActiveCredentials() &&
                    !HasValidSavedCredentials(_activeProfile))
                {
                    ExportCurrentSession(_activeProfile);
                }

                string targetDir = Path.Combine(ProfilesBaseDir, profileName);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                Directory.CreateDirectory(GeminiDir);
                Directory.CreateDirectory(GeminiCliCacheDir);

                // Step 2: Evaluate target profile
                if (HasValidSavedCredentials(profileName))
                {
                    // Case A: Target has saved credentials
                    string targetEmail = GetAccountEmail(profileName) ?? string.Empty;
                    string credFile = Path.Combine(targetDir, "gemini_credential.json");
                    string? credSecret = File.Exists(credFile) ? File.ReadAllText(credFile) : null;
                    string? credEmail = !string.IsNullOrWhiteSpace(credSecret) ? ExtractEmailFromTokenBlob(credSecret) : null;
                    string? srcOAuth = ResolveOAuthCredsPath(targetDir);

                    // Auto-heal check: if credFile is missing, or contains mismatched email while target has a known email
                    bool needsHeal = string.IsNullOrWhiteSpace(credSecret) ||
                        (!string.IsNullOrWhiteSpace(targetEmail) && !string.IsNullOrWhiteSpace(credEmail) && !string.Equals(targetEmail, credEmail, StringComparison.OrdinalIgnoreCase));

                    if (needsHeal && srcOAuth != null && File.Exists(srcOAuth))
                    {
                        try
                        {
                            string oauthRaw = File.ReadAllText(srcOAuth);
                            using var doc = JsonDocument.Parse(oauthRaw);
                            var credObj = new
                            {
                                token = doc.RootElement,
                                email = targetEmail,
                                auth_method = "consumer"
                            };
                            credSecret = JsonSerializer.Serialize(credObj, new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(credFile, credSecret);
                            Log($"[Auto-Heal] Repaired Windows Credential for profile '{profileName}' ({targetEmail}) from oauth_creds.json.");
                        }
                        catch (Exception ex)
                        {
                            Log($"[Auto-Heal] Warning repairing credential for '{profileName}': {ex.Message}");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(credSecret))
                    {
                        WriteWindowsCredential(credSecret);
                        Log($"Restored Windows Credential for profile '{profileName}'.");
                    }

                    if (srcOAuth != null && File.Exists(srcOAuth))
                    {
                        RobustCopyFile(srcOAuth, GeminiOAuthCredsFile);
                    }

                    string? srcAccounts = ResolveGoogleAccountsPath(targetDir);
                    if (srcAccounts != null && File.Exists(srcAccounts))
                    {
                        RobustCopyFile(srcAccounts, GeminiGoogleAccountsFile);
                    }

                    SafeClearDirectoryContents(GeminiCliCacheDir);
                    string srcCache = Path.Combine(targetDir, "cache");
                    if (Directory.Exists(srcCache))
                    {
                        CopyDirectoryRecursive(srcCache, GeminiCliCacheDir);
                    }

                    string savedSettings = Path.Combine(targetDir, "cli_settings.json");
                    if (File.Exists(savedSettings))
                    {
                        string destSettings = Path.Combine(GeminiDir, "antigravity-cli", "settings.json");
                        RobustCopyFile(savedSettings, destSettings);
                    }

                    Log($"Loaded saved credentials for profile '{profileName}'.");
                }
                else
                {
                    // Case B: Target is unauthenticated
                    PurgeWindowsCredential();

                    string pOAuth1 = Path.Combine(GeminiDir, "oauth_creds.json");
                    string pOAuth2 = Path.Combine(GeminiDir, "oauth_creds");
                    string pAcct1 = Path.Combine(GeminiDir, "google_accounts.json");
                    string pAcct2 = Path.Combine(GeminiDir, "google_accounts");

                    if (File.Exists(pOAuth1)) try { File.Delete(pOAuth1); } catch { }
                    if (File.Exists(pOAuth2)) try { File.Delete(pOAuth2); } catch { }
                    if (File.Exists(pAcct1)) try { File.Delete(pAcct1); } catch { }
                    if (File.Exists(pAcct2)) try { File.Delete(pAcct2); } catch { }

                    SafeClearDirectoryContents(GeminiCliCacheDir);
                    Log($"Target profile '{profileName}' is unauthenticated. Live CLI state reset for new login.");
                }

                // Step 3: Update ActiveProfile
                ActiveProfile = profileName;
                Log($"Profile '{profileName}' applied successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to apply profile '{profileName}': {ex.Message}");
                return false;
            }
            finally
            {
                _ = Task.Delay(500).ContinueWith(_ => _isApplyingProfile = false);
            }
        }
    }

    /// <summary>
    /// Prepares a clean environment for onboarding a new account.
    /// Clears existing CLI auth files and purges Windows Credential so agy displays OAuth login prompt.
    /// </summary>
    public bool PrepareNewProfile(string newProfileName)
    {
        if (string.IsNullOrWhiteSpace(newProfileName)) return false;
        newProfileName = SanitizeProfileName(newProfileName);
        if (!IsSafeProfileName(newProfileName)) return false;

        lock (_lock)
        {
            try
            {
                // 1. Auto-save prior active profile first if it has valid credentials
                if (!string.IsNullOrWhiteSpace(_activeProfile) && HasValidLiveCredentials())
                {
                    ExportCurrentSession(_activeProfile);
                }

                // 2. Ensure new profile folder is created
                string newDir = Path.Combine(ProfilesBaseDir, newProfileName);
                Directory.CreateDirectory(newDir);

                var meta = new ProfileMetadata
                {
                    ProfileName = newProfileName,
                    AccountEmail = string.Empty,
                    AutoResumeSession = true,
                    LastExportUtc = DateTime.UtcNow
                };
                SaveProfileMetadata(newProfileName, meta);

                // 3. ActiveProfile = newProfileName updated immediately
                ActiveProfile = newProfileName;
                _lastCapturedSignatures.Remove(newProfileName);

                // 4. Safely delete only CLI auth files from active session
                string pOAuth1 = Path.Combine(GeminiDir, "oauth_creds.json");
                string pOAuth2 = Path.Combine(GeminiDir, "oauth_creds");
                string pAcct1 = Path.Combine(GeminiDir, "google_accounts.json");
                string pAcct2 = Path.Combine(GeminiDir, "google_accounts");

                if (File.Exists(pOAuth1)) try { File.Delete(pOAuth1); } catch { }
                if (File.Exists(pOAuth2)) try { File.Delete(pOAuth2); } catch { }
                if (File.Exists(pAcct1)) try { File.Delete(pAcct1); } catch { }
                if (File.Exists(pAcct2)) try { File.Delete(pAcct2); } catch { }

                // Clear CLI cache
                if (Directory.Exists(GeminiCliCacheDir))
                {
                    SafeClearDirectoryContents(GeminiCliCacheDir);
                }

                // Purge Windows generic credential
                PurgeWindowsCredential();

                Log($"Clean profile '{newProfileName}' initialized. Ready for initial OAuth onboarding.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to prepare new profile '{newProfileName}': {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Renames an existing profile folder and updates active profile / metadata if needed.
    /// </summary>
    public bool RenameProfile(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;

        oldName = oldName.Trim();
        newName = SanitizeProfileName(newName);

        if (!IsSafeProfileName(oldName) || !IsSafeProfileName(newName)) return false;
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return true;

        lock (_lock)
        {
            try
            {
                string oldDir = Path.Combine(ProfilesBaseDir, oldName);
                string newDir = Path.Combine(ProfilesBaseDir, newName);

                if (!Directory.Exists(oldDir))
                {
                    Log($"Cannot rename: profile '{oldName}' does not exist.");
                    return false;
                }

                if (Directory.Exists(newDir))
                {
                    Log($"Cannot rename: profile '{newName}' already exists.");
                    return false;
                }

                // If oldName is currently active, flush active session to disk first if valid
                if (string.Equals(_activeProfile, oldName, StringComparison.OrdinalIgnoreCase) && HasValidLiveCredentials())
                {
                    ExportCurrentSession(oldName);
                }

                // Rename directory
                Directory.Move(oldDir, newDir);

                // Update profile_info.json metadata
                string? acctFile = ResolveGoogleAccountsPath(newDir);
                string email = (acctFile != null ? TryExtractAccountEmail(acctFile) : null) ?? string.Empty;
                var meta = LoadProfileMetadata(newName);
                meta.ProfileName = newName;
                if (!string.IsNullOrWhiteSpace(email)) meta.AccountEmail = email;
                meta.LastExportUtc = DateTime.UtcNow;
                SaveProfileMetadata(newName, meta);

                // If oldName was ActiveProfile, update to newName and persist
                if (string.Equals(_activeProfile, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveProfile = newName;
                }

                if (_lastCapturedSignatures.Remove(oldName, out var oldSig))
                {
                    _lastCapturedSignatures[newName] = oldSig;
                }

                Log($"Profile '{oldName}' renamed to '{newName}'.");
                ProfileRenamed?.Invoke(oldName, newName);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to rename profile '{oldName}' to '{newName}': {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Deletes a saved profile. If the deleted profile was active, switches to another profile or Default.
    /// </summary>
    public bool DeleteProfile(string profileName)
    {
        if (!IsSafeProfileName(profileName)) return false;

        lock (_lock)
        {
            try
            {
                string targetDir = Path.Combine(ProfilesBaseDir, profileName);
                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, true);
                    Log($"Deleted profile '{profileName}'.");
                }
                _lastCapturedSignatures.Remove(profileName);

                var remaining = GetProfiles();
                if (string.Equals(_activeProfile, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (remaining.Count > 0)
                    {
                        ApplyProfile(remaining[0]);
                    }
                    else
                    {
                        // All profiles deleted: clear live CLI auth state without creating any default profile
                        if (File.Exists(GeminiOAuthCredsFile))
                        {
                            try { File.Delete(GeminiOAuthCredsFile); } catch { }
                        }
                        if (File.Exists(GeminiGoogleAccountsFile))
                        {
                            try { File.Delete(GeminiGoogleAccountsFile); } catch { }
                        }

                        SafeClearDirectoryContents(GeminiCliCacheDir);
                        PurgeWindowsCredential();
                        ActiveProfile = string.Empty;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to delete profile '{profileName}': {ex.Message}");
                return false;
            }
        }
    }

    public void EnsureActiveProfileApplied()
    {
        lock (_lock)
        {
            try
            {
                if (HasValidSavedCredentials(_activeProfile) && !HasValidLiveCredentials())
                {
                    ApplyProfile(_activeProfile);
                }
            }
            catch
            {
                // Non-fatal
            }
        }
    }

    public static ProfileMetadata LoadProfileMetadata(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return new ProfileMetadata { AutoResumeSession = true, LastExportUtc = DateTime.UtcNow };
        }

        try
        {
            string profileDir = Path.Combine(ProfilesBaseDir, profileName);
            string metaPath = Path.Combine(profileDir, "profile_info.json");
            if (File.Exists(metaPath))
            {
                string json = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<ProfileMetadata>(json);
                if (meta != null)
                {
                    if (string.IsNullOrWhiteSpace(meta.ProfileName)) meta.ProfileName = profileName;
                    return meta;
                }
            }
        }
        catch { }

        return new ProfileMetadata
        {
            ProfileName = profileName,
            AutoResumeSession = true,
            LastExportUtc = DateTime.UtcNow
        };
    }

    public static void SaveProfileMetadata(string profileName, ProfileMetadata meta)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        try
        {
            string profileDir = Path.Combine(ProfilesBaseDir, profileName);
            Directory.CreateDirectory(profileDir);
            string metaPath = Path.Combine(profileDir, "profile_info.json");
            meta.ProfileName = profileName;
            meta.LastExportUtc = DateTime.UtcNow;
            string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);
        }
        catch { }
    }

    public string? GetProfileWorkingDirectory(string profileName)
    {
        return LoadProfileMetadata(profileName).WorkingDirectory;
    }

    public void SetProfileWorkingDirectory(string profileName, string? directory)
    {
        var meta = LoadProfileMetadata(profileName);
        meta.WorkingDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        SaveProfileMetadata(profileName, meta);
        Log($"Working directory for profile '{profileName}' set to: {meta.WorkingDirectory ?? "(default)"}");
        ProfileSaved?.Invoke(profileName);
    }

    public bool GetProfileAutoResume(string profileName)
    {
        return LoadProfileMetadata(profileName).AutoResumeSession;
    }

    public void SetProfileAutoResume(string profileName, bool autoResume)
    {
        var meta = LoadProfileMetadata(profileName);
        meta.AutoResumeSession = autoResume;
        SaveProfileMetadata(profileName, meta);
        Log($"AutoResumeSession for profile '{profileName}' set to: {autoResume}");
        ProfileSaved?.Invoke(profileName);
    }

    public bool GetProfileSkipPermissions(string profileName)
    {
        return LoadProfileMetadata(profileName).SkipPermissions;
    }

    public void SetProfileSkipPermissions(string profileName, bool skipPermissions)
    {
        var meta = LoadProfileMetadata(profileName);
        meta.SkipPermissions = skipPermissions;
        SaveProfileMetadata(profileName, meta);
        Log($"SkipPermissions for profile '{profileName}' set to: {skipPermissions}");
        ProfileSaved?.Invoke(profileName);
    }

    /// <summary>
    /// Only `tool(target)` entries may be written, so a malformed rule never reaches the CLI. The
    /// tool is not always `command`: a refused file write is granted with `write_file(<path>)`, and
    /// the target may contain parentheses, quotes and slashes.
    /// </summary>
    private static readonly Regex AllowRuleShape = new(@"^[a-z_]+\([^\r\n]{1,400}\)$", RegexOptions.Compiled);

    /// <summary>
    /// Adds an allow-rule to the profile's CLI settings, leaving every other key untouched.
    ///
    /// Headless runs cannot answer a permission prompt, so an allow-rule is the only way to let a
    /// previously denied tool through. It is written to both copies of the settings file: the
    /// VirtualHome one `agy` reads at runtime, and `cli_settings.json`, which PrepareVirtualHome
    /// copies back over it — writing only the first would lose the rule on the next launch.
    /// </summary>
    /// <returns>True when the rule is in effect afterwards, whether it was added now or already there.</returns>
    public bool AddPermissionAllowRule(string profileName, string rule)
    {
        if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(rule)) return false;
        if (!AllowRuleShape.IsMatch(rule)) return false;

        lock (_lock)
        {
            string profileDir = Path.Combine(ProfilesBaseDir, profileName);
            bool present = false;

            // The VirtualHome copy is what `agy` reads right now.
            string liveSettings = Path.Combine(profileDir, "VirtualHome", ".gemini", "antigravity-cli", "settings.json");
            present |= TryMergeAllowRule(liveSettings, rule, createIfMissing: true);

            // `cli_settings.json` is the source PrepareVirtualHome folds in. It is only written when
            // it already exists: creating a file holding nothing but the rule would replace the
            // profile's whole settings (verbosity, trustedWorkspaces) on next launch.
            present |= TryMergeAllowRule(
                Path.Combine(profileDir, "cli_settings.json"),
                rule,
                createIfMissing: false);

            // Read back rather than trust the write: a grant that never reached the file the CLI
            // reads is indistinguishable from a dead button, so it has to fail loudly.
            bool inEffect = present && SettingsContainRule(liveSettings, rule);
            if (inEffect)
            {
                Log($"Allow-rule '{rule}' is in effect for profile '{profileName}'.");
                ProfileSaved?.Invoke(profileName);
            }
            LogGrant(profileName, rule, inEffect);
            return inEffect;
        }
    }

    /// <summary>
    /// Takes an allow-rule back out of the profile's settings.
    ///
    /// The chat prompt offers "run once" and "allow for this conversation", both of which have to be
    /// undone later; the settings file is the only lever `agy` reads, so scope is emulated by
    /// writing and then removing the rule.
    /// </summary>
    /// <returns>True when the rule is gone from the file the CLI reads.</returns>
    public bool RemovePermissionAllowRule(string profileName, string rule)
    {
        if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(rule)) return false;

        lock (_lock)
        {
            string profileDir = Path.Combine(ProfilesBaseDir, profileName);
            string liveSettings = Path.Combine(profileDir, "VirtualHome", ".gemini", "antigravity-cli", "settings.json");
            RemoveRuleFrom(liveSettings, rule);
            RemoveRuleFrom(Path.Combine(profileDir, "cli_settings.json"), rule);

            bool removed = !SettingsContainRule(liveSettings, rule);
            LogGrant(profileName, rule, !removed);
            return removed;
        }
    }

    private void RemoveRuleFrom(string path, string rule)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return;
            if (root["permissions"] is not JsonObject permissions) return;
            if (permissions["allow"] is not JsonArray allow) return;

            for (int i = allow.Count - 1; i >= 0; i -= 1)
            {
                if (allow[i] is JsonValue value && value.TryGetValue<string>(out string? existing) &&
                    string.Equals(existing, rule, StringComparison.OrdinalIgnoreCase))
                {
                    allow.RemoveAt(i);
                }
            }

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log($"Failed to remove allow-rule from '{path}': {ex.Message}");
        }
    }

    /// <summary>True when the rule is actually present in the settings file the CLI reads.</summary>
    private static bool SettingsContainRule(string path, string rule)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return false;
            if (root["permissions"] is not JsonObject permissions) return false;
            if (permissions["allow"] is not JsonArray allow) return false;

            foreach (JsonNode? entry in allow)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out string? existing) &&
                    string.Equals(existing, rule, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Appends every grant to a temp log. A rule that silently failed to reach the right file looked
    /// exactly like a broken Allow button, and nothing in the UI could tell the two apart.
    /// </summary>
    private static void LogGrant(string profileName, string rule, bool present)
    {
        try
        {
            string line = $"{DateTime.Now:O}\t{profileName}\t{present}\t{rule}\t{ProfilesBaseDir}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "agentshub-permissions.log"), line);
        }
        catch
        {
            // Diagnostics must never break the grant.
        }
    }

    /// <returns>True when the rule is present in that file afterwards, false on a read/write failure.</returns>
    /// <summary>
    /// Folds the profile's saved settings into the live VirtualHome file instead of overwriting it.
    ///
    /// The live file grows while the app runs: `agy` writes an "always allow" choice into it, and the
    /// chat approval path writes allow-rules. A plain copy erased those on the next prepare, which is
    /// why an approved command was denied again on the retry. Keys the live file already has win;
    /// the lists that only ever grow are unioned.
    /// </summary>
    private void MergeSettingsInto(string sourcePath, string destPath)
    {
        try
        {
            if (!File.Exists(destPath))
            {
                string? parent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(sourcePath, destPath, overwrite: true);
                return;
            }

            JsonObject source = JsonNode.Parse(File.ReadAllText(sourcePath)) as JsonObject ?? [];
            JsonObject dest = JsonNode.Parse(File.ReadAllText(destPath)) as JsonObject ?? [];

            if (source["permissions"] is JsonObject sourcePermissions)
            {
                if (dest["permissions"] is not JsonObject destPermissions)
                {
                    destPermissions = [];
                    dest["permissions"] = destPermissions;
                }

                foreach (string listName in new[] { "allow", "deny", "ask" })
                {
                    if (sourcePermissions[listName] is not JsonArray sourceList) continue;
                    if (destPermissions[listName] is not JsonArray destList)
                    {
                        destList = [];
                        destPermissions[listName] = destList;
                    }
                    foreach (JsonNode? entry in sourceList) AddIfMissing(destList, entry);
                }
            }

            if (source["trustedWorkspaces"] is JsonArray sourceWorkspaces)
            {
                if (dest["trustedWorkspaces"] is not JsonArray destWorkspaces)
                {
                    destWorkspaces = [];
                    dest["trustedWorkspaces"] = destWorkspaces;
                }
                foreach (JsonNode? entry in sourceWorkspaces) AddIfMissing(destWorkspaces, entry);
            }

            // Everything else is only filled in when the live file does not have it, so a runtime
            // change (verbosity, a new toggle) is never reverted by the profile copy.
            foreach ((string key, JsonNode? value) in source)
            {
                if (key is "permissions" or "trustedWorkspaces") continue;
                if (!dest.ContainsKey(key) && value is not null) dest[key] = value.DeepClone();
            }

            File.WriteAllText(destPath, dest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log($"Settings merge failed for '{destPath}', copying instead: {ex.Message}");
            try { RobustCopyFile(sourcePath, destPath); } catch { }
        }
    }

    private static void AddIfMissing(JsonArray list, JsonNode? entry)
    {
        string? value = entry is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? text) ? text : null;
        foreach (JsonNode? existing in list)
        {
            if (existing is JsonValue existingValue && existingValue.TryGetValue<string>(out string? current) &&
                string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        list.Add(entry?.DeepClone());
    }

    private bool TryMergeAllowRule(string path, string rule, bool createIfMissing)
    {
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                root = string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
            }
            else
            {
                if (!createIfMissing) return false;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                root = [];
            }

            if (root["permissions"] is not JsonObject permissions)
            {
                permissions = [];
                root["permissions"] = permissions;
            }
            if (permissions["allow"] is not JsonArray allow)
            {
                allow = [];
                permissions["allow"] = allow;
            }

            foreach (JsonNode? entry in allow)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out string? existing) &&
                    string.Equals(existing, rule, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            allow.Add(rule);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to add allow-rule to '{path}': {ex.Message}");
            return false;
        }
    }

    public static string? GetAccountEmail(string profileName)
    {
        try
        {
            string profileDir = Path.Combine(ProfilesBaseDir, profileName);
            if (!Directory.Exists(profileDir)) return null;

            string metaPath = Path.Combine(profileDir, "profile_info.json");
            if (File.Exists(metaPath))
            {
                try
                {
                    using var stream = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("AccountEmail", out var emailProp) && emailProp.ValueKind == JsonValueKind.String)
                    {
                        var em = emailProp.GetString();
                        if (!string.IsNullOrWhiteSpace(em)) return em;
                    }
                }
                catch { }
            }

            string credPath = Path.Combine(profileDir, "gemini_credential.json");
            if (File.Exists(credPath))
            {
                try
                {
                    string json = File.ReadAllText(credPath);
                    var em = ExtractEmailFromTokenBlob(json);
                    if (!string.IsNullOrWhiteSpace(em)) return em;
                }
                catch { }
            }

            string? acctPath = ResolveGoogleAccountsPath(profileDir);
            if (acctPath != null)
            {
                var em = TryExtractAccountEmail(acctPath);
                if (!string.IsNullOrWhiteSpace(em)) return em;
            }

            // Check VirtualHome\.gemini
            string virtGemini = Path.Combine(profileDir, "VirtualHome", ".gemini");
            if (Directory.Exists(virtGemini))
            {
                string vCredPath = Path.Combine(virtGemini, "gemini_credential.json");
                if (File.Exists(vCredPath))
                {
                    try
                    {
                        string json = File.ReadAllText(vCredPath);
                        var em = ExtractEmailFromTokenBlob(json);
                        if (!string.IsNullOrWhiteSpace(em)) return em;
                    }
                    catch { }
                }

                string? vAcctPath = ResolveGoogleAccountsPath(virtGemini);
                if (vAcctPath != null)
                {
                    var em = TryExtractAccountEmail(vAcctPath);
                    if (!string.IsNullOrWhiteSpace(em)) return em;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Purges the 'gemini:antigravity' Windows Generic Credential using advapi32 CredDeleteW,
    /// with cmdkey.exe as an execution fallback.
    /// </summary>
    public void PurgeWindowsCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            PurgeUnixCredential(CredentialName);
            return;
        }

        try
        {
            bool nativeDeleted = CredDelete(CredentialName, CRED_TYPE_GENERIC, 0);
            if (nativeDeleted)
            {
                Log($"Windows Credential '{CredentialName}' purged via CredDeleteW.");
            }
        }
        catch (Exception ex)
        {
            Log($"Native CredDelete notice: {ex.Message}");
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmdkey.exe",
                Arguments = $"/delete:{CredentialName}",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            proc?.WaitForExit(1000);
        }
        catch
        {
            // Ignore fallback errors
        }
    }

    private void InitializeFileWatcher()
    {
        try
        {
            if (!Directory.Exists(GeminiDir)) return;

            _fileWatcher = new FileSystemWatcher(GeminiDir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _fileWatcher.Changed += OnGeminiFileChanged;
            _fileWatcher.Created += OnGeminiFileChanged;
        }
        catch (Exception ex)
        {
            Log($"File watcher initialization notice: {ex.Message}");
        }
    }

    private void OnGeminiFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isApplyingProfile) return;
        if (e.ChangeType == WatcherChangeTypes.Deleted) return;

        // Strictly ignore anything inside antigravity-ide
        if (e.FullPath.Contains("antigravity-ide", StringComparison.OrdinalIgnoreCase)) return;

        // Check if active session now has valid credentials
        if (!HasValidActiveCredentials()) return;

        // Debounce sync 1.0 second to allow token/cache writes to settle
        _debounceSyncTimer?.Dispose();
        _debounceSyncTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                lock (_lock)
                {
                    if (!string.IsNullOrWhiteSpace(_activeProfile) && HasValidActiveCredentials())
                    {
                        ExportCurrentSession(_activeProfile);
                    }
                }
            }
            catch
            {
                // Non-fatal
            }
        }, null, 1000, System.Threading.Timeout.Infinite);
    }

    private static void SafeClearDirectoryContents(string dirPath)
    {
        ValidateSafePath(dirPath);

        if (!Directory.Exists(dirPath)) return;

        foreach (var file in Directory.GetFiles(dirPath))
        {
            ValidateSafePath(file);
            try { File.Delete(file); } catch { }
        }

        foreach (var sub in Directory.GetDirectories(dirPath))
        {
            ValidateSafePath(sub);
            try { Directory.Delete(sub, true); } catch { }
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        ValidateSafePath(sourceDir);
        ValidateSafePath(targetDir);

        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            RobustCopyFile(file, dest);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            string destSub = Path.Combine(targetDir, Path.GetFileName(sub));
            CopyDirectoryRecursive(sub, destSub);
        }
    }

    private static void RobustCopyFile(string source, string destination)
    {
        ValidateSafePath(source);
        ValidateSafePath(destination);

        for (int i = 0; i < 5; i++)
        {
            try
            {
                using var srcStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dstStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                srcStream.CopyTo(dstStream);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (i < 4)
            {
                Thread.Sleep(100);
            }
        }

        File.Copy(source, destination, true);
    }

    /// <summary>
    /// Safety guard: strictly ensures that root .gemini and antigravity-ide are NEVER touched.
    /// </summary>
    private static void ValidateSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Path cannot be empty.");

        string normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string geminiNorm = Path.GetFullPath(GeminiDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalized, geminiNorm, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Operation blocked: root .gemini directory cannot be modified or cleared.");
        }

        if (normalized.Contains("antigravity-ide", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Operation blocked: antigravity-ide directory is strictly protected.");
        }
    }

    public static string? TryExtractAccountEmail(string googleAccountsJsonPath)
    {
        try
        {
            if (!File.Exists(googleAccountsJsonPath))
            {
                string alt = googleAccountsJsonPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? googleAccountsJsonPath[..^5]
                    : googleAccountsJsonPath + ".json";
                if (File.Exists(alt)) googleAccountsJsonPath = alt;
                else return null;
            }

            using var stream = new FileStream(googleAccountsJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // الفحص الأساسي لحقل active المستخدم فعلياً في google_accounts.json
                if (root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.String)
                {
                    var s = active.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (root.TryGetProperty("active_account", out var activeAcct) && activeAcct.ValueKind == JsonValueKind.String)
                {
                    var s = activeAcct.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (root.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
                {
                    var s = email.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (root.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.String)
                {
                    var s = acct.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in accounts.EnumerateArray())
                    {
                        if (el.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String)
                        {
                            var s = e.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) return s;
                        }
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    if (el.TryGetProperty("active", out var aActive) && aActive.ValueKind == JsonValueKind.String)
                    {
                        var s = aActive.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                    if (el.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String)
                    {
                        var s = e.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                    if (el.TryGetProperty("active_account", out var a) && a.ValueKind == JsonValueKind.String)
                    {
                        var s = a.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Turns a display name into one path segment. Dots are the part that matters: a name of
    /// ".." passes an invalid-character check but points the profile folder at its parent.
    /// </summary>
    internal static string SanitizeProfileName(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalids.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var clean = new string(chars).Trim().TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(clean)) return "Profile";

        string stem = clean.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem)) clean = "_" + clean;

        return clean.Length > 64 ? clean[..64].TrimEnd('.', ' ') : clean;
    }

    /// <summary>Rejects a name that is not exactly one safe directory segment.</summary>
    internal static bool IsSafeProfileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.Length > 64) return false;
        return string.Equals(name, SanitizeProfileName(name), StringComparison.Ordinal);
    }

    private void Log(string msg)
    {
        LogMessage?.Invoke(msg);
    }

    /// <summary>
    /// A fingerprint of the live session: the credential secret plus the two files it lands in.
    ///
    /// Hashing the content, not the timestamps, is the point. The CLI touches these files on every
    /// cache write, so a stamp would keep changing while the credentials stay identical and the
    /// export would keep running. The same content always produces the same signature.
    /// </summary>
    private string ComputeLiveSessionSignature()
    {
        var builder = new System.Text.StringBuilder();

        builder.Append(Hash(ReadWindowsCredential()));
        builder.Append('|').Append(Hash(ReadText(ResolveOAuthCredsPath(GeminiDir))));
        builder.Append('|').Append(Hash(ReadText(ResolveGoogleAccountsPath(GeminiDir))));

        return builder.ToString();

        static string Hash(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "-";

            byte[] digest = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text));

            return System.Convert.ToHexString(digest, 0, 8);
        }

        static string? ReadText(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch
            {
                return null;
            }
        }
    }

    private string ComputeSessionSignature(string targetDir, string email)
    {
        string credPath = Path.Combine(targetDir, "gemini_credential.json");
        long cLen = 0;
        if (File.Exists(credPath))
        {
            try { using var fs = new FileStream(credPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); cLen = fs.Length; } catch { }
        }

        string? oauthPath = ResolveOAuthCredsPath(targetDir);
        string? accountsPath = ResolveGoogleAccountsPath(targetDir);
        long oLen = 0;
        long aLen = 0;

        if (oauthPath != null && File.Exists(oauthPath))
        {
            try
            {
                using var fs = new FileStream(oauthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                oLen = fs.Length;
            }
            catch { }
        }

        if (accountsPath != null && File.Exists(accountsPath))
        {
            try
            {
                using var fs = new FileStream(accountsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                aLen = fs.Length;
            }
            catch { }
        }

        string hash = string.Empty;
        try
        {
            string hashTarget = File.Exists(credPath) ? credPath : (oauthPath ?? string.Empty);
            if (!string.IsNullOrEmpty(hashTarget) && File.Exists(hashTarget))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = new FileStream(hashTarget, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                hash = Convert.ToHexString(sha.ComputeHash(fs));
            }
        }
        catch
        {
            hash = File.Exists(credPath) ? File.GetLastWriteTimeUtc(credPath).Ticks.ToString() : string.Empty;
        }

        return $"{email.Trim()}|{cLen}|{oLen}|{aLen}|{hash}";
    }

    private void SeedExistingProfileSignatures()
    {
        try
        {
            foreach (var p in GetProfiles())
            {
                string pDir = Path.Combine(ProfilesBaseDir, p);
                string email = GetAccountEmail(p) ?? string.Empty;
                _lastCapturedSignatures[p] = ComputeSessionSignature(pDir, email);
            }
        }
        catch
        {
            // Ignore initialization errors
        }
    }

    public static string GetVirtualHomeDirectory(string profileName)
    {
        return Path.Combine(ProfilesBaseDir, profileName, "VirtualHome");
    }

    /// <summary>
    /// Prepares an isolated VirtualHome for the specified profile:
    /// Profiles\Antigravity\{ProfileName}\VirtualHome\.gemini\
    /// Seeds oauth_creds.json, google_accounts.json, gemini_credential.json,
    /// and antigravity-cli cache/settings from the profile root if available.
    /// Does NOT touch or overwrite host %USERPROFILE%\.gemini.
    /// </summary>
    public string PrepareVirtualHome(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name cannot be empty.", nameof(profileName));

        lock (_lock)
        {
            string profileDir = Path.Combine(ProfilesBaseDir, profileName);
            Directory.CreateDirectory(profileDir);

            string virtualHomeDir = Path.Combine(profileDir, "VirtualHome");
            string virtualGemini = Path.Combine(virtualHomeDir, ".gemini");
            string virtualCli = Path.Combine(virtualGemini, "antigravity-cli");
            string virtualCache = Path.Combine(virtualCli, "cache");
            string virtualRoaming = Path.Combine(virtualHomeDir, "AppData", "Roaming");
            string virtualLocal = Path.Combine(virtualHomeDir, "AppData", "Local");

            Directory.CreateDirectory(virtualGemini);
            Directory.CreateDirectory(virtualCli);
            Directory.CreateDirectory(virtualCache);
            Directory.CreateDirectory(virtualRoaming);
            Directory.CreateDirectory(virtualLocal);

            // Copy credentials and config from profile root to VirtualHome
            string credSrc = Path.Combine(profileDir, "gemini_credential.json");
            string credDst = Path.Combine(virtualGemini, "gemini_credential.json");
            if (File.Exists(credSrc))
            {
                RobustCopyFile(credSrc, credDst);
            }

            string? oauthSrc = ResolveOAuthCredsPath(profileDir);
            if (oauthSrc != null && File.Exists(oauthSrc))
            {
                RobustCopyFile(oauthSrc, Path.Combine(virtualGemini, "oauth_creds.json"));
            }

            string? acctSrc = ResolveGoogleAccountsPath(profileDir);
            if (acctSrc != null && File.Exists(acctSrc))
            {
                RobustCopyFile(acctSrc, Path.Combine(virtualGemini, "google_accounts.json"));
            }

            string cacheSrc = Path.Combine(profileDir, "cache");
            if (Directory.Exists(cacheSrc))
            {
                CopyDirectoryRecursive(cacheSrc, virtualCache);
            }

            string settingsSrc = Path.Combine(profileDir, "cli_settings.json");
            if (File.Exists(settingsSrc))
            {
                MergeSettingsInto(settingsSrc, Path.Combine(virtualCli, "settings.json"));
            }

            Log($"VirtualHome prepared for profile '{profileName}'.");
            return virtualHomeDir;
        }
    }

    /// <summary>
    /// Synchronizes tokens, cache, and settings from VirtualHome back to the profile's root folder.
    /// Never touches host %USERPROFILE%\.gemini or antigravity-ide.
    /// </summary>
    public void SyncVirtualHomeToProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        lock (_lock)
        {
            try
            {
                string profileDir = Path.Combine(ProfilesBaseDir, profileName);
                string virtualHomeDir = Path.Combine(profileDir, "VirtualHome");
                string virtualGemini = Path.Combine(virtualHomeDir, ".gemini");
                string virtualCli = Path.Combine(virtualGemini, "antigravity-cli");
                string virtualCache = Path.Combine(virtualCli, "cache");

                if (!Directory.Exists(virtualGemini)) return;

                string email = string.Empty;
                string expected = GetAccountEmail(profileName) ?? string.Empty;

                // 1. Sync gemini_credential.json
                string virtCred = Path.Combine(virtualGemini, "gemini_credential.json");
                if (File.Exists(virtCred) && CheckFileHasContentSafe(virtCred, 10))
                {
                    string json = File.ReadAllText(virtCred);
                    var em = ExtractEmailFromTokenBlob(json);
                    if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(em) || string.Equals(expected, em, StringComparison.OrdinalIgnoreCase))
                    {
                        RobustCopyFile(virtCred, Path.Combine(profileDir, "gemini_credential.json"));
                        if (!string.IsNullOrWhiteSpace(em)) email = em;
                    }
                    else
                    {
                        Log($"[Isolation Guard] VirtualHome gemini_credential for '{profileName}' has foreign email '{em}'. Sync skipped.");
                    }
                }
                else
                {
                    // If virtCred is absent, but Windows Credential Manager has fresh token matching profile email:
                    string? liveSecret = ReadWindowsCredential();
                    if (!string.IsNullOrWhiteSpace(liveSecret))
                    {
                        var liveEmail = ExtractEmailFromTokenBlob(liveSecret);
                        if (!string.IsNullOrWhiteSpace(expected) && string.Equals(expected, liveEmail, StringComparison.OrdinalIgnoreCase))
                        {
                            File.WriteAllText(Path.Combine(profileDir, "gemini_credential.json"), liveSecret);
                            File.WriteAllText(virtCred, liveSecret);
                            email = liveEmail ?? expected;
                            Log($"Synced refreshed Windows Credential back to profile '{profileName}'.");
                        }
                    }
                }

                // 2. Sync oauth_creds.json
                string? virtOAuth = ResolveOAuthCredsPath(virtualGemini);
                if (virtOAuth != null && File.Exists(virtOAuth) && CheckFileHasContentSafe(virtOAuth, 10))
                {
                    RobustCopyFile(virtOAuth, Path.Combine(profileDir, "oauth_creds.json"));
                }

                // 3. Sync google_accounts.json
                string? virtAcct = ResolveGoogleAccountsPath(virtualGemini);
                if (virtAcct != null && File.Exists(virtAcct) && CheckFileHasContentSafe(virtAcct, 5))
                {
                    RobustCopyFile(virtAcct, Path.Combine(profileDir, "google_accounts.json"));
                    var em = TryExtractAccountEmail(virtAcct);
                    if (!string.IsNullOrWhiteSpace(em)) email = em;
                }

                // 4. Sync cache
                if (Directory.Exists(virtualCache))
                {
                    CopyDirectoryRecursive(virtualCache, Path.Combine(profileDir, "cache"));
                }

                // 5. Sync cli_settings.json
                string virtSettings = Path.Combine(virtualCli, "settings.json");
                if (File.Exists(virtSettings))
                {
                    RobustCopyFile(virtSettings, Path.Combine(profileDir, "cli_settings.json"));
                }

                // 6. Update profile_info.json
                if (string.IsNullOrWhiteSpace(email))
                {
                    email = GetAccountEmail(profileName) ?? string.Empty;
                }

                var meta = LoadProfileMetadata(profileName);
                meta.ProfileName = profileName;
                if (!string.IsNullOrWhiteSpace(email)) meta.AccountEmail = email;
                meta.LastExportUtc = DateTime.UtcNow;
                SaveProfileMetadata(profileName, meta);

                Log($"VirtualHome synchronized to profile '{profileName}'.");
                ProfileSaved?.Invoke(profileName);
            }
            catch (Exception ex)
            {
                Log($"VirtualHome sync notice for '{profileName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Starts a real-time FileSystemWatcher on a profile's VirtualHome\.gemini folder
    /// to debounce sync token / cache changes back to the profile root folder.
    /// </summary>
    public void StartVirtualHomeWatcher(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        try
        {
            string virtualGemini = Path.Combine(ProfilesBaseDir, profileName, "VirtualHome", ".gemini");
            if (!Directory.Exists(virtualGemini))
            {
                Directory.CreateDirectory(virtualGemini);
            }

            if (_virtualHomeWatchers.ContainsKey(profileName)) return;

            var watcher = new FileSystemWatcher(virtualGemini)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            void OnChanged(object sender, FileSystemEventArgs e)
            {
                if (e.ChangeType == WatcherChangeTypes.Deleted) return;
                if (e.FullPath.Contains("antigravity-ide", StringComparison.OrdinalIgnoreCase)) return;

                if (_virtualHomeDebounceTimers.TryGetValue(profileName, out var existingTimer))
                {
                    existingTimer.Dispose();
                }

                _virtualHomeDebounceTimers[profileName] = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        SyncVirtualHomeToProfile(profileName);
                    }
                    catch { }
                }, null, 1500, System.Threading.Timeout.Infinite);
            }

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;

            _virtualHomeWatchers[profileName] = watcher;
        }
        catch (Exception ex)
        {
            Log($"Failed to start watcher for '{profileName}' VirtualHome: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the VirtualHome watcher and flushes any pending writes back to profile root.
    /// </summary>
    public void StopVirtualHomeWatcher(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        try
        {
            if (_virtualHomeWatchers.TryRemove(profileName, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            if (_virtualHomeDebounceTimers.TryRemove(profileName, out var timer))
            {
                timer.Dispose();
            }

            // Final flush
            SyncVirtualHomeToProfile(profileName);
        }
        catch (Exception ex)
        {
            Log($"Error stopping VirtualHome watcher for '{profileName}': {ex.Message}");
        }
    }

    public void Dispose()
    {
        _debounceSyncTimer?.Dispose();
        _fileWatcher?.Dispose();

        foreach (var watcher in _virtualHomeWatchers.Values)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch { }
        }
        _virtualHomeWatchers.Clear();

        foreach (var timer in _virtualHomeDebounceTimers.Values)
        {
            try
            {
                timer.Dispose();
            }
            catch { }
        }
        _virtualHomeDebounceTimers.Clear();
    }
}

public class ProfileMetadata
{
    [JsonPropertyName("ProfileName")]
    public string ProfileName { get; set; } = string.Empty;

    [JsonPropertyName("AccountEmail")]
    public string AccountEmail { get; set; } = string.Empty;

    [JsonPropertyName("WorkingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("AutoResumeSession")]
    public bool AutoResumeSession { get; set; } = true;

    [JsonPropertyName("SkipPermissions")]
    public bool SkipPermissions { get; set; } = false;

    [JsonPropertyName("LastExportUtc")]
    public DateTime LastExportUtc { get; set; }
}
