using UcuModManager.Core.Mods;

namespace UcuModManager.Core.Virtualization;

public sealed record OverlayPreviewEntry(
    int OverlayOrder,
    string SourcePath,
    string TargetRelativePath,
    string TargetAbsolutePath,
    ModTargetKind TargetKind,
    string OwningModId,
    int Priority,
    bool SourceExists,
    bool IsConflict,
    bool IsWinner)
{
    public string Status
    {
        get
        {
            if (!SourceExists)
            {
                return "Missing Source";
            }

            if (!IsConflict)
            {
                return "Active";
            }

            return IsWinner ? "Winner" : "Overridden";
        }
    }
}
