namespace UcuModManager.Core.Mods;

public sealed record ModImportPlan(
    string ArchivePath,
    string SuggestedModName,
    string? SuggestedVersion,
    ModSourceInfo? Source,
    string? StrippedRootDirectory,
    IReadOnlyList<ModFileMapping> Mappings,
    IReadOnlyList<AssemblyIdentityInfo> Assemblies,
    IReadOnlyList<AssemblyReferenceInfo> AssemblyReferences,
    IReadOnlyList<IgnoredArchiveEntry> IgnoredEntries,
    IReadOnlyList<string> Warnings)
{
    public bool RequiresManualReview => Warnings.Count > 0 || IgnoredEntries.Any(entry => entry.Reason.Contains("unknown", StringComparison.OrdinalIgnoreCase));
}
