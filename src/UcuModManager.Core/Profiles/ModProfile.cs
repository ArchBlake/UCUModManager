namespace UcuModManager.Core.Profiles;

public sealed record ModProfile(
    string Id,
    string Name,
    IReadOnlyList<ProfileModEntry> Mods,
    string ProfileBepInExPath)
{
    public static ModProfile CreateDefault(string managerRootPath)
    {
        var id = "default";
        return new ModProfile(
            id,
            "Default",
            Array.Empty<ProfileModEntry>(),
            Path.Combine(managerRootPath, "profiles", id, "BepInEx"));
    }
}
