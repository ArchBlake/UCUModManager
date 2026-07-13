using UcuModManager.Core.Storage;

namespace UcuModManager.Core.Tests;

public sealed class ManagerSettingsServiceTests
{
    [Fact]
    public void LoadAndSave_RemovesLegacyUserOAuthConfiguration()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "ucu-settings-test-" + Guid.NewGuid().ToString("N"));
        var managerPaths = new ManagerPaths(rootPath);
        Directory.CreateDirectory(rootPath);
        try
        {
            File.WriteAllText(managerPaths.SettingsPath, """
                {
                  "GameRootPath": "C:\\Games\\Casualties Unknown Demo",
                  "ActiveProfileId": "default",
                  "BepInExVersion": "5.4.23.5",
                  "UseProfileSpecificBepInEx": true,
                  "NexusGameDomain": "scavprototype",
                  "AutoLinkNexusOnStartup": true,
                  "NexusOAuthClientId": "legacy-user-value",
                  "NexusOAuthRedirectUri": "http://127.0.0.1:17142/ucu-modmanager/oauth/callback"
                }
                """);
            var service = new ManagerSettingsService();

            var settings = service.Load(managerPaths);
            service.Save(managerPaths, settings);
            var savedJson = File.ReadAllText(managerPaths.SettingsPath);

            Assert.Equal("scavprototype", settings.NexusGameDomain);
            Assert.DoesNotContain("NexusOAuthClientId", savedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("NexusOAuthRedirectUri", savedJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
