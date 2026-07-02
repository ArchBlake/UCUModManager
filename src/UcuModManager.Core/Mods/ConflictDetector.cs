namespace UcuModManager.Core.Mods;

public sealed record FileConflict(
    string TargetRelativePath,
    IReadOnlyList<ModFileMapping> CompetingFiles)
{
    public ModFileMapping WinningFile => CompetingFiles[^1];
}

public sealed class ConflictDetector
{
    public IReadOnlyList<FileConflict> DetectConflicts(IEnumerable<ModFileMapping> mappings)
    {
        return mappings
            .Where(mapping => mapping.IsEnabledByDefault)
            .GroupBy(mapping => mapping.NormalizedTargetRelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new FileConflict(group.Key, group.ToArray()))
            .OrderBy(conflict => conflict.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
