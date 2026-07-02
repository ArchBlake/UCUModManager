namespace UcuModManager.Core.Mods;

public sealed record NexusModFileInfo(
    int FileId,
    string Name,
    string FileName,
    string Version,
    string Category,
    DateTimeOffset? UploadedAt,
    long? SizeInBytes,
    bool IsPrimary,
    bool IsOldVersion);

