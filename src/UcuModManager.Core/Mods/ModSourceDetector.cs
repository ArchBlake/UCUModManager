using System.Text.RegularExpressions;

namespace UcuModManager.Core.Mods;

public static class ModSourceDetector
{
    private const string DefaultNexusGameDomain = "scavprototype";

    public static ModSourceInfo? Detect(string archivePath, string? detectedVersion)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        var archiveName = Path.GetFileNameWithoutExtension(archivePath);

        var nexusWithVersion = Regex.Match(
            archiveName,
            @"^(?<name>.+?)-(?<modId>\d+)-(?<version>v?\d+(?:[.-]\d+)+(?:-for-\d+(?:[.-]\d+)*)?)-(?<timestamp>\d{9,})$",
            RegexOptions.IgnoreCase);
        if (nexusWithVersion.Success)
        {
            var version = NormalizeHyphenVersion(nexusWithVersion.Groups["version"].Value) ?? detectedVersion;
            return new ModSourceInfo(
                "NexusMods",
                DefaultNexusGameDomain,
                ParseInt(nexusWithVersion.Groups["modId"].Value),
                null,
                version,
                ParseUnixTimestamp(nexusWithVersion.Groups["timestamp"].Value),
                archiveFileName);
        }

        var nexusWithFileId = Regex.Match(
            archiveName,
            @"^(?<name>.+?)-(?<modId>\d+)-(?<fileId>\d+)-(?<timestamp>\d{9,})$",
            RegexOptions.IgnoreCase);
        if (nexusWithFileId.Success)
        {
            var fileId = nexusWithFileId.Groups["fileId"].Value;
            return new ModSourceInfo(
                "NexusMods",
                DefaultNexusGameDomain,
                ParseInt(nexusWithFileId.Groups["modId"].Value),
                ParseInt(fileId),
                detectedVersion ?? fileId,
                ParseUnixTimestamp(nexusWithFileId.Groups["timestamp"].Value),
                archiveFileName);
        }

        var nexusGeneratedWithVersion = Regex.Match(
            archiveName,
            @"^(?<name>.+?)[ _-](?<modId>\d+)[ _-](?<version>v?\d+(?:[.-]\d+)*(?:-[A-Za-z][A-Za-z0-9.-]*)?)[ _-][A-Za-z0-9]{8,}$",
            RegexOptions.IgnoreCase);
        if (nexusGeneratedWithVersion.Success)
        {
            return new ModSourceInfo(
                "NexusMods",
                DefaultNexusGameDomain,
                ParseInt(nexusGeneratedWithVersion.Groups["modId"].Value),
                null,
                NormalizeGeneratedVersion(nexusGeneratedWithVersion.Groups["version"].Value) ?? detectedVersion,
                null,
                archiveFileName);
        }

        var nexusGeneratedSuffix = Regex.Match(
            archiveName,
            @"^(?<name>.+?)(?:[_-](?<fileId>\d+))?_[A-Za-z0-9]{8,}$",
            RegexOptions.IgnoreCase);
        if (nexusGeneratedSuffix.Success && detectedVersion is not null)
        {
            return new ModSourceInfo(
                "NexusMods",
                DefaultNexusGameDomain,
                null,
                ParseInt(nexusGeneratedSuffix.Groups["fileId"].Value),
                detectedVersion,
                null,
                archiveFileName);
        }

        return null;
    }

    public static string? DetectVersion(string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);

        var nexusGeneratedVersion = Regex.Match(
            name,
            @"(?i)^.+?[ _-]\d+[ _-](?<version>v?\d+(?:[.-]\d+)*(?:-[A-Za-z][A-Za-z0-9.-]*)?)[ _-][A-Za-z0-9]{8,}$");
        if (nexusGeneratedVersion.Success)
        {
            return NormalizeGeneratedVersion(nexusGeneratedVersion.Groups["version"].Value);
        }

        var hyphenVersion = Regex.Match(name, @"(?i)(?:^|[-_])v(?<version>\d+(?:-\d+)+)(?:-for-\d+(?:[.-]\d+)*)?(?:[-_]|$)");
        if (hyphenVersion.Success)
        {
            return hyphenVersion.Groups["version"].Value.Replace('-', '.');
        }

        var dottedVersion = Regex.Match(name, @"(?i)v(?<version>\d+(?:\.\d+)+)");
        if (dottedVersion.Success)
        {
            return dottedVersion.Groups["version"].Value;
        }

        dottedVersion = Regex.Match(name, @"(?i)(?:^|[._-])(?<version>\d+(?:\.\d+)+)(?:[._-]|$)");
        if (dottedVersion.Success)
        {
            return dottedVersion.Groups["version"].Value;
        }

        var nexusHyphenSingleNumberVersion = Regex.Match(name, @"(?i)^.+?-\d+-(?<version>\d+)-\d{9,}$");
        if (nexusHyphenSingleNumberVersion.Success)
        {
            return nexusHyphenSingleNumberVersion.Groups["version"].Value;
        }

        var nexusGeneratedSingleNumberVersion = Regex.Match(name, @"(?i)^.+?[_-](?<version>\d+)_[A-Za-z0-9]{8,}$");
        return nexusGeneratedSingleNumberVersion.Success
            ? nexusGeneratedSingleNumberVersion.Groups["version"].Value
            : null;
    }


    private static string? NormalizeHyphenVersion(string version)
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

        value = value.Replace('-', '.');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? NormalizeGeneratedVersion(string version)
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

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? ParseInt(string value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ParseUnixTimestamp(string value)
    {
        return long.TryParse(value, out var parsed)
            ? DateTimeOffset.FromUnixTimeSeconds(parsed)
            : null;
    }
}
