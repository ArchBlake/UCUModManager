using System.Net;
using System.Net.Http.Headers;
using UcuModManager.Core.Mods;
using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Tests;

public sealed class NexusModDownloadServiceTests
{
    [Fact]
    public async Task Download_UsesBearerOnlyForApiAndPublishesCompletedArchive()
    {
        var rootPath = CreateTemporaryDirectory();
        var apiWasAuthorized = false;
        var cdnReceivedAuthorization = false;
        string? requestedApiPath = null;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase))
            {
                requestedApiPath = request.RequestUri.AbsolutePath;
                apiWasAuthorized = request.Headers.Authorization?.Scheme == "Bearer"
                    && request.Headers.Authorization.Parameter == "access-token"
                    && request.Headers.GetValues("Application-Name").Single() == "UCU Mod Manager";
                return Task.FromResult(NexusOAuthClientTests.JsonResponse(
                    HttpStatusCode.OK,
                    """[{"URI":"https://cdn.nexusmods.com/files/mod-53.zip"}]"""));
            }

            cdnReceivedAuthorization = request.Headers.Authorization is not null
                || request.Headers.Contains("Application-Name");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x50, 0x4b, 0x03, 0x04])
            };
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = "mod-53.zip"
            };
            return Task.FromResult(response);
        });

        try
        {
            using var httpClient = new HttpClient(handler);
            using var service = new NexusModDownloadService(httpClient, "0.1.4-alpha-public");

            var result = await service.DownloadAsync(
                "scavprototype",
                53,
                101,
                "access-token",
                rootPath);

            Assert.True(apiWasAuthorized);
            Assert.Equal("/v1/games/scavprototype/mods/53/files/101/download_link.json", requestedApiPath);
            Assert.False(cdnReceivedAuthorization);
            Assert.Equal("mod-53.zip", result.FileName);
            Assert.Equal(4, result.BytesDownloaded);
            Assert.Equal(new byte[] { 0x50, 0x4b, 0x03, 0x04 }, await File.ReadAllBytesAsync(result.ArchivePath));
            Assert.Empty(Directory.EnumerateFiles(rootPath, "*.partial-*"));
        }
        finally
        {
            DeleteTemporaryDirectory(rootPath);
        }
    }

    [Fact]
    public async Task Download_RejectsInsecureCdnUri()
    {
        var rootPath = CreateTemporaryDirectory();
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(
            NexusOAuthClientTests.JsonResponse(
                HttpStatusCode.OK,
                """[{"URI":"http://cdn.nexusmods.com/files/mod.zip"}]""")));

        try
        {
            using var httpClient = new HttpClient(handler);
            using var service = new NexusModDownloadService(httpClient, "test");

            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(
                "scavprototype",
                53,
                101,
                "access-token",
                rootPath));

            Assert.Equal(1, handler.CallCount);
            Assert.Empty(Directory.EnumerateFiles(rootPath));
        }
        finally
        {
            DeleteTemporaryDirectory(rootPath);
        }
    }

    [Fact]
    public async Task Download_FailedCdnRequestLeavesNoPartialFile()
    {
        var rootPath = CreateTemporaryDirectory();
        var handler = new TestHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase)
                ? NexusOAuthClientTests.JsonResponse(
                    HttpStatusCode.OK,
                    """[{"URI":"https://cdn.nexusmods.com/files/mod.zip"}]""")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        try
        {
            using var httpClient = new HttpClient(handler);
            using var service = new NexusModDownloadService(httpClient, "test");

            var exception = await Assert.ThrowsAsync<NexusModsApiException>(() => service.DownloadAsync(
                "scavprototype",
                53,
                101,
                "access-token",
                rootPath));

            Assert.Equal(503, exception.StatusCode);
            Assert.Empty(Directory.EnumerateFiles(rootPath));
        }
        finally
        {
            DeleteTemporaryDirectory(rootPath);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ucu-nexus-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
