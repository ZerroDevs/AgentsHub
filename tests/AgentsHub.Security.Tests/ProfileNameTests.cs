using AgentsHub.Services;

namespace AgentsHub.Security.Tests;

public sealed class ProfileNameTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    public void DotOnlyNamesFallBackAndAreRejected(string name)
    {
        Assert.Equal("Profile", AntigravityProfileService.SanitizeProfileName(name));
        Assert.False(AntigravityProfileService.IsSafeProfileName(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("CON.txt")]
    public void ReservedDeviceNamesGetPrefixed(string name)
    {
        string sanitized = AntigravityProfileService.SanitizeProfileName(name);

        Assert.StartsWith("_", sanitized);
        Assert.False(AntigravityProfileService.IsSafeProfileName(name));
        Assert.True(AntigravityProfileService.IsSafeProfileName(sanitized));
    }

    [Theory]
    [InlineData("Account 1.")]
    [InlineData("Account 1 ")]
    public void TrailingDotsAndSpacesAreTrimmed(string name)
    {
        string sanitized = AntigravityProfileService.SanitizeProfileName(name);

        Assert.Equal("Account 1", sanitized);
        Assert.False(AntigravityProfileService.IsSafeProfileName(name));
        Assert.True(AntigravityProfileService.IsSafeProfileName(sanitized));
    }

    [Fact]
    public void PathSeparatorsAreReplaced()
    {
        string sanitized = AntigravityProfileService.SanitizeProfileName("a/b");

        Assert.Equal("a_b", sanitized);
        Assert.False(AntigravityProfileService.IsSafeProfileName("a/b"));
        Assert.True(AntigravityProfileService.IsSafeProfileName(sanitized));
    }

    [Fact]
    public void ControlCharactersAreReplaced()
    {
        string sanitized = AntigravityProfileService.SanitizeProfileName("a\u0001b");

        Assert.Equal("a_b", sanitized);
        Assert.False(AntigravityProfileService.IsSafeProfileName("a\u0001b"));
    }

    [Fact]
    public void LongNamesAreCapped()
    {
        string name = new string('a', 80);
        string sanitized = AntigravityProfileService.SanitizeProfileName(name);

        Assert.Equal(64, sanitized.Length);
        Assert.False(AntigravityProfileService.IsSafeProfileName(name));
        Assert.True(AntigravityProfileService.IsSafeProfileName(sanitized));
    }

    [Theory]
    [InlineData("Account_1")]
    [InlineData("Account 1")]
    [InlineData("_CON")]
    public void OrdinaryNamesAreSafe(string name)
    {
        Assert.Equal(name, AntigravityProfileService.SanitizeProfileName(name));
        Assert.True(AntigravityProfileService.IsSafeProfileName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNamesAreRejected(string name)
    {
        Assert.Equal("Profile", AntigravityProfileService.SanitizeProfileName(name));
        Assert.False(AntigravityProfileService.IsSafeProfileName(name));
    }
}
