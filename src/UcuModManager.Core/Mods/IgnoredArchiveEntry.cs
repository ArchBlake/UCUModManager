namespace UcuModManager.Core.Mods;

public sealed record IgnoredArchiveEntry(
    string SourceArchivePath,
    string Reason);
