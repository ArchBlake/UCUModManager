using System.Text.RegularExpressions;

namespace UcuModManager.Core.Mods;

public sealed class NexusMetadataMatcher
{
    private const int ReliableScore = 140;
    private static readonly string[] ForeignDescriptors =
    {
        "translation",
        "translations",
        "supplement",
        "chinese",
        "simplified",
        "traditional",
        "russian",
        "localization",
        "localisation",
        "language",
        "japanese",
        "korean",
        "german",
        "french",
        "spanish",
        "italian",
        "polish",
        "portuguese",
        "turkish"
    };

    public NexusMetadataMatch? FindBestMatch(
        ModLibraryEntry entry,
        IReadOnlyList<NexusMetadataCatalogEntry> catalog)
    {
        if (catalog.Count == 0)
        {
            return null;
        }

        var context = NexusMetadataMatchContext.Create(entry);
        return catalog
            .Select(candidate => ScoreCandidate(context, candidate))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entry.NexusModId ?? int.MaxValue)
            .FirstOrDefault(match => match.IsReliable);
    }

    private static NexusMetadataMatch ScoreCandidate(
        NexusMetadataMatchContext context,
        NexusMetadataCatalogEntry candidate)
    {
        var score = 0;
        var reasons = new List<string>();
        var candidateModId = candidate.NexusModId;
        var downloadReference = candidate.DownloadReference;
        var sourceModId = context.Source?.ModId;
        var detectedModId = context.DetectedSource?.ModId;

        if (candidateModId is not null && candidateModId == sourceModId)
        {
            score += 220;
            reasons.Add("same saved Nexus id");
        }

        if (candidateModId is not null && candidateModId == detectedModId)
        {
            score += 190;
            reasons.Add("same archive Nexus id");
        }

        if (downloadReference is not null && MatchesKnownFileId(context, downloadReference))
        {
            score += 160;
            reasons.Add("same Nexus file marker");
        }

        var dllOverlap = CountDllOverlap(context.LocalDllNames, candidate.DllNames);
        if (dllOverlap > 0)
        {
            score += 135 + dllOverlap * 25;
            reasons.Add(dllOverlap == 1 ? "matching DLL" : $"{dllOverlap} matching DLLs");
        }

        var nameScore = ScoreNameEvidence(context.LocalNames, candidate.Name, candidate.Id, candidate.NexusPageUrl);
        if (nameScore > 0)
        {
            score += nameScore;
            reasons.Add("matching name");
        }

        if (VersionsEqual(context.LocalVersion, candidate.BestVersion))
        {
            score += 25;
            reasons.Add("matching version");
        }

        score = Math.Max(0, score - GetForeignDescriptorPenalty(context, candidate.Name));
        var isReliable = score >= ReliableScore
            || (candidateModId is not null && candidateModId == sourceModId && score >= 110)
            || (candidateModId is not null && candidateModId == detectedModId && score >= 110);
        return new NexusMetadataMatch(candidate, score, isReliable, string.Join(", ", reasons.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static bool MatchesKnownFileId(
        NexusMetadataMatchContext context,
        NexusMetadataDownloadReference downloadReference)
    {
        if (downloadReference.FileId is null)
        {
            return false;
        }

        return downloadReference.FileId == context.Source?.FileId
            || downloadReference.FileId == context.DetectedSource?.FileId;
    }

    private static int CountDllOverlap(
        IReadOnlySet<string> localDllNames,
        IReadOnlyList<string> candidateDllNames)
    {
        return candidateDllNames
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(name => localDllNames.Contains(name!));
    }

    private static int ScoreNameEvidence(
        IReadOnlyList<string> localNames,
        string? candidateName,
        string? candidateId,
        string? candidateUrl)
    {
        var candidates = new[] { candidateName, candidateId, candidateUrl }
            .Select(NormalizeSearchComparable)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            return 0;
        }

        var bestScore = 0;
        foreach (var localName in localNames.Select(NormalizeSearchComparable).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Equals(localName, StringComparison.OrdinalIgnoreCase))
                {
                    bestScore = Math.Max(bestScore, 120);
                    continue;
                }

                if (candidate.StartsWith(localName, StringComparison.OrdinalIgnoreCase)
                    || localName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    bestScore = Math.Max(bestScore, 96);
                }

                if (candidate.Contains(localName, StringComparison.OrdinalIgnoreCase)
                    || localName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    bestScore = Math.Max(bestScore, 80);
                }

                var localTokens = TokenizeSearchComparable(localName);
                var candidateTokens = TokenizeSearchComparable(candidate);
                if (localTokens.Length == 0 || candidateTokens.Length == 0)
                {
                    continue;
                }

                var commonTokens = localTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
                var tokenScore = (int)Math.Round(commonTokens * 80.0 / Math.Max(localTokens.Length, candidateTokens.Length));
                bestScore = Math.Max(bestScore, tokenScore);
            }
        }

        return bestScore;
    }

    private static int GetForeignDescriptorPenalty(NexusMetadataMatchContext context, string? candidateName)
    {
        var candidateTokens = TokenizeSearchComparable(candidateName ?? string.Empty);
        if (candidateTokens.Length == 0)
        {
            return 0;
        }

        return candidateTokens.Any(token => ForeignDescriptors.Contains(token, StringComparer.OrdinalIgnoreCase)
            && !context.LocalTokens.Contains(token))
                ? 95
                : 0;
    }

    private static bool VersionsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        return NormalizeComparableVersion(first)
            .Equals(NormalizeComparableVersion(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparableVersion(string version)
    {
        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var forIndex = value.IndexOf("-for-", StringComparison.OrdinalIgnoreCase);
        if (forIndex >= 0)
        {
            value = value[..forIndex];
        }

        return value.Replace('-', '.');
    }

    private static string NormalizeSearchComparable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var spaced = Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
        spaced = Regex.Replace(spaced, @"(?i)\bv?\d+(?:[.\-_]\d+)+\b", " ");
        spaced = Regex.Replace(spaced, @"[^A-Za-z0-9]+", " ");
        return Regex.Replace(spaced.ToLowerInvariant(), @"\s+", " ").Trim();
    }

    private static string[] TokenizeSearchComparable(string value)
    {
        return NormalizeSearchComparable(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1)
            .Where(token => !int.TryParse(token, out _))
            .Where(token => !token.Equals("mod", StringComparison.OrdinalIgnoreCase))
            .Where(token => !token.Equals("nexus", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private sealed record NexusMetadataMatchContext(
        ModSourceInfo? Source,
        ModSourceInfo? DetectedSource,
        IReadOnlySet<string> LocalDllNames,
        IReadOnlyList<string> LocalNames,
        IReadOnlySet<string> LocalTokens,
        string? LocalVersion)
    {
        public static NexusMetadataMatchContext Create(ModLibraryEntry entry)
        {
            var archiveFileName = GetSourceArchiveFileName(entry);
            var detectedVersion = ModSourceDetector.DetectVersion(archiveFileName);
            var detectedSource = ModSourceDetector.Detect(archiveFileName, detectedVersion);
            var localNames = BuildLocalNames(entry, archiveFileName);
            return new NexusMetadataMatchContext(
                entry.Manifest.Source,
                detectedSource,
                BuildLocalDllNames(entry),
                localNames,
                localNames.SelectMany(TokenizeSearchComparable).ToHashSet(StringComparer.OrdinalIgnoreCase),
                FirstNonEmpty(entry.Manifest.Source?.FileVersion, entry.Mod.Version, detectedVersion));
        }

        private static string GetSourceArchiveFileName(ModLibraryEntry entry)
        {
            return string.IsNullOrWhiteSpace(entry.Manifest.Source?.SourceArchiveFileName)
                ? entry.Manifest.SourceArchiveFileName
                : entry.Manifest.Source!.SourceArchiveFileName;
        }

        private static IReadOnlySet<string> BuildLocalDllNames(ModLibraryEntry entry)
        {
            return entry.Mod.Files
                .Where(file => Path.GetExtension(file.NormalizedTargetRelativePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileName(file.NormalizedTargetRelativePath))
                .Concat(entry.Mod.Assemblies.Select(assembly => assembly.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? assembly.Name
                    : assembly.Name + ".dll"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }

        private static IReadOnlyList<string> BuildLocalNames(ModLibraryEntry entry, string archiveFileName)
        {
            var names = new List<string>
            {
                entry.Mod.Name,
                entry.Mod.Id.Replace('-', ' '),
                BuildSearchNameFromArchive(archiveFileName)
            };
            names.AddRange(entry.Mod.Assemblies.Select(assembly => assembly.Name));
            if (entry.Mod.Id.Equals("krokmp", StringComparison.OrdinalIgnoreCase)
                || entry.Mod.Name.Equals("KrokMP", StringComparison.OrdinalIgnoreCase))
            {
                names.Add("Casualties Together");
            }

            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Regex.Replace(name.Trim(), @"\s+", " "))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string BuildSearchNameFromArchive(string archiveFileName)
        {
            var name = Path.GetFileNameWithoutExtension(archiveFileName);
            name = Regex.Replace(name, @"(?i)-\d+-(?:v?\d+(?:[.-]\d+)*|\d+)-\d{9,}$", " ");
            name = Regex.Replace(name, @"(?i)_[A-Za-z0-9]{8,}$", " ");
            name = Regex.Replace(name, @"(?i)(?:^|[_-])v?\d+(?:[._-]\d+)+(?:-for-\d+(?:[._-]\d+)*)?(?=[_-]|$)", " ");
            name = Regex.Replace(name, @"(?i)v\d+(?:\.\d+)+", " ");
            name = Regex.Replace(name, @"(?i)(?:^|[_-])\d+(?=[_-]|$)", " ");
            name = Regex.Replace(name, @"[_\-.]+", " ");
            return Regex.Replace(name, @"\s+", " ").Trim();
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }
    }
}

public sealed record NexusMetadataMatch(
    NexusMetadataCatalogEntry Entry,
    int Score,
    bool IsReliable,
    string Reason);

