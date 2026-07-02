namespace UcuModManager.Core.BepInEx;

public sealed record BepInExInstallationState(
    bool IsInstalled,
    string GameRootPath,
    IReadOnlyList<string> PresentMarkers,
    IReadOnlyList<string> MissingMarkers)
{
    public bool IsComplete => IsInstalled && MissingMarkers.Count == 0;
}
