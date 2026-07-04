using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UcuModManager.Core.Virtualization;

public sealed class VirtualizedGameImageBuilder
{
    private const string RuntimeGameDirectoryName = "game";

    public VirtualizedGameImageBuildResult Build(VirtualizedLaunchPlanDocument plan)
    {
        ValidatePlan(plan);

        var managerRoot = EnsureTrailingSeparator(Path.GetFullPath(plan.ManagerRootPath));
        var runtimeRoot = EnsureTrailingSeparator(Path.GetFullPath(plan.ProfileRuntimePath));
        var virtualGameRoot = Path.GetFullPath(Path.Combine(runtimeRoot, RuntimeGameDirectoryName));
        var virtualGameRootWithSeparator = EnsureTrailingSeparator(virtualGameRoot);
        if (!IsInsideRoot(virtualGameRootWithSeparator, managerRoot)
            || !IsInsideRoot(virtualGameRootWithSeparator, runtimeRoot))
        {
            throw new InvalidOperationException("Virtualized game image path resolved outside the manager runtime folder.");
        }

        RecreateDirectory(virtualGameRoot, runtimeRoot);

        var warnings = new List<string>();
        var directoriesCreated = 0;
        var gameFilesLinked = 0;
        var overlayFilesLinked = 0;

        var gameRoot = EnsureTrailingSeparator(Path.GetFullPath(plan.GameRootPath));
        var overlayTargets = (plan.EffectiveFiles ?? Array.Empty<VirtualizedLaunchPlanFile>())
            .Select(file => NormalizeTargetKey(file.TargetRelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var directoryPath in Directory.EnumerateDirectories(gameRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(gameRoot, directoryPath);
            if (ShouldSkipPhysicalBepInExRuntimePath(relativePath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(virtualGameRoot, relativePath));
            directoriesCreated++;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeTargetKey(Path.GetRelativePath(gameRoot, sourcePath));
            if (overlayTargets.Contains(relativePath)
                || ShouldSkipPhysicalBepInExRuntimePath(relativePath))
            {
                continue;
            }

            var targetPath = Path.Combine(virtualGameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            LinkFile(sourcePath, targetPath);
            gameFilesLinked++;
        }

        foreach (var file in (plan.EffectiveFiles ?? Array.Empty<VirtualizedLaunchPlanFile>())
            .OrderBy(file => file.TargetRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file.SourcePath))
            {
                warnings.Add($"Skipped missing virtual source: {file.SourcePath}");
                continue;
            }

            var targetPath = Path.Combine(
                virtualGameRoot,
                NormalizeTargetKey(file.TargetRelativePath).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            DeleteExistingFileOrLink(targetPath);
            LinkFile(file.SourcePath, targetPath);
            overlayFilesLinked++;
        }

        var executableName = Path.GetFileName(plan.GameExecutablePath);
        var virtualExecutablePath = Path.Combine(virtualGameRoot, executableName);
        if (!File.Exists(virtualExecutablePath))
        {
            throw new InvalidOperationException($"Virtualized game executable was not created: {virtualExecutablePath}");
        }

        return new VirtualizedGameImageBuildResult(
            virtualGameRoot,
            virtualExecutablePath,
            gameFilesLinked,
            overlayFilesLinked,
            directoriesCreated,
            warnings);
    }

    public VirtualizationSelfTestResult RunSelfTest(string managerRootPath, string? probeSourceFilePath = null)
    {
        var rootPath = Path.GetFullPath(managerRootPath);
        var testRoot = Path.Combine(rootPath, "cache", "virtualization-self-test");
        var sourcePath = probeSourceFilePath;
        var ownsSource = false;
        var targetPath = Path.Combine(testRoot, "target.bin");

        try
        {
            RecreateDirectory(testRoot, rootPath);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                sourcePath = Path.Combine(testRoot, "source.bin");
                File.WriteAllText(sourcePath, "ucu virtualization self-test");
                ownsSource = true;
            }

            var linkMode = LinkFile(sourcePath, targetPath);
            if (!File.Exists(targetPath))
            {
                return new VirtualizationSelfTestResult(false, "None", "The link target was not created.");
            }

            return new VirtualizationSelfTestResult(true, linkMode, $"Virtualization link check passed using {linkMode}.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or Win32Exception
            or InvalidOperationException
            or NotSupportedException)
        {
            return new VirtualizationSelfTestResult(false, "None", exception.Message);
        }
        finally
        {
            TryDeleteFile(targetPath);
            if (ownsSource && sourcePath is not null)
            {
                TryDeleteFile(sourcePath);
            }
        }
    }

    private static void ValidatePlan(VirtualizedLaunchPlanDocument plan)
    {
        if (string.IsNullOrWhiteSpace(plan.ManagerRootPath)
            || string.IsNullOrWhiteSpace(plan.ProfileRuntimePath)
            || string.IsNullOrWhiteSpace(plan.GameRootPath)
            || string.IsNullOrWhiteSpace(plan.GameExecutablePath))
        {
            throw new InvalidOperationException("Virtualized launch plan is missing required paths.");
        }

        if (!Directory.Exists(plan.GameRootPath))
        {
            throw new InvalidOperationException($"Game root is missing: {plan.GameRootPath}");
        }

        if (!File.Exists(plan.GameExecutablePath))
        {
            throw new InvalidOperationException($"Game executable is missing: {plan.GameExecutablePath}");
        }
    }

    private static string LinkFile(string sourcePath, string targetPath)
    {
        DeleteExistingFileOrLink(targetPath);

        try
        {
            if (CreateHardLink(targetPath, sourcePath, IntPtr.Zero))
            {
                return "HardLink";
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        var error = Marshal.GetLastWin32Error();
        try
        {
            File.CreateSymbolicLink(targetPath, sourcePath);
            return "SymbolicLink";
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            throw new IOException(
                $"Could not link '{targetPath}' to '{sourcePath}'. Hardlink error: {new Win32Exception(error).Message}. Symlink error: {exception.Message}",
                exception);
        }
    }

    private static void RecreateDirectory(string directoryPath, string allowedRootPath)
    {
        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        var fullDirectoryPathWithSeparator = EnsureTrailingSeparator(fullDirectoryPath);
        var allowedRootWithSeparator = EnsureTrailingSeparator(Path.GetFullPath(allowedRootPath));
        if (!IsInsideRoot(fullDirectoryPathWithSeparator, allowedRootWithSeparator))
        {
            throw new InvalidOperationException($"Refusing to recreate a folder outside the allowed runtime root: {fullDirectoryPath}");
        }

        if (Directory.Exists(fullDirectoryPath))
        {
            Directory.Delete(fullDirectoryPath, recursive: true);
        }

        Directory.CreateDirectory(fullDirectoryPath);
    }

    private static void DeleteExistingFileOrLink(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeTargetKey(string relativePath)
    {
        return relativePath.Replace('\\', '/').TrimStart('/');
    }

    private static bool ShouldSkipPhysicalBepInExRuntimePath(string relativePath)
    {
        var key = NormalizeTargetKey(relativePath).TrimEnd('/');
        if (!key.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !key.Equals("BepInEx/core", StringComparison.OrdinalIgnoreCase)
            && !key.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideRoot(string candidatePath, string rootPathWithSeparator)
    {
        return candidatePath.StartsWith(rootPathWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
