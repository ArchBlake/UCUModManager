using System.Net;
using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Tests;

public sealed class NexusModsApiClientTests
{
    [Fact]
    public async Task GetModFiles_SendsBearerAndCurrentApplicationHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(NexusOAuthClientTests.JsonResponse(HttpStatusCode.OK, """{"files":[]}"""));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new NexusModsApiClient(httpClient, "0.1.4-alpha-public");

        var files = await client.GetModFilesAsync("scavprototype", 53, "access-token");

        Assert.Empty(files);
        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("access-token", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Equal("UCU Mod Manager", Assert.Single(capturedRequest.Headers.GetValues("Application-Name")));
        Assert.Equal("0.1.4-alpha-public", Assert.Single(capturedRequest.Headers.GetValues("Application-Version")));
    }

    [Fact]
    public async Task GetModFiles_ErrorDoesNotExposeResponseBody()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(NexusOAuthClientTests.JsonResponse(
            HttpStatusCode.InternalServerError,
            "sensitive-response")));
        using var httpClient = new HttpClient(handler);
        using var client = new NexusModsApiClient(httpClient, "test");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetModFilesAsync("scavprototype", 53, "access-token"));

        Assert.DoesNotContain("sensitive-response", exception.Message, StringComparison.Ordinal);
    }
}
