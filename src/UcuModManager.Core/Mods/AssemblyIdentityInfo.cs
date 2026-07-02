namespace UcuModManager.Core.Mods;

public sealed record AssemblyIdentityInfo(
    string SourceArchivePath,
    string Name,
    Version? Version);
