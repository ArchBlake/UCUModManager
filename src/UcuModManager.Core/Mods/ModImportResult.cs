namespace UcuModManager.Core.Mods;

public sealed record ModImportResult(
    ModImportAction Action,
    ModManifest Manifest,
    ModManifest? PreviousManifest,
    string ModDirectoryPath,
    string FilesDirectoryPath,
    string ManifestPath);
