using DotNet.Bundler;
using DotNet.Bundler.AppImage;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.AppImage;

/// <summary>
/// <see cref="AppDirBuilder"/> 的预构建校验面——版本令牌、installRoot、
/// mainExecutable 元字符拒收都在任何 chmod/ln 子进程之前抛，故可在单元层断言。
/// AppDir 全量落树（desktop/图标/AppRun/根链）属集成层。
/// </summary>
public sealed class AppDirBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "adir-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string InputWithMain()
    {
        var input = Path.Combine(_root, "in-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "app"), "elf");
        return input;
    }

    private BundlePlanItem Item(string input, string mainExecutable = "app") =>
        new(BundleTarget.Parse("linux-x86_64"), PackageFormat.AppImage,
            input, mainExecutable, _root, false);

    private static BundleConfiguration Bundle(string version = "1.0.0") => new()
    {
        ProductName = "Sample App",
        Identifier = "com.example.sample",
        Version = version,
    };

    [Theory]
    [InlineData("1.0 beta")]   // 空白
    [InlineData("a/b")]        // 斜杠
    [InlineData("a\\b")]       // 反斜杠
    [InlineData("")]
    public void Build_BadVersionToken_Rejected(string version)
    {
        var settings = new AppImageBundleConfiguration { Version = version };
        Assert.Throws<ArgumentException>(() => AppDirBuilder.Build(
            Bundle(), Item(InputWithMain()), settings, _root, NullBundleLogger.Instance));
    }

    [Theory]
    [InlineData("/abs/root")]
    [InlineData("../escape")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("has space/dir")]
    [InlineData("quote\"dir")]
    [InlineData("dollar$dir")]
    [InlineData("tick`dir")]
    public void Build_UnsafeInstallRoot_Rejected(string installRoot)
    {
        var settings = new AppImageBundleConfiguration { InstallRoot = installRoot };
        Assert.Throws<ArgumentException>(() => AppDirBuilder.Build(
            Bundle(), Item(InputWithMain()), settings, _root, NullBundleLogger.Instance));
    }

    [Theory]
    [InlineData("app;rm -rf")]  // 分号
    [InlineData("app name")]    // 空白
    [InlineData("app\"name")]   // 引号
    [InlineData("$(whoami)")]   // 命令展开
    [InlineData("`id`")]        // 反引号
    [InlineData("a/b/../c")]    // 逃逸段
    public void Build_UnsafeMainExecutable_Rejected(string executable)
    {
        // 校验在文件存在性检查之前——指向不存在的元字符名同样被拒。
        Assert.Throws<ArgumentException>(() => AppDirBuilder.Build(
            Bundle(), Item(InputWithMain(), executable), new AppImageBundleConfiguration(),
            _root, NullBundleLogger.Instance));
    }

    [Fact]
    public void Build_MissingMainExecutable_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => AppDirBuilder.Build(
            Bundle(), Item(InputWithMain(), "ghost"), new AppImageBundleConfiguration(),
            _root, NullBundleLogger.Instance));
    }

    [Fact]
    public void Build_BadArchitectureOverride_Rejected()
    {
        var settings = new AppImageBundleConfiguration { Architecture = "armv7" };
        Assert.Throws<ArgumentException>(() => AppDirBuilder.Build(
            Bundle(), Item(InputWithMain()), settings, _root, NullBundleLogger.Instance));
    }
}
