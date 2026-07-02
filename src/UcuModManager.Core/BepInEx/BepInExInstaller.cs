using System.IO.Compression;

namespace UcuModManager.Core.BepInEx;

public sealed class BepInExInstaller
{
    private readonly BepInExArchivePlanner _planner = new();
    private readonly BepInExInstallationProbe _probe = new();

    public BepInExInstallResult InstallFromArchive(string archivePath, string gameRootPath, bool overwriteExisting = true)
    {
        var plan = _planner.CreatePlan(archivePath, gameRootPath);
        EnsureLooksLikeBepInEx(plan);

        var installedFiles = new List<string>();
        var skippedEntries = new List<string>();
        var gameRoot = EnsureTrailingSeparator(Path.GetFullPath(gameRootPath));

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries.Where(entry => !IsDirectory(entry.FullName)))
        {
            var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            var destinationPath = Path.GetFullPath(Path.Combine(gameRoot, normalized));
            if (!destinationPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
            {
                skippedEntries.Add(entry.FullName);
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            entry.ExtractToFile(destinationPath, overwriteExisting);
            installedFiles.Add(normalized);
        }

        EnsureStandardBepInExDirectories(Path.GetFullPath(gameRootPath));
        var state = _probe.Probe(gameRootPath);

        return new BepInExInstallResult(
            plan,
            installedFiles,
            skippedEntries,
            state);
    }

    private static void EnsureLooksLikeBepInEx(BepInExInstallPlan plan)
    {
        var files = plan.FilesToInstall.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasDoorstop = files.Contains("winhttp.dll")
            && files.Contains("doorstop_config.ini");
        var hasCore = files.Contains("BepInEx/core/BepInEx.dll");

        if (!hasDoorstop || !hasCore)
        {
            throw new InvalidOperationException("Selected archive does not look like BepInEx 5.x for Windows x64.");
        }
    }

    private static void EnsureStandardBepInExDirectories(string gameRootPath)
    {
        foreach (var relativePath in new[]
        {
            Path.Combine("BepInEx", "plugins"),
            Path.Combine("BepInEx", "config"),
            Path.Combine("BepInEx", "patchers"),
            Path.Combine("BepInEx", "Translation")
        })
        {
            Directory.CreateDirectory(Path.Combine(gameRootPath, relativePath));
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool IsDirectory(string archivePath)
    {
        return archivePath.EndsWith("/", StringComparison.Ordinal) || archivePath.EndsWith("\\", StringComparison.Ordinal);
    }
}
