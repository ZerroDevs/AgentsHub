namespace AgentsHub.Helpers;

/// <summary>
/// Decides which URIs an app-owned WebView may load. Kept out of the WebView2 class so the policy
/// can be tested without a desktop session.
/// </summary>
internal static class NavigationPolicy
{
    public static bool IsAllowed(
        string? rawUri,
        bool allowLoopback,
        string uiRoot,
        IReadOnlyList<string> allowedOrigins)
    {
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)) return false;

        if (uri.Scheme == Uri.UriSchemeFile)
        {
            var local = Path.GetFullPath(uri.LocalPath);
            return local.StartsWith(uiRoot, StringComparison.OrdinalIgnoreCase);
        }

        if (uri.Scheme == "about") return uri.AbsolutePath == "blank";

        if (uri.Scheme is not ("http" or "https")) return false;

        if (allowLoopback && uri.IsLoopback) return true;

        var origin = uri.GetLeftPart(UriPartial.Authority);
        foreach (var allowed in allowedOrigins)
        {
            if (string.Equals(origin, allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
