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
    DateTimeOffset? LastCheckedAt = null,
    string? DisplayName = null,
    string? Author = null,
    string? PageUrl = null,
    string? IconUrl = null,
    IReadOnlyList<string>? ImageUrls = null,
    string? Description = null,
    int? Endorsements = null,
    int? UniqueDownloads = null,
    int? TotalDownloads = null,
    int? TotalViews = null)
{
    public bool CanCheckUpdates => Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(GameDomain)
        && ModId.HasValue;
}
