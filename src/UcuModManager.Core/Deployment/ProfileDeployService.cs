using System.Security.Cryptography;
using System.Text.Json;
using UcuModManager.Core.Storage;
using UcuModManager.Core.Virtualization;

namespace UcuModManager.Core.Deployment;

public sealed class ProfileDeployService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public DeploymentManifest? LoadManifest(ManagerPaths managerPaths, string profileId)
    {
        var manifestPath = GetManifestPath(managerPaths, profileId);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllText(manifestPath), JsonOptions);
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

    public IReadOnlyList<ProfileDeployResult> CleanOtherProfiles(ManagerPaths managerPaths, string activeProfileId, string gameRootPath)
    {
        if (!Directory.Exists(managerPaths.ProfilesPath))
        {
            return Array.Empty<ProfileDeployResult>();
        }

        var results = new List<ProfileDeployResult>();
        foreach (var profileDirectory in Directory.EnumerateDirectories(managerPaths.ProfilesPath))
        {
            var profileId = Path.GetFileName(profileDirectory);
            if (string.IsNullOrWhiteSpace(profileId)
                || profileId.Equals(activeProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifest = LoadManifest(managerPaths, profileId);
            if (manifest is null || !PathsEqual(manifest.GameRootPath, gameRootPath))
            {
                continue;
            }

            results.Add(Clean(managerPaths, profileId));
        }

        return results;
    }

    public ProfileDeployResult Deploy(ManagerPaths managerPaths, OverlayPreview overlayPreview)
    {
        if (overlayPreview.MissingSources.Count > 0)
        {
            throw new InvalidOperationException("Deployment cannot continue because one or more source files are missing.");
        }

        var cleanResult = CleanInternal(managerPaths, overlayPreview.ProfileId);
        var warnings = cleanResult.Warnings.ToList();
        var records = cleanResult.PreservedRecords.ToList();
        var copiedFiles = 0;
        var gameRoot = EnsureTrailingSeparator(Path.GetFullPath(overlayPreview.GameRootPath));
        var existingTargets = records
            .Select(record => record.TargetRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in overlayPreview.ActiveEntries.OrderBy(entry => entry.OverlayOrder))
        {
            var targetAbsolutePath = Path.GetFullPath(entry.TargetAbsolutePath);
            if (!IsInsideRoot(targetAbsolutePath, gameRoot))
            {
                warnings.Add($"Skipped unsafe target outside game root: {entry.TargetRelativePath}");
                continue;
            }

            if (existingTargets.Contains(entry.TargetRelativePath))
            {
                warnings.Add($"Skipped target preserved from previous deployment: {entry.TargetRelativePath}");
                continue;
            }

            if (!File.Exists(entry.SourcePath))
            {
                warnings.Add($"Skipped missing source: {entry.SourcePath}");
                continue;
            }

            if (File.Exists(targetAbsolutePath))
            {
                warnings.Add($"Skipped existing unmanaged target: {entry.TargetRelativePath}");
                continue;
            }

            var targetDirectory = Path.GetDirectoryName(targetAbsolutePath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(entry.SourcePath, targetAbsolutePath, overwrite: false);
            copiedFiles++;

            var fileInfo = new FileInfo(targetAbsolutePath);
            records.Add(new DeployedFileRecord(
                entry.TargetRelativePath,
                targetAbsolutePath,
                entry.SourcePath,
                entry.OwningModId,
                fileInfo.Length,
                ComputeSha256(targetAbsolutePath)));
            existingTargets.Add(entry.TargetRelativePath);
        }

        SaveOrDeleteManifest(managerPaths, overlayPreview.ProfileId, overlayPreview.GameRootPath, records);

        return new ProfileDeployResult(
            overlayPreview.ProfileId,
            overlayPreview.GameRootPath,
            copiedFiles,
            cleanResult.DeletedFiles,
            cleanResult.PreservedRecords.Count,
            warnings);
    }

    public ProfileDeployResult Clean(ManagerPaths managerPaths, string profileId)
    {
        var cleanResult = CleanInternal(managerPaths, profileId);
        var manifest = LoadManifest(managerPaths, profileId);
        var gameRootPath = cleanResult.GameRootPath ?? manifest?.GameRootPath ?? string.Empty;

        SaveOrDeleteManifest(managerPaths, profileId, gameRootPath, cleanResult.PreservedRecords);

        return new ProfileDeployResult(
            profileId,
            gameRootPath,
            CopiedFiles: 0,
            cleanResult.DeletedFiles,
            cleanResult.PreservedRecords.Count,
            cleanResult.Warnings);
    }

    private CleanDeploymentResult CleanInternal(ManagerPaths managerPaths, string profileId)
    {
        var manifest = LoadManifest(managerPaths, profileId);
        if (manifest is null)
        {
            return new CleanDeploymentResult(null, DeletedFiles: 0, Array.Empty<DeployedFileRecord>(), Array.Empty<string>());
        }

        var warnings = new List<string>();
        var preservedRecords = new List<DeployedFileRecord>();
        var deletedFiles = 0;
        var gameRoot = EnsureTrailingSeparator(Path.GetFullPath(manifest.GameRootPath));
        if (!Directory.Exists(manifest.GameRootPath))
        {
            warnings.Add($"Game root is not available, deployment manifest was preserved: {manifest.GameRootPath}");
            return new CleanDeploymentResult(manifest.GameRootPath, deletedFiles, manifest.Files, warnings);
        }

        foreach (var record in manifest.Files)
        {
            var targetAbsolutePath = Path.GetFullPath(record.TargetAbsolutePath);
            if (!IsInsideRoot(targetAbsolutePath, gameRoot))
            {
                warnings.Add($"Preserved unsafe manifest target outside game root: {record.TargetRelativePath}");
                preservedRecords.Add(record);
                continue;
            }

            if (!File.Exists(targetAbsolutePath))
            {
                continue;
            }

            var currentHash = ComputeSha256(targetAbsolutePath);
            if (!currentHash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Preserved changed deployed file: {record.TargetRelativePath}");
                preservedRecords.Add(record);
                continue;
            }

            File.Delete(targetAbsolutePath);
            deletedFiles++;
            DeleteEmptyParentDirectories(Path.GetDirectoryName(targetAbsolutePath), gameRoot);
        }

        return new CleanDeploymentResult(manifest.GameRootPath, deletedFiles, preservedRecords, warnings);
    }

    private static void SaveOrDeleteManifest(
        ManagerPaths managerPaths,
        string profileId,
        string gameRootPath,
        IReadOnlyList<DeployedFileRecord> records)
    {
        var manifestPath = GetManifestPath(managerPaths, profileId);
        if (records.Count == 0)
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }

            return;
        }

        var manifest = new DeploymentManifest(
            profileId,
            gameRootPath,
            DateTimeOffset.UtcNow,
            records.OrderBy(record => record.TargetRelativePath, StringComparer.OrdinalIgnoreCase).ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static string GetManifestPath(ManagerPaths managerPaths, string profileId)
    {
        return Path.Combine(managerPaths.ProfilesPath, profileId, "deploy-manifest.json");
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsInsideRoot(string candidatePath, string rootPathWithSeparator)
    {
        return candidatePath.StartsWith(rootPathWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool PathsEqual(string firstPath, string secondPath)
    {
        return EnsureTrailingSeparator(Path.GetFullPath(firstPath))
            .Equals(EnsureTrailingSeparator(Path.GetFullPath(secondPath)), StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteEmptyParentDirectories(string? directoryPath, string gameRootPathWithSeparator)
    {
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            var fullDirectoryPath = Path.GetFullPath(directoryPath);
            var fullDirectoryPathWithSeparator = EnsureTrailingSeparator(fullDirectoryPath);
            if (fullDirectoryPathWithSeparator.Equals(gameRootPathWithSeparator, StringComparison.OrdinalIgnoreCase)
                || IsProtectedGameDirectory(fullDirectoryPathWithSeparator, gameRootPathWithSeparator)
                || !IsInsideRoot(fullDirectoryPathWithSeparator, gameRootPathWithSeparator))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(fullDirectoryPath).Any())
            {
                return;
            }

            Directory.Delete(fullDirectoryPath);
            directoryPath = Path.GetDirectoryName(fullDirectoryPath);
        }
    }

    private static bool IsProtectedGameDirectory(string directoryPathWithSeparator, string gameRootPathWithSeparator)
    {
        foreach (var relativePath in new[]
        {
            "BepInEx",
            Path.Combine("BepInEx", "plugins"),
            Path.Combine("BepInEx", "config"),
            Path.Combine("BepInEx", "patchers"),
            Path.Combine("BepInEx", "Translation"),
            "CasualtiesUnknown_Data"
        })
        {
            var protectedPath = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(gameRootPathWithSeparator, relativePath)));
            if (directoryPathWithSeparator.Equals(protectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record CleanDeploymentResult(
        string? GameRootPath,
        int DeletedFiles,
        IReadOnlyList<DeployedFileRecord> PreservedRecords,
        IReadOnlyList<string> Warnings);
}
