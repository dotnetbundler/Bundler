using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DotNet.Bundler;
using DotNet.Bundler.MacApp;

namespace Bundler.ApiTests;

/// <summary>
/// .app 产包变体腿（收割自旧 MacAppTests 的 spawn 段）：真实 MacAppBundler.BuildAsync
/// 组装——POSIX 宿主会真拉起 chmod，macOS 上 plutil/actool 亦触达。
/// InfoPlist 是 internal——本层经 <see cref="ReadPlist"/> 直读 XML 产物断言。
/// 断言不写宿主分叉：原版门控原样保留（SkipWhen Windows 的腿在非 Windows 验权限位/套接字，
/// plutil 腿仅 macOS）。
/// </summary>
public sealed class MacAppBuildApiTests : TempBase
{
    [Fact]
    public async Task BuildsMinimalAppBundle()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler()
                .BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken);
            var appPath = Assert.Single(artifacts).Path;
            Assert.Equal(PackageFormat.App, artifacts[0].Format);
            Assert.Equal("ExampleApp-1.0.0-arm64.app", Path.GetFileName(appPath));
            Assert.EndsWith("ExampleApp-1.0.0-arm64.app", appPath);
            foreach (var required in new[]
                     {
                         "Contents/Info.plist", "Contents/PkgInfo",
                         "Contents/MacOS/ExampleApp", "Contents/MacOS/ExampleApp.dll",
                         "Contents/Resources"
                     })
            {
                var path = Path.Combine(appPath, required.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path) || Directory.Exists(path), $"Missing bundle entry: {required}");
            }
            Assert.Equal("APPL????", File.ReadAllText(Path.Combine(appPath, "Contents", "PkgInfo")));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task WritesAndReadsCoreInfoPlist()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(input, output),
                TestContext.Current.CancellationToken);
            var values = ReadPlistStrings(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert.Equal("com.example.app", values["CFBundleIdentifier"]);
            Assert.True(values["CFBundleName"] == "ExampleApp" && values["CFBundleDisplayName"] == "ExampleApp",
                "Name keys must default to the product name.");
            Assert.Equal("ExampleApp", values["CFBundleExecutable"]);
            Assert.Equal("APPL", values["CFBundlePackageType"]);
            Assert.True(values["CFBundleShortVersionString"] == "1.0.0" && values["CFBundleVersion"] == "1.0.0",
                "Both version keys must default to the package version.");
            Assert.True(values["CFBundleSignature"] == "????" && values["CFBundleInfoDictionaryVersion"] == "6.0" &&
                   values["NSHighResolutionCapable"] == "true",
                "InfoDictionaryVersion/signature/high-resolution keys mismatch.");
            Assert.False(values.ContainsKey("LSMinimumSystemVersion"), "LSMinimumSystemVersion must not be written unless configured.");
            Assert.False(values.ContainsKey("CFBundleIconFile"), "No icon key without icon input.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task AppliesAppMetadataOverrides()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                PackageName = "ShortName",
                BundleDisplayName = "Long Display Name",
                ShortVersion = "2.0",
                BuildVersion = "2026.9.1",
                MinimumSystemVersion = "11.0",
                Category = "public.app-category.utilities"
            }).BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken);
            var values = ReadPlistStrings(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert.True(values["CFBundleName"] == "ShortName" &&
                   values["CFBundleDisplayName"] == "Long Display Name", "Name overrides must be honored.");
            Assert.True(values["CFBundleShortVersionString"] == "2.0" && values["CFBundleVersion"] == "2026.9.1",
                "Version overrides must be honored.");
            Assert.Equal("11.0", values["LSMinimumSystemVersion"]);
            Assert.Equal("public.app-category.utilities", values["LSApplicationCategoryType"]);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task RejectsNonAppleVersions()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, version: "1.0.0-alpha.5"),
                    TestContext.Current.CancellationToken),
                "A SemVer package version must be rejected without an explicit Apple version.");
            await new MacAppBundler(new MacAppBundleConfiguration { ShortVersion = "1.0" })
                .BuildAsync(MacConfiguration(input, version: "1.0.0-alpha.5"), TestContext.Current.CancellationToken);
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration { BuildVersion = "1.0-beta" })
                    .BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "A non-numeric CFBundleVersion must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    public async Task RejectsNonMachOExecutable()
    {
        var input = CreateInputDirectory();
        File.WriteAllText(Path.Combine(input, "ExampleApp"), "not a binary");
        try
        {
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "A non-Mach-O main executable must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    public async Task MapsPayloadIntoContentsLayout()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        var extras = Temp("app-extras").FullName;
        try
        {
            File.WriteAllText(Path.Combine(input, "nested", "helper.txt"), "helper");
            var resource = Path.Combine(extras, "readme.txt");
            var content = Path.Combine(extras, "plugin.bundle");
            var framework = Path.Combine(extras, "Fixture.framework");
            var dylib = Path.Combine(extras, "libfixture.dylib");
            Directory.CreateDirectory(content);
            Directory.CreateDirectory(framework);
            File.WriteAllText(resource, "resource");
            File.WriteAllText(Path.Combine(content, "inner.txt"), "plugin");
            File.WriteAllText(Path.Combine(framework, "Fixture"), "stub");
            File.WriteAllText(dylib, "dylib");

            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                Files =
                [
                    new MacAppFileEntry { Source = content, Destination = "PlugIns/plugin.bundle" }
                ],
                FrameworkDirectories = [framework, dylib]
            }).BuildAsync(MacConfiguration(
                input, output,
                resources: [new BundleResourceConfiguration { Source = resource, Destination = "docs/readme.txt" }]),
                TestContext.Current.CancellationToken);

            var root = Path.Combine(artifacts[0].Path, "Contents");
            Assert.True(File.Exists(Path.Combine(root, "MacOS", "nested", "helper.txt")),
                "The input tree must be preserved inside Contents/MacOS/.");
            Assert.True(File.Exists(Path.Combine(root, "Resources", "docs", "readme.txt")),
                "Resources must keep their relative target path inside Contents/Resources/.");
            Assert.True(File.Exists(Path.Combine(root, "PlugIns", "plugin.bundle", "inner.txt")),
                "Explicit Contents mappings must land under Contents/.");
            Assert.True(File.Exists(Path.Combine(root, "Frameworks", "Fixture.framework", "Fixture")) &&
                   File.Exists(Path.Combine(root, "Frameworks", "libfixture.dylib")),
                "Framework payloads must land in Contents/Frameworks/.");
        }
        finally
        {
            Cleanup(input, output, extras);
        }
    }

    [Fact]
    public async Task RejectsReparsePointsInAppPayload()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "requires a non-Windows host");
        // CA1416 分析器不认 SkipWhen——守卫保留给编译期平台判断。
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var input = CreateInputDirectory();
        var outside = Temp("app-outside").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "real.txt"), "x");
            var link = Path.Combine(input, "linked.txt");
            try
            {
                File.CreateSymbolicLink(link, Path.Combine(outside, "real.txt"));
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip("The host cannot create symlinks; nothing to verify here.");
            }
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "Symlinks inside the .app payload must be rejected.");
        }
        finally
        {
            Cleanup(input, outside);
        }
    }

    [Fact]
    public async Task SynthesizesIcnsFromPngs()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        var assets = Temp("app-assets").FullName;
        try
        {
            var png128 = Path.Combine(assets, "icon-128.png");
            var png256 = Path.Combine(assets, "icon-256.png");
            File.WriteAllBytes(png128, FakePng(128));
            File.WriteAllBytes(png256, FakePng(256));
            var artifacts = await new MacAppBundler()
                .BuildAsync(MacConfiguration(input, output, icons: [png128, png256]),
                    TestContext.Current.CancellationToken);
            var iconPath = Path.Combine(artifacts[0].Path, "Contents", "Resources", "AppIcon.icns");
            Assert.True(File.Exists(iconPath), "The synthesized .icns icon is missing.");
            var icns = File.ReadAllBytes(iconPath);
            Assert.Equal("icns", Encoding.ASCII.GetString(icns, 0, 4));
            var firstChunk = Encoding.ASCII.GetString(icns, 8, 4);
            Assert.True(firstChunk == "ic07" || firstChunk == "ic08", "Unexpected first icns chunk type.");
            var values = ReadPlistStrings(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert.Equal("AppIcon.icns", values["CFBundleIconFile"]);
        }
        finally
        {
            Cleanup(input, output, assets);
        }
    }

    [Fact]
    public async Task EmitsDocumentTypesForSharedAssociations()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(
                input, output,
                fileAssociations:
                [
                    new BundleFileAssociationConfiguration { Extensions = [".txt", "xyz"], Name = "Document" },
                    new BundleFileAssociationConfiguration { Extensions = ["png"], Description = "Image" }
                ]), TestContext.Current.CancellationToken);
            var plist = ReadPlist(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            var docTypes = (List<object>)plist["CFBundleDocumentTypes"];
            Assert.Equal(2, docTypes.Count);
            var first = (Dictionary<string, object>)docTypes[0];
            Assert.Equal(((List<object>)first["CFBundleTypeExtensions"]), ["txt", "xyz"]);
            Assert.Equal("Document", (string)first["CFBundleTypeName"]);
            Assert.True((string)first["CFBundleTypeRole"] == "Editor" && (string)first["LSHandlerRank"] == "Default",
                "Shared associations default to Editor/Default.");
            var contentTypes = (List<object>)first["LSItemContentTypes"];
            Assert.Contains("public.plain-text", contentTypes);
            Assert.False(contentTypes.Any(value => (string)value == "xyz"), "Unknown extensions must not appear as content types.");
            var second = (Dictionary<string, object>)docTypes[1];
            Assert.Contains("public.png", ((List<object>)second["LSItemContentTypes"]));
            Assert.False(plist.ContainsKey("UTExportedTypeDeclarations"), "Shared associations without an exported type emit no UTExportedTypeDeclarations.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task AppliesDocumentTypeOverridesAndExportedUtis()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                DocumentTypes =
                [
                    new MacAppDocumentTypeConfiguration
                    {
                        Extensions = ["abc"],
                        Role = MacAppTypeRole.Viewer,
                        Rank = MacAppHandlerRank.Owner,
                        ExportedTypeIdentifier = "com.example.abc",
                        ExportedTypeConformsTo = ["public.data"]
                    },
                    new MacAppDocumentTypeConfiguration
                    {
                        ContentTypes = ["public.plain-text"],
                        Name = "Any Text"
                    }
                ]
            }).BuildAsync(MacConfiguration(
                input, output,
                fileAssociations:
                [
                    new BundleFileAssociationConfiguration
                    {
                        Extensions = ["abc"], Name = "ABC File",
                        Description = "ABC document", MimeType = "application/x-abc"
                    }
                ]), TestContext.Current.CancellationToken);
            var plist = ReadPlist(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            var docTypes = (List<object>)plist["CFBundleDocumentTypes"];
            Assert.Equal(2, docTypes.Count);
            var merged = (Dictionary<string, object>)docTypes[0];
            Assert.Equal("ABC File", (string)merged["CFBundleTypeName"]);
            Assert.True((string)merged["CFBundleTypeRole"] == "Viewer" &&
                   (string)merged["LSHandlerRank"] == "Owner",
                "mac-specific role/rank must be honored.");
            var contentTypes = (List<object>)merged["LSItemContentTypes"];
            Assert.Equal(contentTypes, ["com.example.abc"]);
            var exported = (List<object>)plist["UTExportedTypeDeclarations"];
            var declaration = (Dictionary<string, object>)exported[0];
            Assert.Equal("com.example.abc", (string)declaration["UTTypeIdentifier"]);
            Assert.Equal("ABC document", (string)declaration["UTTypeDescription"]);
            Assert.Equal(((List<object>)declaration["UTTypeConformsTo"]), ["public.data"]);
            var tags = (Dictionary<string, object>)declaration["UTTypeTagSpecification"];
            Assert.Equal(((List<object>)tags["public.filename-extension"]), ["abc"]);
            Assert.Equal("application/x-abc", (string)tags["public.mime-type"]);
            var standalone = (Dictionary<string, object>)docTypes[1];
            Assert.False(standalone.ContainsKey("CFBundleTypeExtensions"), "ContentTypes-only entries emit no extension list.");
            Assert.Equal("Any Text", (string)standalone["CFBundleTypeName"]);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task EmitsUrlTypesWithDefaultsAndOverrides()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                UrlTypes = [new MacAppUrlTypeConfiguration { Schemes = ["myapp"], Role = MacAppTypeRole.Viewer }]
            }).BuildAsync(MacConfiguration(
                input, output,
                urlProtocols:
                [
                    new BundleUrlProtocolConfiguration { Schemes = ["myapp"], Name = "MyApp Link" },
                    new BundleUrlProtocolConfiguration { Schemes = ["helper", "helper2"] }
                ]), TestContext.Current.CancellationToken);
            var plist = ReadPlist(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            var urlTypes = (List<object>)plist["CFBundleURLTypes"];
            Assert.Equal(2, urlTypes.Count);
            var merged = (Dictionary<string, object>)urlTypes[0];
            Assert.Equal(((List<object>)merged["CFBundleURLSchemes"]), ["myapp"]);
            Assert.Equal("MyApp Link", (string)merged["CFBundleURLName"]);
            Assert.Equal("Viewer", (string)merged["CFBundleTypeRole"]);
            var standalone = (Dictionary<string, object>)urlTypes[1];
            Assert.Equal("com.example.app helper", (string)standalone["CFBundleURLName"]);
            Assert.Equal("Editor", (string)standalone["CFBundleTypeRole"]);
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    UrlTypes = [new MacAppUrlTypeConfiguration()]
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "URL types without a scheme must be rejected.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task EmitsAtsExceptionDomains()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var plain = await new MacAppBundler().BuildAsync(MacConfiguration(input, output),
                TestContext.Current.CancellationToken);
            Assert.False(ReadPlist(Path.Combine(plain[0].Path, "Contents", "Info.plist"))
                .ContainsKey("NSAppTransportSecurity"), "ATS must stay strict unless an exception domain is configured.");
            var relaxed = await new MacAppBundler(new MacAppBundleConfiguration
            {
                ExceptionDomain = "dev.example.com"
            }).BuildAsync(MacConfiguration(input, Path.Combine(output, "relaxed")),
                TestContext.Current.CancellationToken);
            var ats = (Dictionary<string, object>)ReadPlist(
                Path.Combine(relaxed[0].Path, "Contents", "Info.plist"))["NSAppTransportSecurity"];
            var domains = (Dictionary<string, object>)ats["NSExceptionDomains"];
            var domain = (Dictionary<string, object>)domains["dev.example.com"];
            Assert.True((bool)domain["NSExceptionAllowsInsecureHTTPLoads"] &&
                   (bool)domain["NSIncludesSubdomains"],
                "The exception domain must allow insecure HTTP including subdomains.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration { ExceptionDomain = "  " })
                    .BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "A blank exception domain must be rejected.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task MergesCallerPlistAndEnforcesIdentityKeys()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var inline = """
                <plist version="1.0"><dict>
                <key>NSSupportsSuddenTermination</key><true/>
                <key>MyAppFlags</key><dict><key>Experimental</key><true/><key>Level</key><integer>3</integer></dict>
                <key>CFBundleDisplayName</key><string>Caller Name</string>
                </dict></plist>
                """;
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration { InfoPlistXml = inline })
                .BuildAsync(MacConfiguration(input, output), TestContext.Current.CancellationToken);
            var plist = ReadPlist(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert.True(plist["NSSupportsSuddenTermination"] is true, "Caller boolean keys must merge.");
            Assert.Equal("Caller Name", (string)plist["CFBundleDisplayName"]);
            var flags = (Dictionary<string, object>)plist["MyAppFlags"];
            Assert.True(flags["Experimental"] is true && (long)flags["Level"] == 3,
                "Nested caller dicts must merge typed.");
            Assert.Equal("com.example.app", (string)plist["CFBundleIdentifier"]);

            var file = Path.Combine(input, "custom.plist");
            File.WriteAllText(file, "<plist version=\"1.0\"><dict><key>MyFileKey</key><string>v</string></dict></plist>");
            var fromFile = await new MacAppBundler(new MacAppBundleConfiguration { InfoPlistFile = file })
                .BuildAsync(MacConfiguration(input, Path.Combine(output, "fromfile")),
                    TestContext.Current.CancellationToken);
            Assert.True(ReadPlist(Path.Combine(fromFile[0].Path, "Contents", "Info.plist"))
                    .ContainsKey("MyFileKey"),
                "InfoPlistFile must merge like inline XML.");

            await AssertThrows<InvalidOperationException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistXml = "<dict><key>CFBundleIdentifier</key><string>com.evil.app</string></dict>"
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "Caller plists must not override identity keys.");
            await AssertThrows<InvalidOperationException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistXml = "<dict><key>CFBundlePackageType</key><string>BNDL</string></dict>"
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "CFBundlePackageType conflicts must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistFile = file, InfoPlistXml = "<dict/>"
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "InfoPlistFile and InfoPlistXml are mutually exclusive.");
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistXml = "<dict><key>oops</key>"
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "Invalid caller plist XML must fail the build.");
            await AssertThrows<FileNotFoundException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistFile = Path.Combine(input, "missing.plist")
                }).BuildAsync(MacConfiguration(input), TestContext.Current.CancellationToken),
                "A missing caller plist file must fail early.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task ValidatesMachOArchitecturesPerTarget()
    {
        var arm64Input = CreateInputDirectory();
        var wrongArchInput = CreateInputDirectory();
        File.WriteAllBytes(Path.Combine(wrongArchInput, "ExampleApp"), FakeMachO(0x01000007));
        var fatInput = CreateInputDirectory();
        File.WriteAllBytes(Path.Combine(fatInput, "ExampleApp"), FakeFatMachO(0x01000007, 0x0100000C));
        File.WriteAllBytes(Path.Combine(fatInput, "helper"), FakeFatMachO(0x01000007, 0x0100000C));
        var output = Temp("app-out").FullName;
        try
        {
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(
                    wrongArchInput, Path.Combine(output, "wrong")), TestContext.Current.CancellationToken),
                "An x86_64 executable must not pass in an macos-aarch64 target.");
            var fat = await new MacAppBundler().BuildAsync(MacConfiguration(
                fatInput, Path.Combine(output, "fat"), target: "macos-x86_64"),
                TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(fat[0].Path, "Contents", "MacOS", "ExampleApp")),
                "Fat binaries satisfy both macos-universal targets.");
            var thin = await new MacAppBundler().BuildAsync(MacConfiguration(
                arm64Input, Path.Combine(output, "thin"), target: "macos-aarch64"),
                TestContext.Current.CancellationToken);
            Assert.Single(thin);
            var universal = await new MacAppBundler().BuildAsync(MacConfiguration(
                fatInput, Path.Combine(output, "universal"), target: "macos-universal"),
                TestContext.Current.CancellationToken);
            Assert.Single(universal);
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(
                    arm64Input, Path.Combine(output, "macos-universal-thin"), target: "macos-universal"),
                    TestContext.Current.CancellationToken),
                "The macos-universal target must reject a payload missing the x86_64 slice.");
            var mismatchedHelper = CreateInputDirectory();
            File.WriteAllBytes(Path.Combine(mismatchedHelper, "helper"), FakeMachO(0x01000007));
            try
            {
                await AssertThrows<InvalidDataException>(
                    () => new MacAppBundler().BuildAsync(MacConfiguration(
                        mismatchedHelper, Path.Combine(output, "helper")), TestContext.Current.CancellationToken),
                    "Payload binaries of the wrong architecture must be rejected, not only the main executable.");
            }
            finally
            {
                Cleanup(mismatchedHelper);
            }
        }
        finally
        {
            Cleanup(arm64Input, wrongArchInput, fatInput, output);
        }
    }

    [Fact]
    public async Task PassesCarAndDegradesIconInputs()
    {
        var input = CreateInputDirectory();
        var assets = Temp("app-assets").FullName;
        var output = Temp("app-out").FullName;
        var logger = new ListLogger();
        try
        {
            var car = Path.Combine(assets, "App.car");
            File.WriteAllBytes(car, [1, 2, 3, 4]);
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration(),
                    new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(MacConfiguration(input, output, icons: [car]),
                    TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(artifacts[0].Path, "Contents", "Resources", "Assets.car")),
                "A .car input must be copied to Resources/Assets.car.");
            var plist = ReadPlist(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert.False(plist.ContainsKey("CFBundleIconFile"), "A .car-only icon set writes no CFBundleIconFile.");
            var iconDir = Path.Combine(assets, "Icon.icon");
            Directory.CreateDirectory(iconDir);
            File.WriteAllText(Path.Combine(iconDir, "icon.json"), "{}");
            var degraded = await new MacAppBundler(new MacAppBundleConfiguration(),
                    new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(MacConfiguration(input, Path.Combine(output, "icon"), icons: [iconDir]),
                    TestContext.Current.CancellationToken);
            var degradedPlist = ReadPlist(
                Path.Combine(degraded[0].Path, "Contents", "Info.plist"));
            var hasCar = File.Exists(Path.Combine(degraded[0].Path, "Contents", "Resources", "Assets.car"));
            // actool compiles a fabricated .icon only when the Xcode 26 toolchain accepts it;
            // otherwise the backend must degrade with a warning instead of failing the build.
            Assert.True(hasCar || logger.Messages.Any(m => m.Contains("Skipping Assets.car")),
                "A rejected .icon input must degrade with an Assets.car warning.");
            Assert.Equal(hasCar, degradedPlist.ContainsKey("CFBundleIconName"));
            Assert.False(degradedPlist.ContainsKey("CFBundleIconFile"),
                ".icon 降级路径永不写 CFBundleIconFile（散位图不可用）。");
            var notADir = Path.Combine(assets, "NotADir.icon");
            File.WriteAllBytes(notADir, [1]);
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(
                    input, Path.Combine(output, "missing"), icons: [notADir]),
                    TestContext.Current.CancellationToken),
                "A non-directory .icon input must be rejected.");
        }
        finally
        {
            Cleanup(input, assets, output);
        }
    }

    [Fact]
    public async Task RebuildsIdenticalAppBundles()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var configuration = MacConfiguration(input, output);
            var bundler = new MacAppBundler();
            var first = await bundler.BuildAsync(configuration, TestContext.Current.CancellationToken);
            var firstPlist = File.ReadAllBytes(Path.Combine(first[0].Path, "Contents", "Info.plist"));
            var second = await bundler.BuildAsync(configuration, TestContext.Current.CancellationToken);
            Assert.Equal(first[0].Path, second[0].Path);
            var secondPlist = File.ReadAllBytes(Path.Combine(second[0].Path, "Contents", "Info.plist"));
            Assert.Equal(firstPlist, secondPlist);
            var firstFiles = Directory.EnumerateFiles(first[0].Path, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(first[0].Path.Length))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var secondFiles = Directory.EnumerateFiles(second[0].Path, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(second[0].Path.Length))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(firstFiles, secondFiles);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task MarksMachOFilesExecutable()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "requires a non-Windows host");
        // CA1416 分析器不认 SkipWhen——守卫保留给编译期平台判断。
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(input, output),
                TestContext.Current.CancellationToken);
            var executable = Path.Combine(artifacts[0].Path, "Contents", "MacOS", "ExampleApp");
            var mode = File.GetUnixFileMode(executable);
            Assert.Equal(UnixFileMode.UserExecute, (mode & UnixFileMode.UserExecute));
            var payload = Path.Combine(artifacts[0].Path, "Contents", "MacOS", "ExampleApp.dll");
            Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(payload) & UnixFileMode.UserExecute);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    public async Task PassesPlutilLint()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                ExceptionDomain = "dev.example.com",
                DocumentTypes = [new MacAppDocumentTypeConfiguration
                {
                    Extensions = ["abc"], ExportedTypeIdentifier = "com.example.abc"
                }],
                InfoPlistXml = "<dict><key>Merged</key><dict><key>Flag</key><true/></dict></dict>"
            }).BuildAsync(MacConfiguration(
                input, output,
                fileAssociations: [new BundleFileAssociationConfiguration { Extensions = ["txt"] }],
                urlProtocols: [new BundleUrlProtocolConfiguration { Schemes = ["myapp"] }]),
                TestContext.Current.CancellationToken);
            var plist = Path.Combine(artifacts[0].Path, "Contents", "Info.plist");
            using var process = TestProcessExit.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "plutil",
                Arguments = $"-lint \"{plist}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("plutil spawn did not complete in 60s");
            Assert.True(TestProcessExit.WaitForExit(process, 60_000), "plutil -lint did not exit in 60s");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    // macos-universal 通用载荷按签名表探测拒收非 Mach-O 的按架构散件（预合并防呆方案①）。
    [Fact]
    public async Task RejectsForeignCodeInUniversalPayload()
    {
        var output = Temp("app-out").FullName;
        var cases = new (string name, byte[] bytes, string needle)[]
        {
            ("tool.elf", [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0], "ELF"),
            ("win.exe", FakePortableExecutable(managed: false, readyToRun: false), "native Windows"),
            ("lib.dll", FakePortableExecutable(managed: true, readyToRun: true), "ReadyToRun"),
        };
        foreach (var (name, bytes, needle) in cases)
        {
            var input = CreateInputDirectory();
            File.WriteAllBytes(Path.Combine(input, "ExampleApp"), FakeFatMachO(0x01000007, 0x0100000C));
            File.WriteAllBytes(Path.Combine(input, name), bytes);
            try
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(
                    () => new MacAppBundler().BuildAsync(
                        MacConfiguration(input, Path.Combine(output, name), target: "macos-universal"),
                        TestContext.Current.CancellationToken));
                Assert.Contains(needle, error.Message, StringComparison.Ordinal);
            }
            finally
            {
                Cleanup(input);
            }
        }
        // 纯 IL 托管程序集（无 ReadyToRun 段）属架构中立内容，放行。
        var clean = CreateInputDirectory();
        File.WriteAllBytes(Path.Combine(clean, "ExampleApp"), FakeFatMachO(0x01000007, 0x0100000C));
        File.WriteAllBytes(Path.Combine(clean, "ExampleApp.managed.dll"),
            FakePortableExecutable(managed: true, readyToRun: false));
        try
        {
            var artifacts = await new MacAppBundler().BuildAsync(
                MacConfiguration(clean, Path.Combine(output, "clean"), target: "macos-universal"),
                TestContext.Current.CancellationToken);
            Assert.True(File.Exists(
                Path.Combine(artifacts[0].Path, "Contents", "MacOS", "ExampleApp.managed.dll")));
        }
        finally
        {
            Cleanup(clean, output);
        }
    }

    // R2: 同一共享 FileAssociations 项只归第一个命中的 documentTypes，不重复合并；
    // 后续 docType 声明被吸收扩展名按重复注册拒绝。
    [Fact]
    public async Task SharedFileAssociationMergesOnce()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                DocumentTypes =
                [
                    new MacAppDocumentTypeConfiguration { Extensions = ["foo"], Name = "First" },
                    new MacAppDocumentTypeConfiguration { Extensions = ["qux"], Name = "Second" },
                ]
            }).BuildAsync(MacConfiguration(input, output, fileAssociations:
                [new BundleFileAssociationConfiguration { Extensions = ["foo", "bar"], Name = "Shared" }]),
                TestContext.Current.CancellationToken);
            var plist = ReadPlist(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            var docTypes = (List<object>)plist["CFBundleDocumentTypes"];
            Assert.Equal(2, docTypes.Count);
            var first = (Dictionary<string, object>)docTypes[0];
            Assert.Equal("First", (string)first["CFBundleTypeName"]);
            Assert.Equal(
                (List<object>)first["CFBundleTypeExtensions"], ["foo", "bar"]);
            var second = (Dictionary<string, object>)docTypes[1];
            Assert.Equal("Second", (string)second["CFBundleTypeName"]);
            Assert.Equal((List<object>)second["CFBundleTypeExtensions"], ["qux"]);

            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes =
                    [
                        new MacAppDocumentTypeConfiguration { Extensions = ["foo"], Name = "First" },
                        new MacAppDocumentTypeConfiguration { Extensions = ["bar"], Name = "Second" },
                    ]
                }).BuildAsync(MacConfiguration(input, output + ".dup", fileAssociations:
                    [new BundleFileAssociationConfiguration { Extensions = ["foo", "bar"] }]),
                    TestContext.Current.CancellationToken),
                "已被共享项吸收的扩展名再被 docType 显式声明必须拒绝。");
        }
        finally
        {
            Cleanup(input, output, output + ".dup");
        }
    }

    // R2: 同一共享 UrlProtocols 项同样只归第一个命中的 urlTypes；
    // 后续 urlType 声明被吸收 scheme 按重复注册拒绝。
    [Fact]
    public async Task SharedUrlProtocolMergesOnce()
    {
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                UrlTypes =
                [
                    new MacAppUrlTypeConfiguration { Schemes = ["app-a"], Name = "A" },
                    new MacAppUrlTypeConfiguration { Schemes = ["app-c"], Name = "C" },
                ]
            }).BuildAsync(MacConfiguration(input, output, urlProtocols:
                [new BundleUrlProtocolConfiguration { Schemes = ["app-a", "app-b"], Name = "Shared" }]),
                TestContext.Current.CancellationToken);
            var plist = ReadPlist(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            var urlTypes = (List<object>)plist["CFBundleURLTypes"];
            Assert.Equal(2, urlTypes.Count);
            var first = (Dictionary<string, object>)urlTypes[0];
            Assert.Equal("A", (string)first["CFBundleURLName"]);
            Assert.Equal(
                (List<object>)first["CFBundleURLSchemes"], ["app-a", "app-b"]);
            var second = (Dictionary<string, object>)urlTypes[1];
            Assert.Equal("C", (string)second["CFBundleURLName"]);
            Assert.Equal((List<object>)second["CFBundleURLSchemes"], ["app-c"]);

            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    UrlTypes =
                    [
                        new MacAppUrlTypeConfiguration { Schemes = ["app-a"], Name = "A" },
                        new MacAppUrlTypeConfiguration { Schemes = ["app-b"], Name = "B" },
                    ]
                }).BuildAsync(MacConfiguration(input, output + ".dup", urlProtocols:
                    [new BundleUrlProtocolConfiguration { Schemes = ["app-a", "app-b"] }]),
                    TestContext.Current.CancellationToken),
                "已被共享项吸收的 scheme 再被 urlType 显式声明必须拒绝。");
        }
        finally
        {
            Cleanup(input, output, output + ".dup");
        }
    }

    // A unix socket (or any other non-regular file) inside the input must be
    // skipped rather than copied into the bundle.
    [Fact]
    public async Task SkipsNonRegularPayloadFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires a non-Windows host");
        var input = CreateInputDirectory();
        var output = Temp("app-out").FullName;
        try
        {
            var bindPath = Path.Combine(Path.GetTempPath(), "bt" + Guid.NewGuid().ToString("N")[..8]);
            using (var socket = new System.Net.Sockets.Socket(
                       System.Net.Sockets.AddressFamily.Unix,
                       System.Net.Sockets.SocketType.Stream,
                       System.Net.Sockets.ProtocolType.Unspecified))
            {
                socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(bindPath));
                // .NET unlinks the bound path on dispose; move the node while bound
                File.Move(bindPath, Path.Combine(input, "agent.sock"));
            }
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(input, output),
                TestContext.Current.CancellationToken);
            var macos = Path.Combine(artifacts[0].Path, "Contents", "MacOS");
            Assert.True(File.Exists(Path.Combine(macos, "ExampleApp")),
                "regular payload files still stage");
            Assert.False(File.Exists(Path.Combine(macos, "agent.sock")), "a unix socket in the input must not reach the .app");
        }
        finally
        {
            Cleanup(input, output);
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

    // InfoPlist 是 internal——Api 层经此读真实产出的 XML plist，
    // 值域与其一致：dict→Dictionary、array→List、string/integer/real/true/false/data/date。
    static Dictionary<string, object> ReadPlist(string path)
    {
        XDocument document;
        using (var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        }))
        {
            document = XDocument.Load(reader);
        }
        var root = document.Root;
        var dict = root?.Name.LocalName == "plist"
            ? root.Elements().FirstOrDefault(element => element.Name.LocalName == "dict")
            : root;
        if (dict is null || dict.Name.LocalName != "dict")
        {
            throw new InvalidDataException("Info.plist: a plist <dict> root is required.");
        }
        return ParseDict(dict);
    }

    static Dictionary<string, string> ReadPlistStrings(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in ReadPlist(path))
        {
            switch (pair.Value)
            {
                case string text:
                    values[pair.Key] = text;
                    break;
                case bool flag:
                    values[pair.Key] = flag ? "true" : "false";
                    break;
                case long integer:
                    values[pair.Key] = integer.ToString(CultureInfo.InvariantCulture);
                    break;
                case double real:
                    values[pair.Key] = real.ToString("R", CultureInfo.InvariantCulture);
                    break;
            }
        }
        return values;
    }

    static Dictionary<string, object> ParseDict(XElement dict)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var entries = dict.Elements().ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            var key = entries[index].Value;
            values[key] = ParseValue(entries[++index]);
        }
        return values;
    }

    static object ParseValue(XElement element) => element.Name.LocalName switch
    {
        "dict" => ParseDict(element),
        "array" => element.Elements().Select(ParseValue).ToList<object>(),
        "string" => element.Value,
        "integer" => long.Parse(element.Value.Trim(), CultureInfo.InvariantCulture),
        "real" => double.Parse(element.Value.Trim(), CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "data" => Convert.FromBase64String(element.Value),
        "date" => DateTimeOffset.Parse(element.Value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        var other => throw new InvalidDataException($"Info.plist: unsupported element <{other}>.")
    };

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
}
