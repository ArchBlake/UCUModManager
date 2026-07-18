using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UcuModManager.Core.Updates;

public sealed class GitHubManagerUpdateService : IDisposable
{
    public static readonly Uri DefaultReleasesUri = new(
        "https://api.github.com/repos/ArchBlake/UCUModManager/releases?per_page=30");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GitHubManagerUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public async Task<ManagerUpdateCheckResult> CheckAsync(
        string currentVersion,
        bool includePrereleases = false,
        CancellationToken cancellationToken = default)
    {
        var parsedCurrentVersion = SemanticVersion.Parse(currentVersion);
        using var request = new HttpRequestMessage(HttpMethod.Get, DefaultReleasesUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", parsedCurrentVersion.ToString()));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer
            .DeserializeAsync<IReadOnlyList<GitHubReleaseDto>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? Array.Empty<GitHubReleaseDto>();

        var latestRelease = releases
            .Where(release => !release.Draft)
            .Select(TryCreateRelease)
            .Where(release => release is not null)
            .Select(release => release!)
            .Where(release => release.Version > parsedCurrentVersion)
            .Where(release => IsReleaseEligible(parsedCurrentVersion, release.Version, includePrereleases))
            .OrderByDescending(release => release.Version)
            .ThenByDescending(release => release.PublishedAt)
            .FirstOrDefault();

        return new ManagerUpdateCheckResult(parsedCurrentVersion, latestRelease, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public static bool IsReleaseEligible(
        SemanticVersion currentVersion,
        SemanticVersion candidateVersion,
        bool includePrereleases)
    {
        if (candidateVersion <= currentVersion)
        {
            return false;
        }

        if (!candidateVersion.IsPrerelease)
        {
            return true;
        }

        if (includePrereleases)
        {
            return true;
        }

        return currentVersion.Channel switch
        {
            ManagerReleaseChannel.Alpha => candidateVersion.Channel is
                ManagerReleaseChannel.Alpha or
                ManagerReleaseChannel.Beta or
                ManagerReleaseChannel.ReleaseCandidate,
            ManagerReleaseChannel.Beta => candidateVersion.Channel is
                ManagerReleaseChannel.Beta or
                ManagerReleaseChannel.ReleaseCandidate,
            ManagerReleaseChannel.ReleaseCandidate =>
                candidateVersion.Channel == ManagerReleaseChannel.ReleaseCandidate,
            _ => false
        };
    }

    private static ManagerReleaseInfo? TryCreateRelease(GitHubReleaseDto release)
    {
        if (!SemanticVersion.TryParse(release.TagName, out var version)
            || string.IsNullOrWhiteSpace(release.HtmlUrl)
            || !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var releasePageUri))
        {
            return null;
        }

        var assets = release.Assets ?? Array.Empty<GitHubReleaseAssetDto>();
        var expectedPackageName = $"UCU-ModManager-{version}-win-x64-portable.zip";
        var packageAsset = assets
            .Where(asset => asset.Name.Equals(expectedPackageName, StringComparison.OrdinalIgnoreCase))
            .Select(TryCreateAsset)
            .FirstOrDefault(asset => asset is not null);
        if (packageAsset is null)
        {
            return null;
        }

        var manifestAsset = assets
            .Where(asset => asset.Name.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase))
            .Select(TryCreateAsset)
            .FirstOrDefault(asset => asset is not null);

        return new ManagerReleaseInfo(
            version,
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            release.Body,
            releasePageUri,
            release.Prerelease || version.IsPrerelease,
            release.PublishedAt,
            packageAsset,
            manifestAsset);
    }

    private static ManagerReleaseAsset? TryCreateAsset(GitHubReleaseAssetDto asset)
    {
        return Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri)
            ? new ManagerReleaseAsset(asset.Name, downloadUri, asset.Size, asset.Digest)
            : null;
    }

    private sealed record GitHubReleaseDto(
        [property: JsonPropertyName("tag_name")] string TagName,
        string? Name,
        string? Body,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        bool Draft,
        bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        IReadOnlyList<GitHubReleaseAssetDto>? Assets);

    private sealed record GitHubReleaseAssetDto(
        string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        long Size,
        string? Digest);
}
