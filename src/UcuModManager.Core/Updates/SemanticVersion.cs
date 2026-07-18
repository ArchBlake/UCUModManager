using System.Text.RegularExpressions;

namespace UcuModManager.Core.Updates;

public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex VersionPattern = new(
        @"^(?:v)?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)" +
        @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?" +
        @"(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private SemanticVersion(
        int major,
        int minor,
        int patch,
        IReadOnlyList<string> prereleaseIdentifiers,
        string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PrereleaseIdentifiers = prereleaseIdentifiers;
        BuildMetadata = buildMetadata;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public IReadOnlyList<string> PrereleaseIdentifiers { get; }

    public string? BuildMetadata { get; }

    public bool IsPrerelease => PrereleaseIdentifiers.Count > 0;

    public ManagerReleaseChannel Channel => GetChannel(PrereleaseIdentifiers.FirstOrDefault());

    public static SemanticVersion Parse(string value)
    {
        return TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid semantic version.");
    }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = VersionPattern.Match(value.Trim());
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            return false;
        }

        var prerelease = match.Groups["prerelease"].Success
            ? match.Groups["prerelease"].Value.Split('.')
            : Array.Empty<string>();
        var buildMetadata = match.Groups["build"].Success
            ? match.Groups["build"].Value
            : null;
        version = new SemanticVersion(major, minor, patch, prerelease, buildMetadata);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var coreComparison = Major.CompareTo(other.Major);
        if (coreComparison == 0)
        {
            coreComparison = Minor.CompareTo(other.Minor);
        }

        if (coreComparison == 0)
        {
            coreComparison = Patch.CompareTo(other.Patch);
        }

        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (!IsPrerelease || !other.IsPrerelease)
        {
            return IsPrerelease == other.IsPrerelease
                ? 0
                : IsPrerelease ? -1 : 1;
        }

        var commonCount = Math.Min(PrereleaseIdentifiers.Count, other.PrereleaseIdentifiers.Count);
        for (var index = 0; index < commonCount; index++)
        {
            var comparison = ComparePrereleaseIdentifier(
                PrereleaseIdentifiers[index],
                other.PrereleaseIdentifiers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return PrereleaseIdentifiers.Count.CompareTo(other.PrereleaseIdentifiers.Count);
    }

    public bool Equals(SemanticVersion? other)
    {
        return other is not null && CompareTo(other) == 0;
    }

    public override bool Equals(object? obj)
    {
        return obj is SemanticVersion other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in PrereleaseIdentifiers)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var prerelease = IsPrerelease ? $"-{string.Join('.', PrereleaseIdentifiers)}" : string.Empty;
        var build = string.IsNullOrWhiteSpace(BuildMetadata) ? string.Empty : $"+{BuildMetadata}";
        return $"{Major}.{Minor}.{Patch}{prerelease}{build}";
    }

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    private static int ComparePrereleaseIdentifier(string first, string second)
    {
        var firstIsNumeric = int.TryParse(first, out var firstNumber);
        var secondIsNumeric = int.TryParse(second, out var secondNumber);
        if (firstIsNumeric && secondIsNumeric)
        {
            return firstNumber.CompareTo(secondNumber);
        }

        if (firstIsNumeric != secondIsNumeric)
        {
            return firstIsNumeric ? -1 : 1;
        }

        return string.Compare(first, second, StringComparison.Ordinal);
    }

    private static ManagerReleaseChannel GetChannel(string? identifier)
    {
        return identifier?.ToLowerInvariant() switch
        {
            null => ManagerReleaseChannel.Stable,
            "alpha" => ManagerReleaseChannel.Alpha,
            "beta" => ManagerReleaseChannel.Beta,
            "rc" => ManagerReleaseChannel.ReleaseCandidate,
            _ => ManagerReleaseChannel.UnknownPrerelease
        };
    }
}

public enum ManagerReleaseChannel
{
    UnknownPrerelease,
    Alpha,
    Beta,
    ReleaseCandidate,
    Stable
}
