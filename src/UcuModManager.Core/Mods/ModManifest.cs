namespace UcuModManager.Core.Mods;

public sealed record ModManifest(
    int ManifestVersion,
    ModPackage Mod,
    string SourceArchiveFileName,
    string? StrippedRootDirectory,
    IReadOnlyList<IgnoredArchiveEntry> IgnoredEntries,
    IReadOnlyList<string> Warnings,
    ModSourceInfo? Source = null)
{
    public const int CurrentManifestVersion = 2;

    public static ModManifest Create(ModPackage mod, ModImportPlan plan)
    {
        return new ModManifest(
            CurrentManifestVersion,
            mod,
            Path.GetFileName(plan.ArchivePath),
            plan.StrippedRootDirectory,
            plan.IgnoredEntries,
            plan.Warnings,
            plan.Source);
    }
}
