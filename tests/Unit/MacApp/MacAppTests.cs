using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacApp;
using System.Runtime.InteropServices;
using System.Text;


// 从旧 tests/Bundler.Tests/MacAppTests.cs 收割。
// 去 spawn（真实构建会拉起 plutil/codesign/actool/chmod，或断言经未桩 ToolRunner 触工具）：
//   BuildsMinimalAppBundle、WritesAndReadsCoreInfoPlist、AppliesAppMetadataOverrides、
//   RejectsNonAppleVersions（含一条成功构建腿）、MapsPayloadIntoContentsLayout、
//   RejectsReparsePointsInAppPayload（拒绝点在建包暂存段）、SynthesizesIcnsFromPngs、
//   EmitsDocumentTypesForSharedAssociations、AppliesDocumentTypeOverridesAndExportedUtis、
//   EmitsUrlTypesWithDefaultsAndOverrides、EmitsAtsExceptionDomains、
//   MergesCallerPlistAndEnforcesIdentityKeys、ValidatesMachOArchitecturesPerTarget、
//   RejectsNonMachOExecutable（Mach-O 巡检段先于拒绝可能已 chmod）、
//   PassesCarAndDegradesIconInputs、RebuildsIdenticalAppBundles、MarksMachOFilesExecutable、
//   PassesPlutilLint（直接 Process.Start plutil）、RejectsForeignCodeInUniversalPayload、
//   SharedFileAssociationMergesOnce、SharedUrlProtocolMergesOnce、SkipsNonRegularPayloadFiles
//   ——去向见 Integration/Manual 层对应文件。
// 保留的签名用例全部经 ToolRunner.Handler 桩运行，不产子进程。
// 判重跳过：PlansAppBeforePkg（BundlePlannerTests）、SigningHostGateIsPlatformNotSupported
// （Signing_SignedConfig_NonMacHost_Rejected）、AssemblesCodesignArguments（CodesignArguments_*）、
// ResolvesNotaryCredentials（Credentials_*）、MergesUniversalPayloadDirectories（UniversalMerge_*）、
// ReadsMachOArchitectures（MachO_ThinHeader_ReadsArchitecture）、
// RoundTripsInfoPlistValues（InfoPlist_WriteThenRead_RoundTripsValues）。
namespace Bundler.UnitTests.MacApp;

public sealed class MacAppTests : TempBase
{

        [Fact]
    public void RejectsPkgOutsideMac()
    {
        var configuration = MacConfiguration(CreateInputDirectory(), target: "windows-x86_64", formats: [PackageFormat.Pkg]);
        var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
        Assert.Contains(issues, issue => issue.Path.EndsWith("formats", StringComparison.Ordinal));
    }

