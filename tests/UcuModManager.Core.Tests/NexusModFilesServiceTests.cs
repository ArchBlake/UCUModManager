using UcuModManager.Core.Mods;
using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Tests;

public sealed class NexusModFilesServiceTests
{
    [Fact]
    public async Task LoadOrRefresh_UsesFingerprintCacheAndPersistsItToDisk()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "ucu-nexus-files-test-" + Guid.NewGuid().ToString("N"));
        var managerPaths = new ManagerPaths(rootPath);
        var fingerprint = NexusModFilesService.BuildCacheFingerprint(
            "scavprototype",
            42,
            1001,
            "1.2.3",
            "mod-1.2.3.zip");
        var fetchCount = 0;
        try
        {
            using (var service = new NexusModFilesService())
            {
                var refreshed = await service.LoadOrRefreshAsync(
                    managerPaths,
                    "scavprototype",
                    42,
                    fingerprint,
                    _ =>
                    {
                        fetchCount++;
                        return Task.FromResult<IReadOnlyList<NexusModFileInfo>>(
                        [
                            new NexusModFileInfo(
                                1001,
                                "Current release",
                                "mod-1.2.3.zip",
                                "1.2.3",
                                "Main files",
                                DateTimeOffset.UtcNow,
                                1024,
                                true,
                                false)
                        ]);
                    });
                var cached = await service.LoadOrRefreshAsync(
                    managerPaths,
                    "scavprototype",
                    42,
                    fingerprint,
                    _ => throw new InvalidOperationException("Cache should avoid a second fetch."));

                Assert.False(refreshed.IsFromCache);
                Assert.True(cached.IsFromCache);
                Assert.Equal(1, fetchCount);
            }

            using var reloadedService = new NexusModFilesService();
            var diskCached = reloadedService.TryLoadCached(
                managerPaths,
                "scavprototype",
                42,
                fingerprint);

            Assert.NotNull(diskCached);
            Assert.True(diskCached!.IsFromCache);
            Assert.Single(diskCached.Files);
            Assert.Equal(1001, diskCached.Files[0].FileId);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadOrRefresh_ForceRefreshReplacesExistingFingerprintCache()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "ucu-nexus-files-test-" + Guid.NewGuid().ToString("N"));
        var managerPaths = new ManagerPaths(rootPath);
        const string fingerprint = "same-fingerprint";
        var nextFileId = 10;
        try
        {
            using var service = new NexusModFilesService();
            Task<IReadOnlyList<NexusModFileInfo>> Fetch(CancellationToken _)
            {
                var fileId = nextFileId++;
                return Task.FromResult<IReadOnlyList<NexusModFileInfo>>(
                [
                    new NexusModFileInfo(fileId, "Release", $"release-{fileId}.zip", "1", "Main files", null, null, true, false)
                ]);
            }

            var initial = await service.LoadOrRefreshAsync(
                managerPaths,
                "scavprototype",
                42,
                fingerprint,
                Fetch);
            var refreshed = await service.LoadOrRefreshAsync(
                managerPaths,
                "scavprototype",
                42,
                fingerprint,
                Fetch,
                forceRefresh: true);

            Assert.Equal(10, initial.Files[0].FileId);
            Assert.Equal(11, refreshed.Files[0].FileId);
            Assert.False(refreshed.IsFromCache);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
