using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace UcuModManager.Core.Updates;

public sealed class ManagerUpdateDownloadService : IDisposable
{
    private const long MaxPackageSize = 256L * 1024 * 1024;
    private const int BufferSize = 81920;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ManagerUpdateDownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public async Task<ManagerUpdateDownloadResult> DownloadAsync(
        ManagerReleaseInfo release,
        string destinationDirectory,
        IProgress<ManagerUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var asset = release.PackageAsset;
        if (asset.Size <= 0 || asset.Size > MaxPackageSize)
        {
            throw new InvalidDataException("The manager update package has an invalid size.");
        }

        var expectedHash = ParseSha256Digest(asset.Digest)
            ?? throw new InvalidDataException("The GitHub release asset does not provide a SHA-256 digest.");
        var safeFileName = Path.GetFileName(asset.Name);
        if (!safeFileName.Equals(asset.Name, StringComparison.Ordinal)
            || !safeFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The manager update package name is not safe.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, safeFileName);
        var temporaryPath = destinationPath + ".partial";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", release.Version.ToString()));
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaxPackageSize || contentLength is > 0 && contentLength != asset.Size)
            {
                throw new InvalidDataException("The downloaded manager update size does not match the GitHub release asset.");
            }

            byte[] actualHash;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                long downloadedBytes = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    downloadedBytes += read;
                    if (downloadedBytes > asset.Size || downloadedBytes > MaxPackageSize)
                    {
                        throw new InvalidDataException("The manager update download exceeded its declared size.");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new ManagerUpdateDownloadProgress(downloadedBytes, asset.Size));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (downloadedBytes != asset.Size)
                {
                    throw new InvalidDataException("The downloaded manager update is incomplete.");
                }

                actualHash = hasher.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    throw new InvalidDataException("The downloaded manager update failed SHA-256 verification.");
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new ManagerUpdateDownloadResult(destinationPath, Convert.ToHexString(actualHash).ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static byte[]? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest)
            || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = digest[prefix.Length..];
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }

        return Convert.FromHexString(value);
    }
}

public sealed record ManagerUpdateDownloadProgress(long DownloadedBytes, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : DownloadedBytes * 100d / TotalBytes;
}

public sealed record ManagerUpdateDownloadResult(string FilePath, string Sha256);
