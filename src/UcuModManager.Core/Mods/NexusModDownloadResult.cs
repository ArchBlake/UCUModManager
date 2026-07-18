namespace UcuModManager.Core.Mods;

public sealed record NexusModDownloadResult(
    string ArchivePath,
    Uri DownloadUri,
    string FileName,
    long BytesDownloaded);

public sealed record NexusModDownloadProgress(
    long BytesDownloaded,
    long? TotalBytes,
    string Status);
