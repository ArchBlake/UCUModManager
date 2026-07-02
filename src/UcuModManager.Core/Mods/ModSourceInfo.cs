namespace UcuModManager.Core.Mods;

public sealed record ModSourceInfo(
    string Provider,
    string? GameDomain,
    int? ModId,
    int? FileId,
    string? FileVersion,
    DateTimeOffset? FileTimestamp,
    string SourceArchiveFileName,
    string? LastUpdateStatus = null,
    string? LastLatestVersion = null,
    DateTimeOffset? LastCheckedAt = null)
{
    public bool CanCheckUpdates => Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(GameDomain)
        && ModId.HasValue;
}
