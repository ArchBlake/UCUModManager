using System.Security.Cryptography;
using System.Text;

namespace UcuModManager.Core.Nexus;

public static class NexusOAuthPkce
{
    public static NexusOAuthPkcePair Generate()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new NexusOAuthPkcePair(verifier, challenge, "S256");
    }

    public static string GenerateState()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public sealed record NexusOAuthPkcePair(
    string CodeVerifier,
    string CodeChallenge,
    string CodeChallengeMethod);
