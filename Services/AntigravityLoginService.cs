#nullable enable
using System.IO;
using System.Text.RegularExpressions;
using AgentsHub.Services.ConPty;

namespace AgentsHub.Services;

/// <summary>
/// Answers agy's OAuth prompt from the chat composer.
///
/// A headless turn runs agy with stdin ignored, so when the profile has no live token the CLI
/// prints its sign-in URL, waits for a code, and exits without a word. The prompt only reads a
/// terminal, so the code is written into a pseudo-console. Its output is redirected to a file
/// instead of being read from the console pipe, because the CLI writes the prompt to the inherited
/// console handle and the pipe never sees it (measured).
/// </summary>
public sealed class AntigravityLoginService
{
    /// agy gives up on the sign-in after this long, so a code pasted later belongs to a dead process.
    private static readonly TimeSpan CodeWindow = TimeSpan.FromSeconds(60);

    private static readonly Regex AuthUrlPattern = new(
        @"https://accounts\.google\.com/o/oauth2/auth\?\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SuccessMarkers = ["Authentication successful", "\"event\":\"init\""];

    private static readonly string[] FailureMarkers =
    [
        "token exchange failed",
        "Sign-in failed",
        "authentication failed",
        "invalid state parameter",
        "sign-in link is no longer valid",
    ];

    private readonly System.Threading.Lock _gate = new();
    private ConPtySession? _session;
    private AntigravityProfileService? _profiles;
    private string _profile = string.Empty;
    private string? _outputFile;
    private string? _authUrl;
    private bool _exited;

    public static AntigravityLoginService Instance { get; } = new();

    public bool IsWaiting
    {
        get { lock (_gate) { return _session is not null && !_exited; } }
    }

    public string Profile
    {
        get { lock (_gate) { return _profile; } }
    }

    public string? AuthUrl
    {
        get { lock (_gate) { return _authUrl; } }
    }

    /// <summary>
    /// True when a turn has credentials to use. The saved profile copy covers the usual case;
    /// live credentials cover a profile that has never been exported, which is also what a turn
    /// seeded from the host's own .gemini would run with.
    /// </summary>
    public static bool IsSignedIn(string profileName)
    {
        try
        {
            if (AntigravityProfileService.HasValidSavedCredentials(profileName)) return true;

            // Only a nameless fallback may borrow whatever the machine already has. A named
            // profile with nothing of its own would otherwise look signed in because another
            // account's credentials exist, and chat would run into the CLI's silent prompt.
            return string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase)
                && AntigravityProfileService.HasValidActiveCredentials();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a sign-in process for the profile and returns the URL agy printed.
    /// Null means the CLI never produced one.
    /// </summary>
    public async Task<string?> StartAsync(string profileName, AntigravityProfileService profiles, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return null;

        string virtualHome;
        try
        {
            virtualHome = profiles.PrepareVirtualHome(profileName);
        }
        catch
        {
            return null;
        }

        string outputFile = Path.Combine(virtualHome, ".gemini", "antigravity-login.out");

        ConPtySession? previous;
        lock (_gate)
        {
            if (_session is not null && !_exited && string.Equals(_profile, profileName, StringComparison.OrdinalIgnoreCase))
            {
                return _authUrl;
            }

            previous = _session;
            _session = null;
        }

        try { previous?.Dispose(); } catch { }
        try { File.Delete(outputFile); } catch { }

        var session = new ConPtySession();
        session.Exited += _ =>
        {
            lock (_gate)
            {
                _exited = true;
            }
        };

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = virtualHome,
            ["HOME"] = virtualHome,
            ["APPDATA"] = Path.Combine(virtualHome, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(virtualHome, "AppData", "Local"),
            ["TERM"] = "xterm-256color",
            ["NO_COLOR"] = "1",
            ["FORCE_COLOR"] = "0",
        };

        // Print mode needs no screen, so the turn is only a vehicle for the sign-in prompt.
        string agy = "agy --output-format stream-json -p \"sign in\"";
        string command = OperatingSystem.IsWindows()
            ? $"cmd.exe /c {agy} > \"{outputFile}\" 2>&1"
            : $"{agy} > \"{outputFile}\" 2>&1";

        if (!session.Start(command, virtualHome, 120, 40, env))
        {
            session.Dispose();
            return null;
        }

        lock (_gate)
        {
            _session = session;
            _profiles = profiles;
            _profile = profileName;
            _outputFile = outputFile;
            _authUrl = null;
            _exited = false;
        }

        // The URL is printed before the prompt waits, so this is a short read, not a turn deadline.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var match = AuthUrlPattern.Match(ReadOutput());
            if (match.Success)
            {
                lock (_gate) { _authUrl = match.Value; }
                return match.Value;
            }

            lock (_gate)
            {
                if (_exited) return null;
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Writes a pasted code into the waiting prompt and reports whether the sign-in completed.
    /// </summary>
    public async Task<(bool SignedIn, string Reason)> SubmitCodeAsync(string code, CancellationToken ct = default)
    {
        ConPtySession? session;
        AntigravityProfileService? profiles;
        string profile;

        lock (_gate)
        {
            session = _session;
            profiles = _profiles;
            profile = _profile;
            if (session is null || _exited)
            {
                return (false, "the sign-in window had already closed");
            }
        }

        // The prompt reads one line, so the terminator is what submits it.
        session.Write(code.Trim() + "\r");

        // agy abandons the flow a minute after the link appears; give the exchange the same window.
        var deadline = DateTime.UtcNow.AddSeconds(50);
        while (DateTime.UtcNow < deadline)
        {
            string output = ReadOutput();

            if (SuccessMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                // The token landed in the profile's VirtualHome, which is what turns read. Fold it
                // back into the saved profile so the account survives the next restart.
                try { profiles?.SyncVirtualHomeToProfile(profile); } catch { }
                try { session.Stop(); } catch { }
                return (true, string.Empty);
            }

            var failure = FailureMarkers.FirstOrDefault(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (failure is not null)
            {
                return (false, failure);
            }

            lock (_gate)
            {
                if (_exited) break;
            }

            try
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return (false, "the code was not accepted");
    }

    /// <summary>Stops the waiting process, for a profile switch or a user cancel.</summary>
    public void Cancel()
    {
        ConPtySession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _authUrl = null;
            _exited = true;
        }

        try { session?.Dispose(); } catch { }
    }

    private string ReadOutput()
    {
        string? file;
        lock (_gate)
        {
            file = _outputFile;
        }

        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return string.Empty;

        try
        {
            // The CLI keeps the file open for writing, so the reader must not lock it out.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }
}
