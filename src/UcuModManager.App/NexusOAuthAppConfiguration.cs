using UcuModManager.Core.Nexus;

namespace UcuModManager.App;

internal static class NexusOAuthAppConfiguration
{
    // Public OAuth client id assigned by Nexus Mods after application registration.
    // Public PKCE clients must never embed or transmit a client secret.
    private const string ClientId = "ucu_mod_manager";

    private static readonly Uri RedirectUri = new(
        "http://127.0.0.1:17142/ucu-modmanager/oauth/callback");

    public static NexusOAuthOptions CreateOptions()
    {
        return new NexusOAuthOptions(ClientId, RedirectUri);
    }
}
