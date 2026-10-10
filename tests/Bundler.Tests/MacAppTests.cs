using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacApp;
using System.Runtime.InteropServices;
using System.Text;

public static class MacAppTests
{

    [Fact]
    static void PlansAppBeforePkg()
    {
        var configuration = MacConfiguration(CreateInputDirectory(), formats: [PackageFormat.Pkg]);
        var plan = BundlePlanner.Create(configuration, checkFileSystem: false);
        Assert.Equal(2, plan.Items.Count);
        Assert.True(plan.Items[0].Format == PackageFormat.App && plan.Items[0].Intermediate,
            "The intermediate app step must come first.");
        Assert.True(plan.Items[1].Format == PackageFormat.Pkg && !plan.Items[1].Intermediate,
            "The requested PKG step must come last.");
    }

    [Fact]
    static void RejectsPkgOutsideMac()
    {
        var configuration = MacConfiguration(CreateInputDirectory(), rid: "win-x64", formats: [PackageFormat.Pkg]);
        var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
        Assert.Contains(issues, issue => issue.Path.EndsWith("formats", StringComparison.Ordinal));
    }

    [Fact]
    static async Task BuildsMinimalAppBundle()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = await new MacAppBundler()
                .BuildAsync(MacConfiguration(input, output));
            var appPath = Assert.Single(artifacts).Path;
            Assert.Equal(PackageFormat.App, artifacts[0].Format);
            Assert.Equal("ExampleApp.app", Path.GetFileName(appPath));
            Assert.EndsWith(Path.Combine("osx-arm64", "app", "ExampleApp.app"), appPath);
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
    static async Task WritesAndReadsCoreInfoPlist()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(input, output));
            var values = InfoPlist.ReadStringValues(
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
    static async Task AppliesAppMetadataOverrides()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
            }).BuildAsync(MacConfiguration(input, output));
            var values = InfoPlist.ReadStringValues(
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
    static async Task RejectsNonAppleVersions()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, version: "1.0.0-alpha.5")),
                "A SemVer package version must be rejected without an explicit Apple version.");
            await new MacAppBundler(new MacAppBundleConfiguration { ShortVersion = "1.0" })
                .BuildAsync(MacConfiguration(input, version: "1.0.0-alpha.5"));
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration { BuildVersion = "1.0-beta" })
                    .BuildAsync(MacConfiguration(input)),
                "A non-numeric CFBundleVersion must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    static async Task RejectsInvalidCategoryAndMinVersion()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration { Category = "utilities" })
                    .BuildAsync(MacConfiguration(input)),
                "A non public.app-category value must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration { MinimumSystemVersion = "ten" })
                    .BuildAsync(MacConfiguration(input)),
                "A malformed LSMinimumSystemVersion must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    static async Task RejectsNestedMainExecutable()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, mainExecutable: "tools/ExampleApp")),
                "A main executable outside Contents/MacOS/ root must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    static async Task RejectsNonMachOExecutable()
    {
        var input = CreateInputDirectory();
        File.WriteAllText(Path.Combine(input, "ExampleApp"), "not a binary");
        try
        {
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input)),
                "A non-Mach-O main executable must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    static async Task MapsPayloadIntoContentsLayout()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var extras = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(Path.Combine(input, "nested", "helper.txt"), "helper");
            var resource = Path.Combine(extras, "readme.txt");
            var content = Path.Combine(extras, "plugin.bundle");
            var framework = Path.Combine(extras, "Fixture.framework");
            var dylib = Path.Combine(extras, "libfixture.dylib");
            Directory.CreateDirectory(extras);
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
                resources: [new BundleResourceConfiguration { Source = resource, Destination = "docs/readme.txt" }]));

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
    static async Task RejectsUnsafeContentsMappings()
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
                    }).BuildAsync(MacConfiguration(input)),
                    $"Contents target '{target}' must be rejected.");
            }
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    static async Task RejectsCollidingPayloadDestinations()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                    ])),
                "Two payload entries mapping to the same bundle path must be rejected.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task RejectsReparsePointsInAppPayload()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "requires a non-Windows host");
        // CA1416 分析器不认 SkipWhen——守卫保留给编译期平台判断。
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var input = CreateInputDirectory();
        var outside = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outside);
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
                () => new MacAppBundler().BuildAsync(MacConfiguration(input)),
                "Symlinks inside the .app payload must be rejected.");
        }
        finally
        {
            Cleanup(input, outside);
        }
    }

    [Fact]
    static async Task SynthesizesIcnsFromPngs()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var assets = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(assets);
            var png128 = Path.Combine(assets, "icon-128.png");
            var png256 = Path.Combine(assets, "icon-256.png");
            File.WriteAllBytes(png128, FakePng(128));
            File.WriteAllBytes(png256, FakePng(256));
            var artifacts = await new MacAppBundler()
                .BuildAsync(MacConfiguration(input, output, icons: [png128, png256]));
            var iconPath = Path.Combine(artifacts[0].Path, "Contents", "Resources", "AppIcon.icns");
            Assert.True(File.Exists(iconPath), "The synthesized .icns icon is missing.");
            var icns = File.ReadAllBytes(iconPath);
            Assert.Equal("icns", Encoding.ASCII.GetString(icns, 0, 4));
            var firstChunk = Encoding.ASCII.GetString(icns, 8, 4);
            Assert.True(firstChunk == "ic07" || firstChunk == "ic08", "Unexpected first icns chunk type.");
            var values = InfoPlist.ReadStringValues(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert.Equal("AppIcon.icns", values["CFBundleIconFile"]);
        }
        finally
        {
            Cleanup(input, output, assets);
        }
    }

    [Fact]
    static async Task RejectsInvalidIconInputs()
    {
        var input = CreateInputDirectory();
        var assets = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, icons: [bad])),
                "Unsupported icon extensions must be rejected.");
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, icons: [oddPng])),
                "Unsupported PNG sizes must be rejected.");
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, icons: [flatPng])),
                "Non-square PNG icons must be rejected.");
        }
        finally
        {
            Cleanup(input, assets);
        }
    }

    [Fact]
    static async Task EmitsDocumentTypesForSharedAssociations()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(
                input, output,
                fileAssociations:
                [
                    new BundleFileAssociationConfiguration { Extensions = [".txt", "xyz"], Name = "Document" },
                    new BundleFileAssociationConfiguration { Extensions = ["png"], Description = "Image" }
                ]));
            var plist = InfoPlist.ReadDictionary(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
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
    static async Task AppliesDocumentTypeOverridesAndExportedUtis()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                ]));
            var plist = InfoPlist.ReadDictionary(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
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
    static async Task RejectsExtensionClaimedThroughSharedAssociation()
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
                    [new BundleFileAssociationConfiguration { Extensions = ["foo", "bar"] }])),
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
                    [new BundleFileAssociationConfiguration { Extensions = ["foo", "bar"] }])),
                "先显式声明、后被共享吸收的扩展名同样必须拒绝。");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    static async Task RejectsConflictingOrEmptyDocumentTypes()
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
                }).BuildAsync(MacConfiguration(input)),
                "Two document types claiming the same extension must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes = [new MacAppDocumentTypeConfiguration()]
                }).BuildAsync(MacConfiguration(input)),
                "An empty document type entry must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    DocumentTypes = [new MacAppDocumentTypeConfiguration { Extensions = ["bad!ext"] }]
                }).BuildAsync(MacConfiguration(input)),
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
                }).BuildAsync(MacConfiguration(input)),
                "Invalid exported UTI identifiers must be rejected.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    static async Task EmitsUrlTypesWithDefaultsAndOverrides()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                ]));
            var plist = InfoPlist.ReadDictionary(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
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
                }).BuildAsync(MacConfiguration(input)),
                "URL types without a scheme must be rejected.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task EmitsAtsExceptionDomains()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var plain = await new MacAppBundler().BuildAsync(MacConfiguration(input, output));
            Assert.False(InfoPlist.ReadDictionary(Path.Combine(plain[0].Path, "Contents", "Info.plist")) .ContainsKey("NSAppTransportSecurity"), "ATS must stay strict unless an exception domain is configured.");
            var relaxed = await new MacAppBundler(new MacAppBundleConfiguration
            {
                ExceptionDomain = "dev.example.com"
            }).BuildAsync(MacConfiguration(input, output));
            var ats = (Dictionary<string, object>)InfoPlist
                .ReadDictionary(Path.Combine(relaxed[0].Path, "Contents", "Info.plist"))["NSAppTransportSecurity"];
            var domains = (Dictionary<string, object>)ats["NSExceptionDomains"];
            var domain = (Dictionary<string, object>)domains["dev.example.com"];
            Assert.True((bool)domain["NSExceptionAllowsInsecureHTTPLoads"] &&
                   (bool)domain["NSIncludesSubdomains"],
                "The exception domain must allow insecure HTTP including subdomains.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration { ExceptionDomain = "  " })
                    .BuildAsync(MacConfiguration(input)),
                "A blank exception domain must be rejected.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task MergesCallerPlistAndEnforcesIdentityKeys()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                .BuildAsync(MacConfiguration(input, output));
            var plist = InfoPlist.ReadDictionary(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert.True(plist["NSSupportsSuddenTermination"] is true, "Caller boolean keys must merge.");
            Assert.Equal("Caller Name", (string)plist["CFBundleDisplayName"]);
            var flags = (Dictionary<string, object>)plist["MyAppFlags"];
            Assert.True(flags["Experimental"] is true && (long)flags["Level"] == 3,
                "Nested caller dicts must merge typed.");
            Assert.Equal("com.example.app", (string)plist["CFBundleIdentifier"]);

            var file = Path.Combine(input, "custom.plist");
            File.WriteAllText(file, "<plist version=\"1.0\"><dict><key>MyFileKey</key><string>v</string></dict></plist>");
            var fromFile = await new MacAppBundler(new MacAppBundleConfiguration { InfoPlistFile = file })
                .BuildAsync(MacConfiguration(input, output));
            Assert.True(InfoPlist.ReadDictionary(Path.Combine(fromFile[0].Path, "Contents", "Info.plist"))
                    .ContainsKey("MyFileKey"),
                "InfoPlistFile must merge like inline XML.");

            await AssertThrows<InvalidOperationException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistXml = "<dict><key>CFBundleIdentifier</key><string>com.evil.app</string></dict>"
                }).BuildAsync(MacConfiguration(input)),
                "Caller plists must not override identity keys.");
            await AssertThrows<InvalidOperationException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistXml = "<dict><key>CFBundlePackageType</key><string>BNDL</string></dict>"
                }).BuildAsync(MacConfiguration(input)),
                "CFBundlePackageType conflicts must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistFile = file, InfoPlistXml = "<dict/>"
                }).BuildAsync(MacConfiguration(input)),
                "InfoPlistFile and InfoPlistXml are mutually exclusive.");
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistXml = "<dict><key>oops</key>"
                }).BuildAsync(MacConfiguration(input)),
                "Invalid caller plist XML must fail the build.");
            await AssertThrows<FileNotFoundException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    InfoPlistFile = Path.Combine(input, "missing.plist")
                }).BuildAsync(MacConfiguration(input)),
                "A missing caller plist file must fail early.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task ValidatesMachOArchitecturesPerRid()
    {
        var arm64Input = CreateInputDirectory();
        var wrongArchInput = CreateInputDirectory();
        File.WriteAllBytes(Path.Combine(wrongArchInput, "ExampleApp"), FakeMachO(0x01000007));
        var fatInput = CreateInputDirectory();
        File.WriteAllBytes(Path.Combine(fatInput, "ExampleApp"), FakeFatMachO(0x01000007, 0x0100000C));
        File.WriteAllBytes(Path.Combine(fatInput, "helper"), FakeFatMachO(0x01000007, 0x0100000C));
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(
                    wrongArchInput, Path.Combine(output, "wrong"))),
                "An x86_64 executable must not pass in an osx-arm64 target.");
            var fat = await new MacAppBundler().BuildAsync(MacConfiguration(
                fatInput, Path.Combine(output, "fat"), rid: "osx-x64"));
            Assert.True(File.Exists(Path.Combine(fat[0].Path, "Contents", "MacOS", "ExampleApp")),
                "Fat binaries satisfy both osx targets.");
            var thin = await new MacAppBundler().BuildAsync(MacConfiguration(
                arm64Input, Path.Combine(output, "thin"), rid: "osx-arm64"));
            Assert.Single(thin);
            var universal = await new MacAppBundler().BuildAsync(MacConfiguration(
                fatInput, Path.Combine(output, "universal"), rid: "osx"));
            Assert.Single(universal);
            await AssertThrows<InvalidDataException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(
                    arm64Input, Path.Combine(output, "osx-thin"), rid: "osx")),
                "The osx target must reject a payload missing the x86_64 slice.");
            var mismatchedHelper = CreateInputDirectory();
            File.WriteAllBytes(Path.Combine(mismatchedHelper, "helper"), FakeMachO(0x01000007));
            try
            {
                await AssertThrows<InvalidDataException>(
                    () => new MacAppBundler().BuildAsync(MacConfiguration(
                        mismatchedHelper, Path.Combine(output, "helper"))),
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
    static Task MergesUniversalPayloadDirectories()
    {
        var x64 = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var arm64 = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(x64);
            Directory.CreateDirectory(arm64);
            // Mach-O files merge per architecture slice.
            File.WriteAllBytes(Path.Combine(x64, "ExampleApp"), FakeMachO(0x01000007));
            File.WriteAllBytes(Path.Combine(arm64, "ExampleApp"), FakeMachO(0x0100000C));
            // Identical non-Mach-O files collapse to one copy.
            var shared = Encoding.UTF8.GetBytes("{\"Runtime\":true}");
            File.WriteAllBytes(Path.Combine(x64, "ExampleApp.dll"), shared);
            File.WriteAllBytes(Path.Combine(arm64, "ExampleApp.dll"), shared);
            File.WriteAllBytes(Path.Combine(x64, "runtimeconfig.json"), shared);
            File.WriteAllBytes(Path.Combine(arm64, "runtimeconfig.json"), shared);
            // One-sided files pass through.
            File.WriteAllBytes(Path.Combine(arm64, "arm64-only.txt"), [9, 9]);

            MacUniversalPayloadMerger.Merge([x64, arm64], output);

            var merged = MachO.ReadSliceInfos(Path.Combine(output, "ExampleApp"));
            var cpus = merged.Select(s => s.CpuType).OrderBy(c => c).ToArray();
            Assert.True(cpus.Length == 2 && cpus[0] == 0x01000007 && cpus[1] == 0x0100000C,
                "Merged Mach-O must contain both x86_64 and arm64 slices.");
            foreach (var slice in merged)
            {
                Assert.True(slice.Offset > 0 && slice.Size == 64,
                    "Fat slice records must point at real slice content.");
            }
            Assert.Equal(File.ReadAllBytes(Path.Combine(output, "ExampleApp.dll")), shared);
            Assert.True(File.Exists(Path.Combine(output, "arm64-only.txt")),
                "Files present on one side only must pass through.");

            var conflictArm = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(conflictArm);
            File.WriteAllBytes(Path.Combine(conflictArm, "ExampleApp"), FakeMachO(0x0100000C));
            File.WriteAllBytes(Path.Combine(conflictArm, "differing.json"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(x64, "differing.json"), [4, 5, 6]);
            AssertThrows<InvalidDataException>(
                () => Task.Run(() => MacUniversalPayloadMerger.Merge([x64, conflictArm],
                    Path.Combine(output, "conflict"))),
                "Differing non-Mach-O files must fail the universal merge.").GetAwaiter().GetResult();
            Cleanup(conflictArm);

            // A thin Mach-O present on only one side can never become universal — reject it.
            var thinOnly = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
            var thinOut = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(thinOnly);
            File.WriteAllBytes(Path.Combine(thinOnly, "helper"), FakeMachO(0x0100000C));
            AssertThrows<InvalidDataException>(
                () => Task.Run(() => MacUniversalPayloadMerger.Merge([x64, thinOnly], thinOut)),
                "A one-sided thin Mach-O must fail the universal merge.").GetAwaiter().GetResult();
            Cleanup(thinOnly, thinOut);
        }
        finally
        {
            Cleanup(x64, arm64, output);
        }
        return Task.CompletedTask;
    }

    [Fact]
    static async Task PassesCarAndDegradesIconInputs()
    {
        var input = CreateInputDirectory();
        var assets = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var logger = new ListLogger();
        try
        {
            Directory.CreateDirectory(assets);
            var car = Path.Combine(assets, "App.car");
            File.WriteAllBytes(car, [1, 2, 3, 4]);
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration(),
                    new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(MacConfiguration(input, output, icons: [car]));
            Assert.True(File.Exists(Path.Combine(artifacts[0].Path, "Contents", "Resources", "Assets.car")),
                "A .car input must be copied to Resources/Assets.car.");
            var plist = InfoPlist.ReadDictionary(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert.False(plist.ContainsKey("CFBundleIconFile"), "A .car-only icon set writes no CFBundleIconFile.");
            var iconDir = Path.Combine(assets, "Icon.icon");
            Directory.CreateDirectory(iconDir);
            File.WriteAllText(Path.Combine(iconDir, "icon.json"), "{}");
            var degraded = await new MacAppBundler(new MacAppBundleConfiguration(),
                    new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(MacConfiguration(input, Path.Combine(output, "icon"), icons: [iconDir]));
            var degradedPlist = InfoPlist.ReadDictionary(
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
                    input, Path.Combine(output, "missing"), icons: [notADir])),
                "A non-directory .icon input must be rejected.");
        }
        finally
        {
            Cleanup(input, assets, output);
        }
    }

    [Fact]
    static async Task RejectsDuplicatedAssetIcons()
    {
        var input = CreateInputDirectory();
        var assets = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(assets);
            var carA = Path.Combine(assets, "a.car");
            var carB = Path.Combine(assets, "b.car");
            File.WriteAllBytes(carA, [1]);
            File.WriteAllBytes(carB, [2]);
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, icons: [carA, carB])),
                "Multiple .car inputs must be rejected.");
        }
        finally
        {
            Cleanup(input, assets);
        }
    }

    [Fact]
    static async Task RejectsLaterStageFeatures()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<NotSupportedException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, signingFiles: ["ExampleApp"])),
                "Signing files must be rejected until MAC-APP-3.");
            await AssertThrows<NotSupportedException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(input, licenseFile: "license.txt")),
                "A license file must be rejected for .app packages.");
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

    [Fact]
    static async Task RejectsInvalidSigningOptions()
    {
        var input = CreateInputDirectory();
        var temp = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                }).BuildAsync(MacConfiguration(input)),
                "Identity and a temporary certificate are mutually exclusive.");
            await AssertThrows<FileNotFoundException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration
                    {
                        TemporaryCertificateFile = Path.Combine(temp, "missing.p12")
                    }
                }).BuildAsync(MacConfiguration(input)),
                "A missing temporary certificate must be rejected before any tool runs.");
            await AssertThrows<FileNotFoundException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration
                    {
                        Identity = "-", EntitlementsFile = Path.Combine(temp, "missing.plist")
                    }
                }).BuildAsync(MacConfiguration(input)),
                "A missing entitlements file must be rejected before any tool runs.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-", Notarize = true }
                }).BuildAsync(MacConfiguration(input)),
                "Ad-hoc signatures cannot be notarized.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { SkipStapling = true }
                }).BuildAsync(MacConfiguration(input)),
                "SkipStapling without Notarize must be rejected.");
            await AssertThrows<ArgumentException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-", NotaryWait = false }
                }).BuildAsync(MacConfiguration(input)),
                "NotaryWait=false without Notarize must be rejected.");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await AssertThrows<NotSupportedException>(
                    () => new MacAppBundler(new MacAppBundleConfiguration
                    {
                        Signing = new MacAppSigningConfiguration { Identity = "-" }
                    }).BuildAsync(MacConfiguration(input)),
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
    static void SigningHostGateIsPlatformNotSupported()
    {
        // 签名宿主门禁标记类型：入口统一预检靠它区分“逐格式容错”与“配置错误”。
        Assert.SkipWhen(RuntimeInformation.IsOSPlatform(OSPlatform.OSX), "仅非 macOS 宿主。");
        var input = CreateInputDirectory();
        try
        {
            Assert.Throws<PlatformNotSupportedException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-" }
                }).Validate(MacConfiguration(input)));
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void SigningHostGateDoesNotMaskConfigErrors()
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
    static async Task AssemblesCodesignArguments()
    {
        var entitlements = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests",
            Guid.NewGuid().ToString("N"), "e.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(entitlements)!);
        File.WriteAllText(entitlements, "<plist/>");
        try
        {
            var signing = new MacAppSigningConfiguration
            {
                Identity = "Developer ID Application: X",
                HardenedRuntime = true,
                EntitlementsFile = entitlements
            };
            var inner = MacAppSigning.CodesignArguments(signing.Identity, signing, false, "/tmp/app/Contents/Frameworks/lib.dylib");
            var innerText = string.Join(' ', inner);
            Assert.True(innerText.Contains("--force") && innerText.Contains("--sign") &&
                   innerText.Contains("--options runtime") && innerText.Contains("--timestamp"),
                "Nested signing must force-sign with the hardened runtime and a secure timestamp.");
            Assert.DoesNotContain("--entitlements", innerText);
            var bundle = MacAppSigning.CodesignArguments(signing.Identity, signing, true, "/tmp/app");
            var bundleText = string.Join(' ', bundle);
            Assert.True(bundleText.Contains("--entitlements") &&
                   bundleText.Contains(Path.GetFullPath(entitlements)),
                "The bundle signature must carry the configured entitlements.");
            var adhoc = MacAppSigning.CodesignArguments("-", new MacAppSigningConfiguration(), false, "/tmp/app");
            Assert.Contains("--timestamp=none", string.Join(' ', adhoc));
        }
        finally
        {
            Cleanup(Path.GetDirectoryName(entitlements)!);
        }
        await Task.CompletedTask;
    }

    [Fact]
    static async Task ResolvesNotaryCredentials()
    {
        var profile = MacAppSigning.ResolveCredentials(
            new MacAppSigningConfiguration { KeychainProfile = "my-profile" });
        Assert.Equal("--keychain-profile my-profile", string.Join(' ', profile));
        var apiKey = MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration
        {
            ApiKeyFile = "/keys/AuthKey_ABC.p8", ApiKeyId = "ABC", ApiIssuer = "ISSUER"
        });
        Assert.Equal("--key /keys/AuthKey_ABC.p8 --key-id ABC --issuer ISSUER", string.Join(' ', apiKey));
        var appleId = MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration
        {
            AppleId = "dev@example.com", ApplePassword = "app-pw", AppleTeamId = "TEAM"
        });
        Assert.Equal("--apple-id dev@example.com --password app-pw --team-id TEAM", string.Join(' ', appleId));
        Assert.ThrowsAny<ArgumentException>(
            () => MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration { ApiKeyId = "ABC" }));
        Assert.ThrowsAny<ArgumentException>(
            () => MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration { AppleId = "dev@example.com" }));
        Assert.ThrowsAny<ArgumentException>(
            () => MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration()));
        await Task.CompletedTask;
    }

    [Fact]
    static async Task SignsInsideOutOnStubbedTools()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var requests = new List<MacProcessRunner.Request>();
        var previous = MacProcessRunner.Handler;
        MacProcessRunner.Handler = (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new MacProcessRunner.Result(0, "", ""));
        };
        try
        {
            var entitlements = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests",
                Guid.NewGuid().ToString("N"), "e.plist");
            Directory.CreateDirectory(Path.GetDirectoryName(entitlements)!);
            File.WriteAllText(entitlements, "<plist/>");
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                Signing = new MacAppSigningConfiguration
                {
                    Identity = "-", EntitlementsFile = entitlements, HardenedRuntime = true
                }
            }).BuildAsync(MacConfiguration(input, output));
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
            MacProcessRunner.Handler = previous;
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task CleansUpKeychainOnFailure()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var temp = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var certificate = Path.Combine(temp, "cert.p12");
        File.WriteAllBytes(certificate, [1, 2, 3]);
        var calls = new List<string>();
        var deleted = false;
        var previous = MacProcessRunner.Handler;
        MacProcessRunner.Handler = (request, _) =>
        {
            var joined = string.Join(' ', request.Arguments);
            calls.Add(request.Executable + " " + joined);
            if (request.Executable == "security" && joined.StartsWith("list-keychains") &&
                !joined.Contains("-s", StringComparison.Ordinal))
            {
                return Task.FromResult(new MacProcessRunner.Result(0, "\"/Users/x/Library/Keychains/login.keychain-db\"\n", ""));
            }
            if (request.Executable == "security" && joined.StartsWith("find-identity"))
            {
                return Task.FromResult(new MacProcessRunner.Result(0,
                    "  1) AA11BB22CC33DD44EE55FF6600112233AABBCCDD \"Bundler Test\"\n     1 valid identities found\n", ""));
            }
            if (request.Executable == "security" && joined.StartsWith("delete-keychain"))
            {
                deleted = true;
            }
            if (request.Executable == "codesign" && !joined.Contains("--verify"))
            {
                return Task.FromResult(new MacProcessRunner.Result(1, "", "boom"));
            }
            return Task.FromResult(new MacProcessRunner.Result(0, "", ""));
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
                }).BuildAsync(MacConfiguration(input, output)),
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
            MacProcessRunner.Handler = previous;
            Cleanup(input, output, temp);
        }
    }

    [Fact]
    static async Task SigningFailureLeavesNoArtifact()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var previous = MacProcessRunner.Handler;
        MacProcessRunner.Handler = (request, _) =>
            Task.FromResult(request.Executable == "codesign" && request.Arguments.Contains("--verify")
                ? new MacProcessRunner.Result(1, "", "not signed at all")
                : new MacProcessRunner.Result(0, "", ""));
        try
        {
            await AssertThrows<Exception>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-" }
                }).BuildAsync(MacConfiguration(input, output)),
                "A failed --verify must fail the build.");
            Assert.False(Directory.Exists(Path.Combine(output, "ExampleApp.app")), "A failed signing run must not leave a pseudo-success .app at the output path.");
        }
        finally
        {
            MacProcessRunner.Handler = previous;
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task RebuildsIdenticalAppBundles()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = MacConfiguration(input, output);
            var bundler = new MacAppBundler();
            var first = await bundler.BuildAsync(configuration);
            var firstPlist = File.ReadAllBytes(Path.Combine(first[0].Path, "Contents", "Info.plist"));
            var second = await bundler.BuildAsync(configuration);
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
    static void MapsAppSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
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
        Assert.Contains("StartsWith('osx-')", targets);
        Assert.True(task.Contains("new MacAppBundler(", StringComparison.Ordinal) &&
               task.Contains("MacAppFileEntry", StringComparison.Ordinal),
            "The MSBuild task does not construct the .app backend.");
    }

    [Fact]
    static async Task MarksMachOFilesExecutable()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "requires a non-Windows host");
        // CA1416 分析器不认 SkipWhen——守卫保留给编译期平台判断。
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(input, output));
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
    static async Task WarnsOnWindowsHosts()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var logger = new ListLogger();
        try
        {
            await new MacAppBundler(options: new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(MacConfiguration(input, output));
            Assert.Contains(logger.Messages, message => message.Contains("executable bit", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static async Task PassesPlutilLint()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                urlProtocols: [new BundleUrlProtocolConfiguration { Schemes = ["myapp"] }]));
            var plist = Path.Combine(artifacts[0].Path, "Contents", "Info.plist");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "plutil",
                Arguments = $"-lint \"{plist}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void ReadsMachOArchitectures()
    {
        var dir = CreateInputDirectory();
        try
        {
            var thin = Path.Combine(dir, "thin");
            File.WriteAllBytes(thin, FakeMachO(0x0100000C));
            Assert.Equal(new[] { "arm64" }, MachO.ReadArchitectures(thin));
            File.WriteAllBytes(thin, FakeMachO(0x01000007));
            Assert.Equal(new[] { "x86_64" }, MachO.ReadArchitectures(thin));
            Assert.True(MachO.IsMachO(thin));

            var fat = Path.Combine(dir, "fat");
            File.WriteAllBytes(fat, FakeFatMachO(0x0100000C, 0x01000007));
            Assert.Equal(new[] { "arm64", "x86_64" }, MachO.ReadArchitectures(fat));

            var alien = Path.Combine(dir, "elf");
            File.WriteAllBytes(alien, [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00]);
            Assert.Empty(MachO.ReadArchitectures(alien));
            Assert.False(MachO.IsMachO(alien));

            var truncated = Path.Combine(dir, "short");
            File.WriteAllBytes(truncated, [0xCF, 0xFA]);
            Assert.Empty(MachO.ReadArchitectures(truncated));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    static void RoundTripsInfoPlistValues()
    {
        var dir = CreateInputDirectory();
        var path = Path.Combine(dir, "Info.plist");
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var values = new Dictionary<string, object>
        {
            ["Name"] = "Example App",
            ["Enabled"] = true,
            ["Disabled"] = false,
            ["Count"] = 42L,
            ["Small"] = 7,
            ["Ratio"] = 1.5,
            ["Blob"] = new byte[] { 1, 2, 3 },
            ["Stamp"] = when,
            ["Tags"] = new List<object> { "a", "b" },
            ["Nested"] = new Dictionary<string, object> { ["K"] = "v" }
        };
        try
        {
            InfoPlist.Write(path, values);
            var parsed = InfoPlist.ReadDictionary(path);
            Assert.Equal("Example App", parsed["Name"]);
            Assert.Equal(true, parsed["Enabled"]);
            Assert.Equal(false, parsed["Disabled"]);
            Assert.Equal(42L, parsed["Count"]);
            Assert.Equal(7L, parsed["Small"]);
            Assert.Equal(1.5, parsed["Ratio"]);
            Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])parsed["Blob"]);
            Assert.Equal(when, parsed["Stamp"]);
            Assert.Equal(new List<object> { "a", "b" }, (List<object>)parsed["Tags"]);
            Assert.Equal("v", ((Dictionary<string, object>)parsed["Nested"])["K"]);

            var strings = InfoPlist.ReadStringValues(path);
            Assert.Equal("true", strings["Enabled"]);
            Assert.Equal("42", strings["Count"]);
            Assert.Equal("1.5", strings["Ratio"]);
            Assert.False(strings.ContainsKey("Nested"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    static void RejectsInvalidPlistShapes()
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
    static void RendersPlistScalars()
    {
        Assert.True(InfoPlist.TryToString("x", out var text) && text == "x");
        Assert.True(InfoPlist.TryToString(true, out var flag) && flag == "true");
        Assert.True(InfoPlist.TryToString(42L, out var integer) && integer == "42");
        Assert.False(InfoPlist.TryToString(new List<object>(), out _));
    }

    [Fact]
    static void FindsIconImageNameInAssetutilJson()
    {
        Assert.Equal("AppIcon", MacAppAssetsCar.IconImageName(
            """[{"AssetType":"MultiSized Image","Name":"Other"},{"AssetType":"Icon Image","Name":"AppIcon"}]"""));
        Assert.Null(MacAppAssetsCar.IconImageName("""[{"AssetType":"MultiSized Image"}]"""));
        Assert.Null(MacAppAssetsCar.IconImageName("{}"));
    }

    // R2 MAC-APP-OI-08：osx 通用载荷按签名表探测拒收非 Mach-O 的按架构散件。
    [Fact]
    static async Task RejectsForeignCodeInUniversalPayload()
    {
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                        MacConfiguration(input, Path.Combine(output, name), rid: "osx")));
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
                MacConfiguration(clean, Path.Combine(output, "clean"), rid: "osx"));
            Assert.True(File.Exists(
                Path.Combine(artifacts[0].Path, "Contents", "MacOS", "ExampleApp.managed.dll")));
        }
        finally
        {
            Cleanup(clean, output);
        }
    }

    // R2: fat 头的 32 位 offset/size 字段写不下就显式拒写，不产出静默截断的坏件。
    [Fact]
    static void RejectsOversizedFatSlice()
    {
        var output = Path.Combine(
            Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"), "fat.bin");
        var slices = new List<FatSlice>
        {
            new(0x0100000C, 0, uint.MaxValue + 1L, stream => stream.WriteByte(0))
        };
        Assert.Throws<InvalidDataException>(() => MachOFat.Create(output, slices));
        Assert.False(File.Exists(output), "被拒绝的超宽切片不得写出文件。");
    }

    // R2: 合并输入目录里的目录符号链接环显式报错，不再枚举失控。
    [Fact]
    static void RejectsSymlinkLoopInMergeInput()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires symlink support");
        var arm64 = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
    [Fact]
    static async Task SharedFileAssociationMergesOnce()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                [new BundleFileAssociationConfiguration { Extensions = ["foo", "bar"], Name = "Shared" }]));
            var plist = InfoPlist.ReadDictionary(
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
                    [new BundleFileAssociationConfiguration { Extensions = ["foo", "bar"] }])),
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
    static async Task SharedUrlProtocolMergesOnce()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                [new BundleUrlProtocolConfiguration { Schemes = ["app-a", "app-b"], Name = "Shared" }]));
            var plist = InfoPlist.ReadDictionary(
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
                    [new BundleUrlProtocolConfiguration { Schemes = ["app-a", "app-b"] }])),
                "已被共享项吸收的 scheme 再被 urlType 显式声明必须拒绝。");
        }
        finally
        {
            Cleanup(input, output, output + ".dup");
        }
    }

    // R2: APPLE_API_KEY_PATH_UNSET 兜底已移除——只设它不得当作凭证来源。
    [Fact]
    static void IgnoresUnsetApiKeyPathEnv()
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
    static async Task SignsNestedBundlesInsideOut()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var calls = new List<string>();
        var previous = MacProcessRunner.Handler;
        MacProcessRunner.Handler = (request, _) =>
        {
            calls.Add(request.Executable + " " + string.Join(' ', request.Arguments));
            return Task.FromResult(new MacProcessRunner.Result(0, "", ""));
        };
        try
        {
            await new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration { Identity = "-" }
                }).BuildAsync(MacConfiguration(input, output));
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
                    BundleTarget.TryParse("osx-arm64", out var target) ? target! : throw new InvalidOperationException(),
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
            MacProcessRunner.Handler = previous;
            Cleanup(input, output);
        }
    }

    // R2: p12 口令不走 argv——openssl 可用时经 PEM 导入；缺席/失败如实报错，
    // 不退回 `security import -P`（口令进 argv 可被本机任意进程 ps 读取）。
    [Fact]
    static async Task TemporaryKeychainKeepsPasswordOffArgv()
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "requires a macOS host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var temp = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var certificate = Path.Combine(temp, "cert.p12");
        File.WriteAllBytes(certificate, [1, 2, 3]);
        var calls = new List<string>();
        var previous = MacProcessRunner.Handler;
        var opensslWorks = true;
        MacProcessRunner.Handler = (request, _) =>
        {
            var joined = string.Join(' ', request.Arguments);
            calls.Add(request.Executable + " " + joined);
            if (request.Executable == "openssl")
            {
                if (!opensslWorks)
                {
                    return Task.FromResult(new MacProcessRunner.Result(1, "", "no openssl"));
                }
                var outIndex = request.Arguments.ToList().IndexOf("-out") + 1;
                File.WriteAllText(request.Arguments[outIndex], "PEM");
                return Task.FromResult(new MacProcessRunner.Result(0, "", ""));
            }
            if (request.Executable == "security" && joined.StartsWith("list-keychains") &&
                !joined.Contains("-s", StringComparison.Ordinal))
            {
                return Task.FromResult(new MacProcessRunner.Result(0,
                    "\"/Users/x/Library/Keychains/login.keychain-db\"\n", ""));
            }
            if (request.Executable == "security" && joined.StartsWith("find-identity"))
            {
                return Task.FromResult(new MacProcessRunner.Result(0,
                    "  1) AA11BB22CC33DD44EE55FF6600112233AABBCCDD \"Bundler Test\"\n     1 valid identities found\n", ""));
            }
            return Task.FromResult(new MacProcessRunner.Result(0, "", ""));
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
                }).BuildAsync(MacConfiguration(input, output));
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
                    }).BuildAsync(MacConfiguration(input, Path.Combine(output, "fallback"))),
                "openssl 失败必须报错而不是退回 -P 兜底。");
            Assert.False(calls.Any(c => c.Contains("s3cret-pw") || c.Contains(" -P ")),
                "任何进程参数都不得出现口令或 -P 兜底。");
        }
        finally
        {
            MacProcessRunner.Handler = previous;
            Cleanup(input, output, temp);
        }
    }

    static BundleConfiguration MacConfiguration(
        string input,
        string output = "",
        string rid = "osx-arm64",
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
                    RuntimeIdentifier = rid,
                    InputDirectory = input,
                    MainExecutable = mainExecutable,
                    SigningFiles = signingFiles ?? [],
                    Formats = formats ?? [PackageFormat.App]
                }
            ]
        };

    static string CreateInputDirectory()
    {
        var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "ExampleApp"), FakeMachO());
        File.WriteAllText(Path.Combine(input, "ExampleApp.dll"), "payload");
        Directory.CreateDirectory(Path.Combine(input, "nested"));
        return input;
    }

    // A unix socket (or any other non-regular file) inside the input must be
    // skipped rather than copied into the bundle.
    [Fact]
    static async Task SkipsNonRegularPayloadFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires a non-Windows host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(input, output));
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

    static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);



    sealed class ListLogger : IBundleLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(BundleLogLevel level, string message) => Messages.Add(message);
    }
}
