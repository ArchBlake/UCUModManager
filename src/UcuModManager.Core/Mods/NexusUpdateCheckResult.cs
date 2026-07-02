namespace UcuModManager.Core.Mods;

public sealed record NexusUpdateCheckResult(
    string ModId,
    string Status,
    bool IsUpdateAvailable,
    string? LatestVersion,
    int? LatestFileId,
    string? ErrorMessage,
    string? GameDomain = null,
    int? NexusModId = null,
    string? LatestFileName = null);
