using System.Text.Json;
using System.Text.Json.Serialization;
using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Mods;

public sealed class ModLibraryService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<ModLibraryEntry> LoadLibrary(ManagerPaths managerPaths)
    {
        if (!Directory.Exists(managerPaths.ModsPath))
        {
            return Array.Empty<ModLibraryEntry>();
        }

        var loadedManifests = Directory.EnumerateFiles(managerPaths.ModsPath, "manifest.json", SearchOption.AllDirectories)
            .Select(LoadManifest)
            .Where(entry => entry.Manifest is not null)
            .Select(entry => (Manifest: entry.Manifest!, entry.ModDirectoryPath, entry.ManifestPath))
            .OrderBy(entry => entry.Manifest.Mod.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var assemblyProviders = loadedManifests
            .SelectMany(entry => entry.Manifest.Mod.Assemblies.Select(assembly => new
            {
                assembly.Name,
                ModId = entry.Manifest.Mod.Id
            }))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(entry => entry.ModId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return loadedManifests
            .Select(entry => new ModLibraryEntry(
                entry.Manifest,
                entry.ModDirectoryPath,
                entry.ManifestPath,
                BuildDependencyStatuses(entry.Manifest.Mod, assemblyProviders)))
            .ToArray();
    }

    public void SaveManifest(string manifestPath, ModManifest manifest)
    {
        var directoryPath = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    private static (ModManifest? Manifest, string ModDirectoryPath, string ManifestPath) LoadManifest(string manifestPath)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions);
            return (NormalizeManifest(manifest), Path.GetDirectoryName(manifestPath) ?? string.Empty, manifestPath);
        }
        catch (JsonException)
        {
            return (null, Path.GetDirectoryName(manifestPath) ?? string.Empty, manifestPath);
        }
        catch (IOException)
        {
            return (null, Path.GetDirectoryName(manifestPath) ?? string.Empty, manifestPath);
        }
    }

    private static ModManifest? NormalizeManifest(ModManifest? manifest)
    {
        if (manifest is null)
        {
            return null;
        }

        var detectedVersion = ModSourceDetector.DetectVersion(manifest.SourceArchiveFileName);
        var detectedSource = ModSourceDetector.Detect(manifest.SourceArchiveFileName, detectedVersion);
        var source = MergeSource(manifest.Source, detectedSource);
        var archiveVersion = source?.FileVersion ?? detectedVersion;
        var version = !string.IsNullOrWhiteSpace(archiveVersion)
            ? archiveVersion
            : string.IsNullOrWhiteSpace(manifest.Mod.Version) || manifest.Mod.Version.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                ? "unknown"
                : manifest.Mod.Version;

        return manifest with
        {
            Mod = manifest.Mod with { Version = version },
            Source = source
        };
    }

    private static ModSourceInfo? MergeSource(ModSourceInfo? existingSource, ModSourceInfo? detectedSource)
    {
        if (existingSource is null)
        {
            return detectedSource;
        }

        if (detectedSource is null
            || !existingSource.Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase)
            || !detectedSource.Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase))
        {
            return existingSource;
        }

        return existingSource with
        {
            GameDomain = string.IsNullOrWhiteSpace(existingSource.GameDomain)
                ? detectedSource.GameDomain
                : existingSource.GameDomain,
            ModId = ShouldPreferDetectedModId(existingSource, detectedSource)
                ? detectedSource.ModId
                : existingSource.ModId,
            FileId = existingSource.FileId ?? detectedSource.FileId,
            FileVersion = detectedSource.FileVersion ?? existingSource.FileVersion,
            FileTimestamp = detectedSource.FileTimestamp ?? existingSource.FileTimestamp,
            SourceArchiveFileName = string.IsNullOrWhiteSpace(existingSource.SourceArchiveFileName)
                ? detectedSource.SourceArchiveFileName
                : existingSource.SourceArchiveFileName
        };
    }

    private static bool ShouldPreferDetectedModId(ModSourceInfo existingSource, ModSourceInfo detectedSource)
    {
        if (detectedSource.ModId is null)
        {
            return false;
        }

        if (existingSource.ModId is null)
        {
            return true;
        }

        return existingSource.ModId != detectedSource.ModId
            && !string.Equals(existingSource.FileVersion, detectedSource.FileVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ModDependencyStatus> BuildDependencyStatuses(
        ModPackage mod,
        IReadOnlyDictionary<string, IReadOnlyList<string>> assemblyProviders)
    {
        var ownAssemblyNames = mod.Assemblies
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return mod.AssemblyReferences
            .Where(reference => !reference.IsKnownGameOrFrameworkReference)
            .Where(reference => !ownAssemblyNames.Contains(reference.Name))
            .Select(reference => reference.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(referenceName =>
            {
                var providers = assemblyProviders.TryGetValue(referenceName, out var providerModIds)
                    ? providerModIds.Where(modId => !modId.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)).ToArray()
                    : Array.Empty<string>();

                return new ModDependencyStatus(referenceName, providers.Length > 0, providers);
            })
            .ToArray();
    }
}
