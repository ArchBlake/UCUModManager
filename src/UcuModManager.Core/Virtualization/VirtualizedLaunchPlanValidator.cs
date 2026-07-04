using System.Text.Json;

namespace UcuModManager.Core.Virtualization;

public sealed class VirtualizedLaunchPlanValidator
{
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public VirtualizedLaunchPlanValidationResult ValidateFile(string planPath)
    {
        var issues = new List<VirtualizedLaunchPlanIssue>();
        if (string.IsNullOrWhiteSpace(planPath))
        {
            AddError(issues, "plan.path.empty", "Virtual launch plan path is empty.");
            return new VirtualizedLaunchPlanValidationResult(issues);
        }

        if (!File.Exists(planPath))
        {
            AddError(issues, "plan.file.missing", $"Virtual launch plan was not found: {planPath}");
            return new VirtualizedLaunchPlanValidationResult(issues);
        }

        try
        {
            var document = JsonSerializer.Deserialize<VirtualizedLaunchPlanDocument>(File.ReadAllText(planPath), JsonOptions);
            return Validate(document);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            AddError(issues, "plan.file.invalid", $"Virtual launch plan could not be read: {exception.Message}");
            return new VirtualizedLaunchPlanValidationResult(issues);
        }
    }

    public VirtualizedLaunchPlanDocument Load(string planPath)
    {
        if (string.IsNullOrWhiteSpace(planPath))
        {
            throw new InvalidOperationException("Virtual launch plan path is empty.");
        }

        if (!File.Exists(planPath))
        {
            throw new FileNotFoundException("Virtual launch plan was not found.", planPath);
        }

        var document = JsonSerializer.Deserialize<VirtualizedLaunchPlanDocument>(File.ReadAllText(planPath), JsonOptions)
            ?? throw new InvalidOperationException("Virtual launch plan is empty.");
        var validation = Validate(document);
        if (validation.HasErrors)
        {
            var firstError = validation.Issues.First(issue => issue.Severity == VirtualizedLaunchPlanIssueSeverity.Error);
            throw new InvalidOperationException($"Virtual launch plan is invalid: {firstError.Message}");
        }

        return document;
    }

    public VirtualizedLaunchPlanValidationResult Validate(VirtualizedLaunchPlanDocument? document)
    {
        var issues = new List<VirtualizedLaunchPlanIssue>();
        if (document is null)
        {
            AddError(issues, "plan.empty", "Virtual launch plan is empty.");
            return new VirtualizedLaunchPlanValidationResult(issues);
        }

        ValidateHeader(document, issues);
        ValidateRoots(document, issues);
        ValidateFiles(document, issues);
        ValidateCounts(document, issues);

        return issues.Count == 0
            ? VirtualizedLaunchPlanValidationResult.Success
            : new VirtualizedLaunchPlanValidationResult(issues);
    }

    private static void ValidateHeader(VirtualizedLaunchPlanDocument document, List<VirtualizedLaunchPlanIssue> issues)
    {
        if (document.FormatVersion != CurrentFormatVersion)
        {
            AddError(issues, "plan.format.unsupported", $"Unsupported virtual launch plan format: {document.FormatVersion}.");
        }

        if (!string.Equals(document.Mode, "ExperimentalVirtualizedLaunch", StringComparison.Ordinal))
        {
            AddError(issues, "plan.mode.invalid", $"Unexpected virtual launch plan mode: {document.Mode}.");
        }

        if (string.IsNullOrWhiteSpace(document.ProfileId))
        {
            AddError(issues, "profile.id.empty", "Virtual launch plan does not contain a profile id.");
        }

        if (!document.RequiresPhysicalBepInEx)
        {
            AddError(issues, "bepinex.physical.required", "Virtual launch plan must require physical BepInEx in the game folder.");
        }

        if (document.Policy is null)
        {
            AddError(issues, "plan.policy.missing", "Virtual launch plan does not contain a policy section.");
            return;
        }

        if (document.Policy.RedirectWritesToProfileState
            && !string.Equals(document.Policy.WriteRedirectMode, "ProfileVirtualizationOverwrite", StringComparison.Ordinal))
        {
            AddError(issues, "writes.mode.invalid", $"Write redirect mode is invalid: {document.Policy.WriteRedirectMode}.");
        }
    }

