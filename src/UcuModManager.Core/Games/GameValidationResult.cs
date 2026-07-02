namespace UcuModManager.Core.Games;

public sealed record GameValidationResult(
    bool IsValid,
    string GameRootPath,
    IReadOnlyList<string> MissingMarkers,
    IReadOnlyList<string> Warnings)
{
    public static GameValidationResult Valid(string gameRootPath, IReadOnlyList<string>? warnings = null)
    {
        return new GameValidationResult(true, gameRootPath, Array.Empty<string>(), warnings ?? Array.Empty<string>());
    }

    public static GameValidationResult Invalid(string gameRootPath, IReadOnlyList<string> missingMarkers, IReadOnlyList<string>? warnings = null)
    {
        return new GameValidationResult(false, gameRootPath, missingMarkers, warnings ?? Array.Empty<string>());
    }
}
