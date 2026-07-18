namespace UcuModManager.Core.Archives;

public sealed record ArchiveExtractionLimits(
    int MaxEntryCount,
    long MaxEntryUncompressedBytes,
    long MaxTotalUncompressedBytes,
    double MaxCompressionRatio,
    long CompressionRatioCheckThresholdBytes,
    long RequiredFreeSpaceReserveBytes)
{
    public static ArchiveExtractionLimits Default { get; } = new(
        MaxEntryCount: 20_000,
        MaxEntryUncompressedBytes: 2L * 1024 * 1024 * 1024,
        MaxTotalUncompressedBytes: 8L * 1024 * 1024 * 1024,
        MaxCompressionRatio: 250,
        CompressionRatioCheckThresholdBytes: 1024 * 1024,
        RequiredFreeSpaceReserveBytes: 256L * 1024 * 1024);
}
