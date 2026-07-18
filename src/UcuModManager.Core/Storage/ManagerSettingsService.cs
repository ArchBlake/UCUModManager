using System.Text.Json;

namespace UcuModManager.Core.Storage;

public sealed class ManagerSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ManagerSettings Load(ManagerPaths managerPaths)
    {
        try
        {
            if (!File.Exists(managerPaths.SettingsPath))
            {
                return ManagerSettings.Empty;
            }

            var json = File.ReadAllText(managerPaths.SettingsPath);
            return Normalize(JsonSerializer.Deserialize<ManagerSettings>(json, JsonOptions)
                ?? ManagerSettings.Empty, json);
        }
        catch (JsonException)
        {
            return ManagerSettings.Empty;
        }
        catch (IOException)
        {
            return ManagerSettings.Empty;
        }
    }

    public void Save(ManagerPaths managerPaths, ManagerSettings settings)
    {
        Directory.CreateDirectory(managerPaths.RootPath);
        File.WriteAllText(managerPaths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static ManagerSettings Normalize(ManagerSettings settings, string? json = null)
    {
        var normalized = ShouldResetNexusGameDomain(settings.NexusGameDomain)
            ? settings with { NexusGameDomain = ManagerSettings.Empty.NexusGameDomain }
            : settings;
        if (json is null)
        {
            return normalized;
        }

        if (!HasJsonProperty(json, nameof(ManagerSettings.AutoLinkNexusOnStartup)))
        {
            normalized = normalized with { AutoLinkNexusOnStartup = true };
        }

        if (!HasJsonProperty(json, nameof(ManagerSettings.VirtualizationEnabled)))
        {
            normalized = normalized with { VirtualizationEnabled = true };
        }

        if (!HasJsonProperty(json, nameof(ManagerSettings.VirtualizationIntroShown)))
        {
            normalized = normalized with { VirtualizationIntroShown = false };
        }

        if (!HasJsonProperty(json, nameof(ManagerSettings.NexusCatalogCompactMode)))
        {
            normalized = normalized with { NexusCatalogCompactMode = false };
        }

        return normalized;
    }

    private static bool HasJsonProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject()
                .Any(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldResetNexusGameDomain(string? domain)
    {
        return string.IsNullOrWhiteSpace(domain)
            || domain.Equals("casualtiesunknowndemo", StringComparison.OrdinalIgnoreCase)
            || domain.Equals("casualtiesunknown", StringComparison.OrdinalIgnoreCase);
    }
}
