namespace UcuModManager.Core.Virtualization;

public enum VirtualizedPathResolutionKind
{
    Invalid,
    Missing,
    PlanFile,
    WriteRedirectFile,
    GameFile,
    WriteRedirectTarget,
    GameTarget
}
