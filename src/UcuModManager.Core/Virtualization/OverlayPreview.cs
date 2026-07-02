namespace UcuModManager.Core.Virtualization;

public sealed record OverlayPreview(
    string GameRootPath,
    string GameExecutablePath,
    string ProfileId,
    IReadOnlyList<OverlayPreviewEntry> Entries,
    IReadOnlyList<OverlayConflict> Conflicts,
    IReadOnlyList<OverlayPreviewEntry> MissingSources,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<OverlayPreviewEntry> ActiveEntries { get; } = Entries
        .Where(entry => entry.IsWinner)
        .ToArray();
}
