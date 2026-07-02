using System.Text.Json;

namespace UcuModManager.Core.Profiles;

public sealed class UcuModpackService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public void Save(string filePath, UcuModpackPackage package)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(package, JsonOptions));
    }

    public UcuModpackPackage Load(string filePath)
    {
        var package = JsonSerializer.Deserialize<UcuModpackPackage>(File.ReadAllText(filePath), JsonOptions)
            ?? throw new InvalidOperationException("The .UCU file is empty or invalid.");
        if (package.FormatVersion != UcuModpackPackage.CurrentFormatVersion)
        {
            throw new InvalidOperationException($"Unsupported .UCU format version: {package.FormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(package.ProfileName))
        {
            throw new InvalidOperationException("The .UCU file does not contain a profile name.");
        }

        if (package.Mods is null || package.Mods.Count == 0)
        {
            throw new InvalidOperationException("The .UCU file does not contain any mods. Export the profile again with the updated manager.");
        }

        var mods = package.Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.Name))
            .OrderBy(mod => mod.Priority)
            .ToArray();
        if (mods.Length == 0)
        {
            throw new InvalidOperationException("The .UCU file does not contain valid mod entries. Export the profile again with the updated manager.");
        }

        return package with
        {
            Mods = mods
        };
    }
}
