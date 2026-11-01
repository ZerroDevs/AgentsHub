using AgentsHub.Helpers;

namespace AgentsHub.Security.Tests;

public sealed class WebViewOriginTests
{
    private static readonly string[] AppOrigins =
    [
        "https://app.agentshub.local",
        "https://theme.agentshub.local",
    ];

    private static readonly string UiRoot =
        Path.Combine(Path.GetTempPath(), "agentshub-ui") + Path.DirectorySeparatorChar;

    private static bool Allowed(string? uri, bool allowLoopback = false) =>
        NavigationPolicy.IsAllowed(uri, allowLoopback, UiRoot, AppOrigins);

    [Theory]
    [InlineData("https://app.agentshub.local/index.html")]
    [InlineData("https://app.agentshub.local/")]
    [InlineData("https://theme.agentshub.local/theme.css")]
    [InlineData("about:blank")]
    public void AllowsAppPages(string uri) => Assert.True(Allowed(uri));

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("https://app.agentshub.local.evil.example/")]
    [InlineData("https://app.agentshub.local:444/")]
    [InlineData("http://app.agentshub.local/")]
    public void RejectsForeignOrigins(string uri) => Assert.False(Allowed(uri));

    [Theory]
    [InlineData("http://127.0.0.1:9339/terminal.html")]
    [InlineData("http://localhost:9339/terminal.html")]
    [InlineData("http://[::1]:9339/terminal.html")]
    public void LoopbackNeedsExplicitPermission(string uri)
    {
        Assert.False(Allowed(uri));
        Assert.True(Allowed(uri, allowLoopback: true));
    }

    [Fact]
    public void AllowsShippedUiFiles()
    {
        string page = new Uri(Path.Combine(UiRoot, "terminal.html")).AbsoluteUri;

        Assert.True(Allowed(page));
    }

    [Fact]
    public void RejectsFilesOutsideTheUiFolder()
    {
        string elsewhere = new Uri(Path.Combine(Path.GetTempPath(), "agentshub-other", "page.html")).AbsoluteUri;
        string lookalike = new Uri(Path.Combine(Path.GetTempPath(), "agentshub-ui-evil", "page.html")).AbsoluteUri;

        Assert.False(Allowed(elsewhere));
        Assert.False(Allowed(lookalike));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("/index.html")]
    [InlineData("")]
    public void RejectsNonNavigableTargets(string uri) => Assert.False(Allowed(uri));

    [Fact]
    public void RejectsNull() => Assert.False(Allowed(null));
}
