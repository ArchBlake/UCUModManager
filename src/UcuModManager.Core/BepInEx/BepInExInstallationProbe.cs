namespace UcuModManager.Core.BepInEx;

public sealed class BepInExInstallationProbe
{
    public static readonly string[] RequiredMarkers =
    {
        ".doorstop_version",
        "doorstop_config.ini",
        "winhttp.dll",
        Path.Combine("BepInEx", "core", "BepInEx.dll"),
        Path.Combine("BepInEx", "core", "BepInEx.Preloader.dll")
    };

    public BepInExInstallationState Probe(string gameRootPath)
    {
        var root = Path.GetFullPath(gameRootPath);
        var present = new List<string>();
        var missing = new List<string>();

        foreach (var marker in RequiredMarkers)
        {
            var markerPath = Path.Combine(root, marker);
            if (File.Exists(markerPath) || Directory.Exists(markerPath))
            {
                present.Add(marker);
            }
            else
            {
                missing.Add(marker);
            }
        }

        return new BepInExInstallationState(present.Count > 0, root, present, missing);
    }
}
