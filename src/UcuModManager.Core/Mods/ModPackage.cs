namespace UcuModManager.Core.Mods;

public sealed record ModPackage(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<ModFileMapping> Files,
    IReadOnlyList<AssemblyIdentityInfo> Assemblies,
    IReadOnlyList<AssemblyReferenceInfo> AssemblyReferences,
    DateTimeOffset InstalledAt)
{
    public static ModPackage Create(
        string name,
        IReadOnlyList<ModFileMapping> files,
        string version = "unknown",
        IReadOnlyList<AssemblyIdentityInfo>? assemblies = null,
        IReadOnlyList<AssemblyReferenceInfo>? assemblyReferences = null)
    {
        var id = CreateStableId(name);
        return new ModPackage(
            id,
            name,
            version,
            files,
            assemblies ?? Array.Empty<AssemblyIdentityInfo>(),
            assemblyReferences ?? Array.Empty<AssemblyReferenceInfo>(),
            DateTimeOffset.UtcNow);
    }

    public static string CreateStableId(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        var normalized = new string(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return normalized.Trim('-');
    }
}
