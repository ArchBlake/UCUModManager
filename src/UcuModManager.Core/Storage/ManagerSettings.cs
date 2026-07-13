namespace UcuModManager.Core.Storage;

public sealed record ManagerSettings(
    string GameRootPath,
    string ActiveProfileId,
    string BepInExVersion,
    bool UseProfileSpecificBepInEx,
    string NexusGameDomain = "scavprototype",
    bool AutoLinkNexusOnStartup = true,
    string NexusOAuthClientId = "",
    string NexusOAuthRedirectUri = "http://127.0.0.1:17142/ucu-modmanager/oauth/callback",
    bool ShowAdvancedModColumns = false,
    bool VirtualizationEnabled = true,
    bool VirtualizationIntroShown = false)
{
    public static ManagerSettings Empty { get; } = new(
        string.Empty,
        "default",
        BepInEx.BepInExRelease.Current.Version,
        UseProfileSpecificBepInEx: true,
        NexusGameDomain: "scavprototype",
        AutoLinkNexusOnStartup: true,
        NexusOAuthClientId: "",
        NexusOAuthRedirectUri: "http://127.0.0.1:17142/ucu-modmanager/oauth/callback",
        ShowAdvancedModColumns: false,
        VirtualizationEnabled: true,
        VirtualizationIntroShown: false);
}