    private static void ValidateRoots(VirtualizedLaunchPlanDocument document, List<VirtualizedLaunchPlanIssue> issues)
    {
        var managerRoot = TryFullPath(document.ManagerRootPath);
        var profileRoot = TryFullPath(document.ProfileRootPath);
        var profileBepInExRoot = TryFullPath(document.ProfileBepInExPath);
        var virtualizationRoot = TryFullPath(document.ProfileVirtualizationRootPath);
        var runtimeRoot = TryFullPath(document.ProfileRuntimePath);
        var writeRedirectRoot = TryFullPath(document.ProfileWriteRedirectPath);
        var gameRoot = TryFullPath(document.GameRootPath);
        var gameExecutablePath = TryFullPath(document.GameExecutablePath);

        RequireRoot(managerRoot, "manager.root", "Manager root path is invalid.", issues);
        RequireRoot(profileRoot, "profile.root", "Profile root path is invalid.", issues);
        RequireRoot(profileBepInExRoot, "profile.bepinex", "Profile BepInEx state path is invalid.", issues);
        RequireRoot(virtualizationRoot, "profile.virtualization", "Profile virtualization path is invalid.", issues);
        RequireRoot(runtimeRoot, "profile.runtime", "Profile virtualization runtime path is invalid.", issues);
        RequireRoot(writeRedirectRoot, "profile.overwrite", "Profile virtualization overwrite path is invalid.", issues);
        RequireRoot(gameRoot, "game.root", "Game root path is invalid.", issues);
        RequireRoot(gameExecutablePath, "game.executable", "Game executable path is invalid.", issues);

        if (managerRoot is null || profileRoot is null || profileBepInExRoot is null
            || virtualizationRoot is null || runtimeRoot is null || writeRedirectRoot is null
            || gameRoot is null || gameExecutablePath is null)
        {
            return;
        }

        if (!IsInsideRoot(profileRoot, managerRoot))
        {
            AddError(issues, "profile.root.outside-manager", "Profile root must be inside manager storage.");
        }

        if (!IsInsideRoot(profileBepInExRoot, profileRoot))
        {
            AddError(issues, "profile.bepinex.outside-profile", "Profile BepInEx state must be inside the profile folder.");
        }

        if (!IsInsideRoot(virtualizationRoot, profileRoot))
        {
            AddError(issues, "profile.virtualization.outside-profile", "Profile virtualization folder must be inside the profile folder.");
        }

        if (!IsInsideRoot(runtimeRoot, virtualizationRoot))
        {
            AddError(issues, "profile.runtime.outside-virtualization", "Virtualization runtime folder must be inside the profile virtualization folder.");
        }

        if (!IsInsideRoot(writeRedirectRoot, virtualizationRoot))
        {
            AddError(issues, "profile.overwrite.outside-virtualization", "Virtualization overwrite folder must be inside the profile virtualization folder.");
        }

        if (!IsInsideRoot(gameExecutablePath, gameRoot))
        {
            AddError(issues, "game.executable.outside-game", "Game executable path must be inside the game root.");
        }
    }

