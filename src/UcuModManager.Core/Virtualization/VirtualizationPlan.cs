namespace UcuModManager.Core.Virtualization;

public sealed record VirtualizationPlan(
    string GameRootPath,
    string GameExecutablePath,
    string ProfileId,
    IReadOnlyList<VirtualFileEntry> Files,
    IReadOnlyList<string> Warnings)
{
    public string BepInExPluginsPath => Path.Combine(GameRootPath, "BepInEx", "plugins");
}
