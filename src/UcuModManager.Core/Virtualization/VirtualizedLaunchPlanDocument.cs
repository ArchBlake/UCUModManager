namespace UcuModManager.Core.Virtualization;

public sealed record VirtualizedLaunchPlanDocument(
    int FormatVersion,
    DateTimeOffset CreatedAt,
    string Mode,
    string ProfileId,
    string ManagerRootPath,
    string ProfileRootPath,
    string ProfileBepInExPath,
    string ProfileVirtualizationRootPath,
    string ProfileRuntimePath,
    string ProfileWriteRedirectPath,
    string GameRootPath,
    string GameExecutablePath,
    bool RequiresPhysicalBepInEx,
    VirtualizedLaunchPolicy Policy,
    int ActiveFileCount,
    int TotalOverlayEntryCount,
    int ConflictCount,
    int MissingSourceCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<VirtualizedLaunchPlanFile> EffectiveFiles,
    IReadOnlyList<VirtualizedLaunchPlanFile> OverlayEntries);

public sealed record VirtualizedLaunchPolicy(
    bool RedirectWritesToProfileState,
    bool IncludeProfileBepInExConfig,
    string WriteRedirectMode);

public sealed record VirtualizedLaunchPlanFile(
    string TargetRelativePath,
    string SourcePath,
    string OwningModId,
    string TargetKind,
    int Priority,
    bool IsWinner,
    bool SourceExists);
