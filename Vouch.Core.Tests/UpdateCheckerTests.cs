using Vouch.Core.Update;

namespace Vouch.Core.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v0.1", 0, 1, 0)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0)]
    [InlineData("release-v3.4.5+build", 3, 4, 5)]
    public void ParseTag_ExtractsVersion(string tag, int major, int minor, int patch)
    {
        var v = UpdateChecker.ParseTag(tag);
        Assert.NotNull(v);
        Assert.Equal(new Version(major, minor, patch), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("latest")]
    public void ParseTag_NoVersion_ReturnsNull(string tag)
        => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public void IsNewer_ComparesMajorMinorPatch()
    {
        Assert.True(UpdateChecker.IsNewer(new Version(0, 2, 0), new Version(0, 1, 0)));
        Assert.True(UpdateChecker.IsNewer(new Version(1, 0, 0), new Version(0, 9, 9)));
        Assert.True(UpdateChecker.IsNewer(new Version(0, 1, 1), new Version(0, 1, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(0, 1, 0), new Version(0, 1, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(0, 1, 0), new Version(0, 2, 0)));
        // an unset build (from a 4-part assembly version) compares as 0, not -1
        Assert.False(UpdateChecker.IsNewer(new Version(0, 1, 0), new Version(0, 1, 0, 0)));
    }

    [Fact]
    public void ParseLatestJson_ExtractsRelease()
    {
        const string json = """
            {"tag_name":"v0.3.0","html_url":"https://github.com/o/r/releases/tag/v0.3.0","prerelease":false,"name":"0.3"}
            """;
        var r = UpdateChecker.ParseLatestJson(json);
        Assert.NotNull(r);
        Assert.Equal("v0.3.0", r!.Tag);
        Assert.Equal(new Version(0, 3, 0), r.Version);
        Assert.Equal("https://github.com/o/r/releases/tag/v0.3.0", r.Url);
        Assert.False(r.Prerelease);
    }

    [Fact]
    public void ParseLatestJson_BadInput_ReturnsNull()
    {
        Assert.Null(UpdateChecker.ParseLatestJson("not json"));
        Assert.Null(UpdateChecker.ParseLatestJson("""{"message":"Not Found"}"""));  // no tag_name
        Assert.Null(UpdateChecker.ParseLatestJson("""{"tag_name":"nightly"}"""));   // no version in tag
    }
}
