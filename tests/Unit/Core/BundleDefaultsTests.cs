using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

/// <summary>
/// 入口层共享缺省推导：格式缺省表、宿主 target 词、identifier 规范化——
/// CLI 与 MSBuild（props/targets XML 镜像）同一张表的锚点。
/// </summary>
public sealed class BundleDefaultsTests
{
    [Theory]
    [InlineData("windows-x86_64", new[] { PackageFormat.Nsis })]
    [InlineData("windows-i686", new[] { PackageFormat.Nsis })]
    [InlineData("macos-x86_64", new[] { PackageFormat.Dmg })]
    [InlineData("macos-aarch64", new[] { PackageFormat.Dmg })]
    [InlineData("macos-universal", new[] { PackageFormat.Dmg })]
    [InlineData("linux-x86_64", new[] { PackageFormat.Deb })]
    [InlineData("linux-aarch64", new[] { PackageFormat.Deb })]
    [InlineData("linux-musl-x86_64", new[] { PackageFormat.AlpineApk })]
    [InlineData("linux-musl-aarch64", new[] { PackageFormat.AlpineApk })]
    public void DefaultFormatsForTarget_MatchesTable(string target, PackageFormat[] expected) =>
        Assert.Equal(expected, BundleDefaults.DefaultFormatsForTarget(target));

    [Theory]
    [InlineData("freebsd-x86_64")]
    [InlineData("bogus")]
    [InlineData("")]
    public void DefaultFormatsForTarget_Unknown_FallsBackToZip(string target) =>
        Assert.Equal([PackageFormat.Zip], BundleDefaults.DefaultFormatsForTarget(target));

    [Fact]
    public void HostTarget_IsParseableAndSane()
    {
        var target = BundleDefaults.HostTarget();
        Assert.True(BundleTarget.TryParse(target, out var parsed));
        Assert.NotNull(parsed);
        Assert.True(parsed!.OperatingSystem is
            TargetOperatingSystem.Windows or TargetOperatingSystem.MacOS or TargetOperatingSystem.Linux);
    }

    [Theory]
    [InlineData("My App", "my-app")]
    [InlineData("Hello_Bundler-App!", "hello-bundler-app")]
    [InlineData("---lead", "lead")]
    [InlineData("trail---", "trail")]
    [InlineData("a---b---c", "a-b-c")]
    [InlineData("!!!", "app")]
    [InlineData("", "app")]
    [InlineData("UPPER123", "upper123")]
    public void NormalizeIdentifierSegment_MatchesRules(string input, string expected) =>
        Assert.Equal(expected, BundleDefaults.NormalizeIdentifierSegment(input));
}
