using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Mods;

public sealed class NexusModFilesService : IDisposable
{
    private const int MaxMemoryCacheEntries = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, NexusModFilesLoadResult> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _memoryCacheOrder = new();
    private readonly bool _ownsHttpClient;

    public NexusModFilesService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public bool HasCachedFiles(
        ManagerPaths managerPaths,
        string gameDomain,
        int modId,
        string cacheFingerprint)
    {
        var key = BuildMemoryCacheKey(gameDomain, modId, cacheFingerprint);
        return _memoryCache.ContainsKey(key)
            || File.Exists(GetCachePath(managerPaths, gameDomain, modId, cacheFingerprint));
    }

    public NexusModFilesLoadResult? TryLoadCached(
        ManagerPaths managerPaths,
        string gameDomain,
        int modId,
        string cacheFingerprint)
    {
        var key = BuildMemoryCacheKey(gameDomain, modId, cacheFingerprint);
        if (_memoryCache.TryGetValue(key, out var cached))
        {
            return cached with { IsFromCache = true };
        }

        var cachePath = GetCachePath(managerPaths, gameDomain, modId, cacheFingerprint);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<NexusModFilesCacheDocument>(File.ReadAllText(cachePath), JsonOptions);
            if (document is null
                || !document.GameDomain.Equals(gameDomain, StringComparison.OrdinalIgnoreCase)
                || document.ModId != modId
                || !document.CacheFingerprint.Equals(cacheFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var result = new NexusModFilesLoadResult(
                document.GameDomain,
                document.ModId,
                document.CacheFingerprint,
                NormalizeFiles(document.Files),
                document.CachedAt,
                true,
                null);
            CacheInMemory(key, result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public NexusModFilesLoadResult? TryLoadAnyCached(
        ManagerPaths managerPaths,
        string gameDomain,
        int modId)
    {
        var cacheDirectory = GetCacheDirectory(managerPaths, gameDomain);
        if (!Directory.Exists(cacheDirectory))
        {
            return null;
        }

        foreach (var cachePath in Directory
            .EnumerateFiles(cacheDirectory, $"{modId}-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var document = JsonSerializer.Deserialize<NexusModFilesCacheDocument>(File.ReadAllText(cachePath), JsonOptions);
                if (document is null
                    || !document.GameDomain.Equals(gameDomain, StringComparison.OrdinalIgnoreCase)
                    || document.ModId != modId)
                {
                    continue;
                }

                var result = new NexusModFilesLoadResult(
                    document.GameDomain,
                    document.ModId,
                    document.CacheFingerprint,
                    NormalizeFiles(document.Files),
                    document.CachedAt,
                    true,
                    null);
                CacheInMemory(BuildMemoryCacheKey(document.GameDomain, document.ModId, document.CacheFingerprint), result);
                return result;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
    }

    public async Task<NexusModFilesLoadResult> LoadOrRefreshAsync(
        ManagerPaths managerPaths,
        string gameDomain,
        int modId,
        string cacheFingerprint,
        string apiKey,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh)
        {
            var cached = TryLoadCached(managerPaths, gameDomain, modId, cacheFingerprint);
            if (cached is not null)
            {
                return cached;
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Nexus API key is required to refresh the file list.");
        }

        var files = await DownloadFilesAsync(gameDomain, modId, apiKey, cancellationToken).ConfigureAwait(false);
        var result = new NexusModFilesLoadResult(
            gameDomain,
            modId,
            cacheFingerprint,
            files,
            DateTimeOffset.UtcNow,
            false,
            null);
        SaveCache(managerPaths, result);
        CacheInMemory(BuildMemoryCacheKey(gameDomain, modId, cacheFingerprint), result);
        return result;
    }

    public void Dispose()
    {
        _memoryCache.Clear();
        _memoryCacheOrder.Clear();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public static string BuildCacheFingerprint(
        string gameDomain,
        int modId,
        int? latestFileId,
        string? latestVersion,
        string? latestFileName)
    {
        var normalizedInput = string.Join(
            '|',
            gameDomain.Trim().ToLowerInvariant(),
            modId.ToString(),
            latestFileId?.ToString() ?? string.Empty,
            latestVersion?.Trim().ToLowerInvariant() ?? string.Empty,
            latestFileName?.Trim().ToLowerInvariant() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedInput)))
            .ToLowerInvariant()[..20];
    }

    private async Task<IReadOnlyList<NexusModFileInfo>> DownloadFilesAsync(
        string gameDomain,
        int modId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"https://api.nexusmods.com/v1/games/{gameDomain}/mods/{modId}/files.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        AddNexusHeaders(request, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus returned {(int)response.StatusCode} {response.ReasonPhrase} while requesting files for {gameDomain}/mods/{modId}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseFiles(document.RootElement);
    }

    private static IReadOnlyList<NexusModFileInfo> ParseFiles(JsonElement root)
    {
        var fileElements = EnumerateFileElements(root).ToArray();
        return fileElements
            .Select(ParseFile)
            .Where(file => file is not null)
            .Select(file => file!)
            .Select(NormalizeFile)
            .OrderBy(file => file.IsOldVersion)
            .ThenByDescending(file => file.IsPrimary)
            .ThenByDescending(file => file.UploadedAt)
            .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<JsonElement> EnumerateFileElements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                yield return element;
            }

            yield break;
        }

        if (root.ValueKind == JsonValueKind.Object
            && TryGetProperty(root, "files", out var files)
            && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in files.EnumerateArray())
            {
                yield return element;
            }
        }
    }

    private static NexusModFileInfo? ParseFile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fileId = ReadInt(element, "file_id")
            ?? ReadInt(element, "fileId")
            ?? ReadInt(element, "id");
        if (fileId is null)
        {
            return null;
        }

        var fileName = FirstNonEmpty(
            ReadString(element, "file_name"),
            ReadString(element, "filename"),
            ReadString(element, "name"))
            ?? $"nexus-file-{fileId.Value}.zip";
        var name = FirstNonEmpty(ReadString(element, "name"), fileName) ?? fileName;
        var version = FirstNonEmpty(
            ReadString(element, "version"),
            ModSourceDetector.DetectVersion(fileName),
            ModSourceDetector.DetectVersion(name))
            ?? "unknown";
        var category = FirstNonEmpty(
            ReadString(element, "category_name"),
            ReadString(element, "category"),
            FormatCategoryId(ReadInt(element, "category_id")))
            ?? "Uncategorized";
        var sizeInBytes = ReadLong(element, "size")
            ?? ReadLong(element, "size_in_bytes")
            ?? ReadKilobytesAsBytes(element, "size_kb");
        var uploadedAt = ReadUnixTimestamp(element, "uploaded_timestamp")
            ?? ReadDateTimeOffset(element, "uploaded_time")
            ?? ReadDateTimeOffset(element, "uploaded_at");
        var isPrimary = ReadBool(element, "is_primary") ?? false;
        var isOldVersion = IsOldOrArchivedCategory(category);

        return new NexusModFileInfo(
            fileId.Value,
            name,
            fileName,
            version,
            category,
            uploadedAt,
            sizeInBytes,
            isPrimary,
            isOldVersion);
    }

    private static IReadOnlyList<NexusModFileInfo> NormalizeFiles(IReadOnlyList<NexusModFileInfo> files)
    {
        return files
            .Select(NormalizeFile)
            .OrderBy(file => file.IsOldVersion)
            .ThenByDescending(file => file.IsPrimary)
            .ThenByDescending(file => file.UploadedAt)
            .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static NexusModFileInfo NormalizeFile(NexusModFileInfo file)
    {
        return file with
        {
            IsOldVersion = file.IsOldVersion || IsOldOrArchivedCategory(file.Category)
        };
    }

    private static bool IsOldOrArchivedCategory(string? category)
    {
        return !string.IsNullOrWhiteSpace(category)
            && (category.Contains("old", StringComparison.OrdinalIgnoreCase)
                || category.Contains("archived", StringComparison.OrdinalIgnoreCase));
    }

    private static void SaveCache(ManagerPaths managerPaths, NexusModFilesLoadResult result)
    {
        var cachePath = GetCachePath(managerPaths, result.GameDomain, result.ModId, result.CacheFingerprint);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var document = new NexusModFilesCacheDocument(
            result.GameDomain,
            result.ModId,
            result.CacheFingerprint,
            result.CachedAt,
            result.Files);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(document, JsonOptions));
    }

    private static string GetCachePath(
        ManagerPaths managerPaths,
        string gameDomain,
        int modId,
        string cacheFingerprint)
    {
        return Path.Combine(
            GetCacheDirectory(managerPaths, gameDomain),
            $"{modId}-{SanitizePathSegment(cacheFingerprint)}.json");
    }

    private static string GetCacheDirectory(ManagerPaths managerPaths, string gameDomain)
    {
        return Path.Combine(
            managerPaths.CachePath,
            "nexus-files",
            SanitizePathSegment(gameDomain));
    }

    private static string BuildMemoryCacheKey(string gameDomain, int modId, string cacheFingerprint)
    {
        return $"{gameDomain.Trim().ToLowerInvariant()}:{modId}:{cacheFingerprint.Trim().ToLowerInvariant()}";
    }

    private void CacheInMemory(string key, NexusModFilesLoadResult result)
    {
        var isNewEntry = !_memoryCache.ContainsKey(key);
        _memoryCache[key] = result;
        if (isNewEntry)
        {
            _memoryCacheOrder.Enqueue(key);
        }

        while (_memoryCache.Count > MaxMemoryCacheEntries && _memoryCacheOrder.Count > 0)
        {
            var oldestKey = _memoryCacheOrder.Dequeue();
            _memoryCache.Remove(oldestKey);
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static long? ReadKilobytesAsBytes(JsonElement element, string propertyName)
    {
        var kilobytes = ReadLong(element, propertyName);
        return kilobytes is null ? null : kilobytes.Value * 1024;
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsedBool) => parsedBool,
            JsonValueKind.Number when property.TryGetInt32(out var parsedNumber) => parsedNumber != 0,
            _ => null
        };
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement element, string propertyName)
    {
        var timestamp = ReadLong(element, propertyName);
        if (timestamp is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static string? FormatCategoryId(int? categoryId)
    {
        return categoryId is null ? null : $"Category {categoryId.Value}";
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }

    private static void AddNexusHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("apikey", apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", "0.1"));
    }

    private sealed record NexusModFilesCacheDocument(
        string GameDomain,
        int ModId,
        string CacheFingerprint,
        DateTimeOffset CachedAt,
        IReadOnlyList<NexusModFileInfo> Files);
}

public sealed record NexusModFilesLoadResult(
    string GameDomain,
    int ModId,
    string CacheFingerprint,
    IReadOnlyList<NexusModFileInfo> Files,
    DateTimeOffset CachedAt,
    bool IsFromCache,
    string? Warning);
