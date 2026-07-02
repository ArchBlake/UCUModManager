namespace UcuModManager.Core.Deployment;

public sealed record ProfileDeployResult(
    string ProfileId,
    string GameRootPath,
    int CopiedFiles,
    int DeletedFiles,
    int PreservedFiles,
    IReadOnlyList<string> Warnings);
