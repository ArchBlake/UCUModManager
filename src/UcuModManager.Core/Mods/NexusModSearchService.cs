using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UcuModManager.Core.Mods;

public sealed class NexusModSearchService
{
    private const int MinimumDeepScanMaxModId = 300;
    private const int DeepScanLookAhead = 50;
    private const int DeepScanHardMaxModId = 1000;
    private const int DeepScanParallelism = 6;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, IReadOnlyList<NexusModSearchResult>> _catalogCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<NexusModFileSearchResult>> _fileCache = new(StringComparer.OrdinalIgnoreCase);

    public NexusModSearchService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<IReadOnlyList<NexusModSearchResult>> SearchAsync(
        string gameDomain,
        string query,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDomain) || string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<NexusModSearchResult>();
        }

        return await LoadCatalogAsync(gameDomain.Trim(), apiKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NexusModFileSearchResult>> GetFilesAsync(
        string gameDomain,
        int modId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDomain) || modId <= 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<NexusModFileSearchResult>();
        }

        var cacheKey = $"{gameDomain}:{modId}";
        if (_fileCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var uri = new Uri($"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(gameDomain.Trim())}/mods/{modId}/files.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddNexusHeaders(request, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<NexusModFileSearchResult>();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus API files returned {(int)response.StatusCode} {response.ReasonPhrase} for {gameDomain}/mods/{modId}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var files = ParseApiFileResults(document.RootElement).ToArray();
        _fileCache[cacheKey] = files;
        return files;
    }

    private async Task<IReadOnlyList<NexusModSearchResult>> LoadCatalogAsync(
        string gameDomain,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (_catalogCache.TryGetValue(gameDomain, out var cached))
        {
            return cached;
        }

        var results = new ConcurrentDictionary<int, NexusModSearchResult>();
        var errors = new List<string>();
        foreach (var endpoint in new[] { "latest_added", "latest_updated", "trending" })
        {
            var uri = new Uri($"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(gameDomain)}/mods/{endpoint}.json");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            AddNexusHeaders(request, apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"{endpoint}: {(int)response.StatusCode} {response.ReasonPhrase}");
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var result in ParseApiResults(document.RootElement, gameDomain))
            {
                results.TryAdd(result.ModId, result);
            }
        }

        await DeepScanModsAsync(gameDomain, apiKey, results, cancellationToken).ConfigureAwait(false);

        if (results.Count == 0 && errors.Count > 0)
        {
            throw new InvalidOperationException($"Nexus API search catalog failed: {string.Join("; ", errors)}.");
        }

        var catalog = results.Values.ToArray();
        _catalogCache[gameDomain] = catalog;
        return catalog;
    }

    private async Task DeepScanModsAsync(
        string gameDomain,
        string apiKey,
        ConcurrentDictionary<int, NexusModSearchResult> results,
        CancellationToken cancellationToken)
    {
        var maxKnownId = results.Keys.DefaultIfEmpty(0).Max();
        var scanMaxId = Math.Min(
            Math.Max(MinimumDeepScanMaxModId, maxKnownId + DeepScanLookAhead),
            DeepScanHardMaxModId);
        var idsToScan = Enumerable.Range(1, scanMaxId)
            .Where(id => !results.ContainsKey(id))
            .ToArray();

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = DeepScanParallelism
        };
        await Parallel.ForEachAsync(idsToScan, options, async (modId, token) =>
        {
            var result = await TryLoadModAsync(gameDomain, modId, apiKey, token).ConfigureAwait(false);
            if (result is not null)
            {
                results.TryAdd(result.ModId, result);
            }
        }).ConfigureAwait(false);
    }

    private async Task<NexusModSearchResult?> TryLoadModAsync(
        string gameDomain,
        int modId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddNexusHeaders(request, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus API mod scan returned {(int)response.StatusCode} {response.ReasonPhrase} for {gameDomain}/mods/{modId}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseApiResult(document.RootElement, gameDomain);
    }

    private static IReadOnlyList<NexusModSearchResult> ParseApiResults(JsonElement root, string gameDomain)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<NexusModSearchResult>();
        }

        var results = new List<NexusModSearchResult>();
        foreach (var element in root.EnumerateArray())
        {
            var result = ParseApiResult(element, gameDomain);
            if (result is null)
            {
                continue;
            }

            results.Add(result);
        }

        return results;
    }

    private static NexusModSearchResult? ParseApiResult(JsonElement element, string gameDomain)
    {
        var modId = TryGetInt(element, "mod_id") ?? TryGetInt(element, "id");
        var title = TryGetString(element, "name") ?? TryGetString(element, "title");
        if (modId is null || modId <= 0 || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var domain = TryGetString(element, "domain_name") ?? gameDomain;
        return new NexusModSearchResult(
            domain,
            modId.Value,
            title,
            $"https://www.nexusmods.com/{domain}/mods/{modId.Value}");
    }

    private static IReadOnlyList<NexusModFileSearchResult> ParseApiFileResults(JsonElement root)
    {
        var filesElement = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("files", out var files)
            ? files
            : root;
        if (filesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<NexusModFileSearchResult>();
        }

        var results = new List<NexusModFileSearchResult>();
        foreach (var element in filesElement.EnumerateArray())
        {
            var fileName = TryGetString(element, "file_name")
                ?? TryGetString(element, "filename")
                ?? TryGetString(element, "name");
            results.Add(new NexusModFileSearchResult(
                TryGetInt(element, "file_id"),
                fileName,
                ResolveFileVersion(TryGetString(element, "version"), fileName),
                TryGetInt(element, "category_id"),
                TryGetString(element, "category_name"),
                TryGetLong(element, "uploaded_timestamp") ?? TryGetLong(element, "uploaded_time") ?? 0));
        }

        return results;
    }

    private static string? ResolveFileVersion(string? apiVersion, string? fileName)
    {
        var fileNameVersion = string.IsNullOrWhiteSpace(fileName)
            ? null
            : ModSourceDetector.DetectVersion(fileName);
        if (!string.IsNullOrWhiteSpace(fileNameVersion)
            && (string.IsNullOrWhiteSpace(apiVersion) || LooksLikeNexusFileCounter(apiVersion)))
        {
            return fileNameVersion;
        }

        return string.IsNullOrWhiteSpace(apiVersion) ? fileNameVersion : apiVersion;
    }

    private static bool LooksLikeNexusFileCounter(string value)
    {
        return int.TryParse(value.Trim().TrimStart('v', 'V'), out _);
    }

    private static void AddNexusHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("apikey", apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", "0.1"));
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }
}

public sealed record NexusModSearchResult(
    string GameDomain,
    int ModId,
    string Title,
    string Url);

public sealed record NexusModFileSearchResult(
    int? FileId,
    string? Name,
    string? Version,
    int? CategoryId,
    string? CategoryName,
    long UploadedTimestamp);
