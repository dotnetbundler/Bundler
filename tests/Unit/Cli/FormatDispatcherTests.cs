using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.AppImage;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Cli;
using DotNet.Bundler.Deb;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Rpm;
using DotNet.Bundler.Wix;

namespace Bundler.UnitTests.Cli;

/// <summary>
/// FormatDispatcher：格式词 → PackageFormat → IFormatBundler 后端映射全表。
/// 宿主门禁只活在按 RID 发布臂里——本测试消费的是无 RID 的类库构建，
/// 全部格式臂都在（see Bundler.Cli.csproj 的 BundlerHost* 条件）。
/// </summary>
public sealed class FormatDispatcherTests
{
    private static readonly CliResolvedConfiguration Resolved = new()
    {
        Bundle = new BundleConfiguration()
    };

    [Theory]
    [InlineData("nsis", PackageFormat.Nsis, typeof(NsisBundler))]
    [InlineData("msi", PackageFormat.Msi, typeof(WixBundler))]
    [InlineData("app", PackageFormat.App, typeof(MacAppBundler))]
    [InlineData("dmg", PackageFormat.Dmg, typeof(MacDmgBundler))]
    [InlineData("pkg", PackageFormat.Pkg, typeof(MacPkgBundler))]
    [InlineData("deb", PackageFormat.Deb, typeof(DebBundler))]
    [InlineData("rpm", PackageFormat.Rpm, typeof(RpmBundler))]
    [InlineData("appimage", PackageFormat.AppImage, typeof(AppImageBundler))]
    [InlineData("zip", PackageFormat.Zip, typeof(ArchiveBundler))]
    [InlineData("targz", PackageFormat.TarGz, typeof(ArchiveBundler))]
    [InlineData("tar.gz", PackageFormat.TarGz, typeof(ArchiveBundler))]
    [InlineData("alpineapk", PackageFormat.AlpineApk, typeof(AlpineApkBundler))]
    public void Create_FormatWord_MapsToBackend(string word, PackageFormat format, Type backendType)
    {
        // 词解析与后端创建同链断言：词解析错则整条分发链错。
        Assert.Equal([format], CliProgram.ParseFormats(word, "linux-x86_64"));
        var bundler = FormatDispatcher.Create(format, Resolved, NullBundleLogger.Instance);
        Assert.NotNull(bundler);
        Assert.IsType(backendType, bundler);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("nusi")]
    [InlineData("")]
    public void ParseFormats_UnknownWord_Rejected(string word)
    {
        var exception = Assert.Throws<CliUsageException>(
            () => CliProgram.ParseFormats(word, "linux-x86_64"));
        Assert.Contains("format", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_UnknownEnumValue_Rejected()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => FormatDispatcher.Create((PackageFormat)999, Resolved, NullBundleLogger.Instance));
        Assert.Contains("Unknown format", exception.Message);
    }

    [Fact]
    public void Create_ZipAndTarGz_ShareArchiveBackend()
    {
        var zip = FormatDispatcher.Create(PackageFormat.Zip, Resolved, NullBundleLogger.Instance);
        var targz = FormatDispatcher.Create(PackageFormat.TarGz, Resolved, NullBundleLogger.Instance);
        Assert.IsType<ArchiveBundler>(zip);
        Assert.IsType<ArchiveBundler>(targz);
    }
}
