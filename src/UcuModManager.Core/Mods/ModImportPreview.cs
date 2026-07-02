namespace UcuModManager.Core.Mods;

public sealed record ModImportPreview(
    ModImportAction Action,
    string ModId,
    string SuggestedModName,
    string? SuggestedVersion,
    ModSourceInfo? Source,
    string SourceArchiveFileName,
    IReadOnlyList<string> Warnings);
