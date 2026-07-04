namespace UcuModManager.Core.Virtualization;

public sealed record VirtualizedLaunchPlanValidationResult(IReadOnlyList<VirtualizedLaunchPlanIssue> Issues)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == VirtualizedLaunchPlanIssueSeverity.Error);

    public bool HasWarnings => Issues.Any(issue => issue.Severity == VirtualizedLaunchPlanIssueSeverity.Warning);

    public static VirtualizedLaunchPlanValidationResult Success { get; } = new(Array.Empty<VirtualizedLaunchPlanIssue>());
}
