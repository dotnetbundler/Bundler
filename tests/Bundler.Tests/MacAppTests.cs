using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacApp;
using System.Runtime.InteropServices;
using System.Text;

internal static class MacAppTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Plans an intermediate app before a PKG package", () => RunSync(PlansAppBeforePkg));
            yield return ("Rejects the PKG format outside macOS targets", () => RunSync(RejectsPkgOutsideMac));
            yield return ("Builds a minimal .app bundle", BuildsMinimalAppBundle);
            yield return ("Writes and reads back core Info.plist keys", WritesAndReadsCoreInfoPlist);
            yield return ("Applies explicit .app metadata overrides", AppliesAppMetadataOverrides);
            yield return ("Rejects non-Apple version strings", RejectsNonAppleVersions);
            yield return ("Rejects invalid category and minimum-system values", RejectsInvalidCategoryAndMinVersion);
            yield return ("Rejects a nested main executable path", RejectsNestedMainExecutable);
            yield return ("Rejects a non-Mach-O main executable", RejectsNonMachOExecutable);
            yield return ("Maps payload into the Contents layout", MapsPayloadIntoContentsLayout);
            yield return ("Rejects unsafe Contents mappings", RejectsUnsafeContentsMappings);
            yield return ("Rejects colliding .app payload destinations", RejectsCollidingPayloadDestinations);
            yield return ("Rejects reparse points inside the .app payload", RejectsReparsePointsInAppPayload);
            yield return ("Synthesizes .icns icons from PNG bitmaps", SynthesizesIcnsFromPngs);
            yield return ("Rejects invalid icon inputs", RejectsInvalidIconInputs);
            yield return ("Emits document types for shared file associations", EmitsDocumentTypesForSharedAssociations);
            yield return ("Applies macOS document type overrides and exports UTIs", AppliesDocumentTypeOverridesAndExportedUtis);
            yield return ("Rejects conflicting or empty document types", RejectsConflictingOrEmptyDocumentTypes);
            yield return ("Emits URL types with defaults and overrides", EmitsUrlTypesWithDefaultsAndOverrides);
            yield return ("Emits ATS exception domains when configured", EmitsAtsExceptionDomains);
            yield return ("Merges a caller Info.plist and enforces identity keys", MergesCallerPlistAndEnforcesIdentityKeys);
            yield return ("Validates Mach-O payload architectures per RID", ValidatesMachOArchitecturesPerRid);
            yield return ("Passes .car icons through and degrades .icon without actool", PassesCarAndDegradesIconInputs);
            yield return ("Rejects duplicated asset-catalog icon inputs", RejectsDuplicatedAssetIcons);
            yield return ("Rejects features owned by later MAC stages", RejectsLaterStageFeatures);
            yield return ("Rejects conflicting or incomplete signing options", RejectsInvalidSigningOptions);
            yield return ("Assembles codesign arguments per target kind", AssemblesCodesignArguments);
            yield return ("Resolves notary credentials and rejects partial sets", ResolvesNotaryCredentials);
            yield return ("Signs inside-out then verifies on a stubbed toolchain", SignsInsideOutOnStubbedTools);
            yield return ("Cleans up the temporary keychain when signing fails", CleansUpKeychainOnFailure);
            yield return ("Leaves no output artifact when signing fails", SigningFailureLeavesNoArtifact);
            yield return ("Rebuilds identical .app bundles", RebuildsIdenticalAppBundles);
            yield return ("Maps .app settings through MSBuild", () => RunSync(MapsAppSettingsThroughMsBuild));
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return ("Marks Mach-O payload files executable", MarksMachOFilesExecutable);
            }
            else
            {
                yield return ("Warns when the host cannot set executable bits", WarnsOnWindowsHosts);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                yield return ("Passes plutil lint on generated Info.plist", PassesPlutilLint);
            }
        }
    }

    static void PlansAppBeforePkg()
    {
        var configuration = MacConfiguration(CreateInputDirectory(), formats: [PackageFormat.Pkg]);
        var plan = BundlePlanner.Create(configuration, checkFileSystem: false);
        Assert(plan.Items.Count == 2, "A PKG plan should contain an app and a PKG step.");
        Assert(plan.Items[0].Format == PackageFormat.App && plan.Items[0].Intermediate,
            "The intermediate app step must come first.");
        Assert(plan.Items[1].Format == PackageFormat.Pkg && !plan.Items[1].Intermediate,
            "The requested PKG step must come last.");
    }

    static void RejectsPkgOutsideMac()
    {
        var configuration = MacConfiguration(CreateInputDirectory(), rid: "win-x64", formats: [PackageFormat.Pkg]);
        var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
        Assert(issues.Any(issue => issue.Path.EndsWith("formats", StringComparison.Ordinal)),
            "PKG must be rejected for a Windows target.");
    }

    static async Task BuildsMinimalAppBundle()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = await new MacAppBundler()
                .BuildAsync(MacConfiguration(input, output));
            Assert(artifacts.Count == 1, "A single .app artifact is expected.");
            var appPath = artifacts[0].Path;
            Assert(artifacts[0].Format == PackageFormat.App, "The artifact format must be App.");
            Assert(Path.GetFileName(appPath) == "ExampleApp.app", "The bundle must be named '<ProductName>.app'.");
            Assert(appPath.EndsWith(Path.Combine("osx-arm64", "app", "ExampleApp.app"), StringComparison.Ordinal),
                "The artifact must land in artifacts/<rid>/app/.");
            foreach (var required in new[]
                     {
                         "Contents/Info.plist", "Contents/PkgInfo",
                         "Contents/MacOS/ExampleApp", "Contents/MacOS/ExampleApp.dll",
                         "Contents/Resources"
                     })
            {
                var path = Path.Combine(appPath, required.Replace('/', Path.DirectorySeparatorChar));
                Assert(File.Exists(path) || Directory.Exists(path), $"Missing bundle entry: {required}");
            }
            Assert(File.ReadAllText(Path.Combine(appPath, "Contents", "PkgInfo")) == "APPL????",
                "PkgInfo must contain 'APPL????'.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static async Task WritesAndReadsCoreInfoPlist()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(input, output));
            var values = InfoPlist.ReadStringValues(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert(values["CFBundleIdentifier"] == "com.example.app", "CFBundleIdentifier mismatch.");
            Assert(values["CFBundleName"] == "ExampleApp" && values["CFBundleDisplayName"] == "ExampleApp",
                "Name keys must default to the product name.");
            Assert(values["CFBundleExecutable"] == "ExampleApp", "CFBundleExecutable mismatch.");
            Assert(values["CFBundlePackageType"] == "APPL", "CFBundlePackageType must be APPL.");
            Assert(values["CFBundleShortVersionString"] == "1.0.0" && values["CFBundleVersion"] == "1.0.0",
                "Both version keys must default to the package version.");
            Assert(values["CFBundleSignature"] == "????" && values["CFBundleInfoDictionaryVersion"] == "6.0" &&
                   values["NSHighResolutionCapable"] == "true",
                "InfoDictionaryVersion/signature/high-resolution keys mismatch.");
            Assert(!values.ContainsKey("LSMinimumSystemVersion"),
                "LSMinimumSystemVersion must not be written unless configured.");
            Assert(!values.ContainsKey("CFBundleIconFile"), "No icon key without icon input.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static async Task AppliesAppMetadataOverrides()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = await new MacAppBundler(new MacAppBundleConfiguration
            {
                BundleName = "ShortName",
                BundleDisplayName = "Long Display Name",
                ShortVersion = "2.0",
                BuildVersion = "2026.9.1",
                MinimumSystemVersion = "11.0",
                Category = "public.app-category.utilities"
            }).BuildAsync(MacConfiguration(input, output));
            var values = InfoPlist.ReadStringValues(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert(values["CFBundleName"] == "ShortName" &&
                   values["CFBundleDisplayName"] == "Long Display Name", "Name overrides must be honored.");
            Assert(values["CFBundleShortVersionString"] == "2.0" && values["CFBundleVersion"] == "2026.9.1",
                "Version overrides must be honored.");
            Assert(values["LSMinimumSystemVersion"] == "11.0", "Configured minimum system must be written.");
            Assert(values["LSApplicationCategoryType"] == "public.app-category.utilities", "Category mismatch.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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
                Contents =
                [
                    new MacAppContentConfiguration { Source = content, TargetPath = "PlugIns/plugin.bundle" }
                ],
                Frameworks = [framework, dylib]
            }).BuildAsync(MacConfiguration(
                input, output,
                resources: [new BundleResourceConfiguration { Source = resource, TargetPath = "docs/readme.txt" }]));

            var root = Path.Combine(artifacts[0].Path, "Contents");
            Assert(File.Exists(Path.Combine(root, "MacOS", "nested", "helper.txt")),
                "The input tree must be preserved inside Contents/MacOS/.");
            Assert(File.Exists(Path.Combine(root, "Resources", "docs", "readme.txt")),
                "Resources must keep their relative target path inside Contents/Resources/.");
            Assert(File.Exists(Path.Combine(root, "PlugIns", "plugin.bundle", "inner.txt")),
                "Explicit Contents mappings must land under Contents/.");
            Assert(File.Exists(Path.Combine(root, "Frameworks", "Fixture.framework", "Fixture")) &&
                   File.Exists(Path.Combine(root, "Frameworks", "libfixture.dylib")),
                "Framework payloads must land in Contents/Frameworks/.");
        }
        finally
        {
            Cleanup(input, output, extras);
        }
    }

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
                        Contents = [new MacAppContentConfiguration { Source = file, TargetPath = target }]
                    }).BuildAsync(MacConfiguration(input)),
                    $"Contents target '{target}' must be rejected.");
            }
        }
        finally
        {
            Cleanup(input, input + ".artifacts");
        }
    }

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
                        new BundleResourceConfiguration { Source = file, TargetPath = "shared.txt" },
                        new BundleResourceConfiguration { Source = file, TargetPath = "shared.txt" }
                    ])),
                "Two payload entries mapping to the same bundle path must be rejected.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static async Task RejectsReparsePointsInAppPayload()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
                return; // The host cannot create symlinks; nothing to verify here.
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
            Assert(File.Exists(iconPath), "The synthesized .icns icon is missing.");
            var icns = File.ReadAllBytes(iconPath);
            Assert(Encoding.ASCII.GetString(icns, 0, 4) == "icns", "The icon is not an icns container.");
            var firstChunk = Encoding.ASCII.GetString(icns, 8, 4);
            Assert(firstChunk == "ic07" || firstChunk == "ic08", "Unexpected first icns chunk type.");
            var values = InfoPlist.ReadStringValues(
                Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert(values["CFBundleIconFile"] == "AppIcon.icns", "CFBundleIconFile mismatch.");
        }
        finally
        {
            Cleanup(input, output, assets);
        }
    }

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
            Assert(docTypes.Count == 2, "One CFBundleDocumentTypes entry per association is expected.");
            var first = (Dictionary<string, object>)docTypes[0];
            Assert(((List<object>)first["CFBundleTypeExtensions"]).SequenceEqual(["txt", "xyz"]),
                "Extensions keep their configured order with the leading dot stripped.");
            Assert((string)first["CFBundleTypeName"] == "Document", "Configured CFBundleTypeName must win.");
            Assert((string)first["CFBundleTypeRole"] == "Editor" && (string)first["LSHandlerRank"] == "Default",
                "Shared associations default to Editor/Default.");
            var contentTypes = (List<object>)first["LSItemContentTypes"];
            Assert(contentTypes.Contains("public.plain-text"), ".txt must infer public.plain-text.");
            Assert(!contentTypes.Any(value => (string)value == "xyz"),
                "Unknown extensions must not appear as content types.");
            var second = (Dictionary<string, object>)docTypes[1];
            Assert(((List<object>)second["LSItemContentTypes"]).Contains("public.png"),
                ".png must infer public.png.");
            Assert(!plist.ContainsKey("UTExportedTypeDeclarations"),
                "Shared associations without an exported type emit no UTExportedTypeDeclarations.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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
            Assert(docTypes.Count == 2,
                "The mac override must absorb the shared association instead of duplicating it.");
            var merged = (Dictionary<string, object>)docTypes[0];
            Assert((string)merged["CFBundleTypeName"] == "ABC File",
                "The shared association name fills the mac entry when unset.");
            Assert((string)merged["CFBundleTypeRole"] == "Viewer" &&
                   (string)merged["LSHandlerRank"] == "Owner",
                "mac-specific role/rank must be honored.");
            var contentTypes = (List<object>)merged["LSItemContentTypes"];
            Assert(contentTypes.SequenceEqual(["com.example.abc"]),
                "An exported UTI replaces inferred content types.");
            var exported = (List<object>)plist["UTExportedTypeDeclarations"];
            var declaration = (Dictionary<string, object>)exported[0];
            Assert((string)declaration["UTTypeIdentifier"] == "com.example.abc",
                "UTTypeIdentifier mismatch.");
            Assert((string)declaration["UTTypeDescription"] == "ABC document",
                "The shared description feeds UTTypeDescription.");
            Assert(((List<object>)declaration["UTTypeConformsTo"]).SequenceEqual(["public.data"]),
                "UTTypeConformsTo mismatch.");
            var tags = (Dictionary<string, object>)declaration["UTTypeTagSpecification"];
            Assert(((List<object>)tags["public.filename-extension"]).SequenceEqual(["abc"]),
                "The exported UTI must tag the extension.");
            Assert((string)tags["public.mime-type"] == "application/x-abc",
                "The shared MIME type feeds public.mime-type.");
            var standalone = (Dictionary<string, object>)docTypes[1];
            Assert(!standalone.ContainsKey("CFBundleTypeExtensions"),
                "ContentTypes-only entries emit no extension list.");
            Assert((string)standalone["CFBundleTypeName"] == "Any Text",
                "Standalone entries keep their configured name.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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
            Assert(urlTypes.Count == 2, "The mac override merges into the shared URL protocol.");
            var merged = (Dictionary<string, object>)urlTypes[0];
            Assert(((List<object>)merged["CFBundleURLSchemes"]).SequenceEqual(["myapp"]),
                "Merged URL types keep the scheme list.");
            Assert((string)merged["CFBundleURLName"] == "MyApp Link",
                "The shared URL name must be preserved.");
            Assert((string)merged["CFBundleTypeRole"] == "Viewer", "Role override must apply.");
            var standalone = (Dictionary<string, object>)urlTypes[1];
            Assert((string)standalone["CFBundleURLName"] == "com.example.app helper",
                "CFBundleURLName defaults to '<bundle-id> <first-scheme>'.");
            Assert((string)standalone["CFBundleTypeRole"] == "Editor",
                "URL types default to the Editor role.");
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

    static async Task EmitsAtsExceptionDomains()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var plain = await new MacAppBundler().BuildAsync(MacConfiguration(input, output));
            Assert(!InfoPlist.ReadDictionary(Path.Combine(plain[0].Path, "Contents", "Info.plist"))
                    .ContainsKey("NSAppTransportSecurity"),
                "ATS must stay strict unless an exception domain is configured.");
            var relaxed = await new MacAppBundler(new MacAppBundleConfiguration
            {
                ExceptionDomain = "dev.example.com"
            }).BuildAsync(MacConfiguration(input, output));
            var ats = (Dictionary<string, object>)InfoPlist
                .ReadDictionary(Path.Combine(relaxed[0].Path, "Contents", "Info.plist"))["NSAppTransportSecurity"];
            var domains = (Dictionary<string, object>)ats["NSExceptionDomains"];
            var domain = (Dictionary<string, object>)domains["dev.example.com"];
            Assert((bool)domain["NSExceptionAllowsInsecureHTTPLoads"] &&
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
            Assert(plist["NSSupportsSuddenTermination"] is true, "Caller boolean keys must merge.");
            Assert((string)plist["CFBundleDisplayName"] == "Caller Name",
                "Caller plist wins over generated non-identity keys.");
            var flags = (Dictionary<string, object>)plist["MyAppFlags"];
            Assert(flags["Experimental"] is true && (long)flags["Level"] == 3,
                "Nested caller dicts must merge typed.");
            Assert((string)plist["CFBundleIdentifier"] == "com.example.app",
                "Identity keys survive the merge.");

            var file = Path.Combine(input, "custom.plist");
            File.WriteAllText(file, "<plist version=\"1.0\"><dict><key>MyFileKey</key><string>v</string></dict></plist>");
            var fromFile = await new MacAppBundler(new MacAppBundleConfiguration { InfoPlistFile = file })
                .BuildAsync(MacConfiguration(input, output));
            Assert(InfoPlist.ReadDictionary(Path.Combine(fromFile[0].Path, "Contents", "Info.plist"))
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
            Assert(File.Exists(Path.Combine(fat[0].Path, "Contents", "MacOS", "ExampleApp")),
                "Fat binaries satisfy both osx targets.");
            var thin = await new MacAppBundler().BuildAsync(MacConfiguration(
                arm64Input, Path.Combine(output, "thin"), rid: "osx-arm64"));
            Assert(thin.Count == 1, "A matching thin binary must build.");
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
            Assert(File.Exists(Path.Combine(artifacts[0].Path, "Contents", "Resources", "Assets.car")),
                "A .car input must be copied to Resources/Assets.car.");
            var plist = InfoPlist.ReadDictionary(Path.Combine(artifacts[0].Path, "Contents", "Info.plist"));
            Assert(!plist.ContainsKey("CFBundleIconFile"),
                "A .car-only icon set writes no CFBundleIconFile.");
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
            Assert(hasCar || logger.Messages.Any(m => m.Contains("Skipping Assets.car")),
                "A rejected .icon input must degrade with an Assets.car warning.");
            Assert(degradedPlist.ContainsKey("CFBundleIconName") == hasCar,
                "CFBundleIconName tracks a produced Assets.car.");
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
                        Identity = "Dev ID", TemporaryCertificatePath = certificate
                    }
                }).BuildAsync(MacConfiguration(input)),
                "Identity and a temporary certificate are mutually exclusive.");
            await AssertThrows<FileNotFoundException>(
                () => new MacAppBundler(new MacAppBundleConfiguration
                {
                    Signing = new MacAppSigningConfiguration
                    {
                        TemporaryCertificatePath = Path.Combine(temp, "missing.p12")
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
            Assert(innerText.Contains("--force") && innerText.Contains("--sign") &&
                   innerText.Contains("--options runtime") && innerText.Contains("--timestamp"),
                "Nested signing must force-sign with the hardened runtime and a secure timestamp.");
            Assert(!innerText.Contains("--entitlements"),
                "Nested Mach-O files must not receive entitlements.");
            var bundle = MacAppSigning.CodesignArguments(signing.Identity, signing, true, "/tmp/app");
            var bundleText = string.Join(' ', bundle);
            Assert(bundleText.Contains("--entitlements") &&
                   bundleText.Contains(Path.GetFullPath(entitlements)),
                "The bundle signature must carry the configured entitlements.");
            var adhoc = MacAppSigning.CodesignArguments("-", new MacAppSigningConfiguration(), false, "/tmp/app");
            Assert(string.Join(' ', adhoc).Contains("--timestamp=none"),
                "Ad-hoc signatures must disable secure timestamps.");
        }
        finally
        {
            Cleanup(Path.GetDirectoryName(entitlements)!);
        }
        await Task.CompletedTask;
    }

    static async Task ResolvesNotaryCredentials()
    {
        var profile = MacAppSigning.ResolveCredentials(
            new MacAppSigningConfiguration { KeychainProfile = "my-profile" });
        Assert(string.Join(' ', profile) == "--keychain-profile my-profile",
            "A keychain profile wins over every other credential mode.");
        var apiKey = MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration
        {
            ApiKeyPath = "/keys/AuthKey_ABC.p8", ApiKeyId = "ABC", ApiIssuer = "ISSUER"
        });
        Assert(string.Join(' ', apiKey) == "--key /keys/AuthKey_ABC.p8 --key-id ABC --issuer ISSUER",
            "API-key credentials assemble the notarytool key arguments.");
        var appleId = MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration
        {
            AppleId = "dev@example.com", ApplePassword = "app-pw", AppleTeamId = "TEAM"
        });
        Assert(string.Join(' ', appleId) ==
               "--apple-id dev@example.com --password app-pw --team-id TEAM",
            "Apple-ID credentials assemble id/password/team.");
        try
        {
            MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration { ApiKeyId = "ABC" });
            throw new InvalidOperationException("A partial API-key credential set must be rejected.");
        }
        catch (ArgumentException)
        {
        }
        try
        {
            MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration { AppleId = "dev@example.com" });
            throw new InvalidOperationException("A partial Apple-ID credential set must be rejected.");
        }
        catch (ArgumentException)
        {
        }
        try
        {
            MacAppSigning.ResolveCredentials(new MacAppSigningConfiguration());
            throw new InvalidOperationException("Notarization without credentials must be rejected.");
        }
        catch (ArgumentException)
        {
        }
        await Task.CompletedTask;
    }

    static async Task SignsInsideOutOnStubbedTools()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return; // signing itself is macOS-only; the stub still needs a mac host's paths
        }
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
            Assert(xattr.Count == 1 && xattr[0].Arguments.SequenceEqual(new[] { "-crs", codesign[0].Arguments.Last().Split("/Contents")[0] }),
                "xattr -crs must run once on the .app before signing.");
            // codesign calls: nested files under Contents/MacOS (the managed ExampleApp.dll is
            // signed as nested code even though it is not Mach-O) + main executable + bundle + verify.
            Assert(codesign.Count == 4,
                $"Expected nested + main executable + bundle + verify calls, got {codesign.Count}.");
            Assert(codesign[0].Arguments.Last().EndsWith("/Contents/MacOS/ExampleApp.dll", StringComparison.Ordinal) &&
                   !string.Join(' ', codesign[0].Arguments).Contains("--entitlements"),
                "Nested payload files are signed first, without entitlements.");
            var bundleArgs = string.Join(' ', codesign[2].Arguments);
            Assert(codesign[1].Arguments.Last().EndsWith("/Contents/MacOS/ExampleApp", StringComparison.Ordinal),
                "The main executable is signed before the bundle.");
            Assert(codesign[2].Arguments.Last().EndsWith(".app", StringComparison.Ordinal) &&
                   bundleArgs.Contains("--entitlements"),
                "The bundle is signed after the executable and carries entitlements.");
            Assert(codesign[3].Arguments.Take(3).SequenceEqual(new[] { "--verify", "--deep", "--strict" }),
                "A --verify --deep --strict pass must follow signing.");
            Assert(requests.Any(r => r.Executable == "codesign") &&
                   !requests.Any(r => r.Executable == "spctl"),
                "Ad-hoc signatures skip the spctl assessment.");
        }
        finally
        {
            MacProcessRunner.Handler = previous;
            Cleanup(input, output);
        }
    }

    static async Task CleansUpKeychainOnFailure()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }
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
                        TemporaryCertificatePath = certificate,
                        TemporaryCertificatePassword = "pw"
                    }
                }).BuildAsync(MacConfiguration(input, output)),
                "A failing codesign must surface as a build failure.");
            Assert(deleted, "The temporary keychain must be deleted even when codesign fails.");
            Assert(calls.Any(c => c.StartsWith("security list-keychains") && c.Contains("-s") &&
                                  c.Contains("login.keychain")),
                "The user's keychain search list must be restored after failure.");
            Assert(calls.Any(c => c.StartsWith("security create-keychain")) &&
                   calls.Any(c => c.StartsWith("security import")),
                "The temporary keychain is created and the certificate imported before signing.");
        }
        finally
        {
            MacProcessRunner.Handler = previous;
            Cleanup(input, output, temp);
        }
    }

    static async Task SigningFailureLeavesNoArtifact()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }
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
            Assert(!Directory.Exists(Path.Combine(output, "ExampleApp.app")),
                "A failed signing run must not leave a pseudo-success .app at the output path.");
        }
        finally
        {
            MacProcessRunner.Handler = previous;
            Cleanup(input, output);
        }
    }

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
            Assert(second[0].Path == first[0].Path, "Rebuilding must replace the existing .app in place.");
            var secondPlist = File.ReadAllBytes(Path.Combine(second[0].Path, "Contents", "Info.plist"));
            Assert(firstPlist.SequenceEqual(secondPlist), "A rebuilt Info.plist differs from the first build.");
            var firstFiles = Directory.EnumerateFiles(first[0].Path, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(first[0].Path.Length))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var secondFiles = Directory.EnumerateFiles(second[0].Path, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(second[0].Path.Length))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assert(firstFiles.SequenceEqual(secondFiles), "A rebuilt .app has a different file set.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsAppSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        foreach (var property in new[]
                 {
                     "MacAppBundleName", "MacAppDisplayName", "MacAppShortVersion", "MacAppBuildVersion",
                     "MacAppMinimumSystemVersion", "MacAppCategory", "MacAppIconName",
                     "MacAppExceptionDomain", "MacAppInfoPlistFile", "MacAppInfoPlistXml"
                 })
        {
            Assert(targets.Contains(property + "=\"$(Bundler" + property + ")\"", StringComparison.Ordinal),
                $"MSBuild does not map Bundler{property} to the task.");
        }
        Assert(targets.Contains("MacContents=\"@(BundlerMacContent)\"", StringComparison.Ordinal) &&
               targets.Contains("MacFrameworks=\"@(BundlerMacFramework)\"", StringComparison.Ordinal) &&
               targets.Contains("MacDocumentTypes=\"@(BundlerMacDocumentType)\"", StringComparison.Ordinal) &&
               targets.Contains("MacUrlTypes=\"@(BundlerMacUrlType)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerMac* item groups.");
        Assert(targets.Contains("StartsWith('osx-')", StringComparison.Ordinal),
            "MSBuild must default the main executable name without '.exe' for osx targets.");
        Assert(task.Contains("new MacAppBundler(", StringComparison.Ordinal) &&
               task.Contains("MacAppContentConfiguration", StringComparison.Ordinal),
            "The MSBuild task does not construct the .app backend.");
    }

    static async Task MarksMachOFilesExecutable()
    {
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
            Assert((mode & UnixFileMode.UserExecute) == UnixFileMode.UserExecute,
                "The Mach-O main executable lost its executable bit.");
            var payload = Path.Combine(artifacts[0].Path, "Contents", "MacOS", "ExampleApp.dll");
            Assert((File.GetUnixFileMode(payload) & UnixFileMode.UserExecute) == 0,
                "Non-Mach-O payload files must not gain the executable bit.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static async Task WarnsOnWindowsHosts()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var logger = new ListLogger();
        try
        {
            await new MacAppBundler(options: new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(MacConfiguration(input, output));
            Assert(logger.Messages.Any(message => message.Contains("executable bit", StringComparison.Ordinal)),
                "Windows hosts must warn about the missing executable bit.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static async Task PassesPlutilLint()
    {
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
            Assert(process.ExitCode == 0,
                $"plutil -lint failed on a typed plist (arrays/dicts/booleans): {process.StandardError.ReadToEnd()}");
        }
        finally
        {
            Cleanup(input, output);
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

    static byte[] FakeMachO(uint cpuType = 0x0100000C)
    {
        var bytes = new byte[64];
        bytes[0] = 0xCF; bytes[1] = 0xFA; bytes[2] = 0xED; bytes[3] = 0xFE;
        bytes[4] = (byte)cpuType; bytes[5] = (byte)(cpuType >> 8);
        bytes[6] = (byte)(cpuType >> 16); bytes[7] = (byte)(cpuType >> 24);
        return bytes;
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
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{message} (threw {exception.GetType().Name} instead: {exception.Message})");
        }
        throw new InvalidOperationException(message + " (no exception was thrown)");
    }

    static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    static Task RunSync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    sealed class ListLogger : IBundleLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(BundleLogLevel level, string message) => Messages.Add(message);
    }
}
