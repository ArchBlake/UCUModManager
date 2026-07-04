namespace UcuModManager.Core.Virtualization;

public sealed record VirtualizedDirectoryEntry(
    string Name,
    string TargetRelativePath,
    string? ResolvedPath,
    VirtualizedDirectoryEntryKind Kind,
    string? OwningModId)
{
    public bool IsDirectory => Kind == VirtualizedDirectoryEntryKind.Directory;
}
