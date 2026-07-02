namespace UcuModManager.Core.BepInEx;

public sealed record BepInExInstallResult(
    BepInExInstallPlan Plan,
    IReadOnlyList<string> InstalledFiles,
    IReadOnlyList<string> SkippedEntries,
    BepInExInstallationState InstallationState);
