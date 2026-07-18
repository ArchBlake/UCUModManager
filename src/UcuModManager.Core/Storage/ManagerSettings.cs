namespace UcuModManager.Core.Storage;

public sealed record ManagerSettings(
    string GameRootPath,
    string ActiveProfileId,
    string BepInExVersion,
    bool UseProfileSpecificBepInEx,
    string NexusGameDomain = "scavprototype",
    bool AutoLinkNexusOnStartup = true,
    bool ShowAdvancedModColumns = false,
    bool VirtualizationEnabled = true,
    bool VirtualizationIntroShown = false,
    bool NexusCatalogCompactMode = false,
    bool CheckManagerUpdatesOnStartup = true,
    bool IncludeManagerPrereleases = false,
    DateTimeOffset? LastManagerUpdateCheckAt = null)
{
    public static ManagerSettings Empty { get; } = new(
        string.Empty,
        "default",
        BepInEx.BepInExRelease.Current.Version,
        UseProfileSpecificBepInEx: true,
        NexusGameDomain: "scavprototype",
        AutoLinkNexusOnStartup: true,
        ShowAdvancedModColumns: false,
        VirtualizationEnabled: true,
        VirtualizationIntroShown: false,
        NexusCatalogCompactMode: false,
        CheckManagerUpdatesOnStartup: true,
        IncludeManagerPrereleases: false,
        LastManagerUpdateCheckAt: null);
}
