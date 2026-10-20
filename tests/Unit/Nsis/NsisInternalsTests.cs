using System.Runtime.InteropServices;
using DotNet.Bundler.Core;
using DotNet.Bundler.Nsis;

namespace Bundler.UnitTests.Nsis;

/// <summary>NSIS 版本/文件名/模板/语言目录内件——全进程内，不触编译器。</summary>
public sealed class NsisInternalsTests
{
    // ---- SemanticVersion（SemVer 2.0，不转 int 以便超 Int64 标识符仍可比） ----

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.0.0")]
    [InlineData("1.2.3-alpha.1")]
    [InlineData("1.2.3+build.5")]
    [InlineData("1.2.3-alpha.1+build")]
    [InlineData("99999999999999999999.0.0")] // 超 Int64 数值标识符仍合法
    public void SemVer_Valid_Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.NotNull(version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")] // 前导零
    [InlineData("1.2.3-")] // 空预发布
    [InlineData("1.2.3+")] // 空 build
    [InlineData("1.2.3-01")] // 预发布数值前导零
    [InlineData("1.2.3-中文")] // 非 ASCII
    public void SemVer_Invalid_Rejected(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0", "1.0.0-alpha", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.2", -1)]
    [InlineData("1.0.0-2", "1.0.0-10", -1)] // 数值段按数值非字典序
    [InlineData("99999999999999999999.0.0", "100000000000000000000.0.0", -1)] // 超长数值按位长比
    [InlineData("1.0.0+aaa", "1.0.0+bbb", 0)] // build 不比
    public void SemVer_Ordering(string left, string right, int expected)
    {
        SemanticVersion.TryParse(left, out var a);
        SemanticVersion.TryParse(right, out var b);
        Assert.Equal(expected, Math.Sign(a!.CompareTo(b)));
    }

    [Theory]
    [InlineData("1.2.3", true, "1.2.3.0")]
    [InlineData("65535.65535.65535", true, "65535.65535.65535.0")]
    [InlineData("1.2.3-alpha", true, "1.2.3.0")] // 预发布段不进门——VERSIONINFO 只取核
    public void SemVer_WindowsNumeric(string text, bool ok, string rendered)
    {
        SemanticVersion.TryParse(text, out var version);
        Assert.Equal(ok, version!.TryGetWindowsNumericVersion(out var numeric));
        if (ok)
        {
            Assert.Equal(rendered, numeric);
        }
    }

    [Fact]
    public void SemVer_OverflowingSegment_NotWindowsNumeric()
    {
        SemanticVersion.TryParse("65536.0.0", out var version);
        Assert.False(version!.TryGetWindowsNumericVersion(out _));
    }

    // ---- WindowsFileNames ----

    [Theory]
    [InlineData("app.exe", false)]
    [InlineData("my app.txt", false)]
    [InlineData("a<b", true)]
    [InlineData("a:b", true)]
    [InlineData("a\"b", true)]
    [InlineData("a/b", true)]
    [InlineData("a\\b", true)]
    [InlineData("a|b", true)]
    [InlineData("a?b", true)]
    [InlineData("a*b", true)]
    [InlineData("a", true)]
    [InlineData("con", true)]
    [InlineData("CON", true)]
    [InlineData("con.tar.gz", true)] // 保留名按首个 '.' 前基名判
    [InlineData("COM9", true)]
    [InlineData("lpt1", true)]
    [InlineData("console", false)]
    [InlineData("my-app ", true)] // 尾空格
    [InlineData("my-app.", true)] // 尾点
    public void WindowsName_Validity(string name, bool invalid)
    {
        Assert.Equal(invalid, WindowsFileNames.IsInvalidName(name));
    }

    [Fact]
    public void WindowsName_Sanitize_ReplacesUnsafe()
    {
        Assert.Equal("a_b_c", WindowsFileNames.Sanitize("a<b>c"));
        Assert.Equal("a_1", WindowsFileNames.Sanitize("a\u00011"));
    }

    // ---- TemplateRenderer ----

    [Fact]
    public void Template_Render_SubstitutesKnownTokens()
    {
        var rendered = TemplateRenderer.Render(
            "Name={{name}} Ver={{version}}",
            new Dictionary<string, string> { ["name"] = "App", ["version"] = "1.0" });
        Assert.Equal("Name=App Ver=1.0", rendered);
    }

    [Fact]
    public void Template_Render_UnknownToken_Throws()
    {
        Assert.Throws<InvalidDataException>(() =>
            TemplateRenderer.Render("{{missing}}", new Dictionary<string, string>()));
    }

    [Fact]
    public void Template_Render_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TemplateRenderer.Render(null!, new Dictionary<string, string>()));
        Assert.Throws<ArgumentNullException>(() =>
            TemplateRenderer.Render("x", null!));
    }

    // ---- NsisLanguageCatalog ----

    [Fact]
    public void LanguageCatalog_KeysAreUnique()
    {
        var names = NsisLanguageCatalog.Definitions.Select(d => d.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        var constants = NsisLanguageCatalog.Definitions.Select(d => d.ConstantName).ToArray();
        Assert.Equal(constants.Length, constants.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("English", "ENGLISH")]
    [InlineData("english", "ENGLISH")] // 大小写不敏感
    [InlineData("SimpChinese", "SIMPCHINESE")]
    public void LanguageCatalog_Resolve(string name, string constant)
    {
        Assert.Equal(constant, NsisLanguageCatalog.Resolve(name).ConstantName);
    }

    [Fact]
    public void LanguageCatalog_Persian_CompilerNameIsFarsi()
    {
        // NSIS 编译器侧叫 Farsi——ConstantName 是源内键，NsisName 喂编译器。
        var persian = NsisLanguageCatalog.Resolve("Persian");
        Assert.Equal("FARSI", persian.ConstantName);
        Assert.Equal("Farsi", persian.NsisName);
    }

    [Fact]
    public void LanguageCatalog_Unknown_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => NsisLanguageCatalog.Resolve("Klingon"));
    }

    // ---- 工具集路径映射（纯映射，不物化） ----

    [Fact]
    public void CompilerRelativePath_HostMatrix()
    {
        var win = NsisToolResolver.GetCompilerRelativePath(OSPlatform.Windows, Architecture.X64);
        var linux = NsisToolResolver.GetCompilerRelativePath(OSPlatform.Linux, Architecture.X64);
        var mac = NsisToolResolver.GetCompilerRelativePath(OSPlatform.OSX, Architecture.Arm64);
        var linuxArm = NsisToolResolver.GetCompilerRelativePath(OSPlatform.Linux, Architecture.Arm64);
        // Windows 编译器恒为 win-x86 件；POSIX 按宿主架构分桶。
        Assert.Contains("win-x86", win);
        Assert.EndsWith("makensis.exe", win.Replace('/', '\\'));
        Assert.Contains("linux-x64", linux);
        Assert.Contains("osx-arm64", mac);
        Assert.Contains("linux-arm64", linuxArm);
        Assert.Throws<PlatformNotSupportedException>(() =>
            NsisToolResolver.GetCompilerRelativePath(OSPlatform.Linux, Architecture.X86));
    }
}
