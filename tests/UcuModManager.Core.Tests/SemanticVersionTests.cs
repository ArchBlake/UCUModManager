using UcuModManager.Core.Updates;

namespace UcuModManager.Core.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.2.0-alpha.1")]
    [InlineData("v0.2.0-beta.2")]
    [InlineData("1.0.0-rc.1+build.7")]
    [InlineData("1.0.0")]
    public void TryParse_AcceptsCanonicalVersions(string value)
    {
        Assert.True(SemanticVersion.TryParse(value, out _));
    }

    [Theory]
    [InlineData("0.2")]
    [InlineData("0.2.0-alpha.01")]
    [InlineData("0.2.0-alpha_public")]
    [InlineData("version-0.2.0")]
    public void TryParse_RejectsNonCanonicalVersions(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }

    [Fact]
    public void CompareTo_FollowsSemVerPrereleaseOrder()
    {
        var versions = new[]
        {
            "0.2.0",
            "0.2.0-rc.1",
            "0.2.0-beta.2",
            "0.2.0-beta.1",
            "0.2.0-alpha.2",
            "0.2.0-alpha.1"
        };

        var ordered = versions
            .Select(SemanticVersion.Parse)
            .Order()
            .Select(version => version.ToString())
            .ToArray();

        Assert.Equal(versions.Reverse(), ordered);
    }

    [Fact]
    public void CompareTo_IgnoresBuildMetadata()
    {
        var first = SemanticVersion.Parse("0.2.0-alpha.1+first");
        var second = SemanticVersion.Parse("0.2.0-alpha.1+second");

        Assert.Equal(0, first.CompareTo(second));
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("0.2.0-alpha.1", "0.2.0-alpha.2", false, true)]
    [InlineData("0.2.0-beta.1", "0.3.0-alpha.1", false, false)]
    [InlineData("0.2.0-beta.1", "0.2.0-rc.1", false, true)]
    [InlineData("0.2.0-rc.1", "0.3.0-beta.1", false, false)]
    [InlineData("0.2.0", "0.3.0-alpha.1", false, false)]
    [InlineData("0.2.0", "0.3.0-alpha.1", true, true)]
    [InlineData("0.2.0-alpha.1", "0.2.0", false, true)]
    public void IsReleaseEligible_RespectsReleaseChannel(
        string current,
        string candidate,
        bool includePrereleases,
        bool expected)
    {
        var result = GitHubManagerUpdateService.IsReleaseEligible(
            SemanticVersion.Parse(current),
            SemanticVersion.Parse(candidate),
            includePrereleases);

        Assert.Equal(expected, result);
    }
}
