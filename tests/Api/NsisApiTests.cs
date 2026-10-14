using DotNet.Bundler;
using DotNet.Bundler.Nsis;

namespace Bundler.ApiTests;

/// <summary>
/// NSIS 公开 API 契约：内嵌 makensis 真编译产 setup.exe（内嵌工具在三宿主均有移植件）。
/// 真装真卸属 Integration localinstall 腿；本层只断言产物结构与契约。
/// </summary>
public sealed class NsisApiTests : TempBase
{
    [Fact]
    public async Task BuildAsync_MinimalConfig_ProducesInstaller()
    {
        var bundle = BundleFixture.Minimal(Root, "windows-x86_64", PackageFormat.Nsis);
        var artifacts = await new NsisBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.Nsis, artifact.Format);
        Assert.Equal("windows-x86_64", artifact.Target);
        // NSIS 生态命名 {name}-{ver}-{arch}-setup.exe
        Assert.Equal("Sample-1.2.3-x64-setup.exe", Path.GetFileName(artifact.Path));
        Assert.True(new FileInfo(artifact.Path).Length > 100_000);
        Assert.Equal([0x4D, 0x5A], BundleFixture.Magic(artifact.Path, 2)); // MZ
    }

    [Fact]
    public void BuildAsync_InvalidPayloadName_Fails()
    {
        // 病态名（NUL 设备名）只能在 POSIX 宿主造出来——Windows 文件系统根本不让你建。
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 文件系统无法创建病态名");
        var bundle = BundleFixture.Minimal(Root, "windows-x86_64", PackageFormat.Nsis);
        File.WriteAllText(Path.Combine(bundle.Targets[0].InputDirectory, "NUL"), "x");
        Assert.Throws<ArgumentException>(() => new NsisBundler().Validate(bundle));
    }
}
