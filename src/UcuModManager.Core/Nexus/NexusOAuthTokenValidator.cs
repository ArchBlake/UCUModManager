using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthTokenValidator
{
    public const string DefaultMetadataAddress = "https://users.nexusmods.com/.well-known/openid-configuration";

    private static readonly HttpClient SharedHttpClient = new();

    private readonly Func<CancellationToken, Task<OpenIdConnectConfiguration>> _configurationProvider;
    private readonly Action? _requestRefresh;

    public NexusOAuthTokenValidator(HttpClient? httpClient = null)
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
    }

    public NexusOAuthTokenValidator(
        Func<CancellationToken, Task<OpenIdConnectConfiguration>> configurationProvider,
        Action? requestRefresh = null)
    {
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _requestRefresh = requestRefresh;
    }

    public async Task<NexusOAuthIdentity> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("OAuth access token is required.", nameof(accessToken));
        }

        var configuration = await _configurationProvider(cancellationToken).ConfigureAwait(false);
        try
        {
            return Validate(accessToken, configuration);
        }
        catch (NexusOAuthTokenValidationException exception)
            when (exception.InnerException is SecurityTokenSignatureKeyNotFoundException && _requestRefresh is not null)
        {
            _requestRefresh();
            configuration = await _configurationProvider(cancellationToken).ConfigureAwait(false);
            return Validate(accessToken, configuration);
        }
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
            IssuerSigningKeys = configuration.SigningKeys,
            RequireSignedTokens = true,
            ValidateIssuer = true,
            ValidIssuer = configuration.Issuer,
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

        var username = ReadString(user, "username") ?? $"Nexus user {userId.Value}";
        var groupId = ReadInt32(user, "group_id");
        var roles = ReadStringArray(user, "membership_roles");
        var premiumExpirySeconds = ReadInt64(user, "premium_expiry");
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
