namespace UcuModManager.Core.Games;

public sealed record GameInstallation(string RootPath, string ExecutableName)
{
    public const string DefaultExecutableName = "CasualtiesUnknown.exe";

    public string ExecutablePath => Path.Combine(RootPath, ExecutableName);
    public string DataDirectoryPath => Path.Combine(RootPath, "CasualtiesUnknown_Data");
    public string BepInExDirectoryPath => Path.Combine(RootPath, "BepInEx");

    public static GameInstallation FromRootPath(string rootPath)
    {
        return new GameInstallation(Path.GetFullPath(rootPath), DefaultExecutableName);
    }
}
