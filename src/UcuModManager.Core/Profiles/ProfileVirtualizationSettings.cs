namespace UcuModManager.Core.Profiles;

public sealed record ProfileVirtualizationSettings(
    bool UseExperimentalVirtualizedLaunch = true,
    bool RedirectWritesToProfileState = true)
{
    public static ProfileVirtualizationSettings Empty { get; } = new();
}
