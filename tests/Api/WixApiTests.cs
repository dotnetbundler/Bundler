using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Bundler;
using DotNet.Bundler.Signing.Windows;
using DotNet.Bundler.Wix;

namespace Bundler.ApiTests;

/// <summary>
/// MSI(WiX) 公开 API 契约：真 WiX 编译产 .msi——Windows 腿独占。
/// 真装真卸/升级语义属 Integration localinstall 腿。
/// 收割自旧 WixTests 的 spawn 腿（真实 candle.exe/light.exe 编译、篡改恢复、
/// 并发缓存串行、真证书签名、变体矩阵）。
/// 未移植：CancelsHangingWixToolProcess / RejectsWixCompilerWarnings 直调
/// internal 的 WixProcessRunner/WixToolsetResolver——属 Integration（有 InternalsVisibleTo）。
/// ValidatesMsiBitmapInputs 只收合法位图真实编译腿（校验期拒绝断言已在 Unit 同名方法）。
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

    [Fact]
    public async Task BuildsAndInspectsMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var root = Temp("msi-inspect").FullName;
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(Path.Combine(input, "assets"));
        await File.WriteAllTextAsync(Path.Combine(input, "Msi Test.exe"), "MSI fixture payload",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input, "assets", "说明.txt"), "user visible file",
            TestContext.Current.CancellationToken);
        var resource = Path.Combine(root, "resource.txt");
        await File.WriteAllTextAsync(resource, "external resource", TestContext.Current.CancellationToken);
        var icon = Path.Combine(root, "product.ico");
        await File.WriteAllBytesAsync(icon,
        [
            0,0,1,0,1,0, 1,1,0,0,1,0,32,0,48,0,0,0,22,0,0,0,
            40,0,0,0, 1,0,0,0, 2,0,0,0, 1,0,32,0, 0,0,0,0,
            4,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
            0,0,255,255, 0,0,0,0
        ], TestContext.Current.CancellationToken);

        var configuration = new BundleConfiguration
        {
            ProductName = "Msi 测试",
            Identifier = "com.example.msitest",
            Version = "1.2.3",
            Publisher = "Bundler Tests",
            Description = "Test MSI database",
            Homepage = "https://example.com/msi",
            Icons = [icon],
            FileAssociations = [new BundleFileAssociationConfiguration { Extensions = [".abc"], Name = "ABC document",
                MimeType = "application/x-abc" }],
            UrlProtocols = [new BundleUrlProtocolConfiguration { Schemes = ["bundler-test"], Name = "Bundler test link" }],
            Resources = [new BundleResourceConfiguration { Source = resource, Destination = "docs/外部.txt" }],
            OutputDirectory = output,
            Targets = [new BundleTargetConfiguration
            {
                Target = "windows-x86_64", InputDirectory = input,
                MainExecutable = "Msi Test.exe", Formats = [PackageFormat.Msi]
            }]
        };
        var bundler = new WixBundler(new WixBundleConfiguration { Codepage = 936, StartMenuShortcut = true, DesktopShortcut = true },
            new WixBundlerOptions { ToolCacheDirectory = cache });
        var artifact = (await bundler.BuildAsync(configuration, TestContext.Current.CancellationToken)).Single();
        Assert.True(File.Exists(artifact.Path) && Path.GetExtension(artifact.Path) == ".msi",
            "WiX did not produce an MSI artifact.");
        using (var database = new MsiDatabaseReader(artifact.Path))
        {
            var expected = WixIdentity.Create(configuration.Identifier, configuration.Version,
                "windows-x86_64", WixInstallScope.CurrentUser);
            Assert.Equal(expected.ProductCode.ToString("B").ToUpperInvariant(), database.Property("ProductCode"));
            Assert.Equal(expected.UpgradeCode.ToString("B").ToUpperInvariant(), database.Property("UpgradeCode"));
            Assert.Equal("1.2.3", database.Property("ProductVersion"));
            Assert.Equal("Msi 测试", database.Property("ProductName"));
            Assert.Equal("Bundler Tests", database.Property("Manufacturer"));
            Assert.Equal("Test MSI database", database.Property("ARPCOMMENTS"));
            Assert.Equal("https://example.com/msi", database.Property("ARPURLINFOABOUT"));
            Assert.Equal("ProductIcon", database.Property("ARPPRODUCTICON"));
            Assert.Equal(1, database.RowCount("Icon", "Name"));
            Assert.Equal(3, database.RowCount("File", "File"));
            Assert.Equal(7, database.RowCount("Component", "Component"));
            Assert.True(database.RowCount("Registry", "Registry") > 4, "MSI desktop capabilities and HKCU key paths are missing.");
            Assert.True(database.Contains("Registry", "Name", "application/x-abc"),
                "MSI silently ignored the configured MIME candidate registration.");
            Assert.Equal(2, database.RowCount("Shortcut", "Shortcut"));
            Assert.True(database.RowCount("RemoveFile", "FileKey") >= 4, "Per-user directories need uninstall cleanup rows.");
            Assert.True(database.RowCount("Upgrade", "UpgradeCode") >= 1,
                "The MSI major-upgrade table is missing.");
            Assert.True(database.Property("BUNDLER_PACKAGE_DEFINITION").Length == 64 &&
                   database.Contains("Registry", "Name", "DefinitionHash") &&
                   database.RowCount("AppSearch", "Property") >= 1,
                "The same-version package definition guard is missing.");
            Assert.StartsWith("x64;", database.Template);
            Assert.True(Guid.TryParse(database.PackageCode, out var packageCode) &&
                   packageCode != expected.ProductCode,
                "MSI package code was not written as a distinct GUID.");
        }
    }

    [Fact]
    public async Task ReusesOnlyVerifiedMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var bundler = fixture.Bundler();
        var request = fixture.Request();
        var first = (await bundler.BuildAsync(request, TestContext.Current.CancellationToken)).Single();
        var firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.Path)));
        var repeated = (await bundler.BuildAsync(request, TestContext.Current.CancellationToken)).Single();
        Assert.True(repeated.Path == first.Path &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(repeated.Path))) == firstHash,
            "An unchanged build did not reuse the verified MSI.");
        File.WriteAllText(first.Path + ".bundler-manifest", "unverified");
        await ExpectAsync<IOException>(() => bundler.BuildAsync(request, TestContext.Current.CancellationToken), "same product version");
    }

    [Fact]
    public async Task RejectsChangedSameVersionPayload()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var bundler = fixture.Bundler();
        var request = fixture.Request();
        var first = (await bundler.BuildAsync(request, TestContext.Current.CancellationToken)).Single();
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.Path)));
        await File.WriteAllTextAsync(Path.Combine(fixture.Input, "fixture.exe"), "different payload",
            TestContext.Current.CancellationToken);
        await ExpectAsync<IOException>(() => bundler.BuildAsync(request, TestContext.Current.CancellationToken), "same product version");
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.Path))));
    }

    [Fact]
    public async Task RecoversTamperedWixCompiler()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var bundler = fixture.Bundler();
        var request = fixture.Request();
        await bundler.BuildAsync(request, TestContext.Current.CancellationToken);
        var toolDirectory = Directory.EnumerateDirectories(fixture.Cache, "wix-toolset-*").Single();
        var compiler = Path.Combine(toolDirectory, "candle.exe");
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(compiler)));
        await File.WriteAllTextAsync(compiler, "tampered", TestContext.Current.CancellationToken);
        await bundler.BuildAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(compiler))));
    }

    [Fact]
    public async Task SerializesConcurrentWixCacheUsers()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var root = Temp("wix-concurrent").FullName;
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "test.exe"), "fixture",
            TestContext.Current.CancellationToken);

        Task<IReadOnlyList<BundleArtifact>> Build(int index)
        {
            var configuration = new BundleConfiguration
            {
                ProductName = "Concurrent Msi",
                Identifier = "com.example.concurrentmsi",
                Version = "1.0.0",
                OutputDirectory = Path.Combine(root, "output-" + index),
                Targets = [new BundleTargetConfiguration
                {
                    Target = "windows-x86_64", InputDirectory = input,
                    MainExecutable = "test.exe", Formats = [PackageFormat.Msi]
                }]
            };
            return new WixBundler(options: new WixBundlerOptions
            {
                ToolCacheDirectory = Path.Combine(root, "cache")
            }).BuildAsync(configuration, TestContext.Current.CancellationToken);
        }
        var artifacts = await Task.WhenAll(Build(1), Build(2));
        Assert.True(artifacts.SelectMany(group => group).All(artifact => File.Exists(artifact.Path)),
            "Concurrent MSI builds did not both produce packages.");
        var sameOutput = await Task.WhenAll(Build(3), Build(3));
        Assert.True(sameOutput[0][0].Path == sameOutput[1][0].Path &&
               File.Exists(sameOutput[0][0].Path + ".bundler-manifest"),
            "Concurrent builds to one MSI output did not serialize and reuse a verified package.");
    }

    [Fact]
    public async Task BuildsMachineMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var root = Temp("msi-machine").FullName;
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "machine.exe"), "machine payload",
            TestContext.Current.CancellationToken);

        var configuration = new BundleConfiguration
        {
            ProductName = "Machine MSI Test", Identifier = "com.example.msimachine", Version = "2.0.0",
            FileAssociations = [new BundleFileAssociationConfiguration { Extensions = [".machinetest"] }],
            UrlProtocols = [new BundleUrlProtocolConfiguration { Schemes = ["machine-test"] }],
            OutputDirectory = Path.Combine(root, "output"),
            Targets = [new BundleTargetConfiguration { Target = "windows-x86_64", InputDirectory = input,
                MainExecutable = "machine.exe", Formats = [PackageFormat.Msi] }]
        };
        var artifact = (await new WixBundler(new WixBundleConfiguration { InstallScope = WixInstallScope.PerMachine,
            StartMenuShortcut = true, DesktopShortcut = true },
            new WixBundlerOptions { ToolCacheDirectory = Path.Combine(root, "cache") })
            .BuildAsync(configuration, TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        var expected = WixIdentity.Create(configuration.Identifier, configuration.Version, "windows-x86_64", WixInstallScope.PerMachine);
        Assert.Equal(expected.ProductCode.ToString("B").ToUpperInvariant(), database.Property("ProductCode"));
        Assert.True(database.Contains("Directory", "Directory", "ProgramFiles64Folder"),
            "Per-machine MSI does not target Program Files.");
        var registryRoots = database.Pairs("Registry", "Name", "Root");
        Assert.True(registryRoots.Any(row => row.Second == "2") &&
               registryRoots.Where(row => row.Second == "1")
                   .All(row => row.First.EndsWith("Shortcut", StringComparison.Ordinal)),
            "Per-machine MSI must keep HKLM key paths except HKCU shortcut repair keys.");
        Assert.True(database.RowCount("Shortcut", "Shortcut") == 2 &&
               database.Contains("Directory", "Directory", "ProgramMenuFolder"),
            "Per-machine MSI shortcut directories were not compiled.");
    }

    [Fact]
    public async Task BuildsArm64TargetedMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var root = Temp("msi-arm64").FullName;
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "test.exe"), "fixture",
            TestContext.Current.CancellationToken);

        var configuration = new BundleConfiguration
        {
            ProductName = "Arm64 Msi",
            Identifier = "com.example.arm64msi",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "output"),
            Targets = [new BundleTargetConfiguration
            {
                Target = "windows-aarch64", InputDirectory = input,
                MainExecutable = "test.exe", Formats = [PackageFormat.Msi]
            }]
        };
        var artifact = (await new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = Path.Combine(root, "cache")
        }).BuildAsync(configuration, TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.StartsWith("Arm64;", database.Template);
        Assert.True(database.Property("UpgradeCode") != WixIdentity.Create(configuration.Identifier, configuration.Version,
            "windows-x86_64", WixInstallScope.CurrentUser).UpgradeCode.ToString("B").ToUpperInvariant(),
            "ARM64 and x64 MSI products share an UpgradeCode.");
    }

    [Fact]
    public async Task BuildsX86TargetedMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        foreach (var scope in new[] { WixInstallScope.CurrentUser, WixInstallScope.PerMachine })
        {
            var request = new BundleConfiguration
            {
                ProductName = "X86 MSI Test", Identifier = "com.example.msi.x86", Version = "1.2.3",
                OutputDirectory = Path.Combine(fixture.Root, "x86-" + scope),
                Targets = [new BundleTargetConfiguration
                {
                    Target = "windows-i686", InputDirectory = fixture.Input,
                    MainExecutable = "fixture.exe", Formats = [PackageFormat.Msi]
                }]
            };
            var artifact = (await new WixBundler(new WixBundleConfiguration { InstallScope = scope },
                new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
                .BuildAsync(request, TestContext.Current.CancellationToken)).Single();
            using var database = new MsiDatabaseReader(artifact.Path);
            var expected = WixIdentity.Create(request.Identifier, request.Version, "windows-i686", scope);
            Assert.StartsWith("Intel;", database.Template);
            Assert.True(database.Property("UpgradeCode") == expected.UpgradeCode.ToString("B").ToUpperInvariant() &&
                   database.Property("ProductCode") == expected.ProductCode.ToString("B").ToUpperInvariant(),
                "x86 MSI identity differs from the isolated target vector.");
            Assert.True(database.Values("Component", "Attributes").All(value => (int.Parse(value) & 256) == 0),
                "x86 MSI contains a 64-bit component.");
            Assert.True(database.ContainsSubstring("Directory", "DefaultDir", "com.example.msi.x86-x86"),
                "x86 MSI installation directory is not architecture-specific.");
            Assert.True(scope == WixInstallScope.CurrentUser
                    ? database.Contains("Directory", "Directory", "LocalAppDataFolder") &&
                      database.Contains("Registry", "Root", "1")
                    : database.Contains("Directory", "Directory", "ProgramFilesFolder") &&
                      !database.Contains("Directory", "Directory", "ProgramFiles64Folder") &&
                      database.Contains("Registry", "Root", "2"),
                "x86 MSI scope or install root is incorrect.");
        }
    }

    [Fact]
    public async Task BuildsExplicitVersionAndDowngradePolicy()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var request = new BundleConfiguration
        {
            ProductName = "Mapped MSI Test", Identifier = "com.example.msi.mapped", Version = "2.0.0-beta.1",
            OutputDirectory = Path.Combine(fixture.Root, "mapped"),
            Targets = [new BundleTargetConfiguration
            {
                Target = "windows-x86_64", InputDirectory = fixture.Input,
                MainExecutable = "fixture.exe", Formats = [PackageFormat.Msi]
            }]
        };
        await ExpectAsync<ArgumentException>(() => fixture.Bundler().BuildAsync(request, TestContext.Current.CancellationToken), "version");
        var mappedSettings = new WixBundleConfiguration { Version = "1.9.7", AllowDowngrades = true };
        var artifact = (await new WixBundler(mappedSettings,
            new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(request, TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.True(database.Property("ProductVersion") == "1.9.7" &&
               Path.GetFileName(artifact.Path).Contains("-1.9.7-x64.msi", StringComparison.Ordinal),
            "Explicit MSI version did not reach the compiled product and output name.");
        Assert.Equal(WixIdentity.Create(request.Identifier, request.Version, "windows-x86_64", WixInstallScope.CurrentUser, packageVersion: "1.9.7").ProductCode.ToString("B").ToUpperInvariant(), database.Property("ProductCode"));
        Assert.True(database.Values("Upgrade", "Attributes").Count > 0,
            "Explicit downgrade policy did not compile an Upgrade table.");
    }

    [Fact]
    public async Task GeneratesDistinctPackageCodes()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var bundler = fixture.Bundler();
        var first = (await bundler.BuildAsync(fixture.Request(), TestContext.Current.CancellationToken)).Single();
        var second = (await bundler.BuildAsync(fixture.Request(outputDirectory: Path.Combine(fixture.Root, "second-output")),
            TestContext.Current.CancellationToken)).Single();
        using var firstDatabase = new MsiDatabaseReader(first.Path);
        using var secondDatabase = new MsiDatabaseReader(second.Path);
        Assert.True(firstDatabase.Property("ProductCode") == secondDatabase.Property("ProductCode") &&
               firstDatabase.Property("UpgradeCode") == secondDatabase.Property("UpgradeCode") &&
               firstDatabase.PackageCode != secondDatabase.PackageCode,
            "Two MSI builds for one product version must keep product identity but use distinct package codes.");
    }

    [Fact]
    public async Task BuildsLocalizedMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var request = fixture.Request();
        var english = (await fixture.Bundler().BuildAsync(request, TestContext.Current.CancellationToken)).Single();
        var license = Path.Combine(fixture.Root, "terms.rtf");
        await File.WriteAllTextAsync(license, "{\\rtf1\\ansi Example license terms.}",
            TestContext.Current.CancellationToken);
        request = fixture.Request(licenseFile: license);
        var chinese = (await new WixBundler(new WixBundleConfiguration
        {
            Languages = ["zh-CN"],
            StartMenuShortcut = true,
            DesktopShortcut = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(request, TestContext.Current.CancellationToken)).Single();
        Assert.True(english.Path != chinese.Path && chinese.Path.EndsWith("-zh-cn.msi", StringComparison.Ordinal),
            "Localized MSI outputs collide with the English output.");
        using var en = new MsiDatabaseReader(english.Path);
        using var zh = new MsiDatabaseReader(chinese.Path);
        Assert.True(en.Property("ProductLanguage") == "1033" && zh.Property("ProductLanguage") == "2052" &&
               en.Property("UpgradeCode") != zh.Property("UpgradeCode"),
            "Localized product language or identity is incorrect.");
        var chineseDirectory = zh.ContainsSubstring("Directory", "DefaultDir", "com.example.wixtestfixture-x64-zh-cn");
        var dialogCount = zh.RowCount("Dialog", "Dialog");
        Assert.True(zh.Template.Contains("2052", StringComparison.Ordinal) && chineseDirectory && dialogCount > 0,
            $"Chinese MSI did not include isolated files and a license UI. Template={zh.Template}, directory={chineseDirectory}, dialogs={dialogCount}.");
        using var englishFixture = new WixTestFixture(Temp("wix-fixture-en").FullName);
        var englishLicense = Path.Combine(englishFixture.Root, "terms.rtf");
        await File.WriteAllTextAsync(englishLicense, "{\\rtf1\\ansi Example license terms.}",
            TestContext.Current.CancellationToken);
        var licensedEnglish = (await englishFixture.Bundler().BuildAsync(
            englishFixture.Request(licenseFile: englishLicense), TestContext.Current.CancellationToken)).Single();
        using var englishUi = new MsiDatabaseReader(licensedEnglish.Path);
        Assert.True(englishUi.Property("ProductLanguage") == "1033" &&
               englishUi.RowCount("Dialog", "Dialog") > 0,
            "English MSI did not include the configured license UI.");
    }

    [Fact]
    public async Task SignsMsiArtifacts()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        await File.WriteAllTextAsync(Path.Combine(fixture.Input, "helper.dll"), "helper",
            TestContext.Current.CancellationToken);
        var signer = new MsiRecordingSigner();
        var request = fixture.Request(signingFiles: ["helper.dll"]);
        var artifact = (await new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = fixture.Cache,
            Signer = signer
        }).BuildAsync(request, TestContext.Current.CancellationToken)).Single();
        Assert.Equal(signer.Kinds, [BundleSigningArtifactKind.PayloadExecutable, BundleSigningArtifactKind.PayloadFile, BundleSigningArtifactKind.Installer]);
        Assert.True(signer.Paths[0] != Path.Combine(fixture.Input, "fixture.exe") &&
               File.ReadAllText(Path.Combine(fixture.Input, "fixture.exe")) == "original payload" &&
               File.Exists(artifact.Path + ".bundler-manifest"),
            "MSI signing changed source files or omitted final output manifest.");
        var repeatedSigner = new MsiRecordingSigner();
        await ExpectAsync<IOException>(() => new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = fixture.Cache,
            Signer = repeatedSigner
        }).BuildAsync(request, TestContext.Current.CancellationToken), "same product version");
        Assert.Empty(repeatedSigner.Kinds);
    }

    [Fact]
    public async Task RemovesFailedSignedMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var signer = new MsiRecordingSigner { FailInstaller = true };
        await ExpectAsync<InvalidOperationException>(() => new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = fixture.Cache,
            Signer = signer
        }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "signing failed");
        var output = Path.Combine(fixture.Root, "output");
        Assert.True(!Directory.EnumerateFiles(output, "*.msi").Any() &&
               !Directory.EnumerateFiles(output, "*.bundler-manifest").Any(),
            "An MSI with a failed final signature remained available for publishing.");
    }

    [Fact]
    public async Task SignsRealMsiWithTestCertificate()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test executable path.");
        var input = Path.Combine(fixture.Input, "fixture.exe");
        File.Copy(executable, input, true);
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=DotNet.Bundler MSI test certificate", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-5),
            DateTimeOffset.Now.AddDays(1));
        var pfx = Path.Combine(fixture.Root, "test-signing.pfx");
        const string password = "msi-test-only-password";
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
        var signer = new MsiVerifyingSigner(new WindowsAuthenticodeSigner(new WindowsAuthenticodeSigningOptions
        {
            PfxFile = pfx,
            PfxPassword = password
        }), certificate);
        var artifact = (await new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = fixture.Cache,
            Signer = signer
        }).BuildAsync(fixture.Request(), TestContext.Current.CancellationToken)).Single();
        Assert.True(signer.Kinds.SequenceEqual([BundleSigningArtifactKind.PayloadExecutable,
            BundleSigningArtifactKind.Installer]) &&
            originalHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))) &&
            File.Exists(artifact.Path + ".bundler-manifest"),
            "Real MSI signing changed input files or missed a requested signature.");
    }

    // 合法位图接真实 candle/light 编译腿；全部校验期拒绝断言留在 Unit 同名方法。
    [Fact]
    public async Task ValidatesMsiBitmapInputs()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-bitmaps").FullName);
        var valid = Path.Combine(fixture.Root, "valid-banner.bmp");
        CreateBmp(valid, 493, 58);
        var validDialog = Path.Combine(fixture.Root, "valid-dialog.bmp");
        CreateBmp(validDialog, 503, 314);
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            BannerFile = valid,
            DialogFile = validDialog
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(), TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.True(database.RowCount("Dialog", "Dialog") > 0,
            "Supplied UI bitmaps did not enable the interactive MSI dialog set.");
    }

    [Fact]
    public async Task BuildsFullFeaturedMsi()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var license = Path.Combine(fixture.Root, "terms.rtf");
        await File.WriteAllTextAsync(license, "{\\rtf1\\ansi Example license terms.}",
            TestContext.Current.CancellationToken);
        var settings = new WixBundleConfiguration
        {
            StartMenuShortcut = true,
            DesktopShortcut = true,
            InstallDirectorySelection = true,
            BannerFile = CreateBmp(Path.Combine(fixture.Root, "banner.bmp"), 493, 58),
            DialogFile = CreateBmp(Path.Combine(fixture.Root, "dialog.bmp"), 503, 314),
            AddToPath = true,
            UninstallShortcut = true,
            LaunchAfterInstall = true
        };
        var artifact = (await new WixBundler(settings,
            new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(licenseFile: license), TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        foreach (var dialog in new[] { "WelcomeDlg", "LicenseAgreementDlg", "InstallDirDlg", "BrowseDlg",
                     "VerifyReadyDlg", "ProgressDlg", "ExitDialog", "MaintenanceWelcomeDlg",
                     "MaintenanceTypeDlg", "ErrorDlg", "InvalidDirDlg" })
            Assert.True(database.Contains("Dialog", "Dialog", dialog), "The custom MSI UI is missing dialog " + dialog + ".");
        Assert.True(database.Contains("Feature", "Feature", "Complete") && database.Contains("Feature", "Feature", "Shortcuts") &&
               database.Contains("Feature", "Feature", "PathEnvironment") && database.Contains("Feature", "Feature", "UninstallShortcut"),
            "Optional MSI features must be declared as selectable features.");
        Assert.Equal(6, database.RowCount("Component", "Component"));
        Assert.True(database.RowCount("Shortcut", "Shortcut") == 3 &&
               database.ContainsSubstring("Shortcut", "Arguments", "[ProductCode]"),
            "MSI shortcuts or the managed uninstall entry are incorrect.");
        Assert.True(database.Contains("Environment", "Name", "=-PATH") &&
               database.Contains("Environment", "Value", "[~];[INSTALLFOLDER]"),
            "The MSI PATH feature must append only the product directory.");
        Assert.True(database.Contains("CustomAction", "Action", "BundlerInstallDirScope") &&
               database.Contains("InstallExecuteSequence", "Action", "BundlerInstallDirScope") &&
               database.ContainsSubstring("InstallExecuteSequence", "Condition", "LocalAppDataFolder"),
            "The MSI install-directory scope check is missing from the execute sequence.");
        Assert.True(database.Contains("CustomAction", "Action", "BundlerLaunchAfterInstall") &&
               database.ContainsSubstring("ControlEvent", "Argument", "BundlerLaunchAfterInstall") &&
               database.ContainsSubstring("ControlEvent", "Condition", "WIXUI_EXITDIALOGOPTIONALCHECKBOX"),
            "The interactive launch checkbox is not wired to the exit dialog.");
        Assert.True(database.Property("WIXUI_INSTALLDIR") == "INSTALLFOLDER" &&
               database.Property("WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT") == "Launch [ProductName]" &&
               database.Property("ARPNOMODIFY") == "1" &&
               database.Property("ARPCONTACT").Length > 0 &&
               database.Contains("CustomAction", "Source", "ARPINSTALLLOCATION") &&
               database.ContainsSubstring("CustomAction", "Target", "INSTALLFOLDER"),
            "MSI UI or uninstall metadata properties are missing.");
        Assert.True(database.Contains("AppSearch", "Property", "INSTALLFOLDER") &&
               database.Contains("Registry", "Name", "InstallDir"),
            "MSI must restore a user-chosen install directory during upgrades.");
        Assert.True(database.Contains("Binary", "Name", "WixUI_Bmp_Banner") &&
               database.Contains("Binary", "Name", "WixUI_Bmp_Dialog"),
            "MSI brand bitmaps were not embedded.");
    }

    [Fact]
    public async Task BuildsCustomUiWithoutLicense()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            InstallDirectorySelection = true,
            LaunchAfterInstall = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(), TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.True(database.Contains("Dialog", "Dialog", "InstallDirDlg") &&
               database.Contains("Dialog", "Dialog", "WelcomeDlg") &&
               database.Contains("Dialog", "Dialog", "VerifyReadyDlg") &&
               database.Contains("Dialog", "Dialog", "ExitDialog") &&
               !database.Contains("Dialog", "Dialog", "LicenseAgreementDlg"),
            "A license-free MSI must use the custom dialog sequence without a license page.");
    }

    [Fact]
    public async Task BuildsLicensedDirectorySelection()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var license = Path.Combine(fixture.Root, "terms.rtf");
        await File.WriteAllTextAsync(license, "{\\rtf1\\ansi Example license terms.}",
            TestContext.Current.CancellationToken);
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            InstallDirectorySelection = true,
            Languages = ["zh-CN"]
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(licenseFile: license), TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.True(database.Contains("Dialog", "Dialog", "LicenseAgreementDlg") &&
               database.Contains("Dialog", "Dialog", "InstallDirDlg") &&
               database.ContainsSubstring("ControlEvent", "Condition", "LicenseAccepted") &&
               database.Property("ProductLanguage") == "2052",
            "A licensed MSI must keep the license page before directory selection.");
        Assert.True(database.Contains("Feature", "Feature", "Complete") &&
               !database.Contains("Feature", "Feature", "Shortcuts"),
            "Optional features must not appear unless configured.");
    }

    [Fact]
    public async Task EnforcesMsiInstallDirectoryRoot()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        foreach (var (scope, architecture, expectedRoot) in new[]
        {
            (WixInstallScope.CurrentUser, "windows-x86_64", "LocalAppDataFolder"),
            (WixInstallScope.CurrentUser, "windows-i686", "LocalAppDataFolder"),
            (WixInstallScope.PerMachine, "windows-x86_64", "ProgramFiles64Folder"),
            (WixInstallScope.PerMachine, "windows-i686", "ProgramFilesFolder")
        })
        {
            var scoped = fixture.Request(target: architecture,
                outputDirectory: Path.Combine(fixture.Root, "scope-" + scope + "-" + architecture));
            var artifact = (await new WixBundler(new WixBundleConfiguration { InstallScope = scope },
                new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
                .BuildAsync(scoped, TestContext.Current.CancellationToken)).Single();
            using var database = new MsiDatabaseReader(artifact.Path);
            var conditions = database.Values("InstallExecuteSequence", "Condition");
            Assert.True(conditions.Any(condition => condition.Contains(expectedRoot, StringComparison.Ordinal) &&
                   condition.Contains("INSTALLFOLDER", StringComparison.Ordinal)),
                $"The {scope}/{architecture} MSI must reject directories outside {expectedRoot}.");
        }
    }

    [Fact]
    public async Task GeneratesLaunchCheckboxOnly()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var license = Path.Combine(fixture.Root, "terms.rtf");
        await File.WriteAllTextAsync(license, "{\\rtf1\\ansi Example license terms.}",
            TestContext.Current.CancellationToken);
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            LaunchAfterInstall = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(licenseFile: license), TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.True(database.Contains("CustomAction", "Action", "BundlerLaunchAfterInstall") &&
               database.ContainsSubstring("ControlEvent", "Condition", "NOT Installed"),
            "The licensed minimal UI must expose the opt-in launch checkbox without auto-start.");
        var plain = (await fixture.Bundler().BuildAsync(
            fixture.Request(outputDirectory: Path.Combine(fixture.Root, "plain")),
            TestContext.Current.CancellationToken)).Single();
        using var plainDatabase = new MsiDatabaseReader(plain.Path);
        Assert.True(!plainDatabase.Contains("CustomAction", "Action", "BundlerLaunchAfterInstall") &&
               !plainDatabase.Contains("_Tables", "Name", "Dialog"),
            "A default MSI must not add UI or launch actions.");
    }

    [Fact]
    public async Task BuildsEveryDeclaredMsiLanguage()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var cultures = WixLanguageInfo.Supported.Select(language => language.Culture).ToArray();
        var artifacts = await new WixBundler(new WixBundleConfiguration
        {
            Languages = cultures,
            StartMenuShortcut = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(cultures.Length, artifacts.Count);
        var names = artifacts.Select(artifact => Path.GetFileName(artifact.Path)).ToArray();
        Assert.Equal(artifacts.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(names.Any(name => name.EndsWith("-ja-jp.msi", StringComparison.Ordinal)) &&
               names.Any(name => name.EndsWith("-zh-cn.msi", StringComparison.Ordinal)) &&
               names.Any(name => name.EndsWith("-sr-latn-cs.msi", StringComparison.Ordinal)) &&
               !names.Any(name => name.Contains("-en-us", StringComparison.Ordinal)),
            "Per-language MSI file suffixes are incorrect.");
        using (var japanese = new MsiDatabaseReader(
            artifacts.Single(a => a.Path.EndsWith("-ja-jp.msi")).Path))
        using (var traditional = new MsiDatabaseReader(
            artifacts.Single(a => a.Path.EndsWith("-zh-tw.msi")).Path))
        using (var albanian = new MsiDatabaseReader(
            artifacts.Single(a => a.Path.EndsWith("-sq-sq.msi")).Path))
        {
            Assert.True(japanese.Property("ProductLanguage") == "1041" &&
                   traditional.Property("ProductLanguage") == "1028" &&
                   albanian.Property("ProductLanguage") == "1052",
                "Per-language MSI product languages are incorrect.");
            var codes = artifacts.Select(artifact =>
            {
                using var database = new MsiDatabaseReader(artifact.Path);
                return database.Property("ProductCode") + "|" + database.Property("UpgradeCode");
            }).ToArray();
            Assert.Equal(artifacts.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public async Task AppliesCallerMsiLocaleOverrides()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var locale = Path.Combine(fixture.Root, "override.wxl");
        await File.WriteAllTextAsync(locale,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<WixLocalization xmlns=\"http://schemas.microsoft.com/wix/2006/localization\" " +
            "Culture=\"ja-JP\" Codepage=\"932\">\n" +
            "  <String Id=\"BundlerLaunchCheckboxText\">&#x8D77;&#x52D5; [ProductName]</String>\n" +
            "  <String Id=\"BundlerShortcutsFeature\">&#x30B7;&#x30E7;&#x30FC;&#x30C8;&#x30AB;&#x30C3;&#x30C8;</String>\n" +
            "</WixLocalization>\n", TestContext.Current.CancellationToken);
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            Languages = ["ja-JP"],
            LocaleFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ja-JP"] = locale
            },
            LaunchAfterInstall = true,
            StartMenuShortcut = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(), TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.Equal("起動 [ProductName]", database.Property("WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT"));
        Assert.True(database.Contains("Feature", "Title", "ショートカット"),
            "Caller locale overrides did not reach the MSI feature title.");
        var english = (await new WixBundler(new WixBundleConfiguration
        {
            LaunchAfterInstall = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(
            fixture.Request(outputDirectory: Path.Combine(fixture.Root, "en")),
            TestContext.Current.CancellationToken)).Single();
        using var englishDatabase = new MsiDatabaseReader(english.Path);
        Assert.Equal("Launch [ProductName]", englishDatabase.Property("WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT"));
    }

    [Fact]
    public async Task BuildsFipsMsiWithIconShortcuts()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var icon = Path.Combine(fixture.Root, "app.ico");
        await File.WriteAllBytesAsync(icon,
        [
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
            0x10, 0x10, 0x00, 0x00, 0x01, 0x00, 0x20, 0x00, 0x68, 0x04,
            0x00, 0x00, 0x16, 0x00, 0x00, 0x00,
            .. Enumerable.Repeat((byte)0xAB, 1128)
        ], TestContext.Current.CancellationToken);
        var badIcon = Path.Combine(fixture.Root, "bad.ico");
        await File.WriteAllTextAsync(badIcon, "not an icon", TestContext.Current.CancellationToken);
        var configuration = fixture.Request();
        var iconsArtifact = await new WixBundler(new WixBundleConfiguration
        {
            FipsCompliant = true,
            StartMenuShortcut = true,
            DesktopShortcut = true,
            UninstallShortcut = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(WithIcon(configuration, icon), TestContext.Current.CancellationToken);
        var artifact = iconsArtifact.Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.True(database.RowCount("Shortcut", "Shortcut") == 3 &&
               database.Values("Shortcut", "Icon_")
                   .Count(value => value == "ProductIcon") == 3,
            "The MSI icon was not wired into every shortcut.");
        await ExpectAsync<InvalidDataException>(() => new WixBundler()
            .BuildAsync(WithIcon(fixture.Request(outputDirectory: Path.Combine(fixture.Root, "bad")), badIcon),
                TestContext.Current.CancellationToken),
            ".ico");

        static BundleConfiguration WithIcon(BundleConfiguration configuration, string icon) =>
            new()
            {
                ProductName = configuration.ProductName,
                Identifier = configuration.Identifier,
                Version = configuration.Version,
                LicenseFile = configuration.LicenseFile,
                OutputDirectory = configuration.OutputDirectory,
                Resources = configuration.Resources,
                Icons = [icon],
                Targets = configuration.Targets
            };
    }

    [Fact]
    public async Task BuildsRegularMsiExtensionFragments()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var fragment = Path.Combine(fixture.Root, "extras.wxs");
        await File.WriteAllTextAsync(fragment,
            "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\"><Fragment>" +
            "<ComponentGroup Id=\"Acme.Extras\">" +
            "<Component Id=\"Acme.ExtraComponent\" Guid=\"{11111111-2222-3333-4444-555555555555}\" " +
            " Directory=\"INSTALLFOLDER\">" +
            "<RegistryValue Root=\"HKCU\" Key=\"Software\\Acme\\Extras\" Name=\"Flag\" " +
            " Type=\"string\" Value=\"yes\" KeyPath=\"yes\"/>" +
            "</Component></ComponentGroup></Fragment></Wix>", TestContext.Current.CancellationToken);
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [fragment],
            ExtensionIdPrefix = "Acme.",
            ExtensionComponentGroupRefs = ["Acme.Extras"]
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(), TestContext.Current.CancellationToken)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert.True(database.Contains("Registry", "Key", "Software\\Acme\\Extras"),
            "The caller fragment registry row did not reach the MSI.");
        Assert.Equal(3, database.RowCount("Component", "Component"));
        // A same-version rebuild with changed fragment content must be rejected.
        await File.AppendAllTextAsync(fragment, "<!-- changed -->", TestContext.Current.CancellationToken);
        await ExpectAsync<IOException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [fragment],
            ExtensionIdPrefix = "Acme.",
            ExtensionComponentGroupRefs = ["Acme.Extras"]
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "same product version");
    }

    [Fact]
    public async Task RejectsChangedFragmentReferencedFile()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var payload = Path.Combine(fixture.Root, "fragment-payload.bin");
        var fragment = Path.Combine(fixture.Root, "extras.wxs");
        await File.WriteAllTextAsync(payload, "referenced payload v1", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fragment,
            "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\"><Fragment>" +
            "<ComponentGroup Id=\"Acme.Extras\">" +
            "<Component Id=\"Acme.ExtraFile\" Guid=\"{11111111-2222-3333-4444-555555555555}\" " +
            "Directory=\"INSTALLFOLDER\">" +
            "<File Id=\"Acme.ExtraPayload\" Source=\"fragment-payload.bin\"/>" +
            // 落 user profile 的组件必须有 HKCU 注册表 KeyPath，否则真 WiX ICE38 先于指纹断言炸。
            "<RegistryValue Root=\"HKCU\" Key=\"Software\\Acme\\Extras\" Name=\"Flag\" " +
            " Type=\"string\" Value=\"yes\" KeyPath=\"yes\"/>" +
            "</Component></ComponentGroup></Fragment></Wix>", TestContext.Current.CancellationToken);
        var bundler = new WixBundler(new WixBundleConfiguration
        {
            ExtensionFragmentFiles = [fragment],
            ExtensionIdPrefix = "Acme.",
            ExtensionComponentGroupRefs = ["Acme.Extras"]
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache });
        await bundler.BuildAsync(fixture.Request(), TestContext.Current.CancellationToken);
        // 片段文本不动、仅改其 <File Source> 引用的文件：指纹变化后同版本 MSI 必须拒绝复用。
        await File.WriteAllTextAsync(payload, "referenced payload v2", TestContext.Current.CancellationToken);
        await ExpectAsync<IOException>(() => bundler.BuildAsync(fixture.Request(), TestContext.Current.CancellationToken), "same product version");
    }

    [Fact]
    public async Task BuildsExpertMsiTemplate()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        using var fixture = new WixTestFixture(Temp("wix-fixture").FullName);
        var template = Path.Combine(fixture.Root, "expert.wxs");
        await File.WriteAllTextAsync(template,
            "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\">" +
            "<Product Id=\"$(var.Bundler.ProductCode)\" Name=\"$(var.Bundler.ProductName)\" " +
            " Language=\"$(var.Bundler.ProductLanguage)\" Version=\"$(var.Bundler.ProductVersion)\" " +
            " Manufacturer=\"$(var.Bundler.Manufacturer)\" " +
            " UpgradeCode=\"$(var.Bundler.UpgradeCode)\" Codepage=\"$(var.Bundler.Codepage)\">" +
            "<Package InstallerVersion=\"500\" Compressed=\"yes\" " +
            " InstallScope=\"$(var.Bundler.InstallScope)\"/>" +
            "<Media Id=\"1\" Cabinet=\"app.cab\" EmbedCab=\"yes\"/>" +
            "<MajorUpgrade Schedule=\"afterInstallInitialize\" AllowSameVersionUpgrades=\"no\" " +
            " DowngradeErrorMessage=\"!(loc.BundlerDowngradeErrorMessage)\"/>" +
            "<Property Id=\"MSIINSTALLPERUSER\" Value=\"1\"/>" +
            "<Directory Id=\"TARGETDIR\" Name=\"SourceDir\">" +
            "<Directory Id=\"LocalAppDataFolder\">" +
            "<Directory Id=\"INSTALLFOLDER\" Name=\"$(var.Bundler.ProductName)\"/></Directory>" +
            "</Directory>" +
            "<DirectoryRef Id=\"INSTALLFOLDER\">" +
            "<Component Id=\"Expert.App\" Guid=\"{22222222-3333-4444-5555-666666666666}\">" +
            "<File Source=\"" + Path.Combine(fixture.Input, "fixture.exe") + "\"/>" +
            "<RemoveFolder Id=\"Expert.RemoveFolder\" On=\"uninstall\"/>" +
            "<RegistryValue Root=\"HKCU\" Key=\"Software\\Expert\\Fixture\" Name=\"Mark\" " +
            " Type=\"string\" Value=\"expert\" KeyPath=\"yes\"/>" +
            "</Component></DirectoryRef>" +
            "<Feature Id=\"Complete\" Title=\"$(var.Bundler.ProductName)\" Level=\"1\">" +
            "<ComponentRef Id=\"Expert.App\"/></Feature>" +
            "</Product></Wix>", TestContext.Current.CancellationToken);
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            ExpertTemplateFile = template
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(), TestContext.Current.CancellationToken)).Single();
        using (var database = new MsiDatabaseReader(artifact.Path))
        {
            var identity = WixIdentity.Create("com.example.wixtestfixture", "1.0.0",
                "windows-x86_64", WixInstallScope.CurrentUser);
            Assert.Equal(identity.ProductCode.ToString("B").ToUpperInvariant(), database.Property("ProductCode"));
            Assert.True(database.Contains("Registry", "Key", "Software\\Expert\\Fixture"),
                "The expert template's own rows did not reach the MSI.");
        }
        var forged = Path.Combine(fixture.Root, "forged.wxs");
        await File.WriteAllTextAsync(forged,
            (await File.ReadAllTextAsync(template, TestContext.Current.CancellationToken)).Replace("$(var.Bundler.ProductCode)",
                "{99999999-9999-9999-9999-999999999999}"), TestContext.Current.CancellationToken);
        await ExpectAsync<InvalidOperationException>(() => new WixBundler(new WixBundleConfiguration
        {
            ExpertTemplateFile = forged
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(outputDirectory: Path.Combine(fixture.Root, "forged-out")),
                TestContext.Current.CancellationToken),
            "does not match the Bundler identity");
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

    private sealed class MsiVerifyingSigner(IBundleSigner inner, X509Certificate2 certificate) : IBundleSigner
    {
        public List<BundleSigningArtifactKind> Kinds { get; } = [];

        public async Task SignAsync(BundleSigningRequest request, CancellationToken cancellationToken = default)
        {
            await inner.SignAsync(request, cancellationToken);
            Assert.True(SignedFileCertificates.EmbeddedSignatureContains(request.Path, certificate),
                "A signed MSI artifact has the wrong certificate.");
            Kinds.Add(request.ArtifactKind);
        }
    }

    private sealed class MsiRecordingSigner : IBundleSigner
    {
        public List<BundleSigningArtifactKind> Kinds { get; } = [];
        public List<string> Paths { get; } = [];
        public bool FailInstaller { get; init; }

        public Task SignAsync(BundleSigningRequest request, CancellationToken cancellationToken = default)
        {
            Kinds.Add(request.ArtifactKind);
            Paths.Add(request.Path);
            if (request.ArtifactKind == BundleSigningArtifactKind.Installer && FailInstaller)
                throw new InvalidOperationException("MSI signing failed in the test signer.");
            if (request.ArtifactKind != BundleSigningArtifactKind.Installer)
                File.AppendAllText(request.Path, " signed");
            return Task.CompletedTask;
        }
    }

    private static async Task ExpectAsync<TException>(Func<Task> action, string message)
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
