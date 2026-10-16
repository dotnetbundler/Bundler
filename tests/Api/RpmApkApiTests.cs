using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.Rpm;

namespace Bundler.ApiTests;

/// <summary>.rpm/.apk 公开 API 契约：真产包、生态命名、magic、确定性。</summary>
public sealed class RpmApkApiTests : TempBase
{
    [Fact]
    public async Task Rpm_MinimalConfig_ProducesStructuredPackage()
    {
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.Rpm);
        var artifacts = await new RpmBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.Rpm, artifact.Format);
        Assert.Equal("sample-1.2.3-1.x86_64.rpm", Path.GetFileName(artifact.Path));
        Assert.Equal([0xED, 0xAB, 0xEE, 0xDB],
            BundleFixture.Magic(artifact.Path, 4)); // RPM lead magic
    }

    [Fact]
    public async Task Rpm_Rebuild_DeterministicBytes()
    {
        var a = (await new RpmBundler().BuildAsync(
            BundleFixture.Minimal(Temp("r1").FullName, "linux-x86_64", PackageFormat.Rpm),
            TestContext.Current.CancellationToken))[0];
        var b = (await new RpmBundler().BuildAsync(
            BundleFixture.Minimal(Temp("r2").FullName, "linux-x86_64", PackageFormat.Rpm),
            TestContext.Current.CancellationToken))[0];
        Assert.Equal(File.ReadAllBytes(a.Path), File.ReadAllBytes(b.Path));
    }

    [Fact]
    public async Task Apk_MinimalConfig_ProducesStructuredPackage()
    {
        var bundle = BundleFixture.Minimal(Root, "linux-musl-x86_64", PackageFormat.AlpineApk);
        var artifacts = await new AlpineApkBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.AlpineApk, artifact.Format);
        // apk 生态命名 {name}-{ver}-r{rev}.{arch}.apk
        Assert.Equal("sample-1.2.3-r0.x86_64.apk", Path.GetFileName(artifact.Path));
        Assert.Equal([0x1F, 0x8B], BundleFixture.Magic(artifact.Path, 2)); // gzip magic
    }

    [Fact]
    public async Task Apk_Rebuild_DeterministicBytes()
    {
        var a = (await new AlpineApkBundler().BuildAsync(
            BundleFixture.Minimal(Temp("a1").FullName, "linux-musl-x86_64", PackageFormat.AlpineApk),
            TestContext.Current.CancellationToken))[0];
        var b = (await new AlpineApkBundler().BuildAsync(
            BundleFixture.Minimal(Temp("a2").FullName, "linux-musl-x86_64", PackageFormat.AlpineApk),
            TestContext.Current.CancellationToken))[0];
        Assert.Equal(File.ReadAllBytes(a.Path), File.ReadAllBytes(b.Path));
    }
}
