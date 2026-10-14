using System.Xml.Linq;
using DotNet.Bundler;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;

namespace Bundler.ApiTests;

/// <summary>
/// macOS 三格式公开 API 契约：真 .app 目录树/真 .dmg（hdiutil）/真 .pkg（pkgbuild）
/// ——mac 腿独占。真挂载/真安装/真启动属 Integration。
/// </summary>
public sealed class MacApiTests : TempBase
{
    private BundleConfiguration MacBundle(PackageFormat format)
    {
        var bundle = BundleFixture.Minimal(Root, "macos-arm64", format, mainExecutable: "sample");
        // .app 主程序必须是真 Mach-O——借系统 /bin/ls 充当被包程序。
        File.Copy("/bin/ls", Path.Combine(bundle.Targets[0].InputDirectory, "sample"), overwrite: true);
        return bundle;
    }

    [Fact]
    public async Task App_ProducesBundleTree()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), ".app 组装与签名工具仅 macOS");
        var artifacts = await new MacAppBundler().BuildAsync(
            MacBundle(PackageFormat.App), TestContext.Current.CancellationToken);

        var artifact = Assert.Single(artifacts);
        Assert.True(Directory.Exists(artifact.Path));
        Assert.EndsWith(".app", artifact.Path);

        var plistPath = Path.Combine(artifact.Path, "Contents", "Info.plist");
        Assert.True(File.Exists(plistPath));
        var plist = XDocument.Load(plistPath);
        var keys = plist.Root!.Element("dict")!.Elements()
            .Where(e => e.Name == "key").Select(e => e.Value).ToList();
        Assert.Contains("CFBundleIdentifier", keys);
        Assert.Contains("CFBundleExecutable", keys);
        Assert.Contains("CFBundleShortVersionString", keys);
        var exec = Path.Combine(artifact.Path, "Contents", "MacOS", "sample");
        Assert.True(File.Exists(exec));
    }

    [Fact]
    public async Task Dmg_ProducesImage()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "hdiutil 仅 macOS");
        var artifacts = await new MacDmgBundler().BuildAsync(
            MacBundle(PackageFormat.Dmg), TestContext.Current.CancellationToken);
        // dmg 产出顺带落中间体 .app——按格式取件而非 Single。
        var artifact = Assert.Single(artifacts, a => a.Format == PackageFormat.Dmg);
        Assert.Equal("Sample-1.2.3-arm64.dmg", Path.GetFileName(artifact.Path));
        Assert.True(new FileInfo(artifact.Path).Length > 0);
    }

    [Fact]
    public async Task Pkg_ProducesXarPackage()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "pkgbuild/productbuild 仅 macOS");
        var artifacts = await new MacPkgBundler().BuildAsync(
            MacBundle(PackageFormat.Pkg), TestContext.Current.CancellationToken);
        // pkg 产出顺带落中间体 .app——按格式取件而非 Single。
        var artifact = Assert.Single(artifacts, a => a.Format == PackageFormat.Pkg);
        Assert.Equal("Sample-1.2.3-arm64.pkg", Path.GetFileName(artifact.Path));
        Assert.Equal("xar!"u8.ToArray(), BundleFixture.Magic(artifact.Path, 4));
    }
}
