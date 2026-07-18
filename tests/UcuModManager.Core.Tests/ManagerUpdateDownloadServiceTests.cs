using System.Net;
using System.Security.Cryptography;
using UcuModManager.Core.Updates;

namespace UcuModManager.Core.Tests;

public sealed class ManagerUpdateDownloadServiceTests
{
    [Fact]
    public async Task DownloadAsync_VerifiesAndSavesPackage()
    {
        var content = "verified update"u8.ToArray();
        var digest = $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";
        var release = CreateRelease(content.Length, digest);
        var destination = CreateTemporaryDirectory();
        try
        {
            using var service = new ManagerUpdateDownloadService(
                new HttpClient(new ByteArrayMessageHandler(content)));

            var result = await service.DownloadAsync(release, destination);

            Assert.Equal(content, File.ReadAllBytes(result.FilePath));
            Assert.False(File.Exists(result.FilePath + ".partial"));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsHashMismatchAndDeletesPartialFile()
    {
        var content = "tampered update"u8.ToArray();
        var release = CreateRelease(content.Length, $"sha256:{new string('0', 64)}");
        var destination = CreateTemporaryDirectory();
        try
        {
            using var service = new ManagerUpdateDownloadService(
                new HttpClient(new ByteArrayMessageHandler(content)));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadAsync(release, destination));

            Assert.Empty(Directory.EnumerateFiles(destination));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    private static ManagerReleaseInfo CreateRelease(long size, string digest)
    {
        var version = SemanticVersion.Parse("0.2.0-alpha.2");
        return new ManagerReleaseInfo(
            version,
            "v0.2.0-alpha.2",
            "Alpha 2",
            null,
            new Uri("https://example.invalid/release"),
            true,
            DateTimeOffset.UtcNow,
            new ManagerReleaseAsset(
                "UCU-ModManager-0.2.0-alpha.2-win-x64-portable.zip",
                new Uri("https://example.invalid/update.zip"),
                size,
                digest),
            null);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ucu-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ByteArrayMessageHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }
}
