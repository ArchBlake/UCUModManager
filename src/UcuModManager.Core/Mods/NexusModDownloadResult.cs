namespace UcuModManager.Core.Mods;

public sealed record NexusModDownloadResult(
    string ArchivePath,
    Uri DownloadUri,
    string FileName);
