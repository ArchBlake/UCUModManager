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

            return Normalize(JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(managerPaths.SettingsPath), JsonOptions)
                ?? ManagerSettings.Empty);
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

    private static ManagerSettings Normalize(ManagerSettings settings)
    {
        return ShouldResetNexusGameDomain(settings.NexusGameDomain)
            ? settings with { NexusGameDomain = ManagerSettings.Empty.NexusGameDomain }
            : settings;
    }

    private static bool ShouldResetNexusGameDomain(string? domain)
    {
        return string.IsNullOrWhiteSpace(domain)
            || domain.Equals("casualtiesunknowndemo", StringComparison.OrdinalIgnoreCase)
            || domain.Equals("casualtiesunknown", StringComparison.OrdinalIgnoreCase);
    }
}
