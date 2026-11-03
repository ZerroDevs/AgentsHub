#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentsHub.Services;

/// <summary>
/// A release version, parsed once so every comparison in the app agrees.
///
/// The updater used <see cref="Version"/> with ad-hoc clean-up, which compares a short version as
/// smaller than the same version padded with zeros ("1.0" &lt; "1.0.0"), refuses anything with a
/// suffix ("1.0.0-beta" parsed as a failure and silently reported "up to date"), and turns a missing
/// part into -1 instead of 0. Tags here are written both ways — `0.0.100`, `v0.0.101`, `v1.0.103` —
/// so the number is parsed out once and compared as four integers.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, int Revision, string PreRelease)
{
    // The trailing lookahead is what rejects `1.2.3.4.5`: without it the pattern matches the first
    // four parts and silently ignores the fifth, so a nonsense tag would compare as a real version.
    private static readonly Regex Loose = new(
        @"(?<![\d.])\d+(?:\.\d+){0,3}(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?(?![\d.])",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads `v1.2.3`, `0.0.100`, `1.2`, `1`, `1.0.0.0`, `1.0.0-beta.2`; surrounding text is ignored.
    ///
    /// A patch number is not limited to one or two digits, so a line of releases that walks
    /// `0.0.100` → `0.0.101` → `0.0.102` orders by value, not by text length.
    /// </summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string cleaned = text.Trim().TrimStart('v', 'V', ' ');

        // A tag can arrive as "release-1.2.3" or "Build 1.2.3"; take the first version-shaped token.
        var match = Loose.Match(cleaned);
        if (!match.Success) return false;

        cleaned = match.Value;

        // Build metadata is not part of the order: 1.0.0+abc and 1.0.0 are the same release.
        int plus = cleaned.IndexOf('+');
        if (plus >= 0) cleaned = cleaned[..plus];

        string preRelease = string.Empty;
        int dash = cleaned.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = cleaned[(dash + 1)..];
            cleaned = cleaned[..dash];
        }

        string[] parts = cleaned.Split('.');
        if (parts.Length is < 1 or > 4) return false;

        var numbers = new int[4];

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsDigit)) return false;
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], numbers[3], preRelease);
        return true;
    }

    /// <summary>Same parse, for a caller that already treats a missing number as zero.</summary>
    public static AppVersion Parse(string text) =>
        TryParse(text, out var version) ? version : new AppVersion(0, 0, 0, 0, string.Empty);

    public bool IsNewerThan(AppVersion other) => CompareTo(other) > 0;

    /// <summary>Negative when this version is older, positive when newer, zero when equal.</summary>
    public int CompareTo(AppVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);
        if (Revision != other.Revision) return Revision.CompareTo(other.Revision);

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Semver ordering for the suffix: a release is newer than its own pre-releases, identifiers are
    /// split on dots, numeric ones compare as numbers, and a numeric identifier is older than a word.
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        bool leftEmpty = left.Length == 0, rightEmpty = right.Length == 0;

        if (leftEmpty && rightEmpty) return 0;
        if (leftEmpty) return 1;    // 1.0.0 > 1.0.0-beta
        if (rightEmpty) return -1;

        string[] a = left.Split('.');
        string[] b = right.Split('.');

        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumber = int.TryParse(a[i], out int aValue);
            bool bNumber = int.TryParse(b[i], out int bValue);

            int order =
                aNumber && bNumber ? aValue.CompareTo(bValue)
                : aNumber ? -1
                : bNumber ? 1
                : string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);

            if (order != 0) return order;
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>The version without a leading v, as the interface prints it.</summary>
    public override string ToString()
    {
        string core = Revision > 0 ? $"{Major}.{Minor}.{Patch}.{Revision}" : $"{Major}.{Minor}.{Patch}";
        return PreRelease.Length > 0 ? $"{core}-{PreRelease}" : core;
    }
}
