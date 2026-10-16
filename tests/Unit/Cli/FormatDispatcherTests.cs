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

    /// <summary>
    /// 枚举全表对账：PackageFormat 每个成员都要落到声明的后端类型。
    /// 新增枚举成员不进表 → Count 断言先炸；臂落错类型 → IsType 炸。
    /// </summary>
    [Fact]
    public void Create_EveryPackageFormat_MapsToDeclaredBackend()
    {
        var expected = new Dictionary<PackageFormat, Type>
        {
            [PackageFormat.Nsis] = typeof(NsisBundler),
            [PackageFormat.Msi] = typeof(WixBundler),
            [PackageFormat.App] = typeof(MacAppBundler),
            [PackageFormat.Dmg] = typeof(MacDmgBundler),
            [PackageFormat.Pkg] = typeof(MacPkgBundler),
            [PackageFormat.Deb] = typeof(DebBundler),
            [PackageFormat.Rpm] = typeof(RpmBundler),
            [PackageFormat.AppImage] = typeof(AppImageBundler),
            [PackageFormat.Zip] = typeof(ArchiveBundler),
            [PackageFormat.TarGz] = typeof(ArchiveBundler),
            [PackageFormat.AlpineApk] = typeof(AlpineApkBundler),
        };
        Assert.Equal(Enum.GetValues<PackageFormat>().Length, expected.Count);
        foreach (var (format, backendType) in expected)
        {
            Assert.IsType(backendType,
                FormatDispatcher.Create(format, Resolved, NullBundleLogger.Instance));
        }
    }

    /// <summary>'all' 哨兵按 target 展开为 TargetMatrix 支持集——literal 清单对账不照抄实现。</summary>
    [Theory]
    [InlineData("windows-x86_64", new[] { PackageFormat.Nsis, PackageFormat.Msi, PackageFormat.Zip, PackageFormat.TarGz })]
    [InlineData("macos-aarch64", new[] { PackageFormat.App, PackageFormat.Dmg, PackageFormat.Pkg, PackageFormat.Zip, PackageFormat.TarGz })]
    [InlineData("linux-x86_64", new[] { PackageFormat.Deb, PackageFormat.Rpm, PackageFormat.AppImage, PackageFormat.Zip, PackageFormat.TarGz })]
    [InlineData("linux-musl-x86_64", new[] { PackageFormat.AppImage, PackageFormat.Zip, PackageFormat.TarGz, PackageFormat.AlpineApk })]
    public void ParseFormats_All_ExpandsToTargetMatrixRow(string target, PackageFormat[] expected)
    {
        Assert.Equal(expected.OrderBy(f => f).ToArray(),
            CliProgram.ParseFormats("all", target).OrderBy(f => f).ToArray());
    }

    [Fact]
    public void ParseFormats_All_UnknownTarget_Rejected()
    {
        Assert.Throws<CliUsageException>(() => CliProgram.ParseFormats("all", "plan9-x86"));
    }

    [Theory]
    // 分隔符两种都收；大小写不敏感；重复词去重；空白容忍。
    [InlineData("deb,rpm", new[] { PackageFormat.Deb, PackageFormat.Rpm })]
    [InlineData("deb;rpm", new[] { PackageFormat.Deb, PackageFormat.Rpm })]
    [InlineData("DEB;Deb;deb", new[] { PackageFormat.Deb })]
    [InlineData(" zip , targz ", new[] { PackageFormat.Zip, PackageFormat.TarGz })]
    public void ParseFormats_SeparatorsCaseAndDedup(string words, PackageFormat[] expected)
    {
        Assert.Equal(expected, CliProgram.ParseFormats(words, "linux-x86_64").ToArray());
    }
}
