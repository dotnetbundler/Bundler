using DotNet.Bundler.Updater;

namespace Bundler.UnitTests.Updater;

/// <summary>
/// <see cref="UpdateVersion"/> 语义化版本解析与比较契约：major.minor.patch 数值序 +
/// semver 预发布优先级（正式版高于同核预发布、数值段低于字母段、短前缀更低）、
/// build metadata 不参与比较。
/// </summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("0.0.1-alpha", 0, 0, 1, "alpha")]
    [InlineData("2.0.0-beta.2+build.5", 2, 0, 0, "beta.2")]
    [InlineData(" 1.2.3 ", 1, 2, 3, "")]
    public void TryParse_ValidVersions_ParseComponents(
        string text, int major, int minor, int patch, string prerelease)
    {
        Assert.True(UpdateVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.x.3")]
    [InlineData("1.2.x")]
    public void TryParse_InvalidVersions_ReturnFalse(string? text)
    {
        Assert.False(UpdateVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    // 正式版高于同核预发布
    [InlineData("1.0.0", "1.0.0-alpha", 1)]
    [InlineData("1.0.0-alpha", "1.0.0", -1)]
    // 数值段低于字母段；数值段按数值比；前缀相同短列更低
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-beta", "1.0.0-beta.1", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    // build metadata 被整段截除：不参与比较
    [InlineData("1.0.0+build.1", "1.0.0+build.9", 0)]
    public void CompareTo_SemverOrdering(string left, string right, int expected)
    {
        Assert.True(UpdateVersion.TryParse(left, out var a));
        Assert.True(UpdateVersion.TryParse(right, out var b));
        Assert.Equal(expected, Math.Sign(a.CompareTo(b)));
        Assert.Equal(-expected, Math.Sign(b.CompareTo(a)));
    }
}
