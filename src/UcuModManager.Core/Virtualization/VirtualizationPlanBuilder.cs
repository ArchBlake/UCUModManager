using UcuModManager.Core.Mods;
using UcuModManager.Core.Profiles;

namespace UcuModManager.Core.Virtualization;

public sealed class VirtualizationPlanBuilder
{
    public VirtualizationPlan Build(
        string gameRootPath,
        string executableName,
        ModProfile profile,
        IReadOnlyList<ModLibraryEntry> installedMods)
    {
        var modById = installedMods.ToDictionary(mod => mod.Mod.Id, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var files = new List<VirtualFileEntry>();

        foreach (var profileEntry in profile.Mods.Where(entry => entry.IsEnabled).OrderBy(entry => entry.Priority))
        {
            if (!modById.TryGetValue(profileEntry.ModId, out var libraryEntry))
            {
                warnings.Add($"Profile references missing mod '{profileEntry.ModId}'.");
                continue;
            }

            foreach (var file in libraryEntry.Mod.Files.Where(file => file.IsEnabledByDefault))
            {
                var sourcePath = Path.Combine(
                    libraryEntry.ModDirectoryPath,
                    "files",
                    file.NormalizedTargetRelativePath.Replace('/', Path.DirectorySeparatorChar));

                files.Add(new VirtualFileEntry(
                    sourcePath,
                    file.NormalizedTargetRelativePath,
                    file.TargetKind,
                    libraryEntry.Mod.Id,
                    profileEntry.Priority));
            }
        }

        return new VirtualizationPlan(
            Path.GetFullPath(gameRootPath),
            Path.Combine(Path.GetFullPath(gameRootPath), executableName),
            profile.Id,
            files,
            warnings);
    }
}
