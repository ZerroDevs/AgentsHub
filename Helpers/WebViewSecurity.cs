#if WINDOWS
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace AgentsHub.Helpers;

/// <summary>
/// The dashboard and terminal pages can call host actions, so the WebView must never leave the
/// origins the app mapped for it. Without this guard a link in a chat message navigates the
/// privileged WebView to any site, and that site keeps the message bridge it landed on.
/// </summary>
internal static class WebViewSecurity
{
    private static readonly string AppUiRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ui"))
        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>
    /// Locks a WebView to its own pages: in-app origins always, loopback only when asked, and
    /// local files only from the shipped <c>ui</c> folder. Anything else is cancelled and handed
    /// to the system browser, so a chat link still opens where it should.
    /// </summary>
    public static void Apply(CoreWebView2 core, bool allowLoopback, params string[] allowedOrigins)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        core.NavigationStarting += (_, args) =>
        {
            if (NavigationPolicy.IsAllowed(args.Uri, allowLoopback, AppUiRoot, allowedOrigins)) return;
            args.Cancel = true;
            OpenExternal(args.Uri);
        };

        // NavigationStarting covers the top-level page only. Without this, a page could embed an
        // attacker origin in a frame and keep it inside the privileged WebView.
        core.FrameNavigationStarting += (_, args) =>
        {
            if (!NavigationPolicy.IsAllowed(args.Uri, allowLoopback, AppUiRoot, allowedOrigins)) args.Cancel = true;
        };

        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternal(args.Uri);
        };

        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
        };
    }

    /// <summary>Opens http, https and mailto links in the user's own browser, never in the app.</summary>
    public static void OpenExternal(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // A missing mail client or a blocked shell handler is not worth an error dialog.
        }
    }
}
#endif
