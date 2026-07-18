namespace UcuModManager.Core.Nexus;

public sealed record NexusOAuthIdentity(
    long UserId,
    string Username,
    int? GroupId,
    IReadOnlyList<string> MembershipRoles,
    DateTimeOffset? PremiumExpiry)
{
    public bool HasPremiumMembership(DateTimeOffset? now = null)
    {
        if (MembershipRoles.Any(role =>
                role.Equals("premium", StringComparison.OrdinalIgnoreCase)
                || role.Equals("lifetimepremium", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return PremiumExpiry is not null && PremiumExpiry > (now ?? DateTimeOffset.UtcNow);
    }
}

public sealed record NexusOAuthAccessContext(
    NexusOAuthTokenSet Tokens,
    NexusOAuthIdentity Identity);
