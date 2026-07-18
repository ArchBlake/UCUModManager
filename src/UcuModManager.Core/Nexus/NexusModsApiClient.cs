using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using UcuModManager.Core.Mods;

namespace UcuModManager.Core.Nexus;

public sealed class NexusModsApiClient : IDisposable
{
    private const string ApplicationName = "UCU Mod Manager";
    private const string UserAgentProductName = "UCU-ModManager";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _applicationVersion;

    public NexusModsApiClient(HttpClient? httpClient = null, string? applicationVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _applicationVersion = ResolveApplicationVersion(applicationVersion);
    }

    public async Task<IReadOnlyList<NexusModFileInfo>> GetModFilesAsync(
        string gameDomain,
        int modId,
        NexusOAuthTokenProvider tokenProvider,
        NexusOAuthOptions oauthOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(oauthOptions);
        var access = await tokenProvider.GetAccessContextAsync(oauthOptions, cancellationToken).ConfigureAwait(false);
        return await GetModFilesAsync(gameDomain, modId, access.Tokens.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NexusModFileInfo>> GetModFilesAsync(
        string gameDomain,
        int modId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDomain))
        {
            throw new ArgumentException("Game domain is required.", nameof(gameDomain));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("OAuth access token is required.", nameof(accessToken));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.nexusmods.com/v1/games/{Uri.EscapeDataString(gameDomain.Trim())}/mods/{modId}/files.json");
        AddHeaders(request, accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new NexusModsApiException(
                $"Nexus API request failed: {(int)response.StatusCode} {response.ReasonPhrase}.",
                (int)response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return ParseFiles(document.RootElement);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static IReadOnlyList<NexusModFileInfo> ParseFiles(JsonElement root)
    {
        return EnumerateFileElements(root)
            .Select(ParseFile)
            .Where(file => file is not null)
            .Select(file => file!)
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

    private static bool IsOldOrArchivedCategory(string? category)
    {
        return !string.IsNullOrWhiteSpace(category)
            && (category.Contains("old", StringComparison.OrdinalIgnoreCase)
                || category.Contains("archived", StringComparison.OrdinalIgnoreCase));
    }

    private void AddHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Application-Name", ApplicationName);
        request.Headers.Add("Application-Version", _applicationVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProductName, _applicationVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string ResolveApplicationVersion(string? configuredVersion)
    {
        var version = configuredVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            version = entryAssembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            version ??= entryAssembly?.GetName().Version?.ToString(3);
            version ??= typeof(NexusModsApiClient).Assembly.GetName().Version?.ToString(3);
        }

        version = version?.Split('+', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            return "development";
        }

        var safeVersion = new string(version
            .Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(safeVersion) ? "development" : safeVersion;
    }
}
