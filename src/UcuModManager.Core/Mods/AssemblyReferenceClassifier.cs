namespace UcuModManager.Core.Mods;

public static class AssemblyReferenceClassifier
{
    public static bool IsKnownGameOrFrameworkAssembly(string assemblyName)
    {
        if (assemblyName.StartsWith("System", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("MonoMod", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("NAudio", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return new[]
        {
            "mscorlib",
            "netstandard",
            "BepInEx",
            "0Harmony",
            "0Harmony20",
            "HarmonyXInterop",
            "Assembly-CSharp",
            "DiscordRPC",
            "Newtonsoft.Json"
        }.Contains(assemblyName, StringComparer.OrdinalIgnoreCase);
    }
}
