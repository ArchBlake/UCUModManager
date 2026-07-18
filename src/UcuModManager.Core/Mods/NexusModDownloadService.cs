using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Mods;

public sealed class NexusModDownloadService : IDisposable
{
    private const string ApplicationName = "UCU Mod Manager";
    private const string UserAgentProductName = "UCU-ModManager";
    private const int CopyBufferSize = 81920;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _applicationVersion;

    public NexusModDownloadService(HttpClient? httpClient = null, string? applicationVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _applicationVersion = ResolveApplicationVersion(applicationVersion);
    }

    public async Task<NexusModDownloadResult> DownloadAsync(
        string gameDomain,
        int modId,
        int fileId,
        string accessToken,
        string destinationDirectoryPath,
        string? preferredFileName = null,
        IProgress<NexusModDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDomain))
        {
            throw new ArgumentException("Game domain is required.", nameof(gameDomain));
        }

        if (modId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modId), "Nexus mod id must be positive.");
        }

        if (fileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileId), "Nexus file id must be positive.");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("OAuth access token is required.", nameof(accessToken));
        }

        var destinationRoot = Path.GetFullPath(destinationDirectoryPath);
        Directory.CreateDirectory(destinationRoot);
        progress?.Report(new NexusModDownloadProgress(0, null, "Requesting a secure Nexus download link..."));
        var downloadUri = await GetDownloadUriAsync(
                gameDomain,
                modId,
                fileId,
                accessToken,
                cancellationToken)
            .ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProductName, _applicationVersion));
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new NexusModsApiException(
                $"Nexus download failed: {(int)response.StatusCode} {response.ReasonPhrase}.",
                (int)response.StatusCode);
        }

        var fileName = ResolveFileName(
            response.Content.Headers.ContentDisposition,
            downloadUri,
            preferredFileName,
            $"{SanitizeFileName(gameDomain)}-{modId}-{fileId}.zip");
        var destinationPath = CreateUniqueDestinationPath(destinationRoot, fileName);
        EnsurePathIsInsideRoot(destinationRoot, destinationPath);
        var temporaryPath = destinationPath + ".partial-" + Guid.NewGuid().ToString("N");
        var totalBytes = response.Content.Headers.ContentLength;
        long bytesDownloaded = 0;

        try
        {
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[CopyBufferSize];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesDownloaded += read;
                    progress?.Report(new NexusModDownloadProgress(
                        bytesDownloaded,
                        totalBytes,
                        BuildDownloadStatus(fileName, bytesDownloaded, totalBytes)));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (totalBytes is > 0 && bytesDownloaded != totalBytes.Value)
                {
                    throw new IOException(
                        $"Nexus download ended early. Expected {totalBytes.Value:N0} bytes, received {bytesDownloaded:N0} bytes.");
                }

            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new NexusModDownloadResult(destinationPath, downloadUri, fileName, bytesDownloaded);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<Uri> GetDownloadUriAsync(
        string gameDomain,
        int modId,
        int fileId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(gameDomain.Trim())}/mods/{modId}/files/{fileId}/download_link.json");
        AddApiHeaders(request, accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Reconnect the Nexus account and try again.",
                System.Net.HttpStatusCode.Forbidden => "Automatic downloads require an active Nexus Premium membership.",
                System.Net.HttpStatusCode.TooManyRequests => "The Nexus request limit was reached. Wait a moment and try again.",
                _ => "Nexus did not provide a download link."
            };
            throw new NexusModsApiException(
                $"Nexus download link request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}",
                (int)response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var downloadUri = ParseDownloadUri(document.RootElement);
        if (downloadUri is null || !downloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Nexus returned an invalid or insecure download link.");
        }

        return downloadUri;
    }

    private void AddApiHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Application-Name", ApplicationName);
        request.Headers.Add("Application-Version", _applicationVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProductName, _applicationVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static Uri? ParseDownloadUri(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var uri = ParseDownloadUri(item);
                if (uri is not null)
                {
                    return uri;
                }
            }

            return null;
        }

        if (root.ValueKind == JsonValueKind.String)
        {
            return Uri.TryCreate(root.GetString(), UriKind.Absolute, out var value) ? value : null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("URI", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(property.Value.GetString(), UriKind.Absolute, out var value)
                        ? value
                        : null;
            }
        }

        return null;
    }

    private static string ResolveFileName(
        ContentDispositionHeaderValue? contentDisposition,
        Uri downloadUri,
        string? preferredFileName,
        string fallbackFileName)
    {
        var candidates = new[]
        {
            contentDisposition?.FileNameStar,
            contentDisposition?.FileName,
            Path.GetFileName(downloadUri.LocalPath),
            preferredFileName,
            fallbackFileName
        };
        return candidates
            .Select(candidate => SanitizeFileName(candidate?.Trim('"')))
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))
            ?? fallbackFileName;
    }

    private static string CreateUniqueDestinationPath(string destinationRoot, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var stem = string.IsNullOrWhiteSpace(extension) ? fileName : fileName[..^extension.Length];
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".zip";
        }

        var candidate = Path.Combine(destinationRoot, stem + extension);
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(destinationRoot, $"{stem}-{suffix}{extension}");
        }

        return candidate;
    }

    private static void EnsurePathIsInsideRoot(string rootPath, string filePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(filePath);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Nexus download target resolved outside the manager downloads folder.");
        }
    }

    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        const int maxFileNameLength = 160;
        if (cleaned.Length > maxFileNameLength)
        {
            var extension = Path.GetExtension(cleaned);
            var extensionLength = Math.Min(extension.Length, 16);
            var stemLength = maxFileNameLength - extensionLength;
            cleaned = cleaned[..stemLength] + extension[..extensionLength];
        }

        return string.IsNullOrWhiteSpace(cleaned) ? string.Empty : cleaned;
    }

    private static string BuildDownloadStatus(string fileName, long bytesDownloaded, long? totalBytes)
    {
        return totalBytes is > 0
            ? $"Downloading {fileName}: {FormatBytes(bytesDownloaded)} of {FormatBytes(totalBytes.Value)}"
            : $"Downloading {fileName}: {FormatBytes(bytesDownloaded)}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static string ResolveApplicationVersion(string? configuredVersion)
    {
        var version = configuredVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            version = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? entryAssembly?.GetName().Version?.ToString(3)
                ?? typeof(NexusModDownloadService).Assembly.GetName().Version?.ToString(3);
        }

        version = version?.Split('+', 2)[0].Trim();
        var safeVersion = new string((version ?? string.Empty)
            .Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(safeVersion) ? "development" : safeVersion;
    }
}
