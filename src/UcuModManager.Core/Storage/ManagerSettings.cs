namespace UcuModManager.Core.Storage;

public sealed record ManagerSettings(
    string GameRootPath,
    string ActiveProfileId,
    string BepInExVersion,
    bool UseProfileSpecificBepInEx,
    string NexusGameDomain = "scavprototype")
{
    public static ManagerSettings Empty { get; } = new(
        string.Empty,
        "default",
        BepInEx.BepInExRelease.Current.Version,
        UseProfileSpecificBepInEx: true,
        NexusGameDomain: "scavprototype");
}
