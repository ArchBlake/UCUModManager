using System.Net;
using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Tests;

public sealed class NexusOAuthAuthorizationCoordinatorTests
{
    private static readonly NexusOAuthOptions Options = new(
        "ucu-test-client",
        new Uri("http://127.0.0.1:17142/ucu-modmanager/oauth/callback"));

    [Fact]
    public async Task Connect_StartsCallbackBeforeBrowserAndStoresValidatedIdentity()
    {
        using var tokenFactory = new OAuthTestTokenFactory();
        var accessToken = tokenFactory.CreateToken(DateTimeOffset.UtcNow.AddHours(1));
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(NexusOAuthClientTests.JsonResponse(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{accessToken}}","refresh_token":"refresh","token_type":"Bearer","expires_in":3600}""")));
        using var httpClient = new HttpClient(handler);
        using var oauthClient = new NexusOAuthClient(httpClient);
        var store = new MemoryTokenStore();
        var provider = new NexusOAuthTokenProvider(
            store,
            oauthClient,
            new NexusOAuthTokenValidator(_ => Task.FromResult(tokenFactory.CreateConfiguration())));
        var listener = FakeCallbackListener.Success("authorization-code");
        var coordinator = new NexusOAuthAuthorizationCoordinator(oauthClient, provider, _ => listener);
        var browserOpened = false;
        var progress = new SynchronousProgress<string>();

        var context = await coordinator.ConnectAsync(Options, (authorizationUri, _) =>
        {
            Assert.True(listener.WaitStarted);
            Assert.Contains("client_id=ucu-test-client", authorizationUri.Query, StringComparison.Ordinal);
            browserOpened = true;
            return Task.CompletedTask;
        }, progress);

        Assert.True(browserOpened);
        Assert.True(listener.Started);
        Assert.Equal("TestAccount", context.Identity.Username);
        Assert.True(store.HasTokens);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, handler.CallCount);
        Assert.True(listener.Disposed);
        Assert.Contains("Validating Nexus account...", progress.Values);
        Assert.Contains("Encrypting account connection...", progress.Values);
        Assert.Equal("Nexus account connected.", progress.Values[^1]);
    }

    [Fact]
    public async Task Connect_AccessDeniedDoesNotExchangeOrStoreTokens()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("Token endpoint must not be called."));
        using var httpClient = new HttpClient(handler);
        using var oauthClient = new NexusOAuthClient(httpClient);
        var store = new MemoryTokenStore();
        var provider = new NexusOAuthTokenProvider(
            store,
            oauthClient,
            new NexusOAuthTokenValidator(_ => throw new InvalidOperationException("Validation must not run.")));
        var listener = FakeCallbackListener.Failure("access_denied");
        var coordinator = new NexusOAuthAuthorizationCoordinator(oauthClient, provider, _ => listener);

        var exception = await Assert.ThrowsAsync<NexusOAuthException>(() => coordinator.ConnectAsync(
            Options,
            (_, _) => Task.CompletedTask));

        Assert.Equal("access_denied", exception.ErrorCode);
        Assert.False(store.HasTokens);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Connect_BrowserFailureCancelsPendingCallback()
    {
        using var oauthClient = new NexusOAuthClient();
        var store = new MemoryTokenStore();
        var provider = new NexusOAuthTokenProvider(
            store,
            oauthClient,
            new NexusOAuthTokenValidator(_ => throw new InvalidOperationException("Validation must not run.")));
        var listener = FakeCallbackListener.Pending();
        var coordinator = new NexusOAuthAuthorizationCoordinator(oauthClient, provider, _ => listener);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ConnectAsync(
            Options,
            (_, _) => throw new InvalidOperationException("Browser launch failed.")));

        Assert.Equal("Browser launch failed.", exception.Message);
        Assert.True(listener.CancellationObserved);
        Assert.True(listener.Disposed);
    }

    [Fact]
    public async Task Connect_ListenerStartFailureDoesNotOpenBrowser()
    {
        using var oauthClient = new NexusOAuthClient();
        var store = new MemoryTokenStore();
        var provider = new NexusOAuthTokenProvider(
            store,
            oauthClient,
            new NexusOAuthTokenValidator(_ => throw new InvalidOperationException("Validation must not run.")));
        var listener = FakeCallbackListener.StartFailure();
        var coordinator = new NexusOAuthAuthorizationCoordinator(oauthClient, provider, _ => listener);
        var browserOpened = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ConnectAsync(
            Options,
            (_, _) =>
            {
                browserOpened = true;
                return Task.CompletedTask;
            }));

        Assert.False(browserOpened);
        Assert.False(listener.WaitStarted);
        Assert.True(listener.Disposed);
    }

    private sealed class FakeCallbackListener : INexusOAuthCallbackListener
    {
        private readonly NexusOAuthCallbackResult? _result;
        private readonly bool _failStart;

        private FakeCallbackListener(NexusOAuthCallbackResult? result, bool failStart = false)
        {
            _result = result;
            _failStart = failStart;
        }

        public bool Started { get; private set; }

        public bool WaitStarted { get; private set; }

        public bool CancellationObserved { get; private set; }

        public bool Disposed { get; private set; }

        public static FakeCallbackListener Success(string code)
        {
            return new FakeCallbackListener(new NexusOAuthCallbackResult(code, null, null));
        }

        public static FakeCallbackListener Failure(string error)
        {
            return new FakeCallbackListener(new NexusOAuthCallbackResult(null, error, null));
        }

        public static FakeCallbackListener Pending()
        {
            return new FakeCallbackListener(null);
        }

        public static FakeCallbackListener StartFailure()
        {
            return new FakeCallbackListener(null, failStart: true);
        }

        public void Start()
        {
            if (_failStart)
            {
                throw new InvalidOperationException("Callback port is unavailable.");
            }

            Started = true;
        }

        public async Task<NexusOAuthCallbackResult> WaitForCallbackAsync(
            string expectedState,
            CancellationToken cancellationToken = default)
        {
            Assert.True(Started);
            WaitStarted = true;
            if (_result is not null)
            {
                return _result;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Pending callback completed unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class MemoryTokenStore : INexusOAuthTokenStore
    {
        private NexusOAuthTokenSet? _tokens;

        public bool HasTokens => _tokens is not null;

        public int SaveCount { get; private set; }

        public NexusOAuthTokenSet? LoadTokens() => _tokens;

        public void SaveTokens(NexusOAuthTokenSet tokens)
        {
            _tokens = tokens;
            SaveCount++;
        }

        public void ClearTokens()
        {
            _tokens = null;
        }
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = new();

        public void Report(T value)
        {
            Values.Add(value);
        }
    }
}
