using System.Text.Json;
using UcuModManager.Core.Mods;
using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Profiles;

public sealed class ProfileService
{
    private const string DefaultProfileId = "default";
    private const string ProfileFileName = "profile.json";

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        WriteIndented = true
    };

    public IReadOnlyList<ModProfile> LoadProfiles(ManagerPaths managerPaths, IReadOnlyList<ModLibraryEntry> libraryEntries)
    {
        Directory.CreateDirectory(managerPaths.ProfilesPath);

        var profiles = Directory.EnumerateDirectories(managerPaths.ProfilesPath)
            .Select(directory => new
            {
                DirectoryName = Path.GetFileName(directory),
                Profile = LoadProfile(Path.Combine(directory, ProfileFileName))
            })
            .Where(item => item.Profile is not null
                && IsSafeProfileId(item.Profile.Id)
                && item.Profile.Id.Equals(item.DirectoryName, StringComparison.OrdinalIgnoreCase))
            .Select(item => SynchronizeProfile(item.Profile!, managerPaths, libraryEntries))
            .ToList();

        if (!profiles.Any(profile => profile.Id.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            profiles.Add(SynchronizeProfile(ModProfile.CreateDefault(managerPaths.RootPath), managerPaths, libraryEntries));
        }

        foreach (var profile in profiles)
        {
            SaveProfile(managerPaths, profile);
        }

        return profiles
            .OrderBy(profile => profile.Id.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ModProfile LoadOrCreateDefaultProfile(ManagerPaths managerPaths, IReadOnlyList<ModLibraryEntry> libraryEntries)
    {
        var profilePath = GetProfilePath(managerPaths, DefaultProfileId);
        var profile = LoadProfile(profilePath) ?? ModProfile.CreateDefault(managerPaths.RootPath);
        var synchronized = SynchronizeProfile(profile, managerPaths, libraryEntries);
        SaveProfile(managerPaths, synchronized);
        return synchronized;
    }

    public ModProfile LoadOrCreateProfile(ManagerPaths managerPaths, string profileId, IReadOnlyList<ModLibraryEntry> libraryEntries)
    {
        if (string.IsNullOrWhiteSpace(profileId) || !IsSafeProfileId(profileId))
        {
            return LoadOrCreateDefaultProfile(managerPaths, libraryEntries);
        }

        var profile = LoadProfile(managerPaths, profileId);
        if (profile is null
            || !IsSafeProfileId(profile.Id)
            || !profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase))
        {
            return LoadOrCreateDefaultProfile(managerPaths, libraryEntries);
        }

        var synchronized = SynchronizeProfile(profile, managerPaths, libraryEntries);
        SaveProfile(managerPaths, synchronized);
        return synchronized;
    }

    public ModProfile CreateProfile(ManagerPaths managerPaths, string name, IReadOnlyList<ModLibraryEntry> libraryEntries)
    {
        var profileName = CleanProfileName(name, "New Profile");
        var profileId = CreateUniqueProfileId(managerPaths, profileName);
        var profile = new ModProfile(
            profileId,
            profileName,
            Array.Empty<ProfileModEntry>(),
            GetProfileBepInExPath(managerPaths, profileId));
        var synchronized = SynchronizeProfile(profile, managerPaths, libraryEntries);
        SaveProfile(managerPaths, synchronized);
        return synchronized;
    }

    public ModProfile DuplicateProfile(
        ManagerPaths managerPaths,
        string sourceProfileId,
        string name,
        IReadOnlyList<ModLibraryEntry> libraryEntries)
    {
        var sourceProfile = LoadOrCreateProfile(managerPaths, sourceProfileId, libraryEntries);
        var profileName = CleanProfileName(name, $"{sourceProfile.Name} Copy");
        var profileId = CreateUniqueProfileId(managerPaths, profileName);
        var duplicateProfile = sourceProfile with
        {
            Id = profileId,
            Name = profileName,
            ProfileBepInExPath = GetProfileBepInExPath(managerPaths, profileId)
        };
        var synchronized = SynchronizeProfile(duplicateProfile, managerPaths, libraryEntries);
        SaveProfile(managerPaths, synchronized);
        CopyProfileBepInExDirectory(managerPaths, sourceProfile.Id, synchronized.Id);
        SaveProfile(managerPaths, synchronized);
        return synchronized;
    }

    public ModProfile RenameProfile(
        ManagerPaths managerPaths,
        string profileId,
        string name,
        IReadOnlyList<ModLibraryEntry> libraryEntries)
    {
        var profile = LoadOrCreateProfile(managerPaths, profileId, libraryEntries);
        var renamed = SynchronizeProfile(
            profile with { Name = CleanProfileName(name, profile.Name) },
            managerPaths,
            libraryEntries);
        SaveProfile(managerPaths, renamed);
        return renamed;
    }

    public void DeleteProfile(ManagerPaths managerPaths, string profileId)
    {
        if (!IsSafeProfileId(profileId))
        {
            throw new InvalidOperationException("Profile id is not valid.");
        }

        if (profileId.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The default profile cannot be deleted.");
        }

        var profilesRootPath = EnsureTrailingSeparator(Path.GetFullPath(managerPaths.ProfilesPath));
        var profileDirectoryPath = Path.GetFullPath(GetProfileDirectory(managerPaths, profileId));
        if (!IsInsideRoot(EnsureTrailingSeparator(profileDirectoryPath), profilesRootPath))
        {
            throw new InvalidOperationException("Profile folder is outside the manager storage.");
        }

        if (Directory.Exists(profileDirectoryPath))
        {
            Directory.Delete(profileDirectoryPath, recursive: true);
        }
    }

    public void SaveProfile(ManagerPaths managerPaths, ModProfile profile)
    {
        if (!IsSafeProfileId(profile.Id))
        {
            throw new InvalidOperationException("Profile id is not valid.");
        }

        var profileDirectory = GetProfileDirectory(managerPaths, profile.Id);
        var profileToSave = profile with
        {
            Name = CleanProfileName(profile.Name, profile.Id),
            ProfileBepInExPath = GetProfileBepInExPath(managerPaths, profile.Id)
        };

        Directory.CreateDirectory(profileDirectory);
        EnsureProfileBepInExDirectories(profileDirectory);
        File.WriteAllText(GetProfilePath(managerPaths, profile.Id), JsonSerializer.Serialize(profileToSave, ProfileJsonOptions));
    }

    public ModProfile? LoadProfile(ManagerPaths managerPaths, string profileId)
    {
        return LoadProfile(GetProfilePath(managerPaths, profileId));
    }

    private static ModProfile SynchronizeProfile(ModProfile profile, ManagerPaths managerPaths, IReadOnlyList<ModLibraryEntry> libraryEntries)
    {
        var existingByModId = profile.Mods.ToDictionary(entry => entry.ModId, StringComparer.OrdinalIgnoreCase);
        var orderedModIds = profile.Mods.Count == 0
            ? BuildDependencyAwareOrder(libraryEntries)
            : profile.Mods
                .Where(entry => libraryEntries.Any(libraryEntry => libraryEntry.Mod.Id.Equals(entry.ModId, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(entry => entry.Priority)
                .Select(entry => entry.ModId)
                .Concat(BuildDependencyAwareOrder(libraryEntries)
                    .Where(modId => !existingByModId.ContainsKey(modId)))
                .ToArray();
        var synchronizedEntries = new List<ProfileModEntry>();

        for (var index = 0; index < orderedModIds.Count; index++)
        {
            var modId = orderedModIds[index];
            var isEnabled = !existingByModId.TryGetValue(modId, out var existingEntry) || existingEntry.IsEnabled;
            synchronizedEntries.Add(new ProfileModEntry(modId, isEnabled, index));
        }

        return profile with
        {
            Mods = synchronizedEntries,
            ProfileBepInExPath = GetProfileBepInExPath(managerPaths, profile.Id)
        };
    }

    private static IReadOnlyList<string> BuildDependencyAwareOrder(IReadOnlyList<ModLibraryEntry> libraryEntries)
    {
        var byModId = libraryEntries.ToDictionary(entry => entry.Mod.Id, StringComparer.OrdinalIgnoreCase);
        var providerByAssemblyName = libraryEntries
            .SelectMany(entry => entry.Mod.Assemblies.Select(assembly => new
            {
                AssemblyName = assembly.Name,
                ModId = entry.Mod.Id
            }))
            .GroupBy(entry => entry.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.ModId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        var dependencyEdges = libraryEntries.ToDictionary(
            entry => entry.Mod.Id,
            entry => entry.Dependencies
                .SelectMany(dependency => providerByAssemblyName.TryGetValue(dependency.AssemblyName, out var providers) ? providers : Array.Empty<string>())
                .Where(providerModId => !providerModId.Equals(entry.Mod.Id, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

        var ordered = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var modId in libraryEntries.Select(entry => entry.Mod.Id).Order(StringComparer.OrdinalIgnoreCase))
        {
            Visit(modId);
        }

        return ordered;

        void Visit(string modId)
        {
            if (visited.Contains(modId) || !byModId.ContainsKey(modId))
            {
                return;
            }

            if (!visiting.Add(modId))
            {
                return;
            }

            foreach (var dependencyModId in dependencyEdges[modId].Order(StringComparer.OrdinalIgnoreCase))
            {
                Visit(dependencyModId);
            }

            visiting.Remove(modId);
            visited.Add(modId);
            ordered.Add(modId);
        }
    }

    private static ModProfile? LoadProfile(string profilePath)
    {
        try
        {
            return File.Exists(profilePath)
                ? JsonSerializer.Deserialize<ModProfile>(File.ReadAllText(profilePath), ProfileJsonOptions)
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

    private static string GetProfileDirectory(ManagerPaths managerPaths, string profileId)
    {
        return Path.Combine(managerPaths.ProfilesPath, profileId);
    }

    private static string GetProfilePath(ManagerPaths managerPaths, string profileId)
    {
        return Path.Combine(GetProfileDirectory(managerPaths, profileId), ProfileFileName);
    }

    private static string GetProfileBepInExPath(ManagerPaths managerPaths, string profileId)
    {
        return Path.Combine(GetProfileDirectory(managerPaths, profileId), "BepInEx");
    }

    private static void EnsureProfileBepInExDirectories(string profileDirectory)
    {
        foreach (var relativePath in new[]
        {
            Path.Combine("BepInEx", "config"),
            Path.Combine("BepInEx", "plugins"),
            Path.Combine("BepInEx", "patchers"),
            Path.Combine("BepInEx", "Translation")
        })
        {
            Directory.CreateDirectory(Path.Combine(profileDirectory, relativePath));
        }
    }

    private static string CreateUniqueProfileId(ManagerPaths managerPaths, string profileName)
    {
        var baseId = ModPackage.CreateStableId(profileName);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "profile";
        }

        var candidate = baseId;
        var suffix = 2;
        while (Directory.Exists(GetProfileDirectory(managerPaths, candidate)))
        {
            candidate = $"{baseId}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string CleanProfileName(string name, string fallback)
    {
        var trimmed = name.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    private static bool IsSafeProfileId(string profileId)
    {
        return !string.IsNullOrWhiteSpace(profileId)
            && !profileId.Equals(".", StringComparison.Ordinal)
            && !profileId.Equals("..", StringComparison.Ordinal)
            && profileId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !profileId.Contains(Path.DirectorySeparatorChar)
            && !profileId.Contains(Path.AltDirectorySeparatorChar);
    }

    private static void CopyProfileBepInExDirectory(ManagerPaths managerPaths, string sourceProfileId, string targetProfileId)
    {
        var sourcePath = GetProfileBepInExPath(managerPaths, sourceProfileId);
        var targetPath = GetProfileBepInExPath(managerPaths, targetProfileId);
        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, directoryPath);
            Directory.CreateDirectory(Path.Combine(targetPath, relativePath));
        }

        foreach (var filePath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, filePath);
            var targetFilePath = Path.Combine(targetPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
            File.Copy(filePath, targetFilePath, overwrite: true);
        }
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
}
