namespace UcuModManager.Core.Mods;

public sealed record ArchiveEntryInfo(
    string ArchivePath,
    long UncompressedSize,
    bool IsDirectory)
{
    public string NormalizedPath => ArchivePath.Replace('\\', '/').TrimStart('/');
}
