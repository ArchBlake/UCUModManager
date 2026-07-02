using System.IO.Compression;

namespace UcuModManager.Core.BepInEx;

public sealed class BepInExArchivePlanner
{
    public BepInExInstallPlan CreatePlan(string archivePath, string gameRootPath)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("BepInEx archive was not found.", archivePath);
        }

        var gameRoot = EnsureTrailingSeparator(Path.GetFullPath(gameRootPath));
        var files = new List<string>();
        var existing = new List<string>();
        var warnings = new List<string>();

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries.Where(entry => !IsDirectory(entry.FullName)))
        {
            var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            var destinationPath = Path.GetFullPath(Path.Combine(gameRoot, normalized));
            if (!destinationPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped unsafe archive entry: {entry.FullName}");
                continue;
            }

            files.Add(normalized);
            if (File.Exists(destinationPath))
            {
                existing.Add(normalized);
            }
        }

        var hasDoorstop = files.Contains("winhttp.dll", StringComparer.OrdinalIgnoreCase)
            && files.Contains("doorstop_config.ini", StringComparer.OrdinalIgnoreCase);
        var hasCore = files.Any(file => file.Equals("BepInEx/core/BepInEx.dll", StringComparison.OrdinalIgnoreCase));

        if (!hasDoorstop || !hasCore)
        {
            warnings.Add("Archive does not look like BepInEx x64 5.x for Windows.");
        }

        return new BepInExInstallPlan(archivePath, gameRoot, files, existing, warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsDirectory(string archivePath)
    {
        return archivePath.EndsWith("/", StringComparison.Ordinal) || archivePath.EndsWith("\\", StringComparison.Ordinal);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}



