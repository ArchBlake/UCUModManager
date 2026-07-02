using UcuModManager.Core.Mods;

namespace UcuModManager.Core.Virtualization;

public sealed record VirtualFileEntry(
    string SourcePath,
    string TargetRelativePath,
    ModTargetKind TargetKind,
    string OwningModId,
    int Priority);
