using DotNet.Bundler.Wix;

namespace Bundler.UnitTests.Wix;

/// <summary>
/// WiX 内件：身份确定性（UpgradeCode 族 / ProductCode 逐版逐语言）、语言目录、
/// 包内路径净化、扩展白名单校验。
/// </summary>
public sealed class WixInternalsTests : TempBase
{
    // ---- WixIdentity.Create ----

    [Fact]
    public void Identity_SameInput_SameCodes()
    {
        var a = WixIdentity.Create("com.example.app", "1.2.3", "windows-x86_64", WixInstallScope.PerMachine);
        var b = WixIdentity.Create("com.example.app", "1.2.3", "windows-x86_64", WixInstallScope.PerMachine);
        Assert.Equal(a.UpgradeCode, b.UpgradeCode);
        Assert.Equal(a.ProductCode, b.ProductCode);
        Assert.Equal("1.2.3", a.ProductVersion);
    }

    [Fact]
    public void Identity_DifferentVersion_SameUpgradeDifferentProduct()
    {
        var a = WixIdentity.Create("com.example.app", "1.2.3", "windows-x86_64", WixInstallScope.PerMachine);
        var b = WixIdentity.Create("com.example.app", "1.2.4", "windows-x86_64", WixInstallScope.PerMachine);
        Assert.Equal(a.UpgradeCode, b.UpgradeCode); // 版本不进族——升级链同码
        Assert.NotEqual(a.ProductCode, b.ProductCode); // ProductCode 逐件互异
    }

    [Fact]
    public void Identity_DifferentIdentifier_DifferentUpgrade()
    {
        var a = WixIdentity.Create("com.example.a", "1.0.0", "windows-x86_64", WixInstallScope.PerMachine);
        var b = WixIdentity.Create("com.example.b", "1.0.0", "windows-x86_64", WixInstallScope.PerMachine);
        Assert.NotEqual(a.UpgradeCode, b.UpgradeCode);
    }

    [Fact]
    public void Identity_DifferentScope_DifferentUpgrade()
    {
        var a = WixIdentity.Create("com.example.app", "1.0.0", "windows-x86_64", WixInstallScope.PerMachine);
        var b = WixIdentity.Create("com.example.app", "1.0.0", "windows-x86_64", WixInstallScope.CurrentUser);
        Assert.NotEqual(a.UpgradeCode, b.UpgradeCode);
    }

    [Fact]
    public void Identity_DifferentLanguage_DifferentCodes()
    {
        var a = WixIdentity.Create("com.example.app", "1.0.0", "windows-x86_64",
            WixInstallScope.PerMachine, language: WixLanguageInfo.Resolve("en-US"));
        var b = WixIdentity.Create("com.example.app", "1.0.0", "windows-x86_64",
            WixInstallScope.PerMachine, language: WixLanguageInfo.Resolve("zh-CN"));
        Assert.NotEqual(a.UpgradeCode, b.UpgradeCode);
        Assert.NotEqual(a.ProductCode, b.ProductCode);
    }

    [Fact]
    public void Identity_TargetAlias_CanonicalizeToSameUpgrade()
    {
        // windows-arm64 是 windows-aarch64 的 alias——同一族必须同 UpgradeCode。
        var alias = WixIdentity.Create("com.example.app", "1.0.0", "windows-arm64", WixInstallScope.PerMachine);
        var canonical = WixIdentity.Create("com.example.app", "1.0.0", "windows-aarch64", WixInstallScope.PerMachine);
        Assert.Equal(alias.UpgradeCode, canonical.UpgradeCode);
    }