    private static void ValidateFiles(VirtualizedLaunchPlanDocument document, List<VirtualizedLaunchPlanIssue> issues)
    {
        var managerRoot = TryFullPath(document.ManagerRootPath);
        var gameRoot = TryFullPath(document.GameRootPath);
        var effectiveFiles = document.EffectiveFiles ?? Array.Empty<VirtualizedLaunchPlanFile>();
        var overlayEntries = document.OverlayEntries ?? Array.Empty<VirtualizedLaunchPlanFile>();
        var effectiveTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var winners = overlayEntries
            .Where(entry => entry is not null)
            .Where(entry => entry.IsWinner)
            .Select(entry => NormalizeTargetKey(entry.TargetRelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in effectiveFiles)
        {
            if (file is null)
            {
                AddError(issues, "files.effective.null", "Effective virtual file entry is empty.");
                continue;
            }

            ValidatePlanFile(file, managerRoot, gameRoot, requireWinner: true, "effective", issues);
            if (!effectiveTargets.Add(NormalizeTargetKey(file.TargetRelativePath)))
            {
                AddError(issues, "files.effective.duplicate", $"Duplicate effective virtual target: {file.TargetRelativePath}");
            }

            if (!winners.Contains(NormalizeTargetKey(file.TargetRelativePath)))
            {
                AddError(issues, "files.effective.not-in-overlay", $"Effective virtual target is not a winner in overlay entries: {file.TargetRelativePath}");
            }
        }

        foreach (var file in overlayEntries)
        {
            if (file is null)
            {
                AddError(issues, "files.overlay.null", "Overlay virtual file entry is empty.");
                continue;
            }

            ValidatePlanFile(file, managerRoot, gameRoot, requireWinner: false, "overlay", issues);
        }

        foreach (var target in effectiveTargets)
        {
            if (IsProtectedBepInExBootstrapTarget(target))
            {
                AddError(issues, "files.bepinex.bootstrap", $"Virtual plan must not override BepInEx bootstrap/core files: {target}");
            }
        }

        ValidateTargetPathTypeConflicts(effectiveTargets, issues);
    }

    private static void ValidateTargetPathTypeConflicts(
        IReadOnlyCollection<string> effectiveTargets,
        List<VirtualizedLaunchPlanIssue> issues)
    {
        var orderedTargets = effectiveTargets
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < orderedTargets.Length; index++)
        {
            var parent = orderedTargets[index].TrimEnd('/');
            if (string.IsNullOrWhiteSpace(parent))
            {
                continue;
            }

            var prefix = parent + "/";
            for (var candidateIndex = index + 1; candidateIndex < orderedTargets.Length; candidateIndex++)
            {
                var candidate = orderedTargets[candidateIndex];
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                AddError(
                    issues,
                    "files.effective.path-type-conflict",
                    $"Virtual target cannot be both a file and a directory parent: {parent} conflicts with {candidate}");
            }
        }
    }

