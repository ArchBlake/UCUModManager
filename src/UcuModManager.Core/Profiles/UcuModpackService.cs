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
        return Validate(package, ".UCU");
    }

    public UcuModpackPackage Validate(UcuModpackPackage? package, string packageLabel)
    {
        if (package is null)
        {
            throw new InvalidOperationException($"The {packageLabel} manifest is empty or invalid.");
        }

        if (package.FormatVersion != UcuModpackPackage.CurrentFormatVersion)
        {
            throw new InvalidOperationException($"Unsupported {packageLabel} format version: {package.FormatVersion}.");
        }

        if (string.IsNullOrWhiteSpace(package.ProfileName))
        {
            throw new InvalidOperationException($"The {packageLabel} file does not contain a profile name.");
        }

        if (package.Mods is null || package.Mods.Count == 0)
        {
            throw new InvalidOperationException($"The {packageLabel} file does not contain any mods. Export the profile again with the updated manager.");
        }

        if (package.Mods.Any(mod => mod is null))
        {
            throw new InvalidOperationException($"The {packageLabel} file contains an invalid mod entry.");
        }

        var duplicatePriority = package.Mods
            .GroupBy(mod => mod.Priority)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePriority is not null)
        {
            throw new InvalidOperationException(
                $"The {packageLabel} file contains duplicate load-order priority {duplicatePriority.Key}.");
        }

        var mods = package.Mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.Name))
            .OrderBy(mod => mod.Priority)
            .ToArray();
        if (mods.Length == 0)
        {
            throw new InvalidOperationException($"The {packageLabel} file does not contain valid mod entries. Export the profile again with the updated manager.");
        }

        if (mods.Length != package.Mods.Count)
        {
            throw new InvalidOperationException($"The {packageLabel} file contains a mod without a name.");
        }

        if (string.Equals(
                package.PackageKind,
                UcuModpackPackage.PackageKindPortable,
                StringComparison.OrdinalIgnoreCase))
        {
            var invalidEmbeddedName = mods.FirstOrDefault(mod =>
                string.IsNullOrWhiteSpace(mod.EmbeddedArchiveFileName)
                || !Path.GetFileName(mod.EmbeddedArchiveFileName).Equals(
                    mod.EmbeddedArchiveFileName,
                    StringComparison.Ordinal));
            if (invalidEmbeddedName is not null)
            {
                throw new InvalidOperationException(
                    $"The {packageLabel} file contains an invalid embedded archive name for {invalidEmbeddedName.Name}.");
            }
        }

        return package with
        {
            Mods = mods
        };
    }
}
