using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Mods;

public sealed class ModRemovalService
{
    public void RemoveInstalledMod(ManagerPaths managerPaths, string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)
            || modId.Contains(Path.DirectorySeparatorChar)
            || modId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("Invalid mod id.");
        }

        var modsRootPath = EnsureTrailingSeparator(Path.GetFullPath(managerPaths.ModsPath));
        var modDirectoryPath = Path.GetFullPath(Path.Combine(managerPaths.ModsPath, modId));
        if (!modDirectoryPath.StartsWith(modsRootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mod directory is outside manager storage.");
        }

        if (!Directory.Exists(modDirectoryPath))
        {
            throw new DirectoryNotFoundException($"Installed mod was not found: {modId}");
        }

        Directory.Delete(modDirectoryPath, recursive: true);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
