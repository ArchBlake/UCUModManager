namespace UcuModManager.Core.Profiles;

public sealed record ProfileModEntry(
    string ModId,
    bool IsEnabled,
    int Priority);
