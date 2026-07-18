namespace UcuModManager.Core.Nexus;

public sealed record NexusOAuthOptions(
    string ClientId,
    Uri RedirectUri,
    string Scope = "",
    Uri? AuthorizationEndpoint = null,
    Uri? TokenEndpoint = null)
{
    public static readonly Uri DefaultAuthorizationEndpoint = new("https://users.nexusmods.com/oauth/authorize");
    public static readonly Uri DefaultTokenEndpoint = new("https://users.nexusmods.com/oauth/token");

    public Uri EffectiveAuthorizationEndpoint => AuthorizationEndpoint ?? DefaultAuthorizationEndpoint;

    public Uri EffectiveTokenEndpoint => TokenEndpoint ?? DefaultTokenEndpoint;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && RedirectUri.IsAbsoluteUri;
}
