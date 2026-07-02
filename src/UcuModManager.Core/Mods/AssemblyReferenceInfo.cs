namespace UcuModManager.Core.Mods;

public sealed record AssemblyReferenceInfo(
    string SourceArchivePath,
    string Name,
    Version? Version,
    bool IsKnownGameOrFrameworkReference);
