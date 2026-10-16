using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Wix;
using DotNet.Bundler.Signing.Windows;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;


// 从旧 tests/Bundler.Tests/WixTests.cs 收割。
// 真实 candle.exe/light.exe 构建、进程取消与编译警告腿全部属 spawn，
// 去向见 Integration/Manual 层对应文件：CancelsHangingWixToolProcess、
// RejectsWixCompilerWarnings、BuildsAndInspectsMsi、ReusesOnlyVerifiedMsi、
// RejectsChangedSameVersionPayload、RecoversTamperedWixCompiler、
// SerializesConcurrentWixCacheUsers、BuildsMachineMsi、BuildsArm64TargetedMsi、
// BuildsX86TargetedMsi、BuildsExplicitVersionAndDowngradePolicy、
// GeneratesDistinctPackageCodes、BuildsLocalizedMsi、SignsMsiArtifacts、
// RemovesFailedSignedMsi、SignsRealMsiWithTestCertificate、BuildsFullFeaturedMsi、
// BuildsCustomUiWithoutLicense、BuildsLicensedDirectorySelection、
// EnforcesMsiInstallDirectoryRoot、GeneratesLaunchCheckboxOnly、
// BuildsEveryDeclaredMsiLanguage、AppliesCallerMsiLocaleOverrides、
// BuildsFipsMsiWithIconShortcuts、BuildsRegularMsiExtensionFragments、
// RejectsChangedFragmentReferencedFile、BuildsExpertMsiTemplate，
// 以及 ValidatesMsiBitmapInputs 的合法位图真实构建腿。
// 判重跳过：RejectsInvalidWindowsMsiTargetNames（PackagePath_Valid/PackagePath_Unsafe_Rejected）。
namespace Bundler.UnitTests.Wix;

public sealed class WixTests : TempBase
{

    [Fact]
    public void KeepsMsiIdentityStable()
    {
        var first = WixIdentity.Create("com.Example.App", "1.2.3", "windows-x86_64", WixInstallScope.CurrentUser);
        var repeated = WixIdentity.Create("com.example.app", "1.2.3", "windows-x86_64", WixInstallScope.CurrentUser);
        var next = WixIdentity.Create("com.example.app", "1.2.4", "windows-x86_64", WixInstallScope.CurrentUser);
        var arm = WixIdentity.Create("com.example.app", "1.2.3", "windows-aarch64", WixInstallScope.CurrentUser);
        var x86 = WixIdentity.Create("com.example.app", "1.2.3", "windows-i686", WixInstallScope.CurrentUser);
        Assert.True(first.UpgradeCode == repeated.UpgradeCode && first.ProductCode == repeated.ProductCode,
            "MSI identity must be stable across builds and identifier casing.");
        Assert.True(first.UpgradeCode == Guid.Parse("38ed3cdb-48c9-5d95-b583-fe49d4d7523a") &&
               first.ProductCode == Guid.Parse("6c6d7711-3725-534f-9129-7f4532b38429"),
            "MSI UUIDv5 identity differs from the RFC 4122 test vector for the frozen namespace and input.");
        Assert.True(first.UpgradeCode == next.UpgradeCode && first.ProductCode != next.ProductCode,
            "A new product version must change ProductCode and preserve UpgradeCode.");
        Assert.True(first.UpgradeCode != arm.UpgradeCode && first.ProductCode != arm.ProductCode,
            "Separate architectures must have separate MSI identities.");
        Assert.True(first.UpgradeCode != x86.UpgradeCode && arm.UpgradeCode != x86.UpgradeCode &&
               first.ProductCode != x86.ProductCode,
            "The x86 product line must be isolated from x64 and ARM64.");
        Assert.True(x86.UpgradeCode == Guid.Parse("59093a91-b3e8-5f25-80e1-374ad0716810") &&
               x86.ProductCode == Guid.Parse("327a5767-ecab-5080-b5ce-1f95ed824181"),
            "The new x86 English identity differs from its fixed test vector.");
        var migrated = WixIdentity.Create("com.example.app", "1.2.3", "windows-x86_64",
            WixInstallScope.CurrentUser, "{11111111-2222-3333-4444-555555555555}");
        Assert.True(migrated.UpgradeCode == Guid.Parse("11111111-2222-3333-4444-555555555555") &&
               migrated.ProductCode != first.ProductCode,
            "An explicit historical UpgradeCode did not establish a separate product family.");
        foreach (var version in new[] { "1.0.0-beta.1", "1.0.0+meta", "1.0.0.1", "256.0.0", "1.256.0", "1.0.65536", "01.0.0" })
        {
            var rejected = Assert.ThrowsAny<ArgumentException>(
                () => WixIdentity.Create("com.example.app", version, "windows-x86_64", WixInstallScope.CurrentUser));
            Assert.Equal("version", rejected.ParamName);
        }
        Assert.Equal("255.255.65535",
            WixIdentity.Create("com.example.app", "255.255.65535", "windows-x86_64", WixInstallScope.CurrentUser).ProductVersion);
        var machine = WixIdentity.Create("com.example.app", "1.0.0", "windows-x86_64", WixInstallScope.PerMachine);
        var user = WixIdentity.Create("com.example.app", "1.0.0", "windows-x86_64", WixInstallScope.CurrentUser);
        Assert.True(machine.UpgradeCode != user.UpgradeCode && machine.ProductCode != user.ProductCode,
            "Per-machine and current-user products must have separate identity families.");
        var chinese = WixIdentity.Create("com.example.app", "1.2.3", "windows-x86_64", WixInstallScope.CurrentUser,
            language: WixLanguageInfo.Resolve("zh-CN"));
        Assert.True(chinese.UpgradeCode != first.UpgradeCode && chinese.ProductCode != first.ProductCode,
            "Localized MSI products need separate language identity families.");
        var x86Chinese = WixIdentity.Create("com.example.app", "1.2.3", "windows-i686", WixInstallScope.CurrentUser,
            language: WixLanguageInfo.Resolve("zh-CN"));
        Assert.True(x86Chinese.UpgradeCode == Guid.Parse("f2da6c18-929f-527d-b974-f64977d47a20") &&
               x86Chinese.ProductCode == Guid.Parse("5f92da76-88d9-516b-9791-9f9b2277eadc") &&
               x86Chinese.UpgradeCode != x86.UpgradeCode,
            "The new x86 Chinese identity differs from its fixed test vector.");
    }

