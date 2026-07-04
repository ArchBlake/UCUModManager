namespace UcuModManager.Core.Virtualization;

public sealed record VirtualizedPathResolution(
    VirtualizedPathResolutionKind Kind,
    string RequestedPath,
    string TargetRelativePath,
    string? ResolvedPath,
    string? OwningModId,
    string? TargetKind,
    bool Exists,
    string? ErrorMessage)
{
    public bool IsResolved => Kind is not VirtualizedPathResolutionKind.Invalid
        and not VirtualizedPathResolutionKind.Missing;
}
