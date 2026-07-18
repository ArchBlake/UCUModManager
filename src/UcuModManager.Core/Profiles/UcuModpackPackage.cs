namespace UcuModManager.Core.Profiles;

public sealed record UcuModpackPackage(
    int FormatVersion,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string ProfileName,
    IReadOnlyList<UcuModpackMod> Mods,
    string PackageKind = UcuModpackPackage.PackageKindRecipe)
{
    public const int CurrentFormatVersion = 1;
    public const string DefaultCreatedBy = "UCU Mod Manager";
    public const string PackageKindRecipe = "Recipe";
    public const string PackageKindPortable = "Portable";
}

public sealed record UcuModpackMod(
    string Name,
    bool IsEnabled,
    int Priority,
    string? GameDomain,
    int? NexusModId,
    int? FileId,
    string? Version,
    string? PageUrl,
    string? SourceArchiveFileName,
    string? DownloadUrl = null,
    string? EmbeddedArchiveFileName = null);
