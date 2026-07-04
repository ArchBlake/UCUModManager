namespace UcuModManager.Core.Virtualization;

public sealed class VirtualizedFileSystemResolver
{
    private readonly VirtualizedLaunchPlanDocument _plan;
    private readonly Dictionary<string, VirtualizedLaunchPlanFile> _effectiveFileByTarget;
    private readonly string _gameRootPath;
    private readonly string _writeRedirectRootPath;

    public VirtualizedFileSystemResolver(VirtualizedLaunchPlanDocument plan)
    {
        _plan = plan;
        _gameRootPath = EnsureTrailingSeparator(Path.GetFullPath(plan.GameRootPath));
        _writeRedirectRootPath = EnsureTrailingSeparator(Path.GetFullPath(plan.ProfileWriteRedirectPath));
        _effectiveFileByTarget = (plan.EffectiveFiles ?? Array.Empty<VirtualizedLaunchPlanFile>())
            .Where(file => file is not null)
            .GroupBy(file => NormalizeTargetKey(file.TargetRelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public VirtualizedPathResolution ResolveRead(string requestedPath)
    {
        if (!TryNormalizeRequestedPath(requestedPath, out var targetRelativePath, out var error))
        {
            return Invalid(requestedPath, error);
        }

        var writeRedirectPath = BuildWriteRedirectPath(targetRelativePath);
        if (File.Exists(writeRedirectPath))
        {
            return new VirtualizedPathResolution(
                VirtualizedPathResolutionKind.WriteRedirectFile,
                requestedPath,
                targetRelativePath,
                writeRedirectPath,
                "__profile_overwrite__",
                "WriteRedirect",
                Exists: true,
                ErrorMessage: null);
        }

        if (_effectiveFileByTarget.TryGetValue(NormalizeTargetKey(targetRelativePath), out var mappedFile))
        {
            var exists = File.Exists(mappedFile.SourcePath);
            return new VirtualizedPathResolution(
                exists ? VirtualizedPathResolutionKind.PlanFile : VirtualizedPathResolutionKind.Missing,
                requestedPath,
                targetRelativePath,
                exists ? mappedFile.SourcePath : null,
                mappedFile.OwningModId,
                mappedFile.TargetKind,
                exists,
                exists ? null : $"Mapped source file is missing: {mappedFile.SourcePath}");
        }

        var gamePath = BuildGamePath(targetRelativePath);
        if (File.Exists(gamePath))
        {
            return new VirtualizedPathResolution(
                VirtualizedPathResolutionKind.GameFile,
                requestedPath,
                targetRelativePath,
                gamePath,
                "__game__",
                "GameFile",
                Exists: true,
                ErrorMessage: null);
        }

        return new VirtualizedPathResolution(
            VirtualizedPathResolutionKind.Missing,
            requestedPath,
            targetRelativePath,
            null,
            null,
            null,
            Exists: false,
            ErrorMessage: "No virtual, profile overwrite, or game file exists for this path.");
    }

    public VirtualizedPathResolution ResolveWrite(string requestedPath)
    {
        if (!TryNormalizeRequestedPath(requestedPath, out var targetRelativePath, out var error))
        {
            return Invalid(requestedPath, error);
        }

        var resolvedPath = _plan.Policy.RedirectWritesToProfileState
            ? BuildWriteRedirectPath(targetRelativePath)
            : BuildGamePath(targetRelativePath);
        return new VirtualizedPathResolution(
            _plan.Policy.RedirectWritesToProfileState
                ? VirtualizedPathResolutionKind.WriteRedirectTarget
                : VirtualizedPathResolutionKind.GameTarget,
            requestedPath,
            targetRelativePath,
            resolvedPath,
            _plan.Policy.RedirectWritesToProfileState ? "__profile_overwrite__" : "__game__",
            _plan.Policy.RedirectWritesToProfileState ? "WriteRedirect" : "GameFile",
            File.Exists(resolvedPath),
            ErrorMessage: null);
    }

    public IReadOnlyList<VirtualizedDirectoryEntry> EnumerateDirectory(string requestedPath)
    {
        if (!TryNormalizeDirectoryPath(requestedPath, out var targetRelativePath, out _))
        {
            return Array.Empty<VirtualizedDirectoryEntry>();
        }

        var entries = new Dictionary<string, VirtualizedDirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        AddPhysicalDirectoryEntries(entries, BuildGamePath(targetRelativePath), targetRelativePath, VirtualizedDirectoryEntryKind.GameFile, "__game__");
        AddPlanDirectoryEntries(entries, targetRelativePath);
        AddPhysicalDirectoryEntries(entries, BuildWriteRedirectPath(targetRelativePath), targetRelativePath, VirtualizedDirectoryEntryKind.WriteRedirectFile, "__profile_overwrite__");
        return entries.Values
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void AddPlanDirectoryEntries(
        Dictionary<string, VirtualizedDirectoryEntry> entries,
        string directoryRelativePath)
    {
        foreach (var mappedFile in _effectiveFileByTarget.Values)
        {
            var child = GetImmediateChild(directoryRelativePath, NormalizeTargetKey(mappedFile.TargetRelativePath));
            if (child is null)
            {
                continue;
            }

            var key = BuildEntryKey(child.Name);
            if (child.IsDirectory)
            {
                entries[key] = new VirtualizedDirectoryEntry(
                    child.Name,
                    child.TargetRelativePath,
                    ResolvedPath: null,
                    VirtualizedDirectoryEntryKind.Directory,
                    OwningModId: null);
                continue;
            }

            entries[key] = new VirtualizedDirectoryEntry(
                child.Name,
                child.TargetRelativePath,
                mappedFile.SourcePath,
                VirtualizedDirectoryEntryKind.PlanFile,
                mappedFile.OwningModId);
        }
    }

    private static void AddPhysicalDirectoryEntries(
        Dictionary<string, VirtualizedDirectoryEntry> entries,
        string physicalDirectoryPath,
        string directoryRelativePath,
        VirtualizedDirectoryEntryKind fileKind,
        string ownerId)
    {
        if (!Directory.Exists(physicalDirectoryPath))
        {
            return;
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(physicalDirectoryPath))
        {
            var name = Path.GetFileName(directoryPath);
            var targetRelativePath = CombineTargetPath(directoryRelativePath, name);
            entries[BuildEntryKey(name)] = new VirtualizedDirectoryEntry(
                name,
                targetRelativePath,
                directoryPath,
                VirtualizedDirectoryEntryKind.Directory,
                ownerId);
        }

        foreach (var filePath in Directory.EnumerateFiles(physicalDirectoryPath))
        {
            var name = Path.GetFileName(filePath);
            var targetRelativePath = CombineTargetPath(directoryRelativePath, name);
            entries[BuildEntryKey(name)] = new VirtualizedDirectoryEntry(
                name,
                targetRelativePath,
                filePath,
                fileKind,
                ownerId);
        }
    }

    private bool TryNormalizeRequestedPath(string requestedPath, out string targetRelativePath, out string? error)
    {
        targetRelativePath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            error = "Path is empty.";
            return false;
        }

        string candidateRelativePath;
        try
        {
            if (Path.IsPathRooted(requestedPath))
            {
                var fullPath = Path.GetFullPath(requestedPath);
                if (!IsInsideRoot(fullPath, _gameRootPath))
                {
                    error = "Path is outside the game root.";
                    return false;
                }

                candidateRelativePath = Path.GetRelativePath(_gameRootPath, fullPath);
            }
            else
            {
                candidateRelativePath = requestedPath;
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            error = exception.Message;
            return false;
        }

        return TryNormalizeTargetRelativePath(candidateRelativePath, out targetRelativePath, out error);
    }

    private bool TryNormalizeDirectoryPath(string requestedPath, out string targetRelativePath, out string? error)
    {
        if (!TryNormalizeRequestedPath(requestedPath, out targetRelativePath, out error))
        {
            return false;
        }

        targetRelativePath = targetRelativePath.TrimEnd('/');
        return true;
    }

    private static bool TryNormalizeTargetRelativePath(
        string candidateRelativePath,
        out string targetRelativePath,
        out string? error)
    {
        targetRelativePath = candidateRelativePath
            .Replace('\\', '/')
            .Trim('/');
        error = null;

        if (string.IsNullOrWhiteSpace(targetRelativePath))
        {
            return true;
        }

        if (Path.IsPathRooted(targetRelativePath))
        {
            error = "Virtual target path is rooted.";
            return false;
        }

        var parts = targetRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => part.Equals(".", StringComparison.Ordinal) || part.Equals("..", StringComparison.Ordinal)))
        {
            error = "Virtual target path contains unsafe segments.";
            return false;
        }

        return true;
    }

    private string BuildWriteRedirectPath(string targetRelativePath)
    {
        return Path.GetFullPath(Path.Combine(_writeRedirectRootPath, targetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private string BuildGamePath(string targetRelativePath)
    {
        return Path.GetFullPath(Path.Combine(_gameRootPath, targetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static ChildPath? GetImmediateChild(string directoryRelativePath, string fileRelativePath)
    {
        var directory = NormalizeTargetKey(directoryRelativePath).TrimEnd('/');
        var file = NormalizeTargetKey(fileRelativePath).Trim('/');
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var remainder = string.IsNullOrWhiteSpace(directory)
            ? file
            : file.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)
                ? file[(directory.Length + 1)..]
                : string.Empty;
        if (string.IsNullOrWhiteSpace(remainder) || remainder.Equals(file, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var separatorIndex = remainder.IndexOf('/');
        if (separatorIndex < 0)
        {
            return new ChildPath(remainder, CombineTargetPath(directory, remainder), IsDirectory: false);
        }

        var directoryName = remainder[..separatorIndex];
        return new ChildPath(directoryName, CombineTargetPath(directory, directoryName), IsDirectory: true);
    }

    private static string CombineTargetPath(string directoryRelativePath, string name)
    {
        var directory = NormalizeTargetKey(directoryRelativePath).TrimEnd('/');
        return string.IsNullOrWhiteSpace(directory)
            ? name
            : $"{directory}/{name}";
    }

    private static string BuildEntryKey(string name)
    {
        return name;
    }

    private static string NormalizeTargetKey(string? targetRelativePath)
    {
        if (string.IsNullOrWhiteSpace(targetRelativePath))
        {
            return string.Empty;
        }

        return targetRelativePath.Replace('\\', '/').Trim('/');
    }

    private static bool IsInsideRoot(string candidatePath, string rootPathWithSeparator)
    {
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(rootPathWithSeparator, StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(rootPathWithSeparator.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static VirtualizedPathResolution Invalid(string requestedPath, string? error)
    {
        return new VirtualizedPathResolution(
            VirtualizedPathResolutionKind.Invalid,
            requestedPath,
            string.Empty,
            ResolvedPath: null,
            OwningModId: null,
            TargetKind: null,
            Exists: false,
            error);
    }

    private sealed record ChildPath(string Name, string TargetRelativePath, bool IsDirectory);
}
