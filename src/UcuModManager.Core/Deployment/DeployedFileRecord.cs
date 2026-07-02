namespace UcuModManager.Core.Deployment;

public sealed record DeployedFileRecord(
    string TargetRelativePath,
    string TargetAbsolutePath,
    string SourcePath,
    string OwningModId,
    long Size,
    string Sha256);
