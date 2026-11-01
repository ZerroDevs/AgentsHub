using AgentsHub.Services;

namespace AgentsHub.Security.Tests;

public sealed class UpdateUrlTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo/releases/download/v1/AgentsHub_Setup.exe")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/Setup.exe")]
    [InlineData("https://release-assets.githubusercontent.com/asset/Setup.EXE")]
    [InlineData("https://GITHUB.COM/owner/repo/Setup.exe")]
    public void AcceptsTrustedHttpsAssets(string url) =>
        Assert.True(UpdateService.IsTrustedReleaseUrl(url));

    [Theory]
    [InlineData("http://github.com/owner/repo/Setup.exe")]
    [InlineData("https://evil.example/Setup.exe")]
    [InlineData("https://github.com.evil.example/Setup.exe")]
    [InlineData("https://github.com/owner/repo/Setup.msi")]
    [InlineData("file:///C:/Setup.exe")]
    [InlineData("/Setup.exe")]
    [InlineData("")]
    public void RejectsUntrustedUrls(string url) =>
        Assert.False(UpdateService.IsTrustedReleaseUrl(url));

    [Fact]
    public void RejectsNull() => Assert.False(UpdateService.IsTrustedReleaseUrl(null));
}
