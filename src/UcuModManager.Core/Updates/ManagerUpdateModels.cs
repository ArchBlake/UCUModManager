namespace UcuModManager.Core.Updates;

public sealed record ManagerReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Digest);

public sealed record ManagerReleaseInfo(
    SemanticVersion Version,
    string TagName,
    string Name,
    string? ReleaseNotes,
    Uri ReleasePageUri,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    ManagerReleaseAsset PackageAsset,
    ManagerReleaseAsset? ManifestAsset);

public sealed record ManagerUpdateCheckResult(
    SemanticVersion CurrentVersion,
    ManagerReleaseInfo? LatestRelease,
    DateTimeOffset CheckedAt)
{
    public bool IsUpdateAvailable => LatestRelease is not null && LatestRelease.Version > CurrentVersion;
}
