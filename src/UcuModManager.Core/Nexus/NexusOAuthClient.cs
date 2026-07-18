using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public NexusOAuthClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public NexusOAuthAuthorizationRequest BuildAuthorizationRequest(NexusOAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("Nexus OAuth is not configured. Register the app and set the OAuth client id first.");
        }

        var pkce = NexusOAuthPkce.Generate();
        var state = NexusOAuthPkce.GenerateState();
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", options.ClientId.Trim()),
            new("scope", options.Scope?.Trim() ?? string.Empty),
            new("redirect_uri", options.RedirectUri.ToString()),
            new("code_challenge", pkce.CodeChallenge),
            new("code_challenge_method", pkce.CodeChallengeMethod),
            new("state", state)
        };

        var builder = new UriBuilder(options.EffectiveAuthorizationEndpoint)
        {
            Query = BuildQuery(query)
        };
        return new NexusOAuthAuthorizationRequest(builder.Uri, state, pkce.CodeVerifier, options.RedirectUri, DateTimeOffset.UtcNow);
    }

    public async Task<NexusOAuthTokenSet> ExchangeAuthorizationCodeAsync(
        NexusOAuthOptions options,
        string authorizationCode,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureConfigured(options);
        if (string.IsNullOrWhiteSpace(authorizationCode))
        {
            throw new ArgumentException("Authorization code is required.", nameof(authorizationCode));
        }

        if (string.IsNullOrWhiteSpace(codeVerifier))
        {
            throw new ArgumentException("PKCE code verifier is required.", nameof(codeVerifier));
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = options.ClientId.Trim(),
            ["code"] = authorizationCode.Trim(),
            ["redirect_uri"] = options.RedirectUri.ToString(),
            ["code_verifier"] = codeVerifier.Trim(),
            ["scope"] = options.Scope?.Trim() ?? string.Empty
        };
        return await SendTokenRequestAsync(
                options.EffectiveTokenEndpoint,
                form,
                fallbackRefreshToken: null,
                options.Scope,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<NexusOAuthTokenSet> RefreshAsync(
        NexusOAuthOptions options,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureConfigured(options);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("Refresh token is required.", nameof(refreshToken));
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId.Trim(),
            ["refresh_token"] = refreshToken.Trim()
        };
        return await SendTokenRequestAsync(
                options.EffectiveTokenEndpoint,
                form,
                refreshToken.Trim(),
                options.Scope,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<NexusOAuthTokenSet> SendTokenRequestAsync(
        Uri tokenEndpoint,
        IReadOnlyDictionary<string, string> form,
        string? fallbackRefreshToken,
        string? fallbackScope,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var oauthError = ParseOAuthError(body);
            throw new NexusOAuthException(
                BuildSafeErrorMessage(response, oauthError),
                (int)response.StatusCode,
                oauthError?.Error);
        }

        var token = JsonSerializer.Deserialize<NexusOAuthTokenResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Nexus OAuth token response was empty.");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Nexus OAuth token response did not include an access token.");
        }

        var refreshToken = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? fallbackRefreshToken
            : token.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Nexus OAuth token response did not include a refresh token.");
        }

        var issuedAt = DateTimeOffset.UtcNow;
        var expiresIn = token.ExpiresIn <= 0 ? 3600 : token.ExpiresIn;
        return new NexusOAuthTokenSet(
            token.AccessToken,
            refreshToken,
            string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType,
            string.IsNullOrWhiteSpace(token.Scope) ? fallbackScope : token.Scope,
            issuedAt.AddSeconds(expiresIn),
            issuedAt);
    }

    private static void EnsureConfigured(NexusOAuthOptions options)
    {
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("Nexus OAuth is not configured. Register the app and set the OAuth client id first.");
        }
    }

    private static NexusOAuthErrorResponse? ParseOAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NexusOAuthErrorResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildSafeErrorMessage(HttpResponseMessage response, NexusOAuthErrorResponse? oauthError)
    {
        var message = $"Nexus OAuth token request failed: {(int)response.StatusCode} {response.ReasonPhrase}.";
        if (!string.IsNullOrWhiteSpace(oauthError?.Error))
        {
            message += $" OAuth error: {oauthError.Error}.";
        }

        if (!string.IsNullOrWhiteSpace(oauthError?.ErrorDescription))
        {
            message += $" {Truncate(oauthError.ErrorDescription, 300)}";
        }

        return message;
    }

    private static string Truncate(string value, int maximumLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + "...";
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values)
    {
        return string.Join(
            "&",
            values.Select(value => $"{Uri.EscapeDataString(value.Key)}={Uri.EscapeDataString(value.Value)}"));
    }

    private sealed class NexusOAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }

    private sealed class NexusOAuthErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }
}
