using System.Net;
using System.Net.Sockets;
using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Tests;

public sealed class NexusOAuthLoopbackCallbackListenerTests
{
    [Fact]
    public async Task WaitForCallback_IgnoresNoiseAndAcceptsValidCallback()
    {
        var redirectUri = CreateRedirectUri();
        using var listener = new NexusOAuthLoopbackCallbackListener(redirectUri);
        using var httpClient = new HttpClient();
        var waitTask = listener.WaitForCallbackAsync("expected-state", TimeSpan.FromSeconds(5));

        using var wrongPathResponse = await httpClient.GetAsync(new Uri(redirectUri, "/unrelated"));
        using var wrongStateResponse = await httpClient.GetAsync(
            redirectUri + "?state=wrong&code=wrong-code");
        using var validResponse = await httpClient.GetAsync(
            redirectUri + "?state=expected-state&code=authorization-code");
        var result = await waitTask;

        Assert.Equal(HttpStatusCode.NotFound, wrongPathResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongStateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.Equal("authorization-code", result.AuthorizationCode);
    }

    [Fact]
    public async Task WaitForCallback_TimesOutWithoutBrowserResponse()
    {
        var redirectUri = CreateRedirectUri();
        using var listener = new NexusOAuthLoopbackCallbackListener(redirectUri);

        await Assert.ThrowsAsync<TimeoutException>(
            () => listener.WaitForCallbackAsync("expected-state", TimeSpan.FromMilliseconds(200)));
    }

    [Theory]
    [InlineData("https://127.0.0.1:17142/callback")]
    [InlineData("http://example.com:17142/callback")]
    [InlineData("http://127.0.0.1:17142/callback?query=value")]
    public void Constructor_RejectsUnsafeRedirectUri(string redirectUri)
    {
        Assert.Throws<ArgumentException>(() => new NexusOAuthLoopbackCallbackListener(new Uri(redirectUri)));
    }

    private static Uri CreateRedirectUri()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            return new Uri($"http://127.0.0.1:{port}/ucu-modmanager/oauth/callback");
        }
        finally
        {
            probe.Stop();
        }
    }
}
