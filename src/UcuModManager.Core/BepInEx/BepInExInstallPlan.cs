namespace UcuModManager.Core.BepInEx;

public sealed record BepInExInstallPlan(
    string ArchivePath,
    string GameRootPath,
    IReadOnlyList<string> FilesToInstall,
    IReadOnlyList<string> ExistingFiles,
    IReadOnlyList<string> Warnings);
