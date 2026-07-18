namespace UcuModManager.Core.Nexus;

public sealed record NexusOAuthTokenSet(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    string? Scope,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IssuedAt)
{
    public bool IsExpired(DateTimeOffset? now = null, TimeSpan? clockSkew = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var skew = clockSkew ?? TimeSpan.FromMinutes(2);
        return ExpiresAt <= current.Add(skew);
    }
}
