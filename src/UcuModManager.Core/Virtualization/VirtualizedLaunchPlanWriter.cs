using System.Text.Json;
using UcuModManager.Core.Profiles;
using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Virtualization;

public sealed class VirtualizedLaunchPlanWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Save(ManagerPaths managerPaths, ModProfile profile, OverlayPreview overlayPreview)
    {
        var profileRootPath = Path.Combine(managerPaths.ProfilesPath, profile.Id);
        var profileVirtualizationPath = Path.Combine(profileRootPath, "virtualization");
        var profileRuntimePath = Path.Combine(profileVirtualizationPath, "runtime");
        var profileWriteRedirectPath = Path.Combine(profileVirtualizationPath, "overwrite");
        Directory.CreateDirectory(profileVirtualizationPath);
        Directory.CreateDirectory(profileRuntimePath);
        Directory.CreateDirectory(profileWriteRedirectPath);

        var overlayEntries = overlayPreview.Entries
            .OrderBy(entry => entry.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Priority)
            .ThenBy(entry => entry.OverlayOrder)
            .Select(ToPlanFile)
            .ToArray();

        var document = new VirtualizedLaunchPlanDocument(
            1,
            DateTimeOffset.UtcNow,
            "ExperimentalVirtualizedLaunch",
            profile.Id,
            managerPaths.RootPath,
            profileRootPath,
            profile.ProfileBepInExPath,
            profileVirtualizationPath,
            profileRuntimePath,
            profileWriteRedirectPath,
            overlayPreview.GameRootPath,
            overlayPreview.GameExecutablePath,
            true,
            new VirtualizedLaunchPolicy(
                profile.Virtualization.RedirectWritesToProfileState,
                true,
                profile.Virtualization.RedirectWritesToProfileState
                    ? "ProfileVirtualizationOverwrite"
                    : "GameDefault"),
            overlayPreview.ActiveEntries.Count,
            overlayPreview.Entries.Count,
            overlayPreview.Conflicts.Count,
            overlayPreview.MissingSources.Count,
            overlayPreview.Warnings,
            overlayEntries
                .Where(entry => entry.IsWinner)
                .ToArray(),
            overlayEntries);

        var planPath = Path.Combine(profileVirtualizationPath, "last-plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(document, JsonOptions));
        return planPath;
    }

    private static VirtualizedLaunchPlanFile ToPlanFile(OverlayPreviewEntry entry)
    {
        return new VirtualizedLaunchPlanFile(
            entry.TargetRelativePath,
            entry.SourcePath,
            entry.OwningModId,
            entry.TargetKind.ToString(),
            entry.Priority,
            entry.IsWinner,
            entry.SourceExists);
    }
}
