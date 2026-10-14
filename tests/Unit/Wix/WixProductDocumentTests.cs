using System.Xml.Linq;
using DotNet.Bundler;
using DotNet.Bundler.Wix;

namespace Bundler.UnitTests.Wix;

/// <summary>
/// WixProductDocument：.wxs 生成的结构契约——Product 属性集、MajorUpgrade 分支、
/// 范围根目录、NSIS 旧安装目录检索、definition-hash 版本冲突守卫。
/// 纯 XElement 生成，零编译器调用。
/// </summary>
public sealed class WixProductDocumentTests
{
    private static readonly XNamespace Wix = "http://schemas.microsoft.com/wix/2006/wi";

    private static XElement CreateDoc(
        WixBundleConfiguration? settings = null,
        string arch = "windows-x86_64",
        string? icon = null,
        string language = "en-US")
    {
        var bundle = new BundleConfiguration
        {
            ProductName = "Sample",
            Identifier = "com.example.sample",
            Version = "1.2.3",
            Publisher = "Example Corp",
        };
        var item = new BundlePlanItem(
            BundleTarget.Parse(arch), PackageFormat.Msi,
            InputDirectory: "in", MainExecutable: "app.exe",
            OutputDirectory: "out", Intermediate: false);
        var identity = WixIdentity.Create(
            bundle.Identifier, bundle.Version, arch,
            (settings ?? new WixBundleConfiguration()).InstallScope,
            language: WixLanguageInfo.Resolve(language));
        var root = new WixProductDocument(settings ?? new WixBundleConfiguration(),
                WixLanguageInfo.Resolve(language))
            .Create(bundle, item, identity, [], icon, "defhash");
        return root.Element(Wix + "Product")!; // Create 返回 <Wix> 包装根
    }

    [Fact]
    public void Product_CoreAttributes()
    {
        var product = CreateDoc();
        Assert.Equal("Sample", product.Attribute("Name")!.Value);
        Assert.Equal("Example Corp", product.Attribute("Manufacturer")!.Value);
        Assert.Equal("1.2.3", product.Attribute("Version")!.Value);
        // Product/Upgrade GUID 约定大写带括号。
        Assert.Matches(@"^\{[0-9A-F-]+\}$", product.Attribute("Id")!.Value);
        Assert.Matches(@"^\{[0-9A-F-]+\}$", product.Attribute("UpgradeCode")!.Value);
        var package = product.Element(Wix + "Package")!;
        Assert.Equal("yes", package.Attribute("Compressed")!.Value);
        Assert.Equal("perUser", package.Attribute("InstallScope")!.Value); // 默认 CurrentUser 范围
        Assert.Equal("yes", product.Element(Wix + "MediaTemplate")!.Attribute("EmbedCab")!.Value);
    }

    [Fact]
    public void Product_InstallScope_FlipsRoots()
    {
        var machine = CreateDoc(new WixBundleConfiguration { InstallScope = WixInstallScope.PerMachine });
        Assert.Equal("perMachine",
            machine.Element(Wix + "Package")!.Attribute("InstallScope")!.Value);
        var search = machine.Descendants(Wix + "RegistrySearch")
            .First(s => s.Attribute("Id")!.Value == "BundlerInstallDirSearch");
        Assert.Equal("HKLM", search.Attribute("Root")!.Value);
        // 默认 CurrentUser → HKCU。
        var user = CreateDoc();
        Assert.Equal("perUser",
            user.Element(Wix + "Package")!.Attribute("InstallScope")!.Value);
        Assert.Equal("HKCU", user.Descendants(Wix + "RegistrySearch")
            .First(s => s.Attribute("Id")!.Value == "BundlerInstallDirSearch")
            .Attribute("Root")!.Value);
    }

    [Fact]
    public void MajorUpgrade_DowngradePolicy()
    {
        var strict = CreateDoc();
        var upgrade = strict.Element(Wix + "MajorUpgrade")!;
        Assert.Equal("afterInstallInitialize", upgrade.Attribute("Schedule")!.Value);
        Assert.Null(upgrade.Attribute("AllowDowngrades"));
        Assert.NotNull(upgrade.Attribute("DowngradeErrorMessage"));

        var lax = CreateDoc(new WixBundleConfiguration { AllowDowngrades = true });
        Assert.Equal("yes", lax.Element(Wix + "MajorUpgrade")!.Attribute("AllowDowngrades")!.Value);
    }

    [Fact]
    public void NsisLegacyDirSearch_FourViews_ArchView()
    {
        var product = CreateDoc(arch: "windows-aarch64");
        var nsis = product.Descendants(Wix + "RegistrySearch")
            .Where(s => s.Attribute("Id")!.Value.StartsWith("BundlerNsisDirSearch"))
            .ToList();
        Assert.Equal(4, nsis.Count); // machine/user × legacy InstallLocation/new InstallRoot
        Assert.All(nsis, s => Assert.Equal("yes", s.Attribute("Win64")!.Value));

        var x86 = CreateDoc(arch: "windows-i686");
        Assert.All(
            x86.Descendants(Wix + "RegistrySearch")
                .Where(s => s.Attribute("Id")!.Value.StartsWith("BundlerNsisDirSearch")),
            s => Assert.Equal("no", s.Attribute("Win64")!.Value));
    }

    [Fact]
    public void DefinitionHash_VersionConflictGuard()
    {
        var product = CreateDoc();
        var condition = product.Element(Wix + "Condition")!;
        Assert.Contains("BUNDLER_INSTALLED_DEFINITION=BUNDLER_PACKAGE_DEFINITION", condition.Value);
        Assert.Equal("defhash",
            product.Descendants(Wix + "Property")
                .First(p => p.Attribute("Id")!.Value == "BUNDLER_PACKAGE_DEFINITION")
                .Attribute("Value")!.Value);
    }

    [Fact]
    public void Icon_Optional_Arps()
    {
        Assert.Null(CreateDoc().Descendants(Wix + "Property")
            .FirstOrDefault(p => p.Attribute("Id")!.Value == "ARPPRODUCTICON"));
        var withIcon = CreateDoc(icon: "app.ico");
        Assert.Equal("ProductIcon", withIcon.Descendants(Wix + "Property")
            .First(p => p.Attribute("Id")!.Value == "ARPPRODUCTICON").Attribute("Value")!.Value);
    }

    [Theory]
    [InlineData("normal.exe", "normal.exe")]
    [InlineData("a/b", "a_b")] // '/' 全宿主禁字符 → 下划线
    [InlineData("  ", "application")] // 全空回退
    public void SafeFileName_Sanitizes(string input, string expected)
    {
        Assert.Equal(expected, WixProductDocument.SafeFileName(input));
    }
}
