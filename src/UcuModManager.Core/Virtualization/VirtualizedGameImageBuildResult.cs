namespace UcuModManager.Core.Virtualization;

public sealed record VirtualizedGameImageBuildResult(
    string VirtualGameRootPath,
    string VirtualGameExecutablePath,
    int GameFilesLinked,
    int OverlayFilesLinked,
    int DirectoriesCreated,
    IReadOnlyList<string> Warnings);
