using DotNet.Bundler.Wix;

namespace Bundler.UnitTests.Wix;

/// <summary>
/// WixLocale：Bundler 自带字符串表 + 调用方 .wxl 覆盖文件校验。
/// 结构校验面（文件存在/根元素/Culture/Id）与编码校验面分离——后者走
/// WideCharToMultiByte 只在 Windows 宿主可验，codepage=0 时跳过编码校验。
/// </summary>
public sealed class WixLocaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wixlocale-" + Guid.NewGuid().ToString("N"));

    public WixLocaleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteWxl(string body, string name = "custom.wxl")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, body);
        return path;
    }

    private static readonly WixLanguageInfo En = WixLanguageInfo.Resolve("en-US");

    [Fact]
    public void Defaults_AllIdsPresent_PerCulture()
    {
        var en = WixLocale.Defaults(WixLanguageInfo.Resolve("en-US"), "ProgramFiles64Folder");
        var zh = WixLocale.Defaults(WixLanguageInfo.Resolve("zh-CN"), "ProgramFiles64Folder");
        Assert.Equal(WixLocale.Ids.OrderBy(i => i), en.Keys.OrderBy(k => k));
        Assert.Equal(WixLocale.Ids.OrderBy(i => i), zh.Keys.OrderBy(k => k));
        Assert.NotEqual(en["BundlerDowngradeErrorMessage"], zh["BundlerDowngradeErrorMessage"]);
        // zh-CN 中文默认值，其他区域回落英文。
        var de = WixLocale.Defaults(WixLanguageInfo.Resolve("de-DE"), "ProgramFiles64Folder");
        Assert.Equal(en["BundlerDowngradeErrorMessage"], de["BundlerDowngradeErrorMessage"]);
    }

    [Fact]
    public void ReadCallerStrings_MissingOrWrongExtension_Rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            WixLocale.ReadCallerStrings(Path.Combine(_root, "ghost.wxl"), En, 0));
        var txt = WriteWxl("<x/>", "strings.txt");
        Assert.Throws<ArgumentException>(() => WixLocale.ReadCallerStrings(txt, En, 0));
    }

    [Fact]
    public void ReadCallerStrings_BadXmlOrRoot_Rejected()
    {
        var notXml = WriteWxl("this is not xml");
        Assert.Throws<ArgumentException>(() => WixLocale.ReadCallerStrings(notXml, En, 0));
        var wrongRoot = WriteWxl("<WixLocalization/>"); // 无命名空间
        Assert.Throws<ArgumentException>(() => WixLocale.ReadCallerStrings(wrongRoot, En, 0));
    }

    [Fact]
    public void ReadCallerStrings_CultureMismatch_Rejected()
    {
        var path = WriteWxl("""
            <WixLocalization xmlns="http://schemas.microsoft.com/wix/2006/localization"
                             Culture="de-DE"><String Id="X">v</String></WixLocalization>
            """);
        var ex = Assert.Throws<ArgumentException>(() => WixLocale.ReadCallerStrings(path, En, 0));
        Assert.Contains("de-DE", ex.Message);
    }

    [Fact]
    public void ReadCallerStrings_StringWithoutId_Rejected()
    {
        var path = WriteWxl("""
            <WixLocalization xmlns="http://schemas.microsoft.com/wix/2006/localization"
                             Culture="en-US"><String>orphan</String></WixLocalization>
            """);
        Assert.Throws<ArgumentException>(() => WixLocale.ReadCallerStrings(path, En, 0));
    }

    [Fact]
    public void ReadCallerStrings_Valid_ParseAndOverrides()
    {
        var path = WriteWxl("""
            <WixLocalization xmlns="http://schemas.microsoft.com/wix/2006/localization"
                             Culture="en-US">
              <String Id="BundlerLaunchCheckboxText">Start it now</String>
            </WixLocalization>
            """);
        var strings = WixLocale.ReadCallerStrings(path, En, 0); // 0 → 跳过宿主编码校验
        Assert.Equal("Start it now", strings["BundlerLaunchCheckboxText"]);
    }

    [Fact]
    public void UnencodableIds_CodepageZero_SkipsCheck()
    {
        Assert.Empty(WixLocale.UnencodableIds(
            new Dictionary<string, string> { ["X"] = "任意字符" }, 0));
    }

    [Fact]
    public void UnencodableIds_RealCodepage_WindowsOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // WideCharToMultiByte 是 Windows 内核 API
        }
        var bad = WixLocale.UnencodableIds(
            new Dictionary<string, string> { ["Chinese"] = "中文" }, 1252);
        Assert.Equal(["Chinese"], bad);
        var ok = WixLocale.UnencodableIds(
            new Dictionary<string, string> { ["Ascii"] = "plain" }, 1252);
        Assert.Empty(ok);
    }
}
