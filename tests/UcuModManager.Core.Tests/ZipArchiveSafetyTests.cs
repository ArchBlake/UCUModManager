using System.IO.Compression;
using UcuModManager.Core.Archives;

namespace UcuModManager.Core.Tests;

public sealed class ZipArchiveSafetyTests
{
    [Fact]
    public void Validate_AcceptsArchiveWithinLimits()
    {
        using var archive = OpenArchive(("plugin.dll", new byte[64]));

        var summary = ZipArchiveSafety.Validate(archive, CreateLimits());

        Assert.Equal(1, summary.EntryCount);
        Assert.Equal(64, summary.TotalUncompressedBytes);
    }

    [Fact]
    public void Validate_RejectsTooManyEntries()
    {
        using var archive = OpenArchive(
            ("first.dll", new byte[8]),
            ("second.dll", new byte[8]));

        var exception = Assert.Throws<InvalidDataException>(() =>
            ZipArchiveSafety.Validate(archive, CreateLimits(maxEntryCount: 1)));

        Assert.Contains("too many files", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsExcessiveUncompressedSize()
    {
        using var archive = OpenArchive(("large.bin", new byte[128]));

        var exception = Assert.Throws<InvalidDataException>(() =>
            ZipArchiveSafety.Validate(archive, CreateLimits(maxTotalBytes: 64)));

        Assert.Contains("too large when unpacked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsUnsafeCompressionRatio()
    {
        using var archive = OpenArchive(("zeros.bin", new byte[4096]));

        var exception = Assert.Throws<InvalidDataException>(() =>
            ZipArchiveSafety.Validate(
                archive,
                CreateLimits(
                    maxEntryBytes: 8192,
                    maxTotalBytes: 8192,
                    maxCompressionRatio: 2,
                    compressionRatioThreshold: 1)));

        Assert.Contains("compression ratio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyWithLimit_RejectsEntryLargerThanDeclaredSize()
    {
        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        using var destination = new MemoryStream();

        Assert.Throws<InvalidDataException>(() =>
            ZipArchiveSafety.CopyWithLimit(source, destination, 3, "payload.bin"));
    }

    private static ArchiveExtractionLimits CreateLimits(
        int maxEntryCount = 10,
        long maxEntryBytes = 1024,
        long maxTotalBytes = 4096,
        double maxCompressionRatio = 1000,
        long compressionRatioThreshold = 1024)
    {
        return new ArchiveExtractionLimits(
            maxEntryCount,
            maxEntryBytes,
            maxTotalBytes,
            maxCompressionRatio,
            compressionRatioThreshold,
            RequiredFreeSpaceReserveBytes: 0);
    }

    private static ZipArchive OpenArchive(params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
                using var destination = entry.Open();
                destination.Write(item.Content);
            }
        }

        return new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
    }
}