    [Fact]
    public void ComponentCodeUsesArchTokenSeed()
    {
        var x64 = WixIdentity.ComponentCode("com.Example.App", "windows-x86_64",
            WixInstallScope.PerMachine, "App.exe");
        var x64Again = WixIdentity.ComponentCode("com.example.app", "windows-x86_64",
            WixInstallScope.PerMachine, "app.exe");
        var x86 = WixIdentity.ComponentCode("com.example.app", "windows-i686",
            WixInstallScope.PerMachine, "app.exe");
        var arm = WixIdentity.ComponentCode("com.example.app", "windows-aarch64",
            WixInstallScope.PerMachine, "app.exe");
        Assert.Equal(x64, x64Again);
        Assert.True(x64 != x86 && x64 != arm && x86 != arm,
            "Component GUIDs must diverge per architecture token.");
        Assert.Equal(Guid.Parse("3ee79d14-2037-573b-b314-6167cb6d60ed"), x64);
        Assert.Equal(Guid.Parse("6e8439a3-609a-5fff-9135-77a52c375b4f"), x86);
        Assert.Equal(Guid.Parse("e3eb3b5c-ff03-55ae-8437-41d456328d6e"), arm);
        Assert.ThrowsAny<ArgumentException>(() => WixIdentity.ComponentCode(
            "com.example.app", "windows-riscv64", WixInstallScope.PerMachine, "app.exe"));
    }

    [Fact]
    public void MapsExplicitMsiVersions()
    {
        var baseline = WixIdentity.Create("com.example.app", "1.2.3", "windows-x86_64", WixInstallScope.CurrentUser);
        var explicitSame = WixIdentity.Create("com.example.app", "1.2.3", "windows-x86_64", WixInstallScope.CurrentUser,
            packageVersion: "1.2.3");
        Assert.Equal(explicitSame, baseline);
        var preview = WixIdentity.Create("com.example.app", "2.0.0-beta.1", "windows-x86_64",
            WixInstallScope.CurrentUser, packageVersion: "1.9.7");
        Assert.True(preview.ProductVersion == "1.9.7" && preview.UpgradeCode == baseline.UpgradeCode &&
               preview.ProductCode != baseline.ProductCode, "Explicit MSI version did not retain the product family.");
        foreach (var version in new[] { "", "1.2.3.4", "1.2.3-beta", "01.2.3", "256.0.0", "1.256.0", "1.2.65536" })
        {
            var rejected = Assert.ThrowsAny<ArgumentException>(
                () => WixIdentity.Create("com.example.app", "2.0.0-beta.1", "windows-x86_64",
                    WixInstallScope.CurrentUser, packageVersion: version));
            Assert.Equal("packageVersion", rejected.ParamName);
        }
    }

