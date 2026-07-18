using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace UcuModManager.Core.Tests;

internal sealed class OAuthTestTokenFactory : IDisposable
{
    public const string Issuer = "nexus-user-service";

    private readonly RSA _signingKey = RSA.Create(2048);

    public OpenIdConnectConfiguration CreateConfiguration()
    {
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer
        };
        configuration.SigningKeys.Add(new RsaSecurityKey(_signingKey.ExportParameters(false)));
        return configuration;
    }

    public string CreateToken(
        DateTimeOffset expiresAt,
        long userId = 12345,
        string username = "TestAccount",
        IReadOnlyList<string>? membershipRoles = null)
    {
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var header = new JwtHeader(new SigningCredentials(
            new RsaSecurityKey(_signingKey),
            SecurityAlgorithms.RsaSha256));
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = Issuer,
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Iat] = issuedAt.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = expiresAt.ToUnixTimeSeconds(),
            ["user"] = new Dictionary<string, object>
            {
                ["id"] = userId,
                ["username"] = username,
                ["group_id"] = 1,
                ["membership_roles"] = membershipRoles ?? new[] { "member", "premium" },
                ["premium_expiry"] = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds()
            }
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    public void Dispose()
    {
        _signingKey.Dispose();
    }
}
