using System.Net;
using System.Text;
using UcuModManager.Core.Updates;

namespace UcuModManager.Core.Tests;

public sealed class GitHubManagerUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_SelectsNewestEligibleReleaseWithPortableAsset()
    {
        const string json = """
            [
              {
                "tag_name": "v0.3.0-alpha.1",
                "name": "Future Alpha",
                "body": "User supplied notes",
                "html_url": "https://github.com/ArchBlake/UCUModManager/releases/tag/v0.3.0-alpha.1",
                "draft": false,
                "prerelease": true,
                "published_at": "2026-07-18T12:00:00Z",
                "assets": [
                  {
                    "name": "UCU-ModManager-0.3.0-alpha.1-win-x64-portable.zip",
                    "browser_download_url": "https://example.invalid/future.zip",
                    "size": 1200,
                    "digest": "sha256:future"
                  }
                ]
              },
              {
                "tag_name": "v0.2.0-beta.1",
                "name": "Beta 1",
                "body": "Release notes",
                "html_url": "https://github.com/ArchBlake/UCUModManager/releases/tag/v0.2.0-beta.1",
                "draft": false,
                "prerelease": true,
                "published_at": "2026-07-17T12:00:00Z",
                "assets": [
                  {
                    "name": "UCU-ModManager-0.2.0-beta.1-win-x64-portable.zip",
                    "browser_download_url": "https://example.invalid/beta.zip",
                    "size": 1000,
                    "digest": "sha256:beta"
                  },
                  {
                    "name": "release-manifest.json",
                    "browser_download_url": "https://example.invalid/release-manifest.json",
                    "size": 400,
                    "digest": "sha256:manifest"
                  }
                ]
              }
            ]
            """;
        using var service = new GitHubManagerUpdateService(new HttpClient(new JsonMessageHandler(json)));

        var result = await service.CheckAsync("0.2.0-alpha.1");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.3.0-alpha.1", result.LatestRelease!.Version.ToString());
        Assert.Equal("User supplied notes", result.LatestRelease.ReleaseNotes);
        Assert.Equal("UCU-ModManager-0.3.0-alpha.1-win-x64-portable.zip", result.LatestRelease.PackageAsset.Name);
    }

    [Fact]
    public async Task CheckAsync_IgnoresDraftsInvalidTagsAndReleasesWithoutPortableAsset()
    {
        const string json = """
            [
              {
                "tag_name": "v9.0.0",
                "name": "Draft",
                "html_url": "https://example.invalid/draft",
                "draft": true,
                "prerelease": false,
                "assets": []
              },
              {
                "tag_name": "not-a-version",
                "name": "Invalid",
                "html_url": "https://example.invalid/invalid",
                "draft": false,
                "prerelease": false,
                "assets": []
              },
              {
                "tag_name": "v0.2.1",
                "name": "Source only",
                "html_url": "https://example.invalid/source-only",
                "draft": false,
                "prerelease": false,
                "assets": []
              }
            ]
            """;
        using var service = new GitHubManagerUpdateService(new HttpClient(new JsonMessageHandler(json)));

        var result = await service.CheckAsync("0.2.0-alpha.1");

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestRelease);
    }

    [Fact]
    public async Task CheckAsync_IgnoresReleaseWithPackageForDifferentVersion()
    {
        const string json = """
            [
              {
                "tag_name": "v0.2.0-alpha.2",
                "name": "Alpha 2",
                "html_url": "https://example.invalid/alpha-2",
                "draft": false,
                "prerelease": true,
                "assets": [
                  {
                    "name": "UCU-ModManager-0.2.0-alpha.1-win-x64-portable.zip",
                    "browser_download_url": "https://example.invalid/wrong-package.zip",
                    "size": 1000,
                    "digest": "sha256:wrong"
                  }
                ]
              }
            ]
            """;
        using var service = new GitHubManagerUpdateService(new HttpClient(new JsonMessageHandler(json)));

        var result = await service.CheckAsync("0.2.0-alpha.1");

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestRelease);
    }

    private sealed class JsonMessageHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(GitHubManagerUpdateService.DefaultReleasesUri, request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
