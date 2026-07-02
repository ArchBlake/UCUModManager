namespace UcuModManager.Core.Virtualization;

public sealed record OverlayConflict(
    string TargetRelativePath,
    OverlayPreviewEntry Winner,
    IReadOnlyList<OverlayPreviewEntry> Entries);
