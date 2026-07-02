namespace UcuModManager.Core.Deployment;

public sealed record DeploymentManifest(
    string ProfileId,
    string GameRootPath,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DeployedFileRecord> Files);