                    [Fact]
    public async Task RejectsInvalidCategoryAndMinVersion()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration { Category = "utilities" })
                    .BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "A non public.app-category value must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration { MinimumSystemVersion = "ten" })
                    .BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "A malformed LSMinimumSystemVersion must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    public async Task RejectsNestedMainExecutable()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, mainExecutable: "tools/ExampleApp"), TestContext.Current.CancellationToken),
                "A main executable outside Contents/MacOS/ root must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

            [Fact]
    public async Task RejectsUnsafeContentsMappings()
    {
        var input = CreateInputDirectory();
        try
        {
            var file = Path.Combine(input, "note.txt");
            File.WriteAllText(file, "x");
            foreach (var target in new[] { "MacOS/injected", "Resources/injected", "Frameworks/injected",
                                          "Info.plist", "PkgInfo", "../escape", "/absolute", "C:\\abs" })
            {
                await AssertThrows<ArgumentException>(
                    () => new MacAppBundler(new MacAppBundleConfiguration
                    {
                        Files = [new MacAppFileEntry { Source = file, Destination = target }]
                    }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                    $"Contents target '{target}' must be rejected.");
            }
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    public async Task RejectsCollidingPayloadDestinations()
    {
        var input = CreateInputDirectory();
        var output = Temp("DotNet.Bundler.Tests").FullName;
        try
        {
            var file = Path.Combine(input, "readme.txt");
            File.WriteAllText(file, "x");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(
                    input, output,
                    resources:
                    [
                        new BundleResourceConfiguration { Source = file, Destination = "shared.txt" },
                        new BundleResourceConfiguration { Source = file, Destination = "shared.txt" }
                    ]), TestContext.Current.CancellationToken),
                "Two payload entries mapping to the same bundle path must be rejected.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

            [Fact]
    public async Task RejectsInvalidIconInputs()
    {
        var input = CreateInputDirectory();
        var assets = Temp("DotNet.Bundler.Tests").FullName;
        try
        {
            Directory.CreateDirectory(assets);
            var bad = Path.Combine(assets, "icon.bmp");
            File.WriteAllBytes(bad, [1, 2, 3]);
            var oddPng = Path.Combine(assets, "odd.png");
            File.WriteAllBytes(oddPng, FakePng(96));
            var flatPng = Path.Combine(assets, "flat.png");
            File.WriteAllBytes(flatPng, FakePng(128, 256));
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, icons: [bad]), TestContext.Current.CancellationToken),
                "Unsupported icon extensions must be rejected.");
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, icons: [oddPng]), TestContext.Current.CancellationToken),
                "Unsupported PNG sizes must be rejected.");
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, icons: [flatPng]), TestContext.Current.CancellationToken),
                "Non-square PNG icons must be rejected.");
        }
        finally
        {
            Cleanup(input, assets);
        }
    }

            [Fact]
    public async Task RejectsExtensionClaimedThroughSharedAssociation()
    {
        var input = CreateInputDirectory();
        try
        {
            // 共享 {foo,bar} 被首条 docType 吸收后，第二条再显式声明 bar 必拒。
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes =
                    [
                        new MacAppDocumentTypeConfiguration { Extensions = ["foo"] },
                        new MacAppDocumentTypeConfiguration { Extensions = ["bar"] }
                    ]
                }).BuildAsync(MacConfiguration(
                    input,
                    fileAssociations:
                    [new BundleFileAssociationConfiguration { Extensions = ["foo", "bar"] }]), TestContext.Current.CancellationToken),
                "docType 再声明已被共享关联吸收的扩展名必须拒绝。");
            // 反序同样拒：先显式声明 bar，首条再经共享吸收 bar。
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes =
                    [
                        new MacAppDocumentTypeConfiguration { Extensions = ["bar"] },
                        new MacAppDocumentTypeConfiguration { Extensions = ["foo"] }
                    ]
                }).BuildAsync(MacConfiguration(
                    input,
                    fileAssociations:
                    [new BundleFileAssociationConfiguration { Extensions = ["foo", "bar"] }]), TestContext.Current.CancellationToken),
                "先显式声明、后被共享吸收的扩展名同样必须拒绝。");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    public async Task RejectsConflictingOrEmptyDocumentTypes()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes =
                    [
                        new MacAppDocumentTypeConfiguration { Extensions = ["abc"] },
                        new MacAppDocumentTypeConfiguration { Extensions = ["ABC"] }
                    ]
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "Two document types claiming the same extension must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes = [new MacAppDocumentTypeConfiguration()]
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "An empty document type entry must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes = [new MacAppDocumentTypeConfiguration { Extensions = ["bad!ext"] }]
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "Invalid extensions must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes =
                    [
                        new MacAppDocumentTypeConfiguration
                        {
                            Extensions = ["abc"], ExportedTypeIdentifier = "not-a-uti!"
                        }
                    ]
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "Invalid exported UTI identifiers must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

                            [Fact]
    public async Task RejectsDuplicatedAssetIcons()
    {
        var input = CreateInputDirectory();
        var assets = Temp("DotNet.Bundler.Tests").FullName;
        try
        {
            Directory.CreateDirectory(assets);
            var carA = Path.Combine(assets, "a.car");
            var carB = Path.Combine(assets, "b.car");
            File.WriteAllBytes(carA, [1]);
            File.WriteAllBytes(carB, [2]);
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, icons: [carA, carB]), TestContext.Current.CancellationToken),
                "Multiple .car inputs must be rejected.");
        }
        finally
        {
            Cleanup(input, assets);
        }
    }

    [Fact]
    public async Task RejectsLaterStageFeatures()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<NotSupportedException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, signingFiles: ["ExampleApp"]), TestContext.Current.CancellationToken),
                "Signing files must be rejected until MAC-APP-3.");
            await AssertThrows<NotSupportedException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, licenseFile: "license.txt"), TestContext.Current.CancellationToken),
                "A license file must be rejected for .app packages.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    public async Task RejectsInvalidSigningOptions()
    {
        var input = CreateInputDirectory();
        var temp = Temp("DotNet.Bundler.Tests").FullName;
        Directory.CreateDirectory(temp);
        var certificate = Path.Combine(temp, "cert.p12");
        File.WriteAllBytes(certificate, [1, 2, 3]);
        var entitlements = Path.Combine(temp, "entitlements.plist");
        File.WriteAllText(entitlements, "<plist/>");
        try
        {
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration
                    {
                        Identity = "Dev ID", TemporaryCertificateFile = certificate
                    }
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "Identity and a temporary certificate are mutually exclusive.");
            await AssertThrows<FileNotFoundException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration
                    {
                        TemporaryCertificateFile = Path.Combine(temp, "missing.p12")
                    }
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "A missing temporary certificate must be rejected before any tool runs.");
            await AssertThrows<FileNotFoundException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration
                    {
                        Identity = "-", EntitlementsFile = Path.Combine(temp, "missing.plist")
                    }
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "A missing entitlements file must be rejected before any tool runs.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-", Notarize = true }
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "Ad-hoc signatures cannot be notarized.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { SkipStapling = true }
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "SkipStapling without Notarize must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-", NotaryWait = false }
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "NotaryWait=false without Notarize must be rejected.");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await AssertThrows<NotSupportedException>(
                    () => new MacAppBundler(new MacAppBundleConfiguration
                    {
                        Signing = new MacAppSigningConfiguration { Identity = "-" }
                    }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                    "Signing on a non-macOS host must fail before any build work.");
            }
        }
        finally
        {
            Cleanup(input, temp, input + ".artifacts");
        }
        await Task.CompletedTask;
    }

        [Fact]
    public void SigningHostGateDoesNotMaskConfigErrors()
    {
        // 签名宿主门不得遮住其后的配置错——非法旋钮仍按配置错误聚合。
        Assert.SkipWhen(RuntimeInformation.IsOSPlatform(OSPlatform.OSX), "仅非 macOS 宿主。");
        var input = CreateInputDirectory();
        try
        {
            Assert.Throws<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-" },
                    ExceptionDomain = " "
                }).Validate(MacConfiguration(input)));
        }
        finally
        {
            Cleanup(input);
        }
    }

            [Fact]
    public async Task SignsInsideOutOnStubbedTools()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Temp("DotNet.Bundler.Tests").FullName;
        var requests = new List<ToolRunner.Request>();
        var previous = ToolRunner.Handler;
        ToolRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new ToolRunner.Result(0, "", ""));
        };
        try
        {
            var entitlements = TempPath("e.plist");
            Directory.CreateDirectory(Path.GetDirectoryName(entitlements)!);
            File.WriteAllText(entitlements, "<plist/>");
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                Signing = new MacAppSigningConfiguration
                {
                    Identity = "-", EntitlementsFile = entitlements, HardenedRuntime = true
                }
            }).BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken);
            Cleanup(Path.GetDirectoryName(entitlements)!);
            var codesign = requests.Where(r => r.Executable == "codesign").ToList();
            var xattr = requests.Where(r => r.Executable == "xattr").ToList();
            Assert.True(xattr.Count == 1 && xattr[0].Arguments.SequenceEqual(new[] { "-crs", codesign[0].Arguments.Last().Split("/Contents")[0] }),
                "xattr -crs must run once on the .app before signing.");
            // codesign calls: nested files under Contents/MacOS (the managed ExampleApp.dll is
            // signed as nested code even though it is not Mach-O) + main executable + bundle + verify.
            Assert.Equal(4, codesign.Count);
            Assert.True(codesign[0].Arguments.Last().EndsWith("/Contents/MacOS/ExampleApp.dll", StringComparison.Ordinal) &&
                   !string.Join(' ', codesign[0].Arguments).Contains("--entitlements"),
                "Nested payload files are signed first, without entitlements.");
            var bundleArgs = string.Join(' ', codesign[2].Arguments);
            Assert.EndsWith("/Contents/MacOS/ExampleApp", codesign[1].Arguments.Last());
            Assert.True(codesign[2].Arguments.Last().EndsWith(".app", StringComparison.Ordinal) &&
                   bundleArgs.Contains("--entitlements"),
                "The bundle is signed after the executable and carries entitlements.");
            Assert.Equal(new[] { "--verify", "--deep", "--strict" }, codesign[3].Arguments.Take(3));
            Assert.True(requests.Any(r => r.Executable == "codesign") &&
                   !requests.Any(r => r.Executable == "spctl"),
                "Ad-hoc signatures skip the spctl assessment.");
        }
        finally
        {
            ToolRunner.Handler = previous;
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task CleansUpKeychainOnFailure()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Temp("DotNet.Bundler.Tests").FullName;
        var temp = Temp("DotNet.Bundler.Tests").FullName;
        Directory.CreateDirectory(temp);
        var certificate = Path.Combine(temp, "cert.p12");
        File.WriteAllBytes(certificate, [1, 2, 3]);
        var calls = new List<string>();
        var deleted = false;
        var previous = ToolRunner.Handler;
        ToolRunner.Handler = (request, _) =>
        {
            var joined = string.Join(' ', request.Arguments);
            calls.Add(request.Executable + " " + joined);
            if (request.Executable == "security" && joined.StartsWith("list-keychains") &&
                !joined.Contains("-s", StringComparison.Ordinal))
            {
                return Task.FromResult(new ToolRunner.Result(0, "\"/Users/x/Library/Keychains/login.keychain-db\"\n", ""));
            }
            if (request.Executable == "security" && joined.StartsWith("find-identity"))
            {
                return Task.FromResult(new ToolRunner.Result(0,
                    "  1) AA11BB22CC33DD44EE55FF6600112233AABBCCDD \"Bundler Test\"\n     1 valid identities found\n", ""));
            }
            if (request.Executable == "security" && joined.StartsWith("delete-keychain"))
            {
                deleted = true;
            }
            if (request.Executable == "codesign" && !joined.Contains("--verify"))
            {
                return Task.FromResult(new ToolRunner.Result(1, "", "boom"));
            }
            return Task.FromResult(new ToolRunner.Result(0, "", ""));
        };
        try
        {
            await AssertThrows<Exception>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration
                    {
                        TemporaryCertificateFile = certificate,
                        TemporaryCertificatePassword = "pw"
                    }
                }).BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken),
                "A failing codesign must surface as a build failure.");
            Assert.True(deleted, "The temporary keychain must be deleted even when codesign fails.");
            Assert.True(calls.Any(c => c.StartsWith("security list-keychains") && c.Contains("-s") &&
                                  c.Contains("login.keychain")),
                "The user's keychain search list must be restored after failure.");
            Assert.True(calls.Any(c => c.StartsWith("security create-keychain")) &&
                   calls.Any(c => c.StartsWith("security import")),
                "The temporary keychain is created and the certificate imported before signing.");
        }
        finally
        {
            ToolRunner.Handler = previous;
            Cleanup(input, output, temp);
        }
    }

    [Fact]
    public async Task SigningFailureLeavesNoArtifact()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Temp("DotNet.Bundler.Tests").FullName;
        var previous = ToolRunner.Handler;
        ToolRunner.Handler = (request, _) =>
            Task.FromResult(request.Executable == "codesign" && request.Arguments.Contains("--verify")
                ? new ToolRunner.Result(1, "", "not signed at all")
                : new ToolRunner.Result(0, "", ""));
        try
        {
            await AssertThrows<Exception>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-" }
                }).BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken),
                "A failed --verify must fail the build.");
            Assert.False(Directory.Exists(Path.Combine(output, "ExampleApp-1.0.0-arm64.app")), "A failed signing run must not leave a pseudo-success .app at the output path.");
        }
        finally
        {
            ToolRunner.Handler = previous;
            Cleanup(input, output);
        }
    }

        [Fact]
    public void MapsAppSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var task = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        foreach (var property in new[]
                 {
                     "MacAppPackageName", "MacAppDisplayName", "MacAppShortVersion", "MacAppBuildVersion",
                     "MacAppMinimumSystemVersion", "MacAppCategory", "MacAppIconName",
                     "MacAppExceptionDomain", "MacAppInfoPlistFile", "MacAppInfoPlistXml"
                 })
        {
            Assert.Contains(property + "=\"$(Bundler" + property + ")\"", targets);
        }
        Assert.True(targets.Contains("MacAppFiles=\"@(BundlerMacAppFile)\"", StringComparison.Ordinal) &&
               targets.Contains("MacAppFrameworkDirectories=\"@(BundlerMacAppFrameworkDirectory)\"", StringComparison.Ordinal) &&
               targets.Contains("MacDocumentTypes=\"@(BundlerMacDocumentType)\"", StringComparison.Ordinal) &&
               targets.Contains("MacUrlTypes=\"@(BundlerMacUrlType)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerMac* item groups.");
        Assert.Contains("StartsWith('macos-')", targets);
        Assert.Contains("StartsWith('osx-')", targets);
        Assert.True(task.Contains("new MacAppBundler(", StringComparison.Ordinal) &&
               task.Contains("MacAppFileEntry", StringComparison.Ordinal),
            "The MSBuild task does not construct the .app backend.");
    }

        [Fact]
    public async Task WarnsOnWindowsHosts()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var input = CreateInputDirectory();
        var output = Temp("DotNet.Bundler.Tests").FullName;
        var logger = new ListLogger();
        try
        {
            await new MacAppBundler(options: new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken);
            Assert.Contains(logger.Messages, message => message.Contains("executable bit", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

                [Fact]
    public void RejectsInvalidPlistShapes()
    {
        var dict = InfoPlist.ParseXml("<dict><key>A</key><string>1</string></dict>", "test");
        Assert.Equal("1", dict["A"]);
        Assert.ThrowsAny<InvalidDataException>(() => InfoPlist.ParseXml("<plist><array/></plist>", "test"));
        Assert.ThrowsAny<InvalidDataException>(() => InfoPlist.ParseXml("not xml <", "test"));
        Assert.ThrowsAny<InvalidDataException>(
            () => InfoPlist.ParseXml("<dict><key>A</key><wat/></dict>", "test"));
        Assert.ThrowsAny<InvalidDataException>(
            () => InfoPlist.ParseXml("<dict><key>A</key><integer>NaN</integer></dict>", "test"));
    }

    [Fact]
    public void RendersPlistScalars()
    {
        Assert.True(InfoPlist.TryToString("x", out var text) && text == "x");
        Assert.True(InfoPlist.TryToString(true, out var flag) && flag == "true");
        Assert.True(InfoPlist.TryToString(42L, out var integer) && integer == "42");
        Assert.False(InfoPlist.TryToString(new List<object>(), out _));
    }

    [Fact]
    public void FindsIconImageNameInAssetutilJson()
    {
        Assert.Equal("AppIcon", MacAppAssetsCar.IconImageName(
            """[{"AssetType":"MultiSized Image","Name":"Other"},{"AssetType":"Icon Image","Name":"AppIcon"}]"""));
        Assert.Null(MacAppAssetsCar.IconImageName("""[{"AssetType":"MultiSized Image"}]"""));
        Assert.Null(MacAppAssetsCar.IconImageName("{}"));
    }

    // macos-universal 通用载荷按签名表探测拒收非 Mach-O 的按架构散件（预合并防呆方案①）。
        // R2: fat 头的 32 位 offset/size 字段写不下就显式拒写，不产出静默截断的坏件。
    [Fact]
    public void RejectsOversizedFatSlice()
    {
        var output = TempPath("fat.bin");
        var slices = new List<FatSlice>
        {
            new(0x0100000C, 0, uint.MaxValue + 1L, stream => stream.WriteByte(0))
        };
        Assert.Throws<InvalidDataException>(() => MachOFat.Create(output, slices));
        Assert.False(File.Exists(output), "被拒绝的超宽切片不得写出文件。");
    }

    // R2: 合并输入目录里的目录符号链接环显式报错，不再枚举失控。
    [Fact]
    public void RejectsSymlinkLoopInMergeInput()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires symlink support");
        var arm64 = Temp("DotNet.Bundler.Tests").FullName;
        var x64 = arm64 + ".x64";
        var output = arm64 + ".out";
        try
        {
            foreach (var (dir, cpuType) in new[] { (arm64, 0x0100000Cu), (x64, 0x01000007u) })
            {
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                File.WriteAllBytes(Path.Combine(dir, "ExampleApp"), FakeMachO(cpuType));
                Directory.CreateSymbolicLink(Path.Combine(dir, "sub", "loop"), dir);
            }
            // macOS 内核 MAXSYMLINKS=32 先于托管跳数上限报 ELOOP（IOException），
            // Linux 走托管判定 InvalidDataException（非 IOException 子类）——两种都接受。
            var error = Record.Exception(
                () => MacUniversalPayloadMerger.Merge([arm64, x64], output));
            Assert.True(error is InvalidDataException or IOException,
                $"环链必须被拒：期望 InvalidDataException/IOException，实际 {error?.GetType().Name}: {error?.Message}");
        }
        finally
        {
            Cleanup(arm64, x64, output);
        }
    }

    // R2: 同一共享 FileAssociations 项只归第一个命中的 documentTypes，不重复合并；
    // 后续 docType 声明被吸收扩展名按重复注册拒绝。
        // R2: 同一共享 UrlProtocols 项同样只归第一个命中的 urlTypes；
    // 后续 urlType 声明被吸收 scheme 按重复注册拒绝。
        // R2: APPLE_API_KEY_PATH_UNSET 兜底已移除——只设它不得当作凭证来源。
    [Fact]
    public void IgnoresUnsetApiKeyPathEnv()
    {
        var keys = new[]
        {
            "APPLE_PROFILE", "APPLE_API_KEY_PATH", "APPLE_API_KEY", "APPLE_API_ISSUER",
            "APPLE_ID", "APPLE_PASSWORD", "APPLE_TEAM_ID"
        };
        var saved = keys.ToDictionary(key => key, key => Environment.GetEnvironmentVariable(key));
        try
        {
            foreach (var key in keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
            Environment.SetEnvironmentVariable("APPLE_API_KEY_PATH_UNSET", "/tmp/fake-key.p8");
            Assert.Throws<ArgumentException>(
                () => MacAppSigning.ResolveCredentials(
                    new MacAppSigningConfiguration { Notarize = true }));
        }
        finally
        {
            foreach (var key in keys)
            {
                Environment.SetEnvironmentVariable(key, saved[key]);
            }
            Environment.SetEnvironmentVariable("APPLE_API_KEY_PATH_UNSET", null);
        }
    }

    // R2: SharedFrameworks 文件按嵌套代码签；嵌套 .appex 先签内部再以 bundle 维度签。
    [Fact]
    public async Task SignsNestedBundlesInsideOut()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Temp("DotNet.Bundler.Tests").FullName;
        var calls = new List<string>();
        var previous = ToolRunner.Handler;
        ToolRunner.Handler = (request, _) =>
        {
            calls.Add(request.Executable + " " + string.Join(' ', request.Arguments));
            return Task.FromResult(new ToolRunner.Result(0, "", ""));
        };
        try
        {
            await new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-" }
                }).BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken);
            // 手搭的 app 骨架走直接 RunAsync：把文件放到 Contents/ 根的 SharedFrameworks 与嵌套 .appex。
            var appPath = Path.Combine(output, "manual.app");
            var contents = Path.Combine(appPath, "Contents");
            Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
            Directory.CreateDirectory(Path.Combine(contents, "SharedFrameworks"));
            Directory.CreateDirectory(
                Path.Combine(contents, "PlugIns", "Widget.appex", "Contents", "MacOS"));
            File.WriteAllBytes(Path.Combine(contents, "MacOS", "ExampleApp"), FakeMachO());
            File.WriteAllText(Path.Combine(contents, "SharedFrameworks", "libshared.dylib"), "lib");
            File.WriteAllText(
                Path.Combine(contents, "PlugIns", "Widget.appex", "Contents", "MacOS", "Widget"), "w");
            calls.Clear();
            var context = new BundleBuildContext(
                MacConfiguration(input, output),
                new BundlePlanItem(
                    BundleTarget.TryParse("macos-aarch64", out var target) ? target! : throw new InvalidOperationException(),
                    PackageFormat.App, input, "ExampleApp", output, Intermediate: false),
                output, NullBundleLogger.Instance);
            await MacAppSigning.RunAsync(context, appPath, "ExampleApp",
                new MacAppSigningConfiguration { Identity = "-" }, CancellationToken.None);
            var signs = calls.Where(c => c.StartsWith("codesign") && !c.Contains("--verify")).ToList();
            var targets = signs.Select(c => c.Split(' ').Last()).ToList();
            Assert.Contains(targets, t => t.EndsWith("/Contents/SharedFrameworks/libshared.dylib", StringComparison.Ordinal));
            var appex = targets.FindIndex(t => t.EndsWith("Widget.appex", StringComparison.Ordinal));
            var appexInner = targets.FindIndex(t => t.Contains("Widget.appex/Contents/", StringComparison.Ordinal));
            Assert.True(appexInner >= 0 && appex > appexInner,
                "嵌套 .appex 必须先签内部文件再签 bundle 根。");
            Assert.True(targets.FindIndex(t => t.EndsWith("/Contents/MacOS/ExampleApp", StringComparison.Ordinal)) > appex,
                "主可执行在嵌套 bundle 之后签名。");
        }
        finally
        {
            ToolRunner.Handler = previous;
            Cleanup(input, output);
        }
    }

    // R2: p12 口令不走 argv——openssl 可用时经 PEM 导入；缺席/失败如实报错，
    // 不退回 `security import -P`（口令进 argv 可被本机任意进程 ps 读取）。
    [Fact]
    public async Task TemporaryKeychainKeepsPasswordOffArgv()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Temp("DotNet.Bundler.Tests").FullName;
        var temp = Temp("DotNet.Bundler.Tests").FullName;
        Directory.CreateDirectory(temp);
        var certificate = Path.Combine(temp, "cert.p12");
        File.WriteAllBytes(certificate, [1, 2, 3]);
        var calls = new List<string>();
        var previous = ToolRunner.Handler;
        var opensslWorks = true;
        ToolRunner.Handler = (request, _) =>
        {
            var joined = string.Join(' ', request.Arguments);
            calls.Add(request.Executable + " " + joined);
            if (request.Executable == "openssl")
            {
                if (!opensslWorks)
                {
                    return Task.FromResult(new ToolRunner.Result(1, "", "no openssl"));
                }
                var outIndex = request.Arguments.ToList().IndexOf("-out") + 1;
                File.WriteAllText(request.Arguments[outIndex], "PEM");
                return Task.FromResult(new ToolRunner.Result(0, "", ""));
            }
            if (request.Executable == "security" && joined.StartsWith("list-keychains") &&
                !joined.Contains("-s", StringComparison.Ordinal))
            {
                return Task.FromResult(new ToolRunner.Result(0,
                    "\"/Users/x/Library/Keychains/login.keychain-db\"\n", ""));
            }
            if (request.Executable == "security" && joined.StartsWith("find-identity"))
            {
                return Task.FromResult(new ToolRunner.Result(0,
                    "  1) AA11BB22CC33DD44EE55FF6600112233AABBCCDD \"Bundler Test\"\n     1 valid identities found\n", ""));
            }
            return Task.FromResult(new ToolRunner.Result(0, "", ""));
        };
        try
        {
            await new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration
                    {
                        TemporaryCertificateFile = certificate,
                        TemporaryCertificatePassword = "s3cret-pw"
                    }
                }).BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken);
            var import = calls.Single(c => c.StartsWith("security import"));
            Assert.DoesNotContain("s3cret-pw", import);
            Assert.DoesNotContain(" -P ", " " + import + " ");
            Assert.True(calls.Any(c => c.StartsWith("openssl pkcs12") && c.Contains("-passin")),
                "openssl 转换路径必须先把 p12 换成无口令 PEM。");
            // openssl 缺席/失败：如实报错，任何调用面都不得携带口令。
            opensslWorks = false;
            calls.Clear();
            await AssertThrows<InvalidOperationException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                    {
                        Signing = new MacAppSigningConfiguration
                        {
                            TemporaryCertificateFile = certificate,
                            TemporaryCertificatePassword = "s3cret-pw"
                        }
                    }).BuildAsync(MacConfiguration(input, Path.Combine(output, "fallback")), TestContext.Current.CancellationToken),
                "openssl 失败必须报错而不是退回 -P 兜底。");
            Assert.False(calls.Any(c => c.Contains("s3cret-pw") || c.Contains(" -P ")),
                "任何进程参数都不得出现口令或 -P 兜底。");
        }
        finally
        {
            ToolRunner.Handler = previous;
            Cleanup(input, output, temp);
        }
    }

    static BundleConfiguration MacConfiguration(
        string input,
        string output = "",
        string target = "macos-aarch64",
        string mainExecutable = "ExampleApp",
        IReadOnlyList<PackageFormat>? formats = null,
        string version = "1.0.0",
        IReadOnlyList<BundleResourceConfiguration>? resources = null,
        IReadOnlyList<string>? icons = null,
        IReadOnlyList<BundleFileAssociationConfiguration>? fileAssociations = null,
        IReadOnlyList<BundleUrlProtocolConfiguration>? urlProtocols = null,
        IReadOnlyList<string>? signingFiles = null,
        string? licenseFile = null) => new()
        {
            ProductName = "ExampleApp",
            Identifier = "com.example.app",
            Version = version,
            LicenseFile = licenseFile,
            OutputDirectory = output.Length == 0 ? input + ".artifacts" : output,
            Icons = icons ?? [],
            Resources = resources ?? [],
            FileAssociations = fileAssociations ?? [],
            UrlProtocols = urlProtocols ?? [],
            Targets =
            [
                new BundleTargetConfiguration
                {
                    Target = target,
                    InputDirectory = input,
                    MainExecutable = mainExecutable,
                    SigningFiles = signingFiles ?? [],
                    Formats = formats ?? [PackageFormat.App]
                }
            ]
        };

    string CreateInputDirectory()
    {
        var input = Temp("input").FullName;
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "ExampleApp"), FakeMachO());
        File.WriteAllText(Path.Combine(input, "ExampleApp.dll"), "payload");
        Directory.CreateDirectory(Path.Combine(input, "nested"));
        return input;
    }

    // A unix socket (or any other non-regular file) inside the input must be
    // skipped rather than copied into the bundle.
        static byte[] FakeMachO(uint cpuType = 0x0100000C)
    {
        var bytes = new byte[64];
        bytes[0] = 0xCF; bytes[1] = 0xFA; bytes[2] = 0xED; bytes[3] = 0xFE;
        bytes[4] = (byte)cpuType; bytes[5] = (byte)(cpuType >> 8);
        bytes[6] = (byte)(cpuType >> 16); bytes[7] = (byte)(cpuType >> 24);
        return bytes;
    }

    // 最小 PE32 捏件：DOS 头 + PE 签名 + COFF + 可选头 + 一个 .text 段放 COR20。
    static byte[] FakePortableExecutable(bool managed, bool readyToRun)
    {
        var bytes = new byte[0x400];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        WriteLe32(bytes, 0x3C, 0x80);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        WriteLe16(bytes, 0x86, 1);      // NumberOfSections
        WriteLe16(bytes, 0x94, 0xE0);   // SizeOfOptionalHeader
        WriteLe16(bytes, 0x98, 0x10B);  // PE32 magic
        // CLI（COM descriptor）是第 15 个数据目录项；数据目录基址 = 0x98 + 96。
        var cliDirectory = 0x98 + 96 + 14 * 8;
        if (managed)
        {
            WriteLe32(bytes, cliDirectory, 0x2000);   // CLI rva
            WriteLe32(bytes, cliDirectory + 4, 72);   // CLI size
        }
        // .text 段头 @0x98+0xE0：rva 0x2000 ↔ file offset 0x200。
        var section = 0x98 + 0xE0;
        bytes[section] = (byte)'.'; bytes[section + 1] = (byte)'t'; bytes[section + 2] = (byte)'e';
        bytes[section + 3] = (byte)'x'; bytes[section + 4] = (byte)'t';
        WriteLe32(bytes, section + 8, 0x200);   // VirtualSize
        WriteLe32(bytes, section + 12, 0x2000); // VirtualAddress
        WriteLe32(bytes, section + 16, 0x200);  // SizeOfRawData
        WriteLe32(bytes, section + 20, 0x200);  // PointerToRawData
        if (managed && readyToRun)
        {
            // COR20 头的 ManagedNativeHeader 目录（偏移 64）非空 = ReadyToRun。
            WriteLe32(bytes, 0x200 + 64 + 4, 0x20);
        }
        return bytes;
    }

    static void WriteLe16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    static void WriteLe32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    static byte[] FakeFatMachO(params uint[] cpuTypes)
    {
        var bytes = new byte[8 + 20 * cpuTypes.Length];
        bytes[0] = 0xCA; bytes[1] = 0xFE; bytes[2] = 0xBA; bytes[3] = 0xBE;
        bytes[7] = (byte)cpuTypes.Length;
        for (var index = 0; index < cpuTypes.Length; index++)
        {
            var offset = 8 + index * 20;
            bytes[offset] = (byte)(cpuTypes[index] >> 24);
            bytes[offset + 1] = (byte)(cpuTypes[index] >> 16);
            bytes[offset + 2] = (byte)(cpuTypes[index] >> 8);
            bytes[offset + 3] = (byte)cpuTypes[index];
        }
        return bytes;
    }

    static byte[] FakePng(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16); bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16); bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        return bytes;
    }

    static byte[] FakePng(int size) => FakePng(size, size);

    static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    static async Task AssertThrows<TException>(Func<Task> action, string message)
    {
        var exception = await Record.ExceptionAsync(action);
        Assert.True(exception is TException,
            $"{message}: expected {typeof(TException).Name}, got {exception?.GetType().Name}: {exception?.Message}");
    }



    sealed class ListLogger : IBundleLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(BundleLogLevel level, string message) => Messages.Add(message);
    }

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
}
