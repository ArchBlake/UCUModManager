using System.Text.Json.Serialization;

namespace UcuModManager.Core.Mods;

public sealed class NexusMetadataCatalogEntry
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }

    [JsonPropertyName("bepinexVersion")]
    public string? BepInExVersion { get; init; }

    public string? Description { get; init; }
    public string? Author { get; init; }
    public NexusMetadataLinks? Links { get; init; }
    public string? DownloadUrl { get; init; }
    public string? NexusGameDomain { get; init; }
    public int? NexusModId { get; init; }
    public string? SourceName { get; init; }

    [JsonPropertyName("dllNames")]
    public IReadOnlyList<string> DllNames { get; init; } = Array.Empty<string>();

    [JsonPropertyName("dllVersion")]
    public string? DllVersion { get; init; }

    [JsonPropertyName("dllVersions")]
    public IReadOnlyDictionary<string, string> DllVersions { get; init; } = new Dictionary<string, string>();

    public NexusMetadataStatistics? Statistics { get; init; }
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();

    [JsonIgnore]
    public NexusMetadataDownloadReference? DownloadReference => NexusMetadataDownloadReference.TryParse(DownloadUrl);

    [JsonIgnore]
    public string? BestVersion => FirstNonEmpty(Version, BepInExVersion, DllVersion);

    [JsonIgnore]
    public string? BestIconUrl => FirstNonEmpty(Links?.Icon, Images.FirstOrDefault());

    [JsonIgnore]
    public string? NexusPageUrl => FirstNonEmpty(
        Links?.NexusMods,
        !string.IsNullOrWhiteSpace(NexusGameDomain) && NexusModId is not null
            ? $"https://www.nexusmods.com/{NexusGameDomain}/mods/{NexusModId.Value}"
            : null);

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }
}

public sealed class NexusMetadataLinks
{
    public string? Icon { get; init; }
    public string? NexusMods { get; init; }
}

public sealed class NexusMetadataStatistics
{
    public int? Endorsements { get; init; }
    public int? UniqueDownloads { get; init; }
    public int? TotalDownloads { get; init; }
    public int? TotalViews { get; init; }
}

public sealed record NexusMetadataDownloadReference(string GameDomain, int ModId, int? FileId)
{
    public static NexusMetadataDownloadReference? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("nexus", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var gameDomain = uri.Host;
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(gameDomain)
            || segments.Length < 1
            || !int.TryParse(segments[0], out var modId))
        {
            return null;
        }

        var fileId = segments.Length > 1 && int.TryParse(segments[1], out var parsedFileId)
            ? parsedFileId
            : (int?)null;
        return new NexusMetadataDownloadReference(gameDomain, modId, fileId);
    }
}

