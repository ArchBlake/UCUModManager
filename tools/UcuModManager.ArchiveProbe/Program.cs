using UcuModManager.Core.Mods;
using UcuModManager.Core.Games;
using UcuModManager.Core.Profiles;
using UcuModManager.Core.Storage;
using UcuModManager.Core.Virtualization;

var positionalArgs = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
var samplesPath = positionalArgs.Length > 0
    ? Path.GetFullPath(positionalArgs[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples"));
var importEnabled = args.Contains("--import", StringComparer.OrdinalIgnoreCase);
var librarySummaryEnabled = importEnabled || args.Contains("--library", StringComparer.OrdinalIgnoreCase);
var overlayPreviewEnabled = args.Contains("--overlay", StringComparer.OrdinalIgnoreCase);
var profileSummaryEnabled = overlayPreviewEnabled || args.Contains("--profile", StringComparer.OrdinalIgnoreCase);
var managerRootPath = GetOptionValue(args, "--manager-root")
    ?? Path.GetFullPath(Path.Combine(samplesPath, "..", "dev-data"));
var gameRootPath = GetOptionValue(args, "--game-root") ?? @"E:\Steam\steamapps\common\Casualties Unknown Demo";

var analyzer = new ModArchiveAnalyzer();
var importer = new ModImportService(analyzer);
var managerPaths = new ManagerPaths(managerRootPath);

foreach (var archivePath in Directory.EnumerateFiles(samplesPath, "*.zip").OrderBy(Path.GetFileName))
{
    if (Path.GetFileName(archivePath).StartsWith("BepInEx_", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    var plan = analyzer.AnalyzeZip(archivePath);
    Console.WriteLine($"# {Path.GetFileName(archivePath)}");
    Console.WriteLine($"mod: {plan.SuggestedModName}");
    Console.WriteLine($"stripped root: {plan.StrippedRootDirectory ?? "<none>"}");
    Console.WriteLine($"mappings: {plan.Mappings.Count}; ignored: {plan.IgnoredEntries.Count}; warnings: {plan.Warnings.Count}");

    if (plan.Assemblies.Count > 0)
    {
        Console.WriteLine($"assemblies: {string.Join(", ", plan.Assemblies.Select(assembly => assembly.Name))}");
    }

    var providedAssemblyNames = plan.Assemblies
        .Select(assembly => assembly.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var externalReferences = plan.AssemblyReferences
        .Where(reference => !reference.IsKnownGameOrFrameworkReference)
        .Where(reference => !providedAssemblyNames.Contains(reference.Name))
        .Select(reference => reference.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (externalReferences.Length > 0)
    {
        Console.WriteLine($"external refs: {string.Join(", ", externalReferences)}");
    }

    foreach (var mapping in plan.Mappings.Take(8))
    {
        Console.WriteLine($"  {mapping.TargetKind}: {mapping.SourceArchivePath} -> {mapping.TargetRelativePath}");
    }

    if (plan.Mappings.Count > 8)
    {
        Console.WriteLine($"  ... {plan.Mappings.Count - 8} more mappings");
    }

    foreach (var warning in plan.Warnings)
    {
        Console.WriteLine($"  warning: {warning}");
    }

    foreach (var ignored in plan.IgnoredEntries.Take(5))
    {
        Console.WriteLine($"  ignored: {ignored.SourceArchivePath} ({ignored.Reason})");
    }

    if (importEnabled)
    {
        var result = importer.ImportZip(archivePath, managerPaths);
        Console.WriteLine($"  imported: {result.Manifest.Mod.Id}");
        Console.WriteLine($"  manifest: {result.ManifestPath}");
    }

    Console.WriteLine();
}

if (librarySummaryEnabled || profileSummaryEnabled)
{
    var library = new ModLibraryService().LoadLibrary(managerPaths);
    Console.WriteLine("## Library");
    Console.WriteLine($"mods: {library.Count}; files: {library.Sum(entry => entry.FileCount)}; warnings: {library.Sum(entry => entry.Warnings.Count(IsActionableWarning))}");

    foreach (var entry in library)
    {
        var missing = entry.Dependencies.Where(dependency => !dependency.IsSatisfied).Select(dependency => dependency.AssemblyName).ToArray();
        var satisfied = entry.Dependencies.Where(dependency => dependency.IsSatisfied).Select(dependency => dependency.AssemblyName).ToArray();
        Console.WriteLine($"  {entry.Mod.Id}: files={entry.FileCount}; deps found={satisfied.Length}; deps missing={missing.Length}");
        if (missing.Length > 0)
        {
            Console.WriteLine($"    missing: {string.Join(", ", missing)}");
        }
    }

    if (profileSummaryEnabled)
    {
        var profile = new ProfileService().LoadOrCreateDefaultProfile(managerPaths, library);
        var plan = new VirtualizationPlanBuilder().Build(
            gameRootPath,
            GameInstallation.DefaultExecutableName,
            profile,
            library);
        var overlay = new OverlayPreviewService().Build(plan);

        Console.WriteLine("## Default Profile");
        Console.WriteLine($"profile: {profile.Id}; enabled: {profile.Mods.Count(mod => mod.IsEnabled)}/{profile.Mods.Count}; profile files: {plan.Files.Count}; conflicts: {overlay.Conflicts.Count}");
        foreach (var entry in profile.Mods.OrderBy(entry => entry.Priority))
        {
            Console.WriteLine($"  {entry.Priority:00}: {(entry.IsEnabled ? "on " : "off")} {entry.ModId}");
        }

        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine($"  warning: {warning}");
        }

        foreach (var conflict in overlay.Conflicts.Take(10))
        {
            Console.WriteLine($"  conflict: {conflict.TargetRelativePath} <- {string.Join(", ", conflict.Entries.Select(file => file.OwningModId))}; winner={conflict.Winner.OwningModId}");
        }

        if (overlayPreviewEnabled)
        {
            Console.WriteLine("## Overlay Preview");
            Console.WriteLine($"entries: {overlay.Entries.Count}; active: {overlay.ActiveEntries.Count}; conflicts: {overlay.Conflicts.Count}; missing sources: {overlay.MissingSources.Count}");
            foreach (var entry in overlay.Entries.Take(20))
            {
                Console.WriteLine($"  {entry.Status,-14} {entry.TargetKind,-22} {entry.TargetRelativePath} <- {entry.OwningModId}");
            }

            if (overlay.Entries.Count > 20)
            {
                Console.WriteLine($"  ... {overlay.Entries.Count - 20} more overlay entries");
            }

            foreach (var missingSource in overlay.MissingSources.Take(10))
            {
                Console.WriteLine($"  missing source: {missingSource.SourcePath}");
            }
        }
    }
}

static bool IsActionableWarning(string warning)
{
    return !warning.StartsWith("Potential external assembly references detected:", StringComparison.OrdinalIgnoreCase)
        && !warning.StartsWith("Archive root '", StringComparison.OrdinalIgnoreCase);
}

static string? GetOptionValue(IReadOnlyList<string> args, string optionName)
{
    for (var index = 0; index < args.Count; index++)
    {
        var arg = args[index];
        if (arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(arg[(optionName.Length + 1)..]);
        }

        if (arg.Equals(optionName, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
        {
            return Path.GetFullPath(args[index + 1]);
        }
    }

    return null;
}
