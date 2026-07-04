using UcuModManager.Core.Mods;
using UcuModManager.Core.Profiles;

namespace UcuModManager.Core.Virtualization;

public sealed class VirtualizationPlanBuilder
{
    private const int ProfileStatePriority = int.MaxValue;
    private const string ProfileStateOwner = "__profile_state__";

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

        AddProfileBepInExStateFiles(profile, files, warnings);

        return new VirtualizationPlan(
            Path.GetFullPath(gameRootPath),
            Path.Combine(Path.GetFullPath(gameRootPath), executableName),
            profile.Id,
            files,
            warnings);
    }

    private static void AddProfileBepInExStateFiles(
        ModProfile profile,
        List<VirtualFileEntry> files,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(profile.ProfileBepInExPath)
            || !Directory.Exists(profile.ProfileBepInExPath))
        {
            return;
        }

        var profileBepInExRoot = EnsureTrailingSeparator(Path.GetFullPath(profile.ProfileBepInExPath));
        foreach (var filePath in Directory.EnumerateFiles(profileBepInExRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(profileBepInExRoot, filePath)
                .Replace('\\', '/')
                .TrimStart('/');
            if (!TryBuildProfileBepInExTarget(relativePath, out var targetRelativePath, out var targetKind))
            {
                continue;
            }

            var sourcePath = Path.GetFullPath(filePath);
            if (!sourcePath.StartsWith(profileBepInExRoot, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped unsafe profile state file outside profile BepInEx root: {filePath}");
                continue;
            }

            files.Add(new VirtualFileEntry(
                sourcePath,
                targetRelativePath,
                targetKind,
                ProfileStateOwner,
                ProfileStatePriority));
        }
    }

    private static bool TryBuildProfileBepInExTarget(
        string profileRelativePath,
        out string targetRelativePath,
        out ModTargetKind targetKind)
    {
        targetRelativePath = string.Empty;
        targetKind = ModTargetKind.Unknown;

        if (profileRelativePath.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
        {
            targetRelativePath = $"BepInEx/{profileRelativePath}";
            targetKind = ModTargetKind.BepInExProfileConfig;
            return true;
        }

        if (profileRelativePath.StartsWith("patchers/", StringComparison.OrdinalIgnoreCase))
        {
            targetRelativePath = $"BepInEx/{profileRelativePath}";
            targetKind = ModTargetKind.BepInExPatcher;
            return true;
        }

        if (profileRelativePath.StartsWith("Translation/", StringComparison.OrdinalIgnoreCase))
        {
            targetRelativePath = $"BepInEx/{profileRelativePath}";
            targetKind = ModTargetKind.BepInExTranslation;
            return true;
        }

        return false;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
