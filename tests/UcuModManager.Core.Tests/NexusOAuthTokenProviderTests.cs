using System.Net;
using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Tests;

public sealed class NexusOAuthTokenProviderTests
{
    private static readonly NexusOAuthOptions Options = new(
        "ucu-test-client",
        new Uri("http://127.0.0.1:17142/ucu-modmanager/oauth/callback"));

    [Fact]
    public async Task GetAccessContext_RefreshesExpiredTokenAndStoresRotation()
    {
        using var tokens = new OAuthTestTokenFactory();
        var accessToken = tokens.CreateToken(DateTimeOffset.UtcNow.AddHours(1));
        var store = new MemoryTokenStore(CreateExpiredTokens());
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(NexusOAuthClientTests.JsonResponse(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{accessToken}}","refresh_token":"rotated-refresh","token_type":"Bearer","expires_in":3600}""")));
        using var httpClient = new HttpClient(handler);
        using var oauthClient = new NexusOAuthClient(httpClient);
        var validator = new NexusOAuthTokenValidator(_ => Task.FromResult(tokens.CreateConfiguration()));
        var provider = new NexusOAuthTokenProvider(store, oauthClient, validator);

        var context = await provider.GetAccessContextAsync(Options);

        Assert.Equal("rotated-refresh", context.Tokens.RefreshToken);
        Assert.Equal("TestAccount", context.Identity.Username);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task GetAccessContext_ClearsRevokedAuthorization()
    {
        var store = new MemoryTokenStore(CreateExpiredTokens());
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(NexusOAuthClientTests.JsonResponse(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"revoked"}""")));
        using var httpClient = new HttpClient(handler);
        using var oauthClient = new NexusOAuthClient(httpClient);
        var validator = new NexusOAuthTokenValidator(_ => throw new InvalidOperationException("Validation should not run."));
        var provider = new NexusOAuthTokenProvider(store, oauthClient, validator);

        await Assert.ThrowsAsync<NexusOAuthAuthenticationRequiredException>(
            () => provider.GetAccessContextAsync(Options));

        Assert.False(store.HasTokens);
        Assert.Equal(1, store.ClearCount);
    }

    [Fact]
    public async Task GetAccessContext_ConcurrentCallsPerformSingleRefresh()
    {
        using var tokens = new OAuthTestTokenFactory();
        var accessToken = tokens.CreateToken(DateTimeOffset.UtcNow.AddHours(1));
        var store = new MemoryTokenStore(CreateExpiredTokens());
        var handler = new TestHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(25, cancellationToken);
            return NexusOAuthClientTests.JsonResponse(
                HttpStatusCode.OK,
                $$"""{"access_token":"{{accessToken}}","refresh_token":"rotated","token_type":"Bearer","expires_in":3600}""");
        });
        using var httpClient = new HttpClient(handler);
        using var oauthClient = new NexusOAuthClient(httpClient);
        var configuration = tokens.CreateConfiguration();
        var validator = new NexusOAuthTokenValidator(_ => Task.FromResult(configuration));
        var provider = new NexusOAuthTokenProvider(store, oauthClient, validator);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => provider.GetAccessContextAsync(Options)));

        Assert.All(results, result => Assert.Equal("rotated", result.Tokens.RefreshToken));
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, store.SaveCount);
    }

    private static NexusOAuthTokenSet CreateExpiredTokens()
    {
        var issuedAt = DateTimeOffset.UtcNow.AddHours(-2);
        return new NexusOAuthTokenSet(
            "expired-access",
            "refresh",
            "Bearer",
            string.Empty,
            issuedAt.AddHours(1),
            issuedAt);
    }

    private sealed class MemoryTokenStore : INexusOAuthTokenStore
    {
        private NexusOAuthTokenSet? _tokens;

        public MemoryTokenStore(NexusOAuthTokenSet? tokens)
        {
            _tokens = tokens;
        }

        public bool HasTokens => _tokens is not null;

        public int SaveCount { get; private set; }

        public int ClearCount { get; private set; }

        public NexusOAuthTokenSet? LoadTokens() => _tokens;

        public void SaveTokens(NexusOAuthTokenSet tokens)
        {
            _tokens = tokens;
            SaveCount++;
        }

        public void ClearTokens()
        {
            _tokens = null;
            ClearCount++;
        }
    }
}
