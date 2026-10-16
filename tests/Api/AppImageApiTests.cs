using DotNet.Bundler;
using DotNet.Bundler.AppImage;

namespace Bundler.ApiTests;

/// <summary>
/// AppImage 公开 API 契约：内嵌 appimagetool 真产 .AppImage——Linux 腿独占
/// （runtime 二进件依目标架构内嵌物化）。
/// </summary>
public sealed class AppImageApiTests : TempBase
{
    [Fact]
    public async Task BuildAsync_MinimalConfig_ProducesAppImage()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "appimagetool/runtime 仅 Linux 宿主可执行");
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.AppImage);
        var artifacts = await new AppImageBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.AppImage, artifact.Format);
        Assert.Equal("sample-1.2.3-x86_64.AppImage", Path.GetFileName(artifact.Path));
        // AppImage = ELF 头（runtime）+ squashfs 负载
        Assert.Equal([0x7F, 0x45, 0x4C, 0x46], BundleFixture.Magic(artifact.Path, 4));
    }
}
