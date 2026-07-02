using System.Text.Json.Serialization;

namespace UcuModManager.Core.Mods;

public sealed record ModFileMapping(
    string SourceArchivePath,
    string TargetRelativePath,
    ModTargetKind TargetKind,
    long Size,
    bool IsEnabledByDefault = true)
{
    [JsonIgnore]
    public string NormalizedTargetRelativePath => TargetRelativePath.Replace('\\', '/').TrimStart('/');
}
