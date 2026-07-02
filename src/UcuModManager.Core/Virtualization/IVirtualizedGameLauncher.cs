namespace UcuModManager.Core.Virtualization;

public interface IVirtualizedGameLauncher
{
    Task LaunchAsync(VirtualizationPlan plan, CancellationToken cancellationToken = default);
}