    private static void ValidatePlanFile(
        VirtualizedLaunchPlanFile file,
        string? managerRoot,
        string? gameRoot,
        bool requireWinner,
        string listName,
        List<VirtualizedLaunchPlanIssue> issues)
    {
        if (requireWinner && !file.IsWinner)
        {
            AddError(issues, $"files.{listName}.not-winner", $"Effective virtual file is not marked as a winner: {file.TargetRelativePath}");
        }

        if (!IsSafeRelativeTarget(file.TargetRelativePath))
        {
            AddError(issues, $"files.{listName}.target.unsafe", $"Unsafe virtual target path: {file.TargetRelativePath}");
            return;
        }

        if (gameRoot is not null)
        {
            var targetAbsolutePath = TryFullPath(Path.Combine(gameRoot, file.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (targetAbsolutePath is null || !IsInsideRoot(targetAbsolutePath, gameRoot))
            {
                AddError(issues, $"files.{listName}.target.outside-game", $"Virtual target escapes the game root: {file.TargetRelativePath}");
            }
        }

        var sourcePath = TryFullPath(file.SourcePath);
        if (sourcePath is null)
        {
            AddError(issues, $"files.{listName}.source.invalid", $"Virtual source path is invalid: {file.SourcePath}");
            return;
        }

        if (managerRoot is not null && !IsInsideRoot(sourcePath, managerRoot))
        {
            AddError(issues, $"files.{listName}.source.outside-manager", $"Virtual source must be inside manager storage: {file.SourcePath}");
        }

        var sourceExists = File.Exists(sourcePath);
        if (!sourceExists || !file.SourceExists)
        {
            AddError(issues, $"files.{listName}.source.missing", $"Virtual source file is missing: {file.SourcePath}");
        }

        if (sourceExists != file.SourceExists)
        {
            AddWarning(issues, $"files.{listName}.source.flag-mismatch", $"Virtual source existence flag is stale: {file.SourcePath}");
        }

        if (string.IsNullOrWhiteSpace(file.TargetKind))
        {
            AddWarning(issues, $"files.{listName}.kind.empty", $"Virtual target kind is empty: {file.TargetRelativePath}");
        }
    }

    private static void ValidateCounts(VirtualizedLaunchPlanDocument document, List<VirtualizedLaunchPlanIssue> issues)
    {
        var effectiveFiles = document.EffectiveFiles ?? Array.Empty<VirtualizedLaunchPlanFile>();
        var overlayEntries = document.OverlayEntries ?? Array.Empty<VirtualizedLaunchPlanFile>();

        if (document.EffectiveFiles is null)
        {
            AddError(issues, "files.effective.missing", "Virtual launch plan does not contain effective files.");
        }

        if (document.OverlayEntries is null)
        {
            AddError(issues, "files.overlay.missing", "Virtual launch plan does not contain overlay entries.");
        }

        if (document.ActiveFileCount != effectiveFiles.Count)
        {
            AddError(issues, "counts.effective", $"Active file count mismatch: {document.ActiveFileCount} != {effectiveFiles.Count}.");
        }

        if (document.TotalOverlayEntryCount != overlayEntries.Count)
        {
            AddError(issues, "counts.overlay", $"Overlay entry count mismatch: {document.TotalOverlayEntryCount} != {overlayEntries.Count}.");
        }

        var missingSourceCount = overlayEntries.Count(file => file is null || !file.SourceExists || !File.Exists(file.SourcePath));
        if (document.MissingSourceCount != missingSourceCount)
        {
            AddError(issues, "counts.missing-sources", $"Missing source count mismatch: {document.MissingSourceCount} != {missingSourceCount}.");
        }
    }

    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static void RequireRoot(
        string? path,
        string code,
        string message,
        List<VirtualizedLaunchPlanIssue> issues)
    {
        if (path is null)
        {
            AddError(issues, code, message);
        }
    }

    private static bool IsSafeRelativeTarget(string? targetRelativePath)
    {
        if (string.IsNullOrWhiteSpace(targetRelativePath)
            || Path.IsPathRooted(targetRelativePath))
        {
            return false;
        }

        var parts = targetRelativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0
            && parts.All(part => !part.Equals(".", StringComparison.Ordinal)
                && !part.Equals("..", StringComparison.Ordinal));
    }

    private static bool IsInsideRoot(string candidatePath, string rootPath)
    {
        var root = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string NormalizeTargetKey(string? targetRelativePath)
    {
        if (string.IsNullOrWhiteSpace(targetRelativePath))
        {
            return string.Empty;
        }

        return targetRelativePath.Replace('\\', '/').Trim('/');
    }

    private static bool IsProtectedBepInExBootstrapTarget(string normalizedTarget)
    {
        return normalizedTarget.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddError(List<VirtualizedLaunchPlanIssue> issues, string code, string message)
    {
        issues.Add(new VirtualizedLaunchPlanIssue(VirtualizedLaunchPlanIssueSeverity.Error, code, message));
    }

    private static void AddWarning(List<VirtualizedLaunchPlanIssue> issues, string code, string message)
    {
        issues.Add(new VirtualizedLaunchPlanIssue(VirtualizedLaunchPlanIssueSeverity.Warning, code, message));
    }
}
