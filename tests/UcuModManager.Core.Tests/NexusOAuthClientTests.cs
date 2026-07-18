using System.Net;
using System.Security.Cryptography;
using System.Text;
using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Tests;

public sealed class NexusOAuthClientTests
{
    private static readonly NexusOAuthOptions Options = new(
        "ucu-test-client",
        new Uri("http://127.0.0.1:17142/ucu-modmanager/oauth/callback"));

    [Fact]
    public void BuildAuthorizationRequest_IncludesPkceStateAndEmptyScope()
    {
        using var client = new NexusOAuthClient();

        var request = client.BuildAuthorizationRequest(Options);
        var query = ParseForm(request.AuthorizationUri.Query.TrimStart('?'));

        Assert.Equal("code", query["response_type"]);
        Assert.Equal(Options.ClientId, query["client_id"]);
        Assert.True(query.ContainsKey("scope"));
        Assert.Equal(string.Empty, query["scope"]);
        Assert.Equal(Options.RedirectUri.ToString(), query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(request.State, query["state"]);
        Assert.InRange(request.CodeVerifier.Length, 43, 128);

        var expectedChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)));
        Assert.Equal(expectedChallenge, query["code_challenge"]);
    }

    [Fact]
    public async Task ExchangeAuthorizationCode_SendsPublicPkceFormWithoutClientSecret()
    {
        Dictionary<string, string>? sentForm = null;
        var handler = new TestHttpMessageHandler(async (request, cancellationToken) =>
        {
            sentForm = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse(HttpStatusCode.OK, """
                {"access_token":"access","refresh_token":"refresh","token_type":"Bearer","expires_in":3600}
                """);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new NexusOAuthClient(httpClient);

        var token = await client.ExchangeAuthorizationCodeAsync(Options, "auth-code", "code-verifier");

        Assert.NotNull(sentForm);
        Assert.Equal("authorization_code", sentForm!["grant_type"]);
        Assert.Equal("auth-code", sentForm["code"]);
        Assert.Equal("code-verifier", sentForm["code_verifier"]);
        Assert.False(sentForm.ContainsKey("client_secret"));
        Assert.Equal("refresh", token.RefreshToken);
        Assert.True(token.ExpiresAt > token.IssuedAt);
    }

    [Fact]
    public async Task Refresh_PreservesRefreshTokenWhenRotationValueIsOmitted()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            """{"access_token":"new-access","token_type":"Bearer","expires_in":3600}""")));
        using var httpClient = new HttpClient(handler);
        using var client = new NexusOAuthClient(httpClient);

        var token = await client.RefreshAsync(Options, "existing-refresh");

        Assert.Equal("existing-refresh", token.RefreshToken);
    }

    [Fact]
    public async Task TokenError_DoesNotExposeUnstructuredResponseBody()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.BadRequest,
            "raw-sensitive-response-that-must-not-be-shown",
            "text/plain")));
        using var httpClient = new HttpClient(handler);
        using var client = new NexusOAuthClient(httpClient);

        var exception = await Assert.ThrowsAsync<NexusOAuthException>(
            () => client.RefreshAsync(Options, "refresh"));

        Assert.DoesNotContain("raw-sensitive", exception.Message, StringComparison.Ordinal);
        Assert.Equal(400, exception.StatusCode);
    }

    internal static Dictionary<string, string> ParseForm(string value)
    {
        return value.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
                part => part.Length == 1 ? string.Empty : Uri.UnescapeDataString(part[1].Replace('+', ' ')),
                StringComparer.Ordinal);
    }

    internal static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string body,
        string contentType = "application/json")
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
