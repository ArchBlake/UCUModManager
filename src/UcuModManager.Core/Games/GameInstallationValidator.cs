namespace UcuModManager.Core.Games;

public sealed class GameInstallationValidator
{
    private static readonly string[] RequiredRelativePaths =
    {
        GameInstallation.DefaultExecutableName,
        "UnityPlayer.dll",
        "CasualtiesUnknown_Data",
        Path.Combine("CasualtiesUnknown_Data", "Managed"),
        "MonoBleedingEdge"
    };

    public GameValidationResult Validate(string gameRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath))
        {
            return GameValidationResult.Invalid(gameRootPath, new[] { "Game folder path is empty." });
        }

        var fullPath = Path.GetFullPath(gameRootPath);
        if (!Directory.Exists(fullPath))
        {
            return GameValidationResult.Invalid(fullPath, new[] { "Game folder does not exist." });
        }

        var missingMarkers = RequiredRelativePaths
            .Where(relativePath => !File.Exists(Path.Combine(fullPath, relativePath)) && !Directory.Exists(Path.Combine(fullPath, relativePath)))
            .ToArray();

        if (missingMarkers.Length > 0)
        {
            return GameValidationResult.Invalid(fullPath, missingMarkers);
        }

        var warnings = new List<string>();
        var appInfoPath = Path.Combine(fullPath, "CasualtiesUnknown_Data", "app.info");
        if (!File.Exists(appInfoPath))
        {
            warnings.Add("Unity app.info was not found; this may be a repacked or incomplete game folder.");
        }

        return GameValidationResult.Valid(fullPath, warnings);
    }
}
