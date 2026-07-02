namespace UcuModManager.Core.BepInEx;

public sealed record BepInExRelease(string Version, Uri DownloadUri, string ArchiveFileName)
{
    public static BepInExRelease Current { get; } = new(
        "5.4.23.5",
        new Uri("https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"),
        "BepInEx_win_x64_5.4.23.5.zip");
}
