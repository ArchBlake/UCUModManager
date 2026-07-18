using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Mods;

public sealed class NexusMetadataCatalogService : IDisposable
{
    public static readonly Uri DefaultCatalogUri = new("https://raw.githubusercontent.com/jimmyking9999999/Metadata-generator/main/nexusmods.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromHours(6);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _applicationVersion;
    private IReadOnlyList<NexusMetadataCatalogEntry>? _memoryCache;
    private NexusMetadataCatalogStatus? _memoryCacheStatus;

    public NexusMetadataCatalogService(HttpClient? httpClient = null, string? applicationVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _applicationVersion = string.IsNullOrWhiteSpace(applicationVersion)
            ? ResolveApplicationVersion()
            : applicationVersion.Trim();
    }

    public async Task<NexusMetadataCatalogLoadResult> LoadAsync(
        ManagerPaths managerPaths,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _memoryCache is not null)
        {
            return new NexusMetadataCatalogLoadResult(
                _memoryCache,
                true,
                null,
                _memoryCacheStatus ?? GetStatus(managerPaths));
        }

        Directory.CreateDirectory(managerPaths.CachePath);
        var cachePath = GetCachePath(managerPaths);
        if (!forceRefresh && IsFreshCache(cachePath))
        {
            var cachedEntries = LoadFromFile(cachePath);
            _memoryCache = cachedEntries;
            _memoryCacheStatus = GetStatus(managerPaths, cachedEntries.Count);
            return new NexusMetadataCatalogLoadResult(cachedEntries, true, null, _memoryCacheStatus);
        }

        var attemptedAt = DateTimeOffset.UtcNow;
        try
        {
            var download = await DownloadCatalogAsync(cancellationToken).ConfigureAwait(false);
            File.WriteAllText(cachePath, download.Json);
            var entries = ParseCatalog(download.Json);
            var status = new NexusMetadataCatalogStatus(
                attemptedAt,
                DateTimeOffset.UtcNow,
                download.LastModifiedAt,
                entries.Count,
                DefaultCatalogUri.ToString(),
                null);
            SaveStatus(managerPaths, status);
            _memoryCache = entries;
            _memoryCacheStatus = status;
            return new NexusMetadataCatalogLoadResult(entries, false, null, status);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            if (File.Exists(cachePath))
            {
                var cachedEntries = LoadFromFile(cachePath);
                var status = GetStatus(managerPaths, cachedEntries.Count) with
                {
                    LastAttemptedAt = attemptedAt,
                    LastError = exception.Message
                };
                SaveStatus(managerPaths, status);
                _memoryCache = cachedEntries;
                _memoryCacheStatus = status;
                return new NexusMetadataCatalogLoadResult(
                    cachedEntries,
                    true,
                    $"Using cached Nexus metadata because refresh failed: {exception.Message}",
                    status);
            }

            throw new InvalidOperationException(
                "Could not load Nexus metadata catalog, and no cached catalog is available.",
                exception);
        }
    }

    public void Dispose()
    {
        _memoryCache = null;
        _memoryCacheStatus = null;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public NexusMetadataCatalogStatus GetStatus(ManagerPaths managerPaths)
    {
        return GetStatus(managerPaths, null);
    }

    private static string GetCachePath(ManagerPaths managerPaths)
    {
        return Path.Combine(managerPaths.CachePath, "nexusmods.json");
    }

    private static string GetStatusPath(ManagerPaths managerPaths)
    {
        return Path.Combine(managerPaths.CachePath, "nexusmods.status.json");
    }

    private static bool IsFreshCache(string cachePath)
    {
        if (!File.Exists(cachePath))
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath);
        return age >= TimeSpan.Zero && age <= CacheFreshness;
    }

    private async Task<DownloadedCatalog> DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DefaultCatalogUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", _applicationVersion));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new DownloadedCatalog(
            json,
            response.Content.Headers.LastModified ?? response.Headers.Date);
    }

    private static string ResolveApplicationVersion()
    {
        var assembly = typeof(NexusMetadataCatalogService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
    }

    private static IReadOnlyList<NexusMetadataCatalogEntry> LoadFromFile(string cachePath)
    {
        return ParseCatalog(File.ReadAllText(cachePath));
    }

    private static IReadOnlyList<NexusMetadataCatalogEntry> ParseCatalog(string json)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<NexusMetadataCatalogEntry>>(json, JsonOptions)
            ?.Where(entry => entry.NexusModId is not null && !string.IsNullOrWhiteSpace(entry.NexusGameDomain))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<NexusMetadataCatalogEntry>();
    }

    private static NexusMetadataCatalogStatus GetStatus(ManagerPaths managerPaths, int? entryCount)
    {
        var statusPath = GetStatusPath(managerPaths);
        NexusMetadataCatalogStatus? status = null;
        try
        {
            status = File.Exists(statusPath)
                ? JsonSerializer.Deserialize<NexusMetadataCatalogStatus>(File.ReadAllText(statusPath), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            status = null;
        }

        var cachePath = GetCachePath(managerPaths);
        if (status is not null)
        {
            return entryCount is null || status.EntryCount == entryCount.Value
                ? status
                : status with { EntryCount = entryCount.Value };
        }

        var cachedAt = File.Exists(cachePath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath), TimeSpan.Zero)
            : (DateTimeOffset?)null;
        return new NexusMetadataCatalogStatus(
            null,
            cachedAt,
            cachedAt,
            entryCount ?? 0,
            DefaultCatalogUri.ToString(),
            null);
    }

    private static void SaveStatus(ManagerPaths managerPaths, NexusMetadataCatalogStatus status)
    {
        Directory.CreateDirectory(managerPaths.CachePath);
        File.WriteAllText(GetStatusPath(managerPaths), JsonSerializer.Serialize(status, JsonOptions));
    }

    private sealed record DownloadedCatalog(string Json, DateTimeOffset? LastModifiedAt);
}

public sealed record NexusMetadataCatalogLoadResult(
    IReadOnlyList<NexusMetadataCatalogEntry> Entries,
    bool IsFromCache,
    string? Warning,
    NexusMetadataCatalogStatus Status);

public sealed record NexusMetadataCatalogStatus(
    DateTimeOffset? LastAttemptedAt,
    DateTimeOffset? LastDownloadedAt,
    DateTimeOffset? CatalogLastModifiedAt,
    int EntryCount,
    string? SourceUri,
    string? LastError);
