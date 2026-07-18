using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthTokenValidator
{
    public const string DefaultMetadataAddress = "https://users.nexusmods.com/.well-known/openid-configuration";
    private const string LegacyIssuer = "nexus-user-service";
    private const string LegacyPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDhKHxCWOeUy38S3UOBOB11SNd/
        wyL9TVvzxePkEsZb4fEVGp0U5MEcDcJgXUo/fZOYTUFMX7ipvCC7sbsyKpJ0xZ/M
        l5zXMBcI03gu6p1TvG+eL0xEk6X8LD+t+GbzH9EY58bZ8kOLEx4lbAX3fNYhMhbh
        HJra9ZVW2QdgHoDV6wIDAQAB
        -----END PUBLIC KEY-----
        """;
    public static readonly TimeSpan DefaultConfigurationTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient SharedHttpClient = new();
    private static readonly SecurityKey LegacySigningKey = CreateLegacySigningKey();

    private readonly Func<CancellationToken, Task<OpenIdConnectConfiguration>> _configurationProvider;
    private readonly Action? _requestRefresh;
    private readonly TimeSpan _configurationTimeout;

    public NexusOAuthTokenValidator(
        HttpClient? httpClient = null,
        TimeSpan? configurationTimeout = null)
    {
        var documentRetriever = new HttpDocumentRetriever(httpClient ?? SharedHttpClient)
        {
            RequireHttps = true
        };
        var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            DefaultMetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            documentRetriever);
        _configurationProvider = configurationManager.GetConfigurationAsync;
        _requestRefresh = configurationManager.RequestRefresh;
        _configurationTimeout = ValidateTimeout(configurationTimeout);
    }

    public NexusOAuthTokenValidator(
        Func<CancellationToken, Task<OpenIdConnectConfiguration>> configurationProvider,
        Action? requestRefresh = null,
        TimeSpan? configurationTimeout = null)
    {
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _requestRefresh = requestRefresh;
        _configurationTimeout = ValidateTimeout(configurationTimeout);
    }

    public async Task<NexusOAuthIdentity> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("OAuth access token is required.", nameof(accessToken));
        }

        var configuration = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Validate(accessToken, configuration);
        }
        catch (NexusOAuthTokenValidationException exception)
            when (exception.InnerException is SecurityTokenSignatureKeyNotFoundException && _requestRefresh is not null)
        {
            _requestRefresh();
            configuration = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            return Validate(accessToken, configuration);
        }
    }

    private async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_configurationTimeout);
        try
        {
            return await _configurationProvider(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Timed out while loading Nexus authorization verification keys.",
                exception);
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? DefaultConfigurationTimeout;
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Configuration timeout must be positive and finite.");
        }

        return value;
    }

    private static NexusOAuthIdentity Validate(string accessToken, OpenIdConnectConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Issuer))
        {
            throw new InvalidOperationException("Nexus OpenID configuration did not provide an issuer.");
        }

        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException("Nexus OpenID configuration did not provide signing keys.");
        }

        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys.Concat(new[] { LegacySigningKey }),
            TryAllIssuerSigningKeys = true,
            RequireSignedTokens = true,
            ValidateIssuer = true,
            ValidIssuers = new[] { configuration.Issuer, LegacyIssuer }
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ValidateAudience = false,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        try
        {
            handler.ValidateToken(accessToken, validationParameters, out var validatedToken);
            if (validatedToken is not JwtSecurityToken jwt)
            {
                throw new NexusOAuthTokenValidationException("Nexus returned an unsupported access token format.", false);
            }

            return ReadIdentity(jwt.Payload);
        }
        catch (NexusOAuthTokenValidationException)
        {
            throw;
        }
        catch (SecurityTokenExpiredException exception)
        {
            throw new NexusOAuthTokenValidationException("The Nexus access token has expired.", true, exception);
        }
        catch (SecurityTokenInvalidIssuerException exception)
        {
            throw new NexusOAuthTokenValidationException("The Nexus access token issuer is not recognized.", false, exception);
        }
        catch (SecurityTokenSignatureKeyNotFoundException exception)
        {
            throw new NexusOAuthTokenValidationException("The Nexus access token signing key is not recognized.", false, exception);
        }
        catch (SecurityTokenInvalidSignatureException exception)
        {
            throw new NexusOAuthTokenValidationException("The Nexus access token signature is invalid.", false, exception);
        }
        catch (SecurityTokenException exception)
        {
            throw new NexusOAuthTokenValidationException("The Nexus access token signature or claims are invalid.", false, exception);
        }
        catch (ArgumentException exception)
        {
            throw new NexusOAuthTokenValidationException("Nexus returned a malformed access token.", false, exception);
        }
    }

    private static NexusOAuthIdentity ReadIdentity(JwtPayload payload)
    {
        var user = ReadObject(payload, "user");
        var userId = ReadInt64(user, "id") ?? ReadInt64(payload, "sub");
        if (userId is null || userId <= 0)
        {
            throw new NexusOAuthTokenValidationException("The Nexus access token did not contain a valid user id.", false);
        }

        var username = ReadString(user, "username")
            ?? ReadString(payload, "name")
            ?? ReadString(payload, "username")
            ?? $"Nexus user {userId.Value}";
        var groupId = ReadInt32(user, "group_id") ?? ReadInt32(payload, "group_id");
        var roles = ReadStringArray(user, "membership_roles");
        if (roles.Count == 0)
        {
            roles = ReadStringArray(payload, "membership_roles");
        }

        var premiumExpirySeconds = ReadInt64(user, "premium_expiry")
            ?? ReadInt64(payload, "premium_expiry");
        DateTimeOffset? premiumExpiry = null;
        if (premiumExpirySeconds is > 0)
        {
            try
            {
                premiumExpiry = DateTimeOffset.FromUnixTimeSeconds(premiumExpirySeconds.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                premiumExpiry = null;
            }
        }

        return new NexusOAuthIdentity(userId.Value, username, groupId, roles, premiumExpiry);
    }

    private static JsonElement ReadObject(IReadOnlyDictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return default;
        }

        try
        {
            return value is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(value);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static long? ReadInt64(IReadOnlyDictionary<string, object> values, string key)
    {
        return values.TryGetValue(key, out var value) ? ConvertToInt64(value) : null;
    }

    private static long? ReadInt64(JsonElement element, string key)
    {
        return TryGetProperty(element, key, out var value) ? ConvertToInt64(value) : null;
    }

    private static int? ReadInt32(JsonElement element, string key)
    {
        var value = ReadInt64(element, key);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static int? ReadInt32(IReadOnlyDictionary<string, object> values, string key)
    {
        var value = ReadInt64(values, key);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static long? ConvertToInt64(object? value)
    {
        return value switch
        {
            null => null,
            long number => number,
            int number => number,
            JsonElement element => ConvertToInt64(element),
            string text when long.TryParse(text, out var parsed) => parsed,
            _ when long.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static long? ConvertToInt64(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(element.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement element, string key)
    {
        if (!TryGetProperty(element, key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? ReadString(IReadOnlyDictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => value.ToString()
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string key)
    {
        if (!TryGetProperty(element, key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(
        IReadOnlyDictionary<string, object> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var element = value is JsonElement jsonElement
                ? jsonElement
                : JsonSerializer.SerializeToElement(value);
            if (element.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(item => item.GetString()!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static SecurityKey CreateLegacySigningKey()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(LegacyPublicKeyPem);
        return new RsaSecurityKey(rsa.ExportParameters(false));
    }

    private static bool TryGetProperty(JsonElement element, string key, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
