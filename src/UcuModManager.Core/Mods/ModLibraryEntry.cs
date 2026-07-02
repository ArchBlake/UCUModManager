namespace UcuModManager.Core.Mods;

public sealed record ModLibraryEntry(
    ModManifest Manifest,
    string ModDirectoryPath,
    string ManifestPath,
    IReadOnlyList<ModDependencyStatus> Dependencies)
{
    public ModPackage Mod => Manifest.Mod;
    public IReadOnlyList<string> Warnings => Manifest.Warnings;
    public bool HasWarnings => Warnings.Count > 0 || Dependencies.Any(dependency => !dependency.IsSatisfied);
    public int FileCount => Mod.Files.Count;
}
