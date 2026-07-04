namespace UcuModManager.Core.Virtualization;

public sealed record VirtualizedLaunchPlanIssue(
    VirtualizedLaunchPlanIssueSeverity Severity,
    string Code,
    string Message);
