using System.Net.Http.Headers;
using System.Text.Json;

namespace UcuModManager.Core.Mods;

public sealed class NexusUpdateCheckService
{
    private readonly HttpClient _httpClient;

    public NexusUpdateCheckService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<NexusUpdateCheckResult> CheckAsync(
        ModManifest manifest,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var source = manifest.Source;
        if (source is null || !source.CanCheckUpdates || source.ModId is null)
        {
            return new NexusUpdateCheckResult(manifest.Mod.Id, "Not linked", false, null, null, null);
        }

        try
        {
            var requestUri = new Uri($"https://api.nexusmods.com/v1/games/{source.GameDomain}/mods/{source.ModId}/files.json");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("apikey", apiKey);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", "0.1"));
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new NexusUpdateCheckResult(
                    manifest.Mod.Id,
                    "API error",
                    false,
                    null,
                    null,
                    $"Nexus returned {(int)response.StatusCode} {response.ReasonPhrase} for {source.GameDomain}/mods/{source.ModId}",
                    source.GameDomain,
                    source.ModId);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var latestFile = GetLatestFile(document.RootElement);
            if (latestFile is null)
            {
                return new NexusUpdateCheckResult(
                    manifest.Mod.Id,
                    "No files",
                    false,
                    null,
                    null,
                    null,
                    source.GameDomain,
                    source.ModId);
            }

            var isUpdateAvailable = IsUpdateAvailable(source, latestFile.Value);
            return new NexusUpdateCheckResult(
                manifest.Mod.Id,
                isUpdateAvailable ? "Update available" : "Latest version",
                isUpdateAvailable,
                latestFile.Value.Version,
                latestFile.Value.FileId,
                null,
                source.GameDomain,
                source.ModId,
                latestFile.Value.Name);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or IOException)
        {
            return new NexusUpdateCheckResult(
                manifest.Mod.Id,
                "API error",
                false,
                null,
                null,
                exception.Message,
                source.GameDomain,
                source.ModId);
        }
    }

    private static bool IsUpdateAvailable(ModSourceInfo source, NexusFileInfo latestFile)
    {
        if (source.FileId is not null && latestFile.FileId is not null)
        {
            if (latestFile.FileId.Value == source.FileId.Value)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(source.FileVersion) || string.IsNullOrWhiteSpace(latestFile.Version))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(source.FileVersion) && !string.IsNullOrWhiteSpace(latestFile.Version))
        {
            return !NormalizeComparableVersion(source.FileVersion)
                .Equals(NormalizeComparableVersion(latestFile.Version), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string NormalizeComparableVersion(string version)
    {
        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var forIndex = value.IndexOf("-for-", StringComparison.OrdinalIgnoreCase);
        if (forIndex >= 0)
        {
            value = value[..forIndex];
        }

        return value.Replace('-', '.');
    }

    private static NexusFileInfo? GetLatestFile(JsonElement root)
    {
        var filesElement = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("files", out var files)
            ? files
            : root;
        if (filesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsedFiles = new List<NexusFileInfo>();
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            var fileName = TryGetString(fileElement, "file_name")
                ?? TryGetString(fileElement, "filename")
                ?? TryGetString(fileElement, "name");
            var file = new NexusFileInfo(
                TryGetInt(fileElement, "file_id"),
                fileName,
                ResolveFileVersion(TryGetString(fileElement, "version"), fileName),
                TryGetInt(fileElement, "category_id"),
                TryGetString(fileElement, "category_name"),
                TryGetLong(fileElement, "uploaded_timestamp") ?? TryGetLong(fileElement, "uploaded_time") ?? 0);
            parsedFiles.Add(file);
        }
        if (parsedFiles.Count == 0)
        {
            return null;
        }

        var activeFiles = parsedFiles
            .Where(file => !IsOldFile(file))
            .ToArray();
        if (activeFiles.Length == 0)
        {
            activeFiles = parsedFiles.ToArray();
        }

        var mainFiles = activeFiles
            .Where(IsMainFile)
            .ToArray();
        var candidates = mainFiles.Length > 0 ? mainFiles : activeFiles;

        return candidates
            .OrderBy(file => file.UploadedTimestamp)
            .ThenBy(file => file.FileId ?? 0)
            .LastOrDefault();
    }

    private static string? DetectFileNameVersion(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            ? null
            : ModSourceDetector.DetectVersion(fileName);
    }

    private static string? ResolveFileVersion(string? apiVersion, string? fileName)
    {
        var fileNameVersion = DetectFileNameVersion(fileName);
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

    private static bool IsMainFile(NexusFileInfo file)
    {
        return file.CategoryId == 1
            || file.CategoryName?.Contains("main", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsOldFile(NexusFileInfo file)
    {
        return file.CategoryId == 3
            || file.CategoryName?.Contains("old", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
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

    private readonly record struct NexusFileInfo(
        int? FileId,
        string? Name,
        string? Version,
        int? CategoryId,
        string? CategoryName,
        long UploadedTimestamp);
}
