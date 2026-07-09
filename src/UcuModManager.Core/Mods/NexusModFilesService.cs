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

    private readonly Dictionary<string, NexusModFilesLoadResult> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _memoryCacheOrder = new();

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

    public void Dispose()
    {
        _memoryCache.Clear();
        _memoryCacheOrder.Clear();
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

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
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
