using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using UcuModManager.Core.Archives;

namespace UcuModManager.Core.Mods;

public sealed class ModArchiveAnalyzer
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public ModImportPlan AnalyzeZip(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Mod archive was not found.", archivePath);
        }

        using var archive = ZipFile.OpenRead(archivePath);
        ZipArchiveSafety.Validate(archive);
        var entries = archive.Entries
            .Select(entry => new ArchiveEntryInfo(entry.FullName, entry.Length, IsDirectory(entry.FullName)))
            .ToArray();

        var fileEntries = entries.Where(entry => !entry.IsDirectory).ToArray();
        var strippedRoot = DetectStrippableRoot(fileEntries);
        var suggestedName = SuggestModName(archivePath);
        var suggestedVersion = ModSourceDetector.DetectVersion(archivePath);
        var source = ModSourceDetector.Detect(archivePath, suggestedVersion);
        var pluginCompanionIds = DetectPluginCompanionIds(fileEntries, strippedRoot);
        var mappings = new List<ModFileMapping>();
        var assemblies = new List<AssemblyIdentityInfo>();
        var assemblyReferences = new List<AssemblyReferenceInfo>();
        var ignored = new List<IgnoredArchiveEntry>();
        var warnings = new List<string>();

        foreach (var entry in archive.Entries.Where(entry => !IsDirectory(entry.FullName)))
        {
            var normalizedSource = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (!Path.GetExtension(normalizedSource).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assemblyInfo = TryReadAssemblyMetadata(entry, normalizedSource);
            if (assemblyInfo is null)
            {
                continue;
            }

            assemblies.Add(assemblyInfo.Value.Identity);
            assemblyReferences.AddRange(assemblyInfo.Value.References);
        }

        foreach (var entry in fileEntries)
        {
            var normalizedSource = entry.NormalizedPath;
            var installPath = StripRoot(normalizedSource, strippedRoot);

            if (string.IsNullOrWhiteSpace(installPath))
            {
                ignored.Add(new IgnoredArchiveEntry(normalizedSource, "Empty path after root detection."));
                continue;
            }

            if (IsRuntimeGeneratedPath(installPath))
            {
                ignored.Add(new IgnoredArchiveEntry(normalizedSource, "Runtime-generated BepInEx file; not imported as mod payload."));
                continue;
            }

            var mapping = TryMapFile(normalizedSource, installPath, suggestedName, pluginCompanionIds, entry.UncompressedSize, warnings);
            if (mapping is null)
            {
                ignored.Add(new IgnoredArchiveEntry(normalizedSource, "Unknown target path; manual review is required."));
                continue;
            }

            mappings.Add(mapping);
        }

        if (mappings.Count == 0)
        {
            warnings.Add("No installable mod files were detected in this archive.");
        }

        if (strippedRoot is not null)
        {
            warnings.Add($"Archive root '{strippedRoot}' will be stripped during import.");
        }

        var providedAssemblyNames = assemblies
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var externalAssemblyReferences = assemblyReferences
            .Where(reference => !reference.IsKnownGameOrFrameworkReference)
            .Where(reference => !providedAssemblyNames.Contains(reference.Name))
            .Select(reference => reference.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (externalAssemblyReferences.Length > 0)
        {
            warnings.Add($"Potential external assembly references detected: {string.Join(", ", externalAssemblyReferences)}.");
        }

        return new ModImportPlan(
            archivePath,
            suggestedName,
            source?.FileVersion ?? suggestedVersion,
            source,
            strippedRoot,
            mappings,
            assemblies,
            assemblyReferences,
            ignored,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static ModFileMapping? TryMapFile(
        string sourcePath,
        string installPath,
        string modName,
        IReadOnlySet<string> pluginCompanionIds,
        long size,
        List<string> warnings)
    {
        var path = installPath.Replace('\\', '/').TrimStart('/');
        var lower = path.ToLowerInvariant();
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);

        if (lower.Equals("steam_appid.txt", StringComparison.Ordinal))
        {
            warnings.Add("This mod includes steam_appid.txt in the game root. It will be virtualized with the active profile instead of copied permanently.");
            return new ModFileMapping(sourcePath, path, ModTargetKind.GameRootContent, size);
        }

        if (IsRootDocumentation(path))
        {
            return new ModFileMapping(sourcePath, path, ModTargetKind.Documentation, size, IsEnabledByDefault: false);
        }

        if (lower.StartsWith("bepinex/plugins/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, path, ModTargetKind.BepInExPlugin, size);
        }

        if (lower.StartsWith("bepinex/config/", StringComparison.Ordinal))
        {
            if (IsBepInExProfileConfig(path))
            {
                return new ModFileMapping(sourcePath, path, ModTargetKind.BepInExProfileConfig, size);
            }

            return new ModFileMapping(sourcePath, path, ModTargetKind.BepInExConfig, size);
        }

        if (lower.StartsWith("bepinex/patchers/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, path, ModTargetKind.BepInExPatcher, size);
        }

        if (lower.StartsWith("bepinex/translation/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, path, ModTargetKind.BepInExTranslation, size);
        }

        if (lower.StartsWith("bepinex/core/", StringComparison.Ordinal))
        {
            warnings.Add("Archive contains BepInEx core files. Core files should normally come from the managed BepInEx installer, not from a mod.");
            return new ModFileMapping(sourcePath, path, ModTargetKind.BepInExOther, size, IsEnabledByDefault: false);
        }

        if (lower.StartsWith("bepinex/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, path, ModTargetKind.BepInExOther, size);
        }

        if (lower.StartsWith("plugins/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, $"BepInEx/{path}", ModTargetKind.BepInExPlugin, size);
        }

        if (lower.StartsWith("config/", StringComparison.Ordinal))
        {
            if (Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                return new ModFileMapping(sourcePath, $"BepInEx/{path}", ModTargetKind.BepInExProfileConfig, size);
            }

            return new ModFileMapping(sourcePath, $"BepInEx/{path}", ModTargetKind.BepInExConfig, size);
        }

        if (lower.StartsWith("patchers/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, $"BepInEx/{path}", ModTargetKind.BepInExPatcher, size);
        }

        if (lower.StartsWith("translation/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, $"BepInEx/{path}", ModTargetKind.BepInExTranslation, size);
        }

        if (lower.StartsWith("managed/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, $"CasualtiesUnknown_Data/{path}", ModTargetKind.GameDataContent, size);
        }

        if (fileName.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Archive contains Assembly-CSharp.dll. It will be virtualized into CasualtiesUnknown_Data/Managed because this is a game assembly replacement, not a BepInEx plugin.");
            return new ModFileMapping(sourcePath, "CasualtiesUnknown_Data/Managed/Assembly-CSharp.dll", ModTargetKind.GameDataContent, size);
        }

        if (IsRootFile(path) && extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new ModFileMapping(sourcePath, $"BepInEx/plugins/{fileName}", ModTargetKind.BepInExPlugin, size);
        }

        if (lower.StartsWith("casualtiesunknown_data/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, path, ModTargetKind.GameDataContent, size);
        }

        if (lower.StartsWith("lang/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, $"CasualtiesUnknown_Data/{path}", ModTargetKind.GameDataContent, size);
        }

        if (lower.StartsWith("custommusic/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, $"CasualtiesUnknown_Data/{path}", ModTargetKind.GameDataContent, size);
        }

        if (lower.StartsWith("customsprites/", StringComparison.Ordinal))
        {
            return new ModFileMapping(sourcePath, $"BepInEx/plugins/{path}", ModTargetKind.BepInExPlugin, size);
        }

        var knownPluginContentTarget = TryMapKnownPluginContent(path);
        if (knownPluginContentTarget is not null)
        {
            return new ModFileMapping(sourcePath, knownPluginContentTarget, ModTargetKind.BepInExPlugin, size);
        }

        if (IsCompanionContentFolder(path, modName, pluginCompanionIds))
        {
            return new ModFileMapping(sourcePath, $"BepInEx/plugins/{path}", ModTargetKind.BepInExPlugin, size);
        }

        if (IsLikelyPluginContent(path, pluginCompanionIds))
        {
            return new ModFileMapping(sourcePath, $"BepInEx/plugins/{path}", ModTargetKind.BepInExPlugin, size);
        }

        return null;
    }

    private static (AssemblyIdentityInfo Identity, IReadOnlyList<AssemblyReferenceInfo> References)? TryReadAssemblyMetadata(ZipArchiveEntry entry, string sourcePath)
    {
        try
        {
            using var source = entry.Open();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            memory.Position = 0;

            using var peReader = new PEReader(memory);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var metadata = peReader.GetMetadataReader();
            var definition = metadata.GetAssemblyDefinition();
            var identity = new AssemblyIdentityInfo(
                sourcePath,
                metadata.GetString(definition.Name),
                definition.Version);

            var references = metadata.AssemblyReferences
                .Select(handle =>
                {
                    var reference = metadata.GetAssemblyReference(handle);
                    var name = metadata.GetString(reference.Name);
                    return new AssemblyReferenceInfo(sourcePath, name, reference.Version, AssemblyReferenceClassifier.IsKnownGameOrFrameworkAssembly(name));
                })
                .ToArray();

            return (identity, references);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? DetectStrippableRoot(IReadOnlyList<ArchiveEntryInfo> entries)
    {
        var candidates = entries
            .Select(entry => entry.NormalizedPath.Split('/'))
            .Where(parts => parts.Length > 1)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Root = group.Key,
                Score = group.Count(parts => LooksLikeInstallRoot(string.Join('/', parts.Skip(1))))
            })
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();

        return candidates?.Score > 0 && !IsKnownTopLevel(candidates.Root) ? candidates.Root : null;
    }

    private static bool LooksLikeInstallRoot(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.StartsWith("bepinex/", StringComparison.Ordinal)
            || lower.StartsWith("plugins/", StringComparison.Ordinal)
            || lower.StartsWith("config/", StringComparison.Ordinal)
            || lower.StartsWith("patchers/", StringComparison.Ordinal)
            || lower.StartsWith("customsprites/", StringComparison.Ordinal)
            || lower.StartsWith("casualtiesunknown_data/", StringComparison.Ordinal)
            || lower.Equals("steam_appid.txt", StringComparison.Ordinal)
            || lower.EndsWith(".dll", StringComparison.Ordinal);
    }

    private static bool IsKnownTopLevel(string root)
    {
        return new[]
        {
            "BepInEx",
            "plugins",
            "config",
            "patchers",
            "Translation",
            "CustomSprites",
            "CasualtiesUnknown_Data",
            "Lang",
            "custommusic"
        }.Contains(root, PathComparer);
    }

    private static string StripRoot(string sourcePath, string? root)
    {
        if (root is null)
        {
            return sourcePath;
        }

        var prefix = root.TrimEnd('/') + "/";
        return sourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? sourcePath[prefix.Length..]
            : sourcePath;
    }

    private static bool IsDirectory(string archivePath)
    {
        return archivePath.EndsWith("/", StringComparison.Ordinal) || archivePath.EndsWith("\\", StringComparison.Ordinal);
    }

    private static bool IsRootFile(string path)
    {
        return !path.Contains('/', StringComparison.Ordinal) && !path.Contains('\\', StringComparison.Ordinal);
    }

    private static bool IsRootDocumentation(string path)
    {
        if (!IsRootFile(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBepInExProfileConfig(string path)
    {
        return path.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeGeneratedPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("BepInEx/cache/", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("BepInEx/LogOutput.log", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> DetectPluginCompanionIds(IReadOnlyList<ArchiveEntryInfo> entries, string? strippedRoot)
    {
        return entries
            .Select(entry => StripRoot(entry.NormalizedPath, strippedRoot).Replace('\\', '/').TrimStart('/'))
            .Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => ModPackage.CreateStableId(Path.GetFileNameWithoutExtension(path)))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsCompanionContentFolder(string path, string modName, IReadOnlySet<string> pluginCompanionIds)
    {
        var slashIndex = path.IndexOf('/');
        if (slashIndex <= 0)
        {
            return false;
        }

        var topLevelFolder = path[..slashIndex];
        var folderId = ModPackage.CreateStableId(topLevelFolder);
        if (folderId.Equals(ModPackage.CreateStableId(modName), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return pluginCompanionIds.Any(pluginId =>
            folderId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)
            || pluginId.StartsWith(folderId + "-", StringComparison.OrdinalIgnoreCase)
            || folderId.StartsWith(pluginId + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryMapKnownPluginContent(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var topLevel = GetTopLevelSegment(normalized);
        if (string.IsNullOrWhiteSpace(topLevel))
        {
            return null;
        }

        if (topLevel.Equals("recipes", StringComparison.OrdinalIgnoreCase))
        {
            return $"BepInEx/plugins/CraftingFramework/{normalized}";
        }

        return IsKnownPluginContentTopLevel(topLevel)
            ? $"BepInEx/plugins/{normalized}"
            : null;
    }

    private static bool IsLikelyPluginContent(string path, IReadOnlySet<string> pluginCompanionIds)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var topLevel = GetTopLevelSegment(normalized);
        if (string.IsNullOrWhiteSpace(topLevel)
            || IsKnownNonPluginTopLevel(topLevel)
            || IsLocalizationTopLevel(topLevel)
            || IsDocumentationTopLevel(topLevel))
        {
            return false;
        }

        return IsRootFile(normalized)
            ? pluginCompanionIds.Count > 0 && IsLoosePluginCompanionFile(normalized)
            : pluginCompanionIds.Count > 0 || IsLikelyStandalonePluginContentFolder(topLevel);
    }

    private static string GetTopLevelSegment(string path)
    {
        var slashIndex = path.IndexOf('/');
        return slashIndex < 0 ? path : path[..slashIndex];
    }

    private static bool IsLoosePluginCompanionFile(string path)
    {
        var fileName = Path.GetFileName(path);
        if (IsPackageMetadataFile(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return new[]
        {
            ".asset",
            ".assets",
            ".bank",
            ".bin",
            ".bundle",
            ".bytes",
            ".csv",
            ".dat",
            ".json",
            ".ogg",
            ".png",
            ".wav",
            ".xml"
        }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsPackageMetadataFile(string fileName)
    {
        return new[]
        {
            "icon.png",
            "manifest.json",
            "package.json",
            "thunderstore.toml"
        }.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsKnownNonPluginTopLevel(string topLevel)
    {
        return new[]
        {
            "BepInEx",
            "CasualtiesUnknown.exe",
            "CasualtiesUnknown_Data",
            "Data",
            "Managed",
            "MonoBleedingEdge",
            "config",
            "custommusic",
            "doorstop_config.ini",
            "patchers",
            "plugins",
            "steam_appid.txt",
            "winhttp.dll"
        }.Contains(topLevel, PathComparer);
    }

    private static bool IsKnownPluginContentTopLevel(string topLevel)
    {
        return new[]
        {
            "Audio",
            "AudioPacks",
            "ChangeSkin",
            "CraftingFramework",
            "CustomSprites",
            "Items",
            "NewClothing",
            "NewCloting",
            "NewFirearms",
            "NewGun",
            "Pets",
            "ResourcePack",
            "Resources",
            "Sounds"
        }.Contains(topLevel, PathComparer);
    }

    private static bool IsLikelyStandalonePluginContentFolder(string topLevel)
    {
        return !IsPackageMetadataFile(topLevel)
            && !IsKnownNonPluginTopLevel(topLevel)
            && !IsLocalizationTopLevel(topLevel)
            && !IsDocumentationTopLevel(topLevel);
    }

    private static bool IsLocalizationTopLevel(string topLevel)
    {
        return new[]
        {
            "i18n",
            "lang",
            "langs",
            "language",
            "languages",
            "locale",
            "locales",
            "localisation",
            "localisations",
            "localization",
            "localizations",
            "translation",
            "translations"
        }.Contains(ModPackage.CreateStableId(topLevel), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDocumentationTopLevel(string topLevel)
    {
        return new[]
        {
            "changelog",
            "changelogs",
            "doc",
            "docs",
            "documentation",
            "license",
            "licenses",
            "readme",
            "readmes"
        }.Contains(ModPackage.CreateStableId(topLevel), StringComparer.OrdinalIgnoreCase);
    }

    private static string SuggestModName(string archivePath)
    {
        var originalName = Path.GetFileNameWithoutExtension(archivePath);
        var name = Regex.Replace(originalName, @"(?i)[ _-]\d+[ _-]v?\d+(?:[.-]\d+)*(?:-[A-Za-z][A-Za-z0-9.-]*)?[ _-][A-Za-z0-9]{8,}$", string.Empty);
        name = Regex.Replace(name, @"(?i)-\d+-v?\d+(?:-\d+)+(?:-for-\d+(?:-\d+)*)?-\d+$", string.Empty);
        name = Regex.Replace(name, @"(?i)[_-]?v?\d+(?:\.\d+)+(?:[_-].*)?$", string.Empty);
        name = Regex.Replace(name, @"_\d+_[A-Za-z0-9]+$", string.Empty);
        name = Regex.Replace(name, @"-\d{2,}(?:-\d+)+$", string.Empty);
        name = name.Replace('_', ' ').Trim(' ', '.', '-', '_');

        return string.IsNullOrWhiteSpace(name) ? originalName : name;
    }

}
