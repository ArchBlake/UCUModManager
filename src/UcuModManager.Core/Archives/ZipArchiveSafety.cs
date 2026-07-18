using System.IO.Compression;

namespace UcuModManager.Core.Archives;

public static class ZipArchiveSafety
{
    public static ZipArchiveSafetySummary Validate(
        ZipArchive archive,
        ArchiveExtractionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        limits ??= ArchiveExtractionLimits.Default;

        var entryCount = 0;
        long totalUncompressedBytes = 0;
        foreach (var entry in archive.Entries.Where(entry => !IsDirectory(entry.FullName)))
        {
            entryCount++;
            if (entryCount > limits.MaxEntryCount)
            {
                throw new InvalidDataException(
                    $"Archive contains too many files. Limit: {limits.MaxEntryCount:N0}.");
            }

            var entryLength = entry.Length;
            if (entryLength < 0 || entryLength > limits.MaxEntryUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Archive entry is too large: {entry.FullName}. Limit: {FormatBytes(limits.MaxEntryUncompressedBytes)}.");
            }

            try
            {
                totalUncompressedBytes = checked(totalUncompressedBytes + entryLength);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("Archive uncompressed size is invalid.", exception);
            }

            if (totalUncompressedBytes > limits.MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Archive is too large when unpacked. Limit: {FormatBytes(limits.MaxTotalUncompressedBytes)}.");
            }

            if (entryLength >= limits.CompressionRatioCheckThresholdBytes)
            {
                var compressedLength = entry.CompressedLength;
                if (compressedLength <= 0
                    || entryLength / (double)compressedLength > limits.MaxCompressionRatio)
                {
                    throw new InvalidDataException(
                        $"Archive entry has an unsafe compression ratio: {entry.FullName}.");
                }
            }
        }

        return new ZipArchiveSafetySummary(entryCount, totalUncompressedBytes);
    }

    public static void EnsureSufficientDiskSpace(
        string destinationPath,
        long requiredBytes,
        ArchiveExtractionLimits? limits = null)
    {
        limits ??= ArchiveExtractionLimits.Default;
        var fullPath = Path.GetFullPath(destinationPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        var availableForArchive = Math.Max(0, drive.AvailableFreeSpace - limits.RequiredFreeSpaceReserveBytes);
        if (requiredBytes > availableForArchive)
        {
            throw new IOException(
                $"Not enough free disk space to unpack the archive. Required: {FormatBytes(requiredBytes)}, available: {FormatBytes(availableForArchive)}.");
        }
    }

    public static void ExtractEntryToFile(
        ZipArchiveEntry entry,
        string destinationPath,
        bool overwrite,
        ArchiveExtractionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        limits ??= ArchiveExtractionLimits.Default;
        if (entry.Length > limits.MaxEntryUncompressedBytes)
        {
            throw new InvalidDataException(
                $"Archive entry is too large: {entry.FullName}. Limit: {FormatBytes(limits.MaxEntryUncompressedBytes)}.");
        }

        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var destinationOpened = false;
        try
        {
            using var source = entry.Open();
            using var destination = new FileStream(
                destinationPath,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            destinationOpened = true;
            CopyWithLimit(source, destination, entry.Length, entry.FullName);
        }
        catch
        {
            if (destinationOpened && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    public static void CopyWithLimit(
        Stream source,
        Stream destination,
        long expectedBytes,
        string entryName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (expectedBytes < 0)
        {
            throw new InvalidDataException($"Archive entry has an invalid size: {entryName}.");
        }

        var buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            totalBytes = checked(totalBytes + read);
            if (totalBytes > expectedBytes)
            {
                throw new InvalidDataException($"Archive entry expanded beyond its declared size: {entryName}.");
            }

            destination.Write(buffer, 0, read);
        }

        if (totalBytes != expectedBytes)
        {
            throw new InvalidDataException($"Archive entry size did not match its metadata: {entryName}.");
        }
    }

    private static bool IsDirectory(string path)
    {
        return path.EndsWith("/", StringComparison.Ordinal)
            || path.EndsWith("\\", StringComparison.Ordinal);
    }

    private static string FormatBytes(long value)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        var amount = Math.Max(0, value);
        var display = (double)amount;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.##} {units[unit]}";
    }
}

public sealed record ZipArchiveSafetySummary(int EntryCount, long TotalUncompressedBytes);
