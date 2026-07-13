namespace UcuModManager.Core.Nexus;

public sealed record NexusOAuthAuthorizationRequest(
    Uri AuthorizationUri,
    string State,
    string CodeVerifier,
    Uri RedirectUri,
    DateTimeOffset CreatedAt);
