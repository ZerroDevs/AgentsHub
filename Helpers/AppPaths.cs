namespace AgentsHub.Helpers;

/// <summary>
/// Where the app keeps data that belongs to the signed-in user. It must never live under
/// <see cref="AppContext.BaseDirectory"/>: an installer under Program Files is read-only, and a
/// publish or zip step would happily package the OAuth tokens stored there.
/// </summary>
internal static class AppPaths
{
    public static string UserDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentsHub");

    public static string AntigravityProfilesDir { get; } = Path.Combine(UserDataDir, "Profiles", "Antigravity");

    /// <summary>
    /// One-time relocation for installs that wrote profiles next to the executable. A move keeps
    /// the (potentially large) virtual home on the same disk and is cheaper than copying it.
    /// </summary>
    public static void MigrateLegacyAntigravityProfiles()
    {
        try
        {
            string legacy = Path.Combine(AppContext.BaseDirectory, "Profiles", "Antigravity");
            if (!Directory.Exists(legacy)) return;

            Directory.CreateDirectory(AntigravityProfilesDir);

            foreach (string source in Directory.GetDirectories(legacy))
            {
                string target = Path.Combine(AntigravityProfilesDir, Path.GetFileName(source));
                if (Directory.Exists(target)) continue;

                try
                {
                    Directory.Move(source, target);
                }
                catch
                {
                    // Different volume, or the folder is locked. Copying is the fallback; a failure
                    // here leaves the legacy folder untouched rather than half-moved.
                    CopyDirectory(source, target);
                }
            }
        }
        catch
        {
            // Startup must continue even if an old folder cannot be relocated.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
