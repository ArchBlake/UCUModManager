using System.Net.Http.Headers;
using System.Text.Json;

namespace UcuModManager.Core.Mods;

public sealed class NexusModDownloadService
{
    private readonly HttpClient _httpClient;

    public NexusModDownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<NexusModDownloadResult> DownloadUpdateArchiveAsync(
        ModSourceInfo source,
        int fileId,
        string apiKey,
        string destinationDirectoryPath,
        string? preferredFileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.GameDomain) || source.ModId is null)
        {
            throw new InvalidOperationException("Nexus source is missing game domain or mod id.");
        }

        Directory.CreateDirectory(destinationDirectoryPath);
        var downloadUri = await GetDownloadUriAsync(source.GameDomain, source.ModId.Value, fileId, apiKey, cancellationToken)
            .ConfigureAwait(false);
        var destinationPath = await DownloadFileAsync(
                downloadUri,
                destinationDirectoryPath,
                preferredFileName,
                BuildFallbackFileName(source.GameDomain, source.ModId.Value, fileId),
                cancellationToken)
            .ConfigureAwait(false);

        return new NexusModDownloadResult(destinationPath, downloadUri, Path.GetFileName(destinationPath));
    }

    private async Task<Uri> GetDownloadUriAsync(
        string gameDomain,
        int modId,
        int fileId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"https://api.nexusmods.com/v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        AddNexusHeaders(request, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus returned {(int)response.StatusCode} {response.ReasonPhrase} while requesting the download link for {gameDomain}/mods/{modId}/files/{fileId}. Automatic downloads may require Nexus Premium for this account.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseDownloadUri(document.RootElement)
            ?? throw new InvalidOperationException("Nexus did not return a usable download link for this file. Automatic downloads may require Nexus Premium for this account.");
    }

    private async Task<string> DownloadFileAsync(
        Uri downloadUri,
        string destinationDirectoryPath,
        string? preferredFileName,
        string fallbackFileName,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus download returned {(int)response.StatusCode} {response.ReasonPhrase}. Automatic downloads may require Nexus Premium for this account.");
        }

        var fileName = GetFileName(response.Content.Headers.ContentDisposition, downloadUri, preferredFileName, fallbackFileName);
        var destinationPath = CreateUniqueDestinationPath(destinationDirectoryPath, fileName);
        var fullDestinationDirectoryPath = EnsureTrailingSeparator(Path.GetFullPath(destinationDirectoryPath));
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (!fullDestinationPath.StartsWith(fullDestinationDirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Nexus download target resolved outside the manager downloads folder.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return destinationPath;
    }

    private static Uri? ParseDownloadUri(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                var uri = TryGetUri(element);
                if (uri is not null)
                {
                    return uri;
                }
            }
        }

        return TryGetUri(root);
    }

    private static Uri? TryGetUri(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String
            && Uri.TryCreate(element.GetString(), UriKind.Absolute, out var stringUri))
        {
            return stringUri;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if ((property.Name.Equals("URI", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("uri", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("url", StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String
                && Uri.TryCreate(property.Value.GetString(), UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    private static string GetFileName(
        ContentDispositionHeaderValue? contentDisposition,
        Uri downloadUri,
        string? preferredFileName,
        string fallbackFileName)
    {
        var candidates = new[]
        {
            preferredFileName,
            contentDisposition?.FileNameStar,
            contentDisposition?.FileName,
            Path.GetFileName(downloadUri.LocalPath),
            fallbackFileName
        };

        foreach (var candidate in candidates)
        {
            var fileName = SanitizeFileName(candidate?.Trim('"'));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return fallbackFileName;
    }

    private static string CreateUniqueDestinationPath(string destinationDirectoryPath, string fileName)
    {
        var safeFileName = SanitizeFileName(fileName) ?? "nexus-update.zip";
        var extension = Path.GetExtension(safeFileName);
        var stem = string.IsNullOrWhiteSpace(extension)
            ? safeFileName
            : safeFileName[..^extension.Length];
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".zip";
        }

        var candidate = Path.Combine(destinationDirectoryPath, stem + extension);
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(destinationDirectoryPath, $"{stem}-{suffix}{extension}");
            suffix++;
        }

        return candidate;
    }

    private static string? SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string BuildFallbackFileName(string gameDomain, int modId, int fileId)
    {
        return $"{gameDomain}-{modId}-{fileId}.zip";
    }

    private static void AddNexusHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("apikey", apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", "0.1"));
    }
}