    [Fact]
    public void NonWindowsHostGateIsPlatformNotSupported()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "the host gate only fires off-Windows");
        // 宿主门禁标记类型：入口层靠它区分“逐格式容错”与“配置错误聚合”。
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "windows-x86_64", InputDirectory = ".",
            MainExecutable = "test.exe", Formats = [PackageFormat.Msi]
        });
        Assert.Throws<PlatformNotSupportedException>(() => new WixBundler().Validate(configuration));
    }

    [Fact]
    public async Task RejectsUnsafeMsiPaths()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        foreach (var path in new[] { "../escape", "docs/../escape", "C:\\Windows\\file" })
        {
            var rejected = await Assert.ThrowsAnyAsync<BundleValidationException>(
                () => fixture.Bundler().BuildAsync(fixture.Request(path), TestContext.Current.CancellationToken));
            Assert.Contains(rejected.Issues, issue => issue.Path == "resources[0].targetPath");
        }
        foreach (var path in new[] { "docs//file", "file. " })
        {
            await ExpectAsync<ArgumentException>(() => fixture.Bundler().BuildAsync(fixture.Request(path), TestContext.Current.CancellationToken), "path");
        }
        var output = Path.Combine(fixture.Root, "output");
        Assert.True(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*.msi", SearchOption.AllDirectories).Any(),
            "An invalid resource path produced an MSI.");
    }

            [Fact]
    public void MapsMsiSettingsThroughMsBuild()
    {
        var root = RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var targets = File.ReadAllText(Path.Combine(root, "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var task = File.ReadAllText(Path.Combine(root, "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        foreach (var property in new[] { "WixInstallScope", "WixUpgradeCode", "WixVersion", "WixAllowDowngrades",
                     "WixCodepage", "WixLanguage", "WixLanguages", "WixFipsCompliant", "WixToolsetArchivePath",
                     "WixStartMenuShortcut", "WixDesktopShortcut", "WixInstallDirectorySelection",
                     "WixBannerFile", "WixDialogFile", "WixAddToPath",
                     "WixUninstallShortcut", "WixLaunchAfterInstall",
                     "WixExtensionIdPrefix", "WixExpertTemplateFile" })
        {
            Assert.True(props.Contains("<Bundler" + property, StringComparison.Ordinal) &&
                   targets.Contains(property + "=\"$(Bundler" + property + ")\"", StringComparison.Ordinal),
                "MSBuild did not map MSI property Bundler" + property + ".");
        }
        Assert.True(targets.Contains("WixLanguageFiles=\"@(BundlerWixLanguageFile)\"", StringComparison.Ordinal) &&
               task.Contains("ParseWixLanguageFiles()", StringComparison.Ordinal) &&
               task.Contains("BundlerWixLanguageFile", StringComparison.Ordinal),
            "MSBuild does not map per-language MSI locale files.");
        Assert.True(targets.Contains("WixExtensionFragmentFiles=\"@(BundlerWixExtensionFragment)\"", StringComparison.Ordinal) &&
               targets.Contains("WixExtensionComponentRefs=\"@(BundlerWixExtensionComponentRef)\"", StringComparison.Ordinal) &&
               targets.Contains("WixExtensionComponentGroupRefs=\"@(BundlerWixExtensionComponentGroupRef)\"", StringComparison.Ordinal) &&
               targets.Contains("WixExtensionFeatureRefs=\"@(BundlerWixExtensionFeatureRef)\"", StringComparison.Ordinal) &&
               targets.Contains("WixExpertMergeModuleFiles=\"@(BundlerWixExpertMergeModule)\"", StringComparison.Ordinal) &&
               task.Contains("ExtensionFragmentFiles", StringComparison.Ordinal) &&
               task.Contains("ExpertMergeModuleFiles", StringComparison.Ordinal),
            "MSBuild does not map the MSI extension surface.");
        Assert.True(task.Contains("new WixBundler(", StringComparison.Ordinal) &&
               task.Contains("Codepage = WixCodepage", StringComparison.Ordinal) &&
               task.Contains("FipsCompliant = WixFipsCompliant", StringComparison.Ordinal) &&
               task.Contains("Signer = CreateWindowsSigner()", StringComparison.Ordinal),
            "MSBuild task does not call the same public MSI backend or map signing.");
    }

    [Fact]
    public void VerifiesWixRedistribution()
    {
        var root = Path.Combine(RepoRoot(), "third_party", "wix");
        var binary = Path.Combine(root, "wix3141-tools.zip");
        var source = Path.Combine(root, "wix3141-source.zip");
        Assert.Equal("ABE572B353CD4151B1C69907BB5C5E84886138E518607432C9723B454853B358", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))));
        Assert.Equal("A56184E798885641821666BD389FE6276F99363F65BAE8F88630B17DE297FE9F", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        using var toolZip = System.IO.Compression.ZipFile.OpenRead(binary);
        var expected = File.ReadAllLines(Path.Combine(root, "SHA256SUMS"))
            .Select(line => (Hash: line.Substring(0, 64), Name: line.Substring(66)))
            .ToDictionary(pair => pair.Name, pair => pair.Hash, StringComparer.Ordinal);
        Assert.Equal(expected.Count, toolZip.Entries.Count);
        Assert.NotNull(toolZip.GetEntry("WixUIExtension.dll"));
        foreach (var entry in toolZip.Entries)
        {
            using var stream = entry.Open();
            Assert.True(expected.TryGetValue(entry.FullName, out var hash) &&
                   Convert.ToHexString(SHA256.HashData(stream)) == hash,
                "WiX subset entry differs from the recorded upstream file hash: " + entry.FullName);
        }
        using var sourceZip = System.IO.Compression.ZipFile.OpenRead(source);
        Assert.True(sourceZip.GetEntry("wix3-wix3141rtm/LICENSE.TXT") is not null &&
               sourceZip.GetEntry("wix3-wix3141rtm/src/tools/candle/candle.cs") is not null &&
               sourceZip.GetEntry("wix3-wix3141rtm/src/tools/light/light.cs") is not null &&
               sourceZip.GetEntry("wix3-wix3141rtm/src/ext/UIExtension/wixext/WixUIExtension.csproj") is not null,
            "The bundled corresponding source archive is incomplete.");
        var licenseEntry = toolZip.GetEntry("LICENSE.TXT")!;
        using var license = licenseEntry.Open();
        var archiveLicense = new StreamReader(license).ReadToEnd().Replace("\r\n", "\n");
        var packagedLicense = File.ReadAllText(Path.Combine(root, "LICENSE.TXT")).Replace("\r\n", "\n");
        Assert.Equal(packagedLicense, archiveLicense);
    }

    [Fact]
    public async Task ValidatesMsiPublishingInputs()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var configuration = new BundleConfiguration
        {
            ProductName = "Msi Test",
            Identifier = "com.example.msitest",
            Version = "1.0.0",
            LicenseFile = "future-license.txt",
            Targets = [new BundleTargetConfiguration
            {
                Target = "windows-x86_64", InputDirectory = "unused", MainExecutable = "test.exe",
                Formats = [PackageFormat.Msi]
            }]
        };
        var licenseFormat = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => new WixBundler().BuildAsync(configuration, TestContext.Current.CancellationToken));
        Assert.Contains("RTF", licenseFormat.Message, StringComparison.Ordinal);
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["zh-CN"],
            Codepage = 1252
        }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "code page 1252");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["xx-99"]
        }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "xx-99");
        await ExpectAsync<ArgumentException>(() => fixture.Bundler().BuildAsync(
            fixture.Request(signingFiles: ["fixture.exe"]), TestContext.Current.CancellationToken), "signer");
    }

                [Fact]
    public async Task RejectsMsiInputReparsePoints()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve", TestContext.Current.CancellationToken);
        var linked = Path.Combine(fixture.Input, "linked-outside");
        Directory.CreateSymbolicLink(linked, outside);
        await ExpectAsync<InvalidDataException>(() => fixture.Bundler().BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "reparse point");
        Assert.Equal("preserve", File.ReadAllText(sentinel));
    }

        [Fact]
    public async Task RejectsTamperedWixArchive()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var root = Temp("DotNet.Bundler.Msi.Tests").FullName;
        var archive = Path.Combine(root, "tampered.zip");
        File.Copy(Path.Combine(RepoRoot(), "third_party", "wix", "wix3141-tools.zip"), archive);
        using (var stream = new FileStream(archive, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 50;
            stream.WriteByte(255);
        }
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "test.exe"), "fixture", TestContext.Current.CancellationToken);
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "windows-x86_64", InputDirectory = input,
            MainExecutable = "test.exe", Formats = [PackageFormat.Msi]
        });
        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => new WixBundler(options: new WixBundlerOptions
            {
                ToolCacheDirectory = Path.Combine(root, "cache"),
                ToolsetArchivePath = archive
            }).BuildAsync(configuration, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidatesMsiBitmapInputs()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-bitmaps").FullName);
        var wrongSize = CreateBmp(Path.Combine(fixture.Root, "wrong.bmp"), 100, 100);
        var renamedText = Path.Combine(fixture.Root, "renamed.bmp");
        await File.WriteAllTextAsync(renamedText, "not a bitmap", TestContext.Current.CancellationToken);
        var missing = Path.Combine(fixture.Root, "missing.bmp");
        var disguised = Path.Combine(fixture.Root, "banner.png");
        File.Copy(CreateBmp(Path.Combine(fixture.Root, "ok.png"), 493, 58), disguised);
        var linked = Path.Combine(fixture.Root, "linked.bmp");
        File.CreateSymbolicLink(linked, wrongSize);
        foreach (var (banner, dialog) in new (string? Banner, string? Dialog)[]
        {
            (wrongSize, null), (renamedText, null), (missing, null), (disguised, null), (linked, null),
            (null, wrongSize), (null, renamedText), (null, missing)
        })
        {
            var settings = new WixBundleConfiguration { BannerFile = banner, DialogFile = dialog };
            await ExpectAsync<ArgumentException>(() => new WixBundler(settings,
                new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "bmp");
        }
        // 合法位图接真实 candle/light 构建的腿属 spawn，去向见 Integration/Manual 层对应文件；
        // 这里保留全部校验期拒绝断言（CheckBitmap 在 WixBundler.Validate 内完成，不触工具）。
    }

[Fact]
    public async Task RejectsInvalidMsiLocaleInputs()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var wrongCulture = Path.Combine(fixture.Root, "wrong.wxl");
        await File.WriteAllTextAsync(wrongCulture,
            "<?xml version=\"1.0\"?><WixLocalization xmlns=\"http://schemas.microsoft.com/wix/2006/localization\"" +
            " Culture=\"de-DE\" Codepage=\"1252\"><String Id=\"BundlerShortcutsFeature\">x</String></WixLocalization>", TestContext.Current.CancellationToken);
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["ja-JP"],
            LocaleFiles = new Dictionary<string, string> { ["ja-JP"] = wrongCulture }
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken),
            "does not match");
        var unencodable = Path.Combine(fixture.Root, "unencodable.wxl");
        await File.WriteAllTextAsync(unencodable,
            "<?xml version=\"1.0\"?><WixLocalization xmlns=\"http://schemas.microsoft.com/wix/2006/localization\"" +
            " Culture=\"ja-JP\" Codepage=\"932\"><String Id=\"BundlerShortcutsFeature\">&#x1F600;</String></WixLocalization>", TestContext.Current.CancellationToken);
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["ja-JP"],
            LocaleFiles = new Dictionary<string, string> { ["ja-JP"] = unencodable }
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken),
            "code page");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["ja-JP"],
            LocaleFiles = new Dictionary<string, string> { ["fr-FR"] = wrongCulture }
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken),
            "fr-FR");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["en-US", "en-us"]
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken),
            "duplicates");
    }

    [Fact]
    public async Task RejectsUnsafeMsiExtensions()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var customAction = Path.Combine(fixture.Root, "action.wxs");
        await File.WriteAllTextAsync(customAction,
            "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\"><Fragment>" +
            "<CustomAction Id=\"Acme.Run\" Execute=\"deferred\" ExeCommand=\"cmd.exe\"/>" +
            "</Fragment></Wix>", TestContext.Current.CancellationToken);
        var extensionNs = Path.Combine(fixture.Root, "util.wxs");
        await File.WriteAllTextAsync(extensionNs,
            "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\" " +
            "xmlns:util=\"http://schemas.microsoft.com/wix/UtilExtension\"><Fragment>" +
            "<Component Id=\"Acme.C\" Guid=\"{11111111-2222-3333-4444-555555555555}\" " +
            "Directory=\"INSTALLFOLDER\"><util:User Id=\"Acme.U\" Name=\"x\"/>" +
            "</Component></Fragment></Wix>", TestContext.Current.CancellationToken);
        var unprefixed = Path.Combine(fixture.Root, "plain.wxs");
        await File.WriteAllTextAsync(unprefixed,
            "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\"><Fragment>" +
            "<Component Id=\"Bare\" Guid=\"{11111111-2222-3333-4444-555555555555}\" " +
            "Directory=\"INSTALLFOLDER\"/></Fragment></Wix>", TestContext.Current.CancellationToken);
        var broken = Path.Combine(fixture.Root, "broken.wxs");
        await File.WriteAllTextAsync(broken, "<Wix><Fragment>", TestContext.Current.CancellationToken);
        var notWxs = Path.Combine(fixture.Root, "fake.xml");
        await File.WriteAllTextAsync(notWxs, "<Wix/>", TestContext.Current.CancellationToken);
        var options = new WixBundlerOptions { ToolCacheDirectory = fixture.Cache };

        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [customAction]
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "ExtensionIdPrefix");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [customAction], ExtensionIdPrefix = "Acme."
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "whitelist");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [extensionNs], ExtensionIdPrefix = "Acme."
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "expert mode");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [unprefixed], ExtensionIdPrefix = "Acme."
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "prefix");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [broken], ExtensionIdPrefix = "Acme."
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "valid XML");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [notWxs], ExtensionIdPrefix = "Acme."
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), ".wxs");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionIdPrefix = "Acme.", ExtensionComponentRefs = ["NotMine.Component"]
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "prefix");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionIdPrefix = "bad-prefix", ExtensionComponentRefs = ["x"]
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "prefix");
        var template = Path.Combine(fixture.Root, "expert.wxs");
        await File.WriteAllTextAsync(template, "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\"/>", TestContext.Current.CancellationToken);
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExpertTemplateFile = template, ExtensionFragmentFiles = [customAction], ExtensionIdPrefix = "Acme."
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "cannot be combined");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExpertMergeModuleFiles = [template]
        }, options).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "expert mode");
    }

    static async Task ExpectAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception) when (exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} containing '{message}'.");
    }

    private static string CreateBmp(string path, int width, int height)
    {
        var rowBytes = (width * 3 + 3) / 4 * 4;
        var pixelBytes = rowBytes * height;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + pixelBytes);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(new byte[pixelBytes]);
        return path;
    }

    static BundleConfiguration ValidConfiguration(
        BundleTargetConfiguration target, UpdateBundleConfiguration? update = null) => new()
    {
        ProductName = "ExampleApp",
        Identifier = "com.example.app",
        Version = "1.0.0",
        OutputDirectory = "artifacts",
        Targets = [target],
        Update = update
    };

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bundler.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root not found above " + AppContext.BaseDirectory);
    }

    private sealed class WixTestFixture : IDisposable
    {
        // 根目录由 TempBase 统一回收；这里只兜住 reparse-point 链接，防止回收跟着链出测试目录。
        public string Root { get; }
        public string Input => Path.Combine(Root, "input");
        public string Cache => Path.Combine(Root, "cache");

        public WixTestFixture(string root)
        {
            Root = root;
            Directory.CreateDirectory(Input);
            File.WriteAllText(Path.Combine(Input, "fixture.exe"), "original payload");
            File.WriteAllText(Path.Combine(Root, "resource.txt"), "resource payload");
        }

        public WixBundler Bundler() => new(options: new WixBundlerOptions { ToolCacheDirectory = Cache });

        public BundleConfiguration Request(string? resourceTarget = null, string? licenseFile = null,
            IReadOnlyList<string>? signingFiles = null, string? outputDirectory = null,
            string? target = null) => new()
        {
            ProductName = "WiX test fixture",
            Identifier = "com.example.wixtestfixture",
            Version = "1.0.0",
            LicenseFile = licenseFile,
            OutputDirectory = outputDirectory ?? Path.Combine(Root, "output"),
            Resources = resourceTarget is null ? [] :
                [new BundleResourceConfiguration { Source = Path.Combine(Root, "resource.txt"), Destination = resourceTarget }],
            Targets = [new BundleTargetConfiguration
            {
                Target = target ?? "windows-x86_64", InputDirectory = Input,
                MainExecutable = "fixture.exe", Formats = [PackageFormat.Msi],
                SigningFiles = signingFiles ?? []
            }]
        };

        public void Dispose()
        {
            var link = Path.Combine(Input, "linked-outside");
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link);
        }
    }
}
