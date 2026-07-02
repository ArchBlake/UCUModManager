using System.Net.Http.Headers;
using System.Text.Json;
using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Mods;

public sealed class NexusMetadataCatalogService
{
    public static readonly Uri DefaultCatalogUri = new("https://raw.githubusercontent.com/jimmyking9999999/Metadata-generator/main/nexusmods.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromHours(6);

    private readonly HttpClient _httpClient;
    private IReadOnlyList<NexusMetadataCatalogEntry>? _memoryCache;

    public NexusMetadataCatalogService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<NexusMetadataCatalogLoadResult> LoadAsync(
        ManagerPaths managerPaths,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _memoryCache is not null)
        {
            return new NexusMetadataCatalogLoadResult(_memoryCache, true, null);
        }

        Directory.CreateDirectory(managerPaths.CachePath);
        var cachePath = GetCachePath(managerPaths);
        if (!forceRefresh && IsFreshCache(cachePath))
        {
            var cachedEntries = LoadFromFile(cachePath);
            _memoryCache = cachedEntries;
            return new NexusMetadataCatalogLoadResult(cachedEntries, true, null);
        }

        try
        {
            var json = await DownloadCatalogAsync(cancellationToken).ConfigureAwait(false);
            File.WriteAllText(cachePath, json);
            var entries = ParseCatalog(json);
            _memoryCache = entries;
            return new NexusMetadataCatalogLoadResult(entries, false, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            if (File.Exists(cachePath))
            {
                var cachedEntries = LoadFromFile(cachePath);
                _memoryCache = cachedEntries;
                return new NexusMetadataCatalogLoadResult(
                    cachedEntries,
                    true,
                    $"Using cached Nexus metadata because refresh failed: {exception.Message}");
            }

            throw new InvalidOperationException(
                "Could not load Nexus metadata catalog, and no cached catalog is available.",
                exception);
        }
    }

    private static string GetCachePath(ManagerPaths managerPaths)
    {
        return Path.Combine(managerPaths.CachePath, "nexusmods.json");
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

    private async Task<string> DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DefaultCatalogUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", "0.1"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
}

public sealed record NexusMetadataCatalogLoadResult(
    IReadOnlyList<NexusMetadataCatalogEntry> Entries,
    bool IsFromCache,
    string? Warning);

