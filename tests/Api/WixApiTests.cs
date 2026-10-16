using DotNet.Bundler;
using DotNet.Bundler.Wix;

namespace Bundler.ApiTests;

/// <summary>
/// MSI(WiX) 公开 API 契约：真 WiX 编译产 .msi——Windows 腿独占。
/// 真装真卸/升级语义属 Integration localinstall 腿。
/// </summary>
public sealed class WixApiTests : TempBase
{
    [Fact]
    public async Task BuildAsync_MinimalConfig_ProducesMsi()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WiX 3.14 编译需要 Windows 宿主");
        var bundle = BundleFixture.Minimal(Root, "windows-x86_64", PackageFormat.Msi);
        var artifacts = await new WixBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.Equal(PackageFormat.Msi, artifact.Format);
        Assert.Equal("Sample-1.2.3-x64.msi", Path.GetFileName(artifact.Path));
        // MSI 是 OLE 复合文档
        Assert.Equal([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1],
            BundleFixture.Magic(artifact.Path, 8));
    }

    [Fact]
    public async Task BuildAsync_TwoLanguages_FansOutTwoMsis()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WiX 3.14 编译需要 Windows 宿主");
        var bundle = BundleFixture.Minimal(Root, "windows-x86_64", PackageFormat.Msi);
        var artifacts = await new WixBundler(
            new WixBundleConfiguration { Languages = ["en-US", "zh-CN"] }).BuildAsync(
            bundle, TestContext.Current.CancellationToken);

        Assert.Equal(2, artifacts.Count);
        // en-US 不带后缀；其余语言小写区域名后缀。
        Assert.Equal(["Sample-1.2.3-x64-zh-cn.msi", "Sample-1.2.3-x64.msi"],
            artifacts.Select(a => Path.GetFileName(a.Path))
                .OrderBy(n => n, StringComparer.Ordinal).ToList());
    }
}
