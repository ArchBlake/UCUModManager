namespace UcuModManager.Core.Virtualization;

public sealed class OverlayPreviewService
{
    public OverlayPreview Build(VirtualizationPlan plan)
    {
        var indexedFiles = plan.Files
            .Select((file, order) => new IndexedFile(order, file))
            .ToArray();

        var groupedFiles = indexedFiles
            .GroupBy(item => item.File.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var winnerByTargetPath = groupedFiles.ToDictionary(
            group => group.Key,
            group => group
                .OrderBy(item => item.File.Priority)
                .ThenBy(item => item.OverlayOrder)
                .Last(),
            StringComparer.OrdinalIgnoreCase);

        var conflictTargets = groupedFiles
            .Where(group => group.Count() > 1)
            .Where(group => !IsIgnoredConflict(group.Key, group.Select(item => item.File)))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = indexedFiles
            .Select(item =>
            {
                var winner = winnerByTargetPath[item.File.TargetRelativePath];
                var isConflict = conflictTargets.Contains(item.File.TargetRelativePath);

                return new OverlayPreviewEntry(
                    item.OverlayOrder,
                    item.File.SourcePath,
                    item.File.TargetRelativePath,
                    BuildTargetAbsolutePath(plan.GameRootPath, item.File.TargetRelativePath),
                    item.File.TargetKind,
                    item.File.OwningModId,
                    item.File.Priority,
                    File.Exists(item.File.SourcePath),
                    isConflict,
                    item.OverlayOrder == winner.OverlayOrder);
            })
            .OrderBy(entry => entry.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Priority)
            .ThenBy(entry => entry.OverlayOrder)
            .ToArray();

        var conflicts = entries
            .Where(entry => entry.IsConflict)
            .GroupBy(entry => entry.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var conflictEntries = group
                    .OrderBy(entry => entry.Priority)
                    .ThenBy(entry => entry.OverlayOrder)
                    .ToArray();
                var winner = conflictEntries.Single(entry => entry.IsWinner);

                return new OverlayConflict(group.Key, winner, conflictEntries);
            })
            .OrderBy(conflict => conflict.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var missingSources = entries
            .Where(entry => !entry.SourceExists)
            .ToArray();

        return new OverlayPreview(
            plan.GameRootPath,
            plan.GameExecutablePath,
            plan.ProfileId,
            entries,
            conflicts,
            missingSources,
            plan.Warnings);
    }

    private static string BuildTargetAbsolutePath(string gameRootPath, string targetRelativePath)
    {
        var platformPath = targetRelativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(gameRootPath, platformPath));
    }

    private static bool IsIgnoredConflict(string targetRelativePath, IEnumerable<VirtualFileEntry> files)
    {
        var normalizedTarget = targetRelativePath.Replace('\\', '/').TrimStart('/');
        if (!normalizedTarget.StartsWith("BepInEx/config/Cat-Patch/sounds/", StringComparison.OrdinalIgnoreCase)
            && !normalizedTarget.StartsWith("BepInEx/config/Cat-Patch/sprays/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var entries = files.ToArray();
        return entries.Any(entry => entry.OwningModId.Equals("__profile_state__", StringComparison.OrdinalIgnoreCase))
            && entries.Any(entry => entry.OwningModId.Contains("Catpatch", StringComparison.OrdinalIgnoreCase)
                || entry.OwningModId.Contains("Cat-Patch", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record IndexedFile(int OverlayOrder, VirtualFileEntry File);
}
