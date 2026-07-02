namespace UcuModManager.Core.Mods;

public sealed record ModDependencyStatus(
    string AssemblyName,
    bool IsSatisfied,
    IReadOnlyList<string> ProviderModIds);
