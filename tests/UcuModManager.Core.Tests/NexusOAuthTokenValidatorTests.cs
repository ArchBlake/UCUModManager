using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Tests;

public sealed class NexusOAuthTokenValidatorTests
{
    [Fact]
    public async Task Validate_ReturnsVerifiedNexusIdentity()
    {
        using var tokens = new OAuthTestTokenFactory();
        var configuration = tokens.CreateConfiguration();
        var validator = new NexusOAuthTokenValidator(_ => Task.FromResult(configuration));
        var accessToken = tokens.CreateToken(DateTimeOffset.UtcNow.AddHours(1));

        var identity = await validator.ValidateAsync(accessToken);

        Assert.Equal(12345, identity.UserId);
        Assert.Equal("TestAccount", identity.Username);
        Assert.True(identity.HasPremiumMembership());
    }

    [Fact]
    public async Task Validate_RejectsTokenSignedByDifferentKey()
    {
        using var trusted = new OAuthTestTokenFactory();
        using var untrusted = new OAuthTestTokenFactory();
        var configuration = trusted.CreateConfiguration();
        var validator = new NexusOAuthTokenValidator(_ => Task.FromResult(configuration));
        var accessToken = untrusted.CreateToken(DateTimeOffset.UtcNow.AddHours(1));

        var exception = await Assert.ThrowsAsync<NexusOAuthTokenValidationException>(
            () => validator.ValidateAsync(accessToken));

        Assert.False(exception.IsExpired);
    }

    [Fact]
    public async Task Validate_AcceptsDocumentedLegacyIssuerWithCurrentDiscoveryIssuer()
    {
        using var tokens = new OAuthTestTokenFactory();
        var configuration = tokens.CreateConfiguration();
        configuration.Issuer = "https://users.nexusmods.com";
        var validator = new NexusOAuthTokenValidator(_ => Task.FromResult(configuration));
        var accessToken = tokens.CreateToken(DateTimeOffset.UtcNow.AddHours(1));

        var identity = await validator.ValidateAsync(accessToken);

        Assert.Equal("TestAccount", identity.Username);
    }

    [Fact]
    public async Task Validate_RejectsExpiredToken()
    {
        using var tokens = new OAuthTestTokenFactory();
        var configuration = tokens.CreateConfiguration();
        var validator = new NexusOAuthTokenValidator(_ => Task.FromResult(configuration));
        var accessToken = tokens.CreateToken(DateTimeOffset.UtcNow.AddMinutes(-10));

        var exception = await Assert.ThrowsAsync<NexusOAuthTokenValidationException>(
            () => validator.ValidateAsync(accessToken));

        Assert.True(exception.IsExpired);
    }

    [Fact]
    public async Task Validate_TimesOutWhenConfigurationProviderDoesNotComplete()
    {
        var validator = new NexusOAuthTokenValidator(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            },
            configurationTimeout: TimeSpan.FromMilliseconds(50));

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => validator.ValidateAsync("header.payload.signature"));

        Assert.Contains("verification keys", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
