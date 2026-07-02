namespace UcuModManager.Core.Storage;

public sealed record ManagerPaths(string RootPath)
{
    public string SettingsPath => Path.Combine(RootPath, "settings.json");
    public string ModsPath => Path.Combine(RootPath, "mods");
    public string ProfilesPath => Path.Combine(RootPath, "profiles");
    public string DownloadsPath => Path.Combine(RootPath, "downloads");
    public string CachePath => Path.Combine(RootPath, "cache");

    public static ManagerPaths FromApplicationDirectory(string? applicationDirectoryPath = null)
    {
        return new ManagerPaths(Path.GetFullPath(applicationDirectoryPath ?? AppContext.BaseDirectory));
    }
}