    [Fact]
    public void Identity_ExplicitUpgradeCode_UsedVerbatim()
    {
        var code = Guid.NewGuid();
        var identity = WixIdentity.Create("com.example.app", "1.0.0", "windows-x86_64",
            WixInstallScope.PerMachine, upgradeCode: code.ToString("B"));
        Assert.Equal(code, identity.UpgradeCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")] // 空 GUID 拒
    public void Identity_BadUpgradeCode_Rejected(string code)
    {
        Assert.Throws<ArgumentException>(() => WixIdentity.Create(
            "com.example.app", "1.0.0", "windows-x86_64", WixInstallScope.PerMachine, upgradeCode: code));
    }

    [Theory]
    [InlineData("256.0.0")]   // major > 255
    [InlineData("1.256.0")]   // minor > 255
    [InlineData("1.0.65536")] // patch > 65535
    [InlineData("1.0.0-beta")] // 预发布不进 MSI
    [InlineData("1.0")]        // 必须三段
    [InlineData("v1.0.0")]
    public void Identity_MsiVersionBounds(string version)
    {
        Assert.Throws<ArgumentException>(() => WixIdentity.Create(
            "com.example.app", version, "windows-x86_64", WixInstallScope.PerMachine));
    }

    [Fact]
    public void Identity_NonWindowsTarget_Rejected()
    {
        Assert.Throws<NotSupportedException>(() => WixIdentity.Create(
            "com.example.app", "1.0.0", "linux-x86_64", WixInstallScope.PerMachine));
    }

    [Fact]
    public void Identity_PackageVersion_OverridesMsiVersion()
    {
        var identity = WixIdentity.Create("com.example.app", "1.0.0-beta.1",
            "windows-x86_64", WixInstallScope.PerMachine, packageVersion: "1.0.0");
        Assert.Equal("1.0.0", identity.ProductVersion);
    }

    // ---- ComponentCode / StableId ----

    [Fact]
    public void ComponentCode_DeterministicAndPathDistinct()
    {
        var a = WixIdentity.ComponentCode("com.example.app", "windows-x86_64",
            WixInstallScope.PerMachine, "bin/app.exe");
        var again = WixIdentity.ComponentCode("com.example.app", "windows-x86_64",
            WixInstallScope.PerMachine, "bin/app.exe");
        var other = WixIdentity.ComponentCode("com.example.app", "windows-x86_64",
            WixInstallScope.PerMachine, "bin/other.exe");
        Assert.Equal(a, again);
        Assert.NotEqual(a, other);
    }

    [Fact]
    public void StableId_PrefixPlus24UpperHex()
    {
        var id = WixIdentity.StableId("Fil", "bin/app.exe");
        Assert.StartsWith("Fil", id);
        Assert.Equal(27, id.Length);
        Assert.Matches("^[0-9A-F]{24}$", id.Substring(3));
        Assert.Equal(id, WixIdentity.StableId("Fil", "BIN/APP.EXE")); // 大小写不敏感
    }

    // ---- WixLanguageInfo ----

    [Theory]
    [InlineData("en-US", 1033, 1252, "")]
    [InlineData("zh-CN", 2052, 936, "-zh-cn")]
    [InlineData("EN-us", 1033, 1252, "")] // 大小写不敏感
    public void Language_Resolve(string culture, int lcid, int codepage, string suffix)
    {
        var info = WixLanguageInfo.Resolve(culture);
        Assert.Equal(lcid, info.Lcid);
        Assert.Equal(codepage, info.Codepage);
        Assert.Equal(suffix, info.Suffix);
    }

    [Fact]
    public void Language_UnknownOrEmpty_Rejected()
    {
        Assert.Throws<ArgumentException>(() => WixLanguageInfo.Resolve("kl-KL"));
        Assert.Throws<ArgumentException>(() => WixLanguageInfo.Resolve(""));
    }

    [Fact]
    public void Language_HiIn_DeliberatelyAbsent()
    {
        // hi-IN 走 LGHT0311 失败被刻意排除（WiX 3.14.1 资源缺）——防回归误加回。
        Assert.DoesNotContain(WixLanguageInfo.Supported, e => e.Culture == "hi-IN");
        Assert.DoesNotContain(WixLanguageInfo.Supported, e => e.Culture == "kk-KZ");
    }

    // ---- WixPackagePaths ----

    [Theory]
    [InlineData("app.exe", "app.exe")]
    [InlineData("bin/app.exe", "bin/app.exe")]
    [InlineData("bin\\app.exe", "bin/app.exe")] // 反斜杠统一
    public void PackagePath_Valid(string input, string expected)
    {
        Assert.Equal(expected, WixPackagePaths.NormalizeTarget(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:/abs/app.exe")]
    [InlineData("\\unc\\share")]
    [InlineData("/abs/app.exe")]
    [InlineData("../escape.exe")]
    [InlineData("a//b.exe")]
    [InlineData("a/./b.exe")]
    [InlineData("con/app.exe")] // 段内设备名
    [InlineData("bad<name/app.exe")]
    [InlineData("trail /app.exe")] // 段尾空格
    [InlineData("trail./app.exe")] // 段尾点
    public void PackagePath_Unsafe_Rejected(string input)
    {
        Assert.Throws<ArgumentException>(() => WixPackagePaths.NormalizeTarget(input));
    }

    // ---- WixExtensionValidator ----

    [Fact]
    public void Fragment_AllowlistedCoreElements_Pass()
    {
        var dir = Temp("wix-frag");
        var path = Path.Combine(dir.FullName, "frag.wxs");
        File.WriteAllText(path, """
            <Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
              <Fragment><ComponentGroup Id="Acme.Files"><Component Id="Acme.F1"/></ComponentGroup></Fragment>
            </Wix>
            """);
        var content = WixExtensionValidator.ValidateFragment(path, "Acme.");
        Assert.Contains("Acme.Files", content);
    }

    [Fact]
    public void Fragment_NonCoreNamespace_Rejected()
    {
        var dir = Temp("wix-frag");
        var path = Path.Combine(dir.FullName, "frag.wxs");
        File.WriteAllText(path, """
            <Wix xmlns="http://schemas.microsoft.com/wix/2006/wi"
                 xmlns:ui="http://schemas.microsoft.com/wix/UIExtension">
              <Fragment><ui:Dialog Id="Acme.D"/></Fragment>
            </Wix>
            """);
        Assert.Throws<ArgumentException>(() => WixExtensionValidator.ValidateFragment(path, "Acme."));
    }

    [Fact]
    public void Fragment_NonWhitelistedElement_Rejected()
    {
        var dir = Temp("wix-frag");
        var path = Path.Combine(dir.FullName, "frag.wxs");
        File.WriteAllText(path, """
            <Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
              <Fragment><CustomAction Id="Acme.Run" ExeCommand="x"/></Fragment>
            </Wix>
            """);
        Assert.Throws<ArgumentException>(() => WixExtensionValidator.ValidateFragment(path, "Acme."));
    }

    [Fact]
    public void Fragment_ForeignIdPrefix_Rejected()
    {
        var dir = Temp("wix-frag");
        var path = Path.Combine(dir.FullName, "frag.wxs");
        File.WriteAllText(path, """
            <Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
              <Fragment><ComponentGroup Id="Other.Files"/></Fragment>
            </Wix>
            """);
        Assert.Throws<ArgumentException>(() => WixExtensionValidator.ValidateFragment(path, "Acme."));
    }

    [Fact]
    public void Fragment_MissingOrNonWxs_Rejected()
    {
        var dir = Temp("wix-frag");
        var missing = Path.Combine(dir.FullName, "gone.wxs");
        Assert.Throws<ArgumentException>(() =>
            WixExtensionValidator.ValidateFragment(missing, "Acme."));
        var txt = Path.Combine(dir.FullName, "frag.txt");
        File.WriteAllText(txt, "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\"/>");
        Assert.Throws<ArgumentException>(() =>
            WixExtensionValidator.ValidateFragment(txt, "Acme."));
    }

    [Fact]
    public void Ref_MustCarryPrefix()
    {
        WixExtensionValidator.ValidateRef("Acme.Files", "Acme.", "componentGroup");
        Assert.Throws<ArgumentException>(() =>
            WixExtensionValidator.ValidateRef("Other.Files", "Acme.", "componentGroup"));
        Assert.Throws<ArgumentException>(() =>
            WixExtensionValidator.ValidateRef("bad id!", "Acme.", "componentGroup"));
    }

    [Theory]
    [InlineData("Acme.", true)]
    [InlineData("Acme.Plugins.", true)]
    [InlineData("Acme", false)]       // 必须以 '.' 收尾
    [InlineData("WixUI_", false)]     // 预留前缀
    [InlineData("Bundler.X.", false)] // 预留
    [InlineData("Cmp.X.", false)]     // 预留
    [InlineData("bad prefix.", false)]
    public void IdPrefix_Validation(string prefix, bool valid)
    {
        if (valid)
        {
            Assert.Equal(prefix, WixExtensionValidator.ValidateIdPrefix(prefix));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => WixExtensionValidator.ValidateIdPrefix(prefix));
        }
    }

    [Fact]
    public void DetectExtensionDlls_FindsBundledNames()
    {
        var dir = Temp("wix-ext");
        File.WriteAllText(Path.Combine(dir.FullName, "WixUIExtension.dll"), "stub");
        var found = WixExtensionValidator.DetectExtensionDlls(
            """<Wix xmlns="http://schemas.microsoft.com/wix/UIExtension"/>""", dir.FullName);
        Assert.Single(found);
        Assert.EndsWith("WixUIExtension.dll", found[0]);
        // 核心 wi 命名空间不算扩展。
        Assert.Empty(WixExtensionValidator.DetectExtensionDlls(
            """<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi"/>""", dir.FullName));
    }

    // ---- AppendReferencedSourceHashes ----

    [Fact]
    public void ReferencedSources_PercentLiteralName_Fingerprinted()
    {
        // % 不是 WiX 预处理器语法——含 % 的字面文件名必须照常进指纹，
        // 否则其内容变更不再翻转指纹。
        var dir = Temp("wix-refsrc");
        File.WriteAllText(Path.Combine(dir.FullName, "percent%name.txt"), "content");
        var fragment = Path.Combine(dir.FullName, "frag.wxs");
        File.WriteAllText(fragment, """
            <Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
              <Fragment><Component><File Source="percent%name.txt"/></Component></Fragment>
            </Wix>
            """);
        var text = new System.Text.StringBuilder();
        WixBundleBackend.AppendReferencedSourceHashes(text, fragment);
        var output = text.ToString();
        Assert.Contains("percent%name.txt", output, StringComparison.Ordinal);
        Assert.DoesNotContain("MISSING", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencedSources_PreprocessorAndMissing_Covered()
    {
        var dir = Temp("wix-refsrc2");
        var fragment = Path.Combine(dir.FullName, "frag.wxs");
        File.WriteAllText(fragment, """
            <Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
              <Fragment>
                <Component><File Source="$(var.BuildDir)\dyn.dll"/></Component>
                <Component><File Source="!(bindpath.binsecrets)\gen.dll"/></Component>
                <Component><File Source="literal$(paren).txt"/></Component>
                <Component><File Source="$(varx.fake)near-miss.txt"/></Component>
                <Component><File Source="absent-file.txt"/></Component>
              </Fragment>
            </Wix>
            """);
        var text = new System.Text.StringBuilder();
        WixBundleBackend.AppendReferencedSourceHashes(text, fragment);
        var output = text.ToString();
        // 编译期展开的已知命名空间引用不进指纹（既定口径）；
        // 未知 $()/!() 串按字面处理——多半落 MISSING 哨兵，可观测。
        Assert.DoesNotContain("dyn.dll", output, StringComparison.Ordinal);
        Assert.DoesNotContain("gen.dll", output, StringComparison.Ordinal);
        Assert.Contains("literal$(paren).txt", output, StringComparison.Ordinal);
        // $(varx.*) 非文档化命名空间——按字面处理而非静默跳过。
        Assert.Contains("near-miss.txt", output, StringComparison.Ordinal);
        Assert.Contains("MISSING", output, StringComparison.Ordinal);
        Assert.Contains("absent-file.txt", output, StringComparison.Ordinal);
    }

    // ---- WixLocale ----

    [Fact]
    public void Locale_Defaults_ProvideEnglishAndChinese()
    {
        var english = WixLocale.Defaults(WixLanguageInfo.Resolve("en-US"), "ProgramFiles64Folder");
        var chinese = WixLocale.Defaults(WixLanguageInfo.Resolve("zh-CN"), "ProgramFiles64Folder");
        Assert.Equal("A newer version of [ProductName] is already installed.",
            english["BundlerDowngradeErrorMessage"]);
        Assert.Equal("已安装较新版本的 [ProductName]。", chinese["BundlerDowngradeErrorMessage"]);
        // {0} 占位被 scopeRoot 填。
        Assert.Contains("ProgramFiles64Folder", english["BundlerInstallDirScopeError"]);
    }
}
