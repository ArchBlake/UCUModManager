using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using UcuModManager.Core.Archives;
using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Mods;

public sealed class ModImportService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ModArchiveAnalyzer _archiveAnalyzer;

    public ModImportService(ModArchiveAnalyzer? archiveAnalyzer = null)
    {
        _archiveAnalyzer = archiveAnalyzer ?? new ModArchiveAnalyzer();
    }

    public ModImportPreview PreviewZip(string archivePath, ManagerPaths managerPaths)
    {
        if (LooksLikeBepInExReleaseArchive(archivePath))
        {
            throw new InvalidOperationException("This archive looks like BepInEx. Use BepInEx Setup instead of installing it as a mod.");
        }

        var plan = _archiveAnalyzer.AnalyzeZip(archivePath);
        if (plan.Mappings.Count == 0)
        {
            throw new InvalidOperationException("The archive does not contain importable mod files.");
        }

        var preferredModId = ModPackage.CreateStableId(plan.SuggestedModName);
        var existingModId = FindExistingModId(managerPaths.ModsPath, preferredModId, plan.Source);
        var isUpdate = existingModId is not null;
        var modId = isUpdate
            ? existingModId!
            : CreateUniqueModId(managerPaths.ModsPath, preferredModId);

        return new ModImportPreview(
            isUpdate ? ModImportAction.Updated : ModImportAction.Installed,
            modId,
            plan.SuggestedModName,
            plan.SuggestedVersion,
            plan.Source,
            Path.GetFileName(archivePath),
            plan.Warnings);
    }

    public ModImportResult ImportZip(string archivePath, ManagerPaths managerPaths)
    {
        if (LooksLikeBepInExReleaseArchive(archivePath))
        {
            throw new InvalidOperationException("This archive looks like BepInEx. Use BepInEx Setup instead of installing it as a mod.");
        }

        var plan = _archiveAnalyzer.AnalyzeZip(archivePath);
        if (plan.Mappings.Count == 0)
        {
            throw new InvalidOperationException("The archive does not contain importable mod files.");
        }

        Directory.CreateDirectory(managerPaths.ModsPath);
        Directory.CreateDirectory(managerPaths.CachePath);

        var preferredModId = ModPackage.CreateStableId(plan.SuggestedModName);
        var existingModId = FindExistingModId(managerPaths.ModsPath, preferredModId, plan.Source);
        var isUpdate = existingModId is not null;
        var modId = isUpdate
            ? existingModId!
            : CreateUniqueModId(managerPaths.ModsPath, preferredModId);
        var modDirectoryPath = Path.Combine(managerPaths.ModsPath, modId);
        var stagingRootPath = Path.Combine(managerPaths.CachePath, "import-staging", Guid.NewGuid().ToString("N"));
        var stagingModDirectoryPath = Path.Combine(stagingRootPath, modId);
        var stagingFilesDirectoryPath = Path.Combine(stagingModDirectoryPath, "files");
        Directory.CreateDirectory(stagingFilesDirectoryPath);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var archiveSafety = ZipArchiveSafety.Validate(archive);
            ZipArchiveSafety.EnsureSufficientDiskSpace(stagingFilesDirectoryPath, archiveSafety.TotalUncompressedBytes);
            var entryByPath = archive.Entries
                .Where(entry => !IsDirectory(entry.FullName))
                .ToDictionary(
                    entry => NormalizeArchivePath(entry.FullName),
                    entry => entry,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in plan.Mappings)
            {
                if (!entryByPath.TryGetValue(NormalizeArchivePath(mapping.SourceArchivePath), out var entry))
                {
                    throw new InvalidOperationException($"Archive entry was not found during import: {mapping.SourceArchivePath}");
                }

                CopyEntryToModStorage(entry, stagingFilesDirectoryPath, mapping.NormalizedTargetRelativePath);
            }

            var package = new ModPackage(
                modId,
                plan.SuggestedModName,
                string.IsNullOrWhiteSpace(plan.SuggestedVersion) ? "unknown" : plan.SuggestedVersion,
                plan.Mappings,
                plan.Assemblies,
                plan.AssemblyReferences,
                DateTimeOffset.UtcNow);
            var manifest = ModManifest.Create(package, plan);
            var stagingManifestPath = Path.Combine(stagingModDirectoryPath, "manifest.json");
            File.WriteAllText(stagingManifestPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions));

            var previousManifest = isUpdate
                ? LoadManifest(Path.Combine(modDirectoryPath, "manifest.json"))
                : null;
            if (isUpdate)
            {
                ReplaceDirectory(stagingModDirectoryPath, modDirectoryPath, Path.Combine(managerPaths.CachePath, "import-backups", $"{modId}-{Guid.NewGuid():N}"));
            }
            else
            {
                Directory.Move(stagingModDirectoryPath, modDirectoryPath);
            }

            var filesDirectoryPath = Path.Combine(modDirectoryPath, "files");
            var manifestPath = Path.Combine(modDirectoryPath, "manifest.json");
            return new ModImportResult(
                isUpdate ? ModImportAction.Updated : ModImportAction.Installed,
                manifest,
                previousManifest,
                modDirectoryPath,
                filesDirectoryPath,
                manifestPath);
        }
        finally
        {
            if (Directory.Exists(stagingRootPath))
            {
                Directory.Delete(stagingRootPath, recursive: true);
            }
        }
    }

    private static void CopyEntryToModStorage(ZipArchiveEntry entry, string filesDirectoryPath, string targetRelativePath)
    {
        var destinationPath = Path.GetFullPath(Path.Combine(filesDirectoryPath, targetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var fullFilesDirectoryPath = EnsureTrailingSeparator(Path.GetFullPath(filesDirectoryPath));
        if (!destinationPath.StartsWith(fullFilesDirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Archive entry targets a path outside mod storage: {targetRelativePath}");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        ZipArchiveSafety.ExtractEntryToFile(entry, destinationPath, overwrite: true);
    }

    private static string CreateUniqueModId(string modsPath, string preferredModId)
    {
        var baseId = string.IsNullOrWhiteSpace(preferredModId) ? "mod" : preferredModId;
        var candidate = baseId;
        var suffix = 2;

        while (Directory.Exists(Path.Combine(modsPath, candidate)))
        {
            candidate = $"{baseId}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string? FindExistingModId(string modsPath, string preferredModId, ModSourceInfo? source)
    {
        var normalizedPreferredModId = string.IsNullOrWhiteSpace(preferredModId) ? "mod" : preferredModId;
        if (Directory.Exists(Path.Combine(modsPath, normalizedPreferredModId)))
        {
            return normalizedPreferredModId;
        }

        if (!CanMatchExistingNexusSource(source) || !Directory.Exists(modsPath))
        {
            return null;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(modsPath, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var manifest = LoadManifest(manifestPath);
            if (manifest?.Source is null || !SourcesReferToSameNexusMod(source!, manifest.Source))
            {
                continue;
            }

            return Path.GetFileName(Path.GetDirectoryName(manifestPath));
        }

        return null;
    }

    private static bool CanMatchExistingNexusSource(ModSourceInfo? source)
    {
        return source is not null
            && source.Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase)
            && source.ModId is not null;
    }

    private static bool SourcesReferToSameNexusMod(ModSourceInfo incoming, ModSourceInfo existing)
    {
        if (!existing.Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase)
            || incoming.ModId is null
            || existing.ModId is null
            || incoming.ModId.Value != existing.ModId.Value)
        {
            return false;
        }

        var incomingDomain = NormalizeNexusDomain(incoming.GameDomain);
        var existingDomain = NormalizeNexusDomain(existing.GameDomain);
        return string.IsNullOrWhiteSpace(incomingDomain)
            || string.IsNullOrWhiteSpace(existingDomain)
            || incomingDomain.Equals(existingDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeNexusDomain(string? domain)
    {
        return string.IsNullOrWhiteSpace(domain) ? string.Empty : domain.Trim().ToLowerInvariant();
    }

    private static ModManifest? LoadManifest(string manifestPath)
    {
        try
        {
            return File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void ReplaceDirectory(string sourceDirectoryPath, string targetDirectoryPath, string backupDirectoryPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupDirectoryPath)!);
        Directory.Move(targetDirectoryPath, backupDirectoryPath);
        try
        {
            Directory.Move(sourceDirectoryPath, targetDirectoryPath);
        }
        catch
        {
            if (!Directory.Exists(targetDirectoryPath) && Directory.Exists(backupDirectoryPath))
            {
                Directory.Move(backupDirectoryPath, targetDirectoryPath);
            }

            throw;
        }

        try
        {
            Directory.Delete(backupDirectoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeArchivePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
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

    private static bool LooksLikeBepInExReleaseArchive(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(entry => !IsDirectory(entry.FullName))
            .Select(entry => NormalizeArchivePath(entry.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return entries.Contains("winhttp.dll")
            && entries.Contains("doorstop_config.ini")
            && entries.Contains("BepInEx/core/BepInEx.dll");
    }
}
