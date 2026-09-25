using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Wix;
using DotNet.Bundler.Signing.Windows;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

internal static class WixTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Keeps MSI identity and version rules stable", () => RunSync(KeepsMsiIdentityStable));
            yield return ("Maps explicit MSI versions without changing installed product families", () => RunSync(MapsExplicitMsiVersions));
            yield return ("Verifies WiX binary and source redistribution", () => RunSync(VerifiesWixRedistribution));
            yield return ("Maps MSI configuration through MSBuild", () => RunSync(MapsMsiSettingsThroughMsBuild));
            yield return ("Validates MSI language, license, and signing inputs", ValidatesMsiPublishingInputs);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) yield break;
            yield return ("Rejects unsafe MSI installation paths", RejectsUnsafeMsiPaths);
            yield return ("Builds and inspects a real WiX MSI without installing it", BuildsAndInspectsMsi);
            yield return ("Rejects WiX compiler warnings during MSI generation", RejectsWixCompilerWarnings);
            yield return ("Lets WiX generate a distinct package code for each MSI build", GeneratesDistinctPackageCodes);
            yield return ("Reuses only a verified MSI artifact", ReusesOnlyVerifiedMsi);
            yield return ("Rejects a changed same-version MSI payload", RejectsChangedSameVersionPayload);
            yield return ("Rejects MSI input reparse points", RejectsMsiInputReparsePoints);
            yield return ("Recovers a tampered WiX compiler cache", RecoversTamperedWixCompiler);
            yield return ("Builds an isolated per-machine WiX MSI", BuildsMachineMsi);
            yield return ("Rejects a tampered WiX tool archive", RejectsTamperedWixArchive);
            yield return ("Serializes concurrent WiX tool cache users", SerializesConcurrentWixCacheUsers);
            yield return ("Builds an ARM64-targeted MSI on Windows x64", BuildsArm64TargetedMsi);
            yield return ("Builds x86 MSI packages with isolated identity and 32-bit components", BuildsX86TargetedMsi);
            yield return ("Builds explicit MSI versions and downgrade policy", BuildsExplicitVersionAndDowngradePolicy);
            yield return ("Builds isolated English and Chinese MSI languages", BuildsLocalizedMsi);
            yield return ("Signs staged MSI payload and final package in order", SignsMsiArtifacts);
            yield return ("Removes a failed signed MSI output", RemovesFailedSignedMsi);
            yield return ("Signs a real payload PE and MSI with a test certificate", SignsRealMsiWithTestCertificate);
            yield return ("Validates MSI UI bitmap dimensions", ValidatesMsiBitmapInputs);
            yield return ("Builds MSI with directory selection UI and optional features", BuildsFullFeaturedMsi);
            yield return ("Builds MSI custom UI without a license page", BuildsCustomUiWithoutLicense);
            yield return ("Builds MSI with license and directory selection", BuildsLicensedDirectorySelection);
            yield return ("Enforces the allowed MSI install directory root", EnforcesMsiInstallDirectoryRoot);
            yield return ("Launches MSI only through the interactive exit checkbox", GeneratesLaunchCheckboxOnly);
            yield return ("Builds one MSI per declared language", BuildsEveryDeclaredMsiLanguage);
            yield return ("Applies caller MSI locale overrides", AppliesCallerMsiLocaleOverrides);
            yield return ("Rejects invalid MSI locale inputs", RejectsInvalidMsiLocaleInputs);
            yield return ("Builds MSI in FIPS mode and wires shortcut icons", BuildsFipsMsiWithIconShortcuts);
        }
    }

    static void KeepsMsiIdentityStable()
    {
        var first = WixIdentity.Create("com.Example.App", "1.2.3", "win-x64", WixInstallScope.CurrentUser);
        var repeated = WixIdentity.Create("com.example.app", "1.2.3", "win-x64", WixInstallScope.CurrentUser);
        var next = WixIdentity.Create("com.example.app", "1.2.4", "win-x64", WixInstallScope.CurrentUser);
        var arm = WixIdentity.Create("com.example.app", "1.2.3", "win-arm64", WixInstallScope.CurrentUser);
        var x86 = WixIdentity.Create("com.example.app", "1.2.3", "win-x86", WixInstallScope.CurrentUser);
        Assert(first.UpgradeCode == repeated.UpgradeCode && first.ProductCode == repeated.ProductCode,
            "MSI identity must be stable across builds and identifier casing.");
        Assert(first.UpgradeCode == Guid.Parse("a4544d5a-7d38-54b0-bfef-2f43efedb406") &&
               first.ProductCode == Guid.Parse("d182fb03-d132-5fe3-8098-5f008419f688"),
            "MSI UUIDv5 identity differs from the RFC 4122 test vector for the frozen namespace and input.");
        Assert(first.UpgradeCode == next.UpgradeCode && first.ProductCode != next.ProductCode,
            "A new product version must change ProductCode and preserve UpgradeCode.");
        Assert(first.UpgradeCode != arm.UpgradeCode && first.ProductCode != arm.ProductCode,
            "Separate architectures must have separate MSI identities.");
        Assert(first.UpgradeCode != x86.UpgradeCode && arm.UpgradeCode != x86.UpgradeCode &&
               first.ProductCode != x86.ProductCode,
            "The x86 product line must be isolated from x64 and ARM64.");
        Assert(x86.UpgradeCode == Guid.Parse("f9d236f1-6d33-5a1e-940d-00257ce2a020") &&
               x86.ProductCode == Guid.Parse("4b6c833e-0645-5d4c-83b9-1ba094b77eed"),
            "The new x86 English identity differs from its fixed test vector.");
        var migrated = WixIdentity.Create("com.example.app", "1.2.3", "win-x64",
            WixInstallScope.CurrentUser, "{11111111-2222-3333-4444-555555555555}");
        Assert(migrated.UpgradeCode == Guid.Parse("11111111-2222-3333-4444-555555555555") &&
               migrated.ProductCode != first.ProductCode,
            "An explicit historical UpgradeCode did not establish a separate product family.");
        foreach (var version in new[] { "1.0.0-beta.1", "1.0.0+meta", "1.0.0.1", "256.0.0", "1.256.0", "1.0.65536", "01.0.0" })
        {
            try
            {
                WixIdentity.Create("com.example.app", version, "win-x64", WixInstallScope.CurrentUser);
                throw new InvalidOperationException("MSI accepted an unmappable version: " + version);
            }
            catch (ArgumentException exception) when (exception.ParamName == "version") { }
        }
        Assert(WixIdentity.Create("com.example.app", "255.255.65535", "win-x64", WixInstallScope.CurrentUser)
            .ProductVersion == "255.255.65535", "MSI maximum version was rejected.");
        var machine = WixIdentity.Create("com.example.app", "1.0.0", "win-x64", WixInstallScope.PerMachine);
        var user = WixIdentity.Create("com.example.app", "1.0.0", "win-x64", WixInstallScope.CurrentUser);
        Assert(machine.UpgradeCode != user.UpgradeCode && machine.ProductCode != user.ProductCode,
            "Per-machine and current-user products must have separate identity families.");
        var chinese = WixIdentity.Create("com.example.app", "1.2.3", "win-x64", WixInstallScope.CurrentUser,
            language: WixLanguageInfo.Resolve("zh-CN"));
        Assert(chinese.UpgradeCode != first.UpgradeCode && chinese.ProductCode != first.ProductCode,
            "Localized MSI products need separate language identity families.");
        var x86Chinese = WixIdentity.Create("com.example.app", "1.2.3", "win-x86", WixInstallScope.CurrentUser,
            language: WixLanguageInfo.Resolve("zh-CN"));
        Assert(x86Chinese.UpgradeCode == Guid.Parse("c4985894-4b1c-5e49-941b-a120850e0667") &&
               x86Chinese.ProductCode == Guid.Parse("eeeead45-80e4-57a2-ad45-faa3f2cc2be1") &&
               x86Chinese.UpgradeCode != x86.UpgradeCode,
            "The new x86 Chinese identity differs from its fixed test vector.");
    }

    static void MapsExplicitMsiVersions()
    {
        var baseline = WixIdentity.Create("com.example.app", "1.2.3", "win-x64", WixInstallScope.CurrentUser);
        var explicitSame = WixIdentity.Create("com.example.app", "1.2.3", "win-x64", WixInstallScope.CurrentUser,
            msiVersion: "1.2.3");
        Assert(baseline == explicitSame, "Explicit mapping to the legacy version changed installed identity.");
        var preview = WixIdentity.Create("com.example.app", "2.0.0-beta.1", "win-x64",
            WixInstallScope.CurrentUser, msiVersion: "1.9.7");
        Assert(preview.ProductVersion == "1.9.7" && preview.UpgradeCode == baseline.UpgradeCode &&
               preview.ProductCode != baseline.ProductCode, "Explicit MSI version did not retain the product family.");
        foreach (var version in new[] { "", "1.2.3.4", "1.2.3-beta", "01.2.3", "256.0.0", "1.256.0", "1.2.65536" })
        {
            try
            {
                WixIdentity.Create("com.example.app", "2.0.0-beta.1", "win-x64",
                    WixInstallScope.CurrentUser, msiVersion: version);
                throw new InvalidOperationException("An invalid explicit MSI version was accepted: " + version);
            }
            catch (ArgumentException exception) when (exception.ParamName == "msiVersion") { }
        }
    }

    static async Task RejectsUnsafeMsiPaths()
    {
        using var fixture = new WixTestFixture();
        foreach (var path in new[] { "../escape", "docs/../escape", "C:\\Windows\\file" })
        {
            try
            {
                await fixture.Bundler().BuildAsync(fixture.Request(path));
                throw new InvalidOperationException("Core accepted an escaped MSI resource path: " + path);
            }
            catch (BundleValidationException exception) when
                (exception.Issues.Any(issue => issue.Path == "resources[0].targetPath")) { }
        }
        foreach (var path in new[] { "docs//file", "file. " })
        {
            await ExpectAsync<ArgumentException>(() => fixture.Bundler().BuildAsync(fixture.Request(path)), "path");
        }
        var output = Path.Combine(fixture.Root, "output");
        Assert(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*.msi", SearchOption.AllDirectories).Any(),
            "An invalid resource path produced an MSI.");
    }

    static void MapsMsiSettingsThroughMsBuild()
    {
        var root = RepositoryRoot();
        var props = File.ReadAllText(Path.Combine(root, "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var targets = File.ReadAllText(Path.Combine(root, "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var task = File.ReadAllText(Path.Combine(root, "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        foreach (var property in new[] { "WixInstallScope", "WixUpgradeCode", "WixMsiVersion", "WixAllowDowngrades",
                     "WixCodepage", "WixLanguage", "WixLanguages", "WixFipsCompliant", "WixToolsetArchivePath",
                     "WixStartMenuShortcut", "WixDesktopShortcut", "WixInstallDirectorySelection",
                     "WixBannerBitmap", "WixDialogBitmap", "WixAddToPath",
                     "WixUninstallShortcut", "WixLaunchAfterInstall" })
        {
            Assert(props.Contains("<Bundler" + property, StringComparison.Ordinal) &&
                   targets.Contains(property + "=\"$(Bundler" + property + ")\"", StringComparison.Ordinal),
                "MSBuild did not map MSI property Bundler" + property + ".");
        }
        Assert(targets.Contains("WixLanguageFiles=\"@(BundlerWixLanguageFile)\"", StringComparison.Ordinal) &&
               task.Contains("ParseWixLanguageFiles()", StringComparison.Ordinal) &&
               task.Contains("BundlerWixLanguageFile", StringComparison.Ordinal),
            "MSBuild does not map per-language MSI locale files.");
        Assert(task.Contains("new WixBundler(", StringComparison.Ordinal) &&
               task.Contains("Codepage = WixCodepage", StringComparison.Ordinal) &&
               task.Contains("FipsCompliant = WixFipsCompliant", StringComparison.Ordinal) &&
               task.Contains("Signer = CreateWindowsSigner()", StringComparison.Ordinal),
            "MSBuild task does not call the same public MSI backend or map signing.");
    }

    static void VerifiesWixRedistribution()
    {
        var root = Path.Combine(RepositoryRoot(), "third_party", "wix");
        var binary = Path.Combine(root, "wix3141-tools.zip");
        var source = Path.Combine(root, "wix3141-source.zip");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))) ==
               "ABE572B353CD4151B1C69907BB5C5E84886138E518607432C9723B454853B358",
            "The bundled WiX binary subset changed without updating its pinned hash.");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) ==
               "A56184E798885641821666BD389FE6276F99363F65BAE8F88630B17DE297FE9F",
            "The corresponding WiX source archive changed without review.");
        using var toolZip = System.IO.Compression.ZipFile.OpenRead(binary);
        var expected = File.ReadAllLines(Path.Combine(root, "SHA256SUMS"))
            .Select(line => (Hash: line.Substring(0, 64), Name: line.Substring(66)))
            .ToDictionary(pair => pair.Name, pair => pair.Hash, StringComparer.Ordinal);
        Assert(toolZip.Entries.Count == expected.Count, "The WiX subset file manifest is incomplete.");
        Assert(toolZip.GetEntry("WixUIExtension.dll") is not null,
            "The selected WiX UI/localization extension is missing.");
        foreach (var entry in toolZip.Entries)
        {
            using var stream = entry.Open();
            Assert(expected.TryGetValue(entry.FullName, out var hash) &&
                   Convert.ToHexString(SHA256.HashData(stream)) == hash,
                "WiX subset entry differs from the recorded upstream file hash: " + entry.FullName);
        }
        using var sourceZip = System.IO.Compression.ZipFile.OpenRead(source);
        Assert(sourceZip.GetEntry("wix3-wix3141rtm/LICENSE.TXT") is not null &&
               sourceZip.GetEntry("wix3-wix3141rtm/src/tools/candle/candle.cs") is not null &&
               sourceZip.GetEntry("wix3-wix3141rtm/src/tools/light/light.cs") is not null &&
               sourceZip.GetEntry("wix3-wix3141rtm/src/ext/UIExtension/wixext/WixUIExtension.csproj") is not null,
            "The bundled corresponding source archive is incomplete.");
        var licenseEntry = toolZip.GetEntry("LICENSE.TXT")!;
        using var license = licenseEntry.Open();
        Assert(Convert.ToHexString(SHA256.HashData(license)) ==
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "LICENSE.TXT")))),
            "The distributed WiX license differs from the original binary archive.");
    }

    static async Task ValidatesMsiPublishingInputs()
    {
        var configuration = new BundleConfiguration
        {
            ProductName = "Msi Test",
            Identifier = "com.example.msitest",
            Version = "1.0.0",
            LicenseFile = "future-license.txt",
            Targets = [new BundleTargetConfiguration
            {
                RuntimeIdentifier = "win-x64", InputDirectory = "unused", MainExecutable = "test.exe",
                Formats = [PackageFormat.Msi]
            }]
        };
        try
        {
            await new WixBundler().BuildAsync(configuration);
            throw new InvalidOperationException("An unsupported MSI license format was accepted.");
        }
        catch (ArgumentException exception) when (exception.Message.Contains("RTF", StringComparison.Ordinal)) { }
        using var fixture = new WixTestFixture();
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["zh-CN"],
            Codepage = 1252
        }).BuildAsync(fixture.Request()), "code page 1252");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["xx-99"]
        }).BuildAsync(fixture.Request()), "xx-99");
        await ExpectAsync<ArgumentException>(() => fixture.Bundler().BuildAsync(
            fixture.Request(signingFiles: ["fixture.exe"])), "signer");
    }

    static async Task BuildsAndInspectsMsi()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Msi.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(Path.Combine(input, "assets"));
        await File.WriteAllTextAsync(Path.Combine(input, "Msi Test.exe"), "MSI fixture payload");
        await File.WriteAllTextAsync(Path.Combine(input, "assets", "说明.txt"), "user visible file");
        var resource = Path.Combine(root, "resource.txt");
        await File.WriteAllTextAsync(resource, "external resource");
        var icon = Path.Combine(root, "product.ico");
        File.WriteAllBytes(icon,
        [
            0,0,1,0,1,0, 1,1,0,0,1,0,32,0,48,0,0,0,22,0,0,0,
            40,0,0,0, 1,0,0,0, 2,0,0,0, 1,0,32,0, 0,0,0,0,
            4,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
            0,0,255,255, 0,0,0,0
        ]);
        try
        {
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
                Resources = [new BundleResourceConfiguration { Source = resource, TargetPath = "docs/外部.txt" }],
                OutputDirectory = output,
                Targets = [new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64", InputDirectory = input,
                    MainExecutable = "Msi Test.exe", Formats = [PackageFormat.Msi]
                }]
            };
            var bundler = new WixBundler(new WixBundleConfiguration { Codepage = 936, StartMenuShortcut = true, DesktopShortcut = true },
                new WixBundlerOptions { ToolCacheDirectory = cache });
            var artifact = (await bundler.BuildAsync(configuration)).Single();
            Assert(File.Exists(artifact.Path) && Path.GetExtension(artifact.Path) == ".msi",
                "WiX did not produce an MSI artifact.");
            using (var database = new MsiDatabaseReader(artifact.Path))
            {
                var expected = WixIdentity.Create(configuration.Identifier, configuration.Version,
                    "win-x64", WixInstallScope.CurrentUser);
                Assert(database.Property("ProductCode") == expected.ProductCode.ToString("B").ToUpperInvariant(),
                    "MSI ProductCode differs from the stable identity policy.");
                Assert(database.Property("UpgradeCode") == expected.UpgradeCode.ToString("B").ToUpperInvariant(),
                    "MSI UpgradeCode differs from the stable identity policy.");
                Assert(database.Property("ProductVersion") == "1.2.3", "MSI version mapping is incorrect.");
                Assert(database.Property("ProductName") == "Msi 测试", "MSI database did not preserve the selected Chinese codepage.");
                Assert(database.Property("Manufacturer") == "Bundler Tests", "MSI publisher is missing.");
                Assert(database.Property("ARPCOMMENTS") == "Test MSI database", "MSI description is missing.");
                Assert(database.Property("ARPURLINFOABOUT") == "https://example.com/msi", "MSI homepage is missing.");
                Assert(database.Property("ARPPRODUCTICON") == "ProductIcon", "MSI product icon is missing.");
                Assert(database.RowCount("Icon", "Name") == 1, "MSI icon table is missing the supplied icon.");
                Assert(database.RowCount("File", "File") == 3, "MSI payload file count is incorrect.");
                Assert(database.RowCount("Component", "Component") == 7, "MSI must give every file, cleanup, registration, and shortcut a component.");
                Assert(database.RowCount("Registry", "Registry") > 4, "MSI desktop capabilities and HKCU key paths are missing.");
                Assert(database.Contains("Registry", "Name", "application/x-abc"),
                    "MSI silently ignored the configured MIME candidate registration.");
                Assert(database.RowCount("Shortcut", "Shortcut") == 2, "MSI desktop and Start Menu shortcuts are missing.");
                Assert(database.RowCount("RemoveFile", "FileKey") >= 4, "Per-user directories need uninstall cleanup rows.");
                Assert(database.RowCount("Upgrade", "UpgradeCode") >= 1,
                    "The MSI major-upgrade table is missing.");
                Assert(database.Property("BUNDLER_PACKAGE_DEFINITION").Length == 64 &&
                       database.Contains("Registry", "Name", "DefinitionHash") &&
                       database.RowCount("AppSearch", "Property") >= 1,
                    "The same-version package definition guard is missing.");
                Assert(database.Template.StartsWith("x64;", StringComparison.OrdinalIgnoreCase),
                    "MSI architecture summary is not x64.");
                Assert(Guid.TryParse(database.PackageCode, out var packageCode) &&
                       packageCode != expected.ProductCode,
                    "MSI package code was not written as a distinct GUID.");
            }
        }
        finally
        {
            DeleteOwnedTestDirectory(root);
        }
    }

    static async Task ReusesOnlyVerifiedMsi()
    {
        using var fixture = new WixTestFixture();
        var bundler = fixture.Bundler();
        var request = fixture.Request();
        var first = (await bundler.BuildAsync(request)).Single();
        var firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.Path)));
        var repeated = (await bundler.BuildAsync(request)).Single();
        Assert(repeated.Path == first.Path &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(repeated.Path))) == firstHash,
            "An unchanged build did not reuse the verified MSI.");
        File.WriteAllText(first.Path + ".bundler-manifest", "unverified");
        await ExpectAsync<IOException>(() => bundler.BuildAsync(request), "same product version");
    }

    static async Task RejectsChangedSameVersionPayload()
    {
        using var fixture = new WixTestFixture();
        var bundler = fixture.Bundler();
        var request = fixture.Request();
        var first = (await bundler.BuildAsync(request)).Single();
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.Path)));
        await File.WriteAllTextAsync(Path.Combine(fixture.Input, "fixture.exe"), "different payload");
        await ExpectAsync<IOException>(() => bundler.BuildAsync(request), "same product version");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.Path))) == originalHash,
            "The rejected rebuild changed the existing MSI.");
    }

    static async Task RejectsMsiInputReparsePoints()
    {
        using var fixture = new WixTestFixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        var linked = Path.Combine(fixture.Input, "linked-outside");
        Directory.CreateSymbolicLink(linked, outside);
        await ExpectAsync<InvalidDataException>(() => fixture.Bundler().BuildAsync(fixture.Request()), "reparse point");
        Assert(File.ReadAllText(sentinel) == "preserve", "The rejected input modified the symlink target.");
    }

    static async Task RecoversTamperedWixCompiler()
    {
        using var fixture = new WixTestFixture();
        var bundler = fixture.Bundler();
        var request = fixture.Request();
        await bundler.BuildAsync(request);
        var toolDirectory = Directory.EnumerateDirectories(fixture.Cache, "wix-toolset-*").Single();
        var compiler = Path.Combine(toolDirectory, "candle.exe");
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(compiler)));
        await File.WriteAllTextAsync(compiler, "tampered");
        await bundler.BuildAsync(request);
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(compiler))) == originalHash,
            "The WiX tool cache did not restore the pinned compiler after tampering.");
    }

    static async Task RejectsTamperedWixArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Msi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "tampered.zip");
            File.Copy(Path.Combine(RepositoryRoot(), "third_party", "wix", "wix3141-tools.zip"), archive);
            using (var stream = new FileStream(archive, FileMode.Open, FileAccess.Write))
            {
                stream.Position = 50;
                stream.WriteByte(255);
            }
            var input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            await File.WriteAllTextAsync(Path.Combine(input, "test.exe"), "fixture");
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                RuntimeIdentifier = "win-x64", InputDirectory = input,
                MainExecutable = "test.exe", Formats = [PackageFormat.Msi]
            });
            try
            {
                await new WixBundler(options: new WixBundlerOptions
                {
                    ToolCacheDirectory = Path.Combine(root, "cache"),
                    ToolsetArchivePath = archive
                }).BuildAsync(configuration);
                throw new InvalidOperationException("A modified WiX archive was accepted.");
            }
            catch (InvalidDataException) { }
        }
        finally { DeleteOwnedTestDirectory(root); }
    }

    static async Task SerializesConcurrentWixCacheUsers()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Msi.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "test.exe"), "fixture");
        try
        {
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
                        RuntimeIdentifier = "win-x64", InputDirectory = input,
                        MainExecutable = "test.exe", Formats = [PackageFormat.Msi]
                    }]
                };
                return new WixBundler(options: new WixBundlerOptions
                {
                    ToolCacheDirectory = Path.Combine(root, "cache")
                }).BuildAsync(configuration);
            }
            var artifacts = await Task.WhenAll(Build(1), Build(2));
            Assert(artifacts.SelectMany(group => group).All(artifact => File.Exists(artifact.Path)),
                "Concurrent MSI builds did not both produce packages.");
            var sameOutput = await Task.WhenAll(Build(3), Build(3));
            Assert(sameOutput[0][0].Path == sameOutput[1][0].Path &&
                   File.Exists(sameOutput[0][0].Path + ".bundler-manifest"),
                "Concurrent builds to one MSI output did not serialize and reuse a verified package.");
        }
        finally { DeleteOwnedTestDirectory(root); }
    }

    static async Task BuildsMachineMsi()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Msi.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "machine.exe"), "machine payload");
        try
        {
            var configuration = new BundleConfiguration
            {
                ProductName = "Machine MSI Test", Identifier = "com.example.msimachine", Version = "2.0.0",
                FileAssociations = [new BundleFileAssociationConfiguration { Extensions = [".machinetest"] }],
                UrlProtocols = [new BundleUrlProtocolConfiguration { Schemes = ["machine-test"] }],
                OutputDirectory = Path.Combine(root, "output"),
                Targets = [new BundleTargetConfiguration { RuntimeIdentifier = "win-x64", InputDirectory = input,
                    MainExecutable = "machine.exe", Formats = [PackageFormat.Msi] }]
            };
            var artifact = (await new WixBundler(new WixBundleConfiguration { InstallScope = WixInstallScope.PerMachine,
                StartMenuShortcut = true, DesktopShortcut = true },
                new WixBundlerOptions { ToolCacheDirectory = Path.Combine(root, "cache") }).BuildAsync(configuration)).Single();
            using var database = new MsiDatabaseReader(artifact.Path);
            var expected = WixIdentity.Create(configuration.Identifier, configuration.Version, "win-x64", WixInstallScope.PerMachine);
            Assert(database.Property("ProductCode") == expected.ProductCode.ToString("B").ToUpperInvariant(),
                "Per-machine MSI identity differs from the scope-specific contract.");
            Assert(database.Contains("Directory", "Directory", "ProgramFiles64Folder"),
                "Per-machine MSI does not target Program Files.");
            var registryRoots = database.Pairs("Registry", "Name", "Root");
            Assert(registryRoots.Any(row => row.Second == "2") &&
                   registryRoots.Where(row => row.Second == "1")
                       .All(row => row.First.EndsWith("Shortcut", StringComparison.Ordinal)),
                "Per-machine MSI must keep HKLM key paths except HKCU shortcut repair keys.");
            Assert(database.RowCount("Shortcut", "Shortcut") == 2 &&
                   database.Contains("Directory", "Directory", "ProgramMenuFolder"),
                "Per-machine MSI shortcut directories were not compiled.");
        }
        finally { DeleteOwnedTestDirectory(root); }
    }

    static async Task BuildsArm64TargetedMsi()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Msi.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "test.exe"), "fixture");
        try
        {
            var configuration = new BundleConfiguration
            {
                ProductName = "Arm64 Msi",
                Identifier = "com.example.arm64msi",
                Version = "1.0.0",
                OutputDirectory = Path.Combine(root, "output"),
                Targets = [new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-arm64", InputDirectory = input,
                    MainExecutable = "test.exe", Formats = [PackageFormat.Msi]
                }]
            };
            var artifact = (await new WixBundler(options: new WixBundlerOptions
            {
                ToolCacheDirectory = Path.Combine(root, "cache")
            }).BuildAsync(configuration)).Single();
            using var database = new MsiDatabaseReader(artifact.Path);
            Assert(database.Template.StartsWith("Arm64;", StringComparison.OrdinalIgnoreCase),
                "WiX did not mark the package ARM64.");
            Assert(database.Property("UpgradeCode") != WixIdentity.Create(configuration.Identifier, configuration.Version,
                "win-x64", WixInstallScope.CurrentUser).UpgradeCode.ToString("B").ToUpperInvariant(),
                "ARM64 and x64 MSI products share an UpgradeCode.");
        }
        finally { DeleteOwnedTestDirectory(root); }
    }

    static async Task BuildsX86TargetedMsi()
    {
        using var fixture = new WixTestFixture();
        foreach (var scope in new[] { WixInstallScope.CurrentUser, WixInstallScope.PerMachine })
        {
            var request = new BundleConfiguration
            {
                ProductName = "X86 MSI Test", Identifier = "com.example.msi.x86", Version = "1.2.3",
                OutputDirectory = Path.Combine(fixture.Root, "x86-" + scope),
                Targets = [new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x86", InputDirectory = fixture.Input,
                    MainExecutable = "fixture.exe", Formats = [PackageFormat.Msi]
                }]
            };
            var artifact = (await new WixBundler(new WixBundleConfiguration { InstallScope = scope },
                new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(request)).Single();
            using var database = new MsiDatabaseReader(artifact.Path);
            var expected = WixIdentity.Create(request.Identifier, request.Version, "win-x86", scope);
            Assert(database.Template.StartsWith("Intel;", StringComparison.OrdinalIgnoreCase),
                "x86 MSI did not use the Intel template summary.");
            Assert(database.Property("UpgradeCode") == expected.UpgradeCode.ToString("B").ToUpperInvariant() &&
                   database.Property("ProductCode") == expected.ProductCode.ToString("B").ToUpperInvariant(),
                "x86 MSI identity differs from the isolated target vector.");
            Assert(database.Values("Component", "Attributes").All(value => (int.Parse(value) & 256) == 0),
                "x86 MSI contains a 64-bit component.");
            Assert(database.ContainsSubstring("Directory", "DefaultDir", "com.example.msi.x86-x86"),
                "x86 MSI installation directory is not architecture-specific.");
            Assert(scope == WixInstallScope.CurrentUser
                    ? database.Contains("Directory", "Directory", "LocalAppDataFolder") &&
                      database.Contains("Registry", "Root", "1")
                    : database.Contains("Directory", "Directory", "ProgramFilesFolder") &&
                      !database.Contains("Directory", "Directory", "ProgramFiles64Folder") &&
                      database.Contains("Registry", "Root", "2"),
                "x86 MSI scope or install root is incorrect.");
        }
    }

    static async Task BuildsExplicitVersionAndDowngradePolicy()
    {
        using var fixture = new WixTestFixture();
        var request = new BundleConfiguration
        {
            ProductName = "Mapped MSI Test", Identifier = "com.example.msi.mapped", Version = "2.0.0-beta.1",
            OutputDirectory = Path.Combine(fixture.Root, "mapped"),
            Targets = [new BundleTargetConfiguration
            {
                RuntimeIdentifier = "win-x64", InputDirectory = fixture.Input,
                MainExecutable = "fixture.exe", Formats = [PackageFormat.Msi]
            }]
        };
        await ExpectAsync<ArgumentException>(() => fixture.Bundler().BuildAsync(request), "version");
        var mappedSettings = new WixBundleConfiguration { MsiVersion = "1.9.7", AllowDowngrades = true };
        var artifact = (await new WixBundler(mappedSettings,
            new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(request)).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert(database.Property("ProductVersion") == "1.9.7" &&
               Path.GetFileName(artifact.Path).Contains("-1.9.7.msi", StringComparison.Ordinal),
            "Explicit MSI version did not reach the compiled product and output name.");
        Assert(database.Property("ProductCode") == WixIdentity.Create(request.Identifier, request.Version,
                   "win-x64", WixInstallScope.CurrentUser, msiVersion: "1.9.7").ProductCode.ToString("B").ToUpperInvariant(),
            "Mapped MSI ProductCode differs from its documented identity.");
        Assert(database.Values("Upgrade", "Attributes").Count > 0,
            "Explicit downgrade policy did not compile an Upgrade table.");
    }

    static async Task RejectsWixCompilerWarnings()
    {
        using var fixture = new WixTestFixture();
        var toolset = await WixToolsetResolver.ResolveAsync(fixture.Cache, null, CancellationToken.None);
        var source = Path.Combine(fixture.Root, "warning.wxs");
        var output = Path.Combine(fixture.Root, "warning.wixobj");
        File.WriteAllText(source,
            "<?xml version=\"1.0\"?>\n<?warning freeze sentinel warning?>\n" +
            "<Wix xmlns=\"http://schemas.microsoft.com/wix/2006/wi\"><Fragment /></Wix>");
        await ExpectAsync<InvalidOperationException>(() => WixProcessRunner.RunAsync(toolset.CandlePath,
            ["-nologo", "-out", output, source], fixture.Root, CancellationToken.None), "freeze sentinel warning");
    }

    static async Task GeneratesDistinctPackageCodes()
    {
        using var fixture = new WixTestFixture();
        var bundler = fixture.Bundler();
        var first = (await bundler.BuildAsync(fixture.Request())).Single();
        var second = (await bundler.BuildAsync(fixture.Request(outputDirectory: Path.Combine(fixture.Root, "second-output")))).Single();
        using var firstDatabase = new MsiDatabaseReader(first.Path);
        using var secondDatabase = new MsiDatabaseReader(second.Path);
        Assert(firstDatabase.Property("ProductCode") == secondDatabase.Property("ProductCode") &&
               firstDatabase.Property("UpgradeCode") == secondDatabase.Property("UpgradeCode") &&
               firstDatabase.PackageCode != secondDatabase.PackageCode,
            "Two MSI builds for one product version must keep product identity but use distinct package codes.");
    }

    static async Task BuildsLocalizedMsi()
    {
        using var fixture = new WixTestFixture();
        var request = fixture.Request();
        var english = (await fixture.Bundler().BuildAsync(request)).Single();
        var license = Path.Combine(fixture.Root, "terms.rtf");
        File.WriteAllText(license, "{\\rtf1\\ansi Example license terms.}");
        request = fixture.Request(licenseFile: license);
        var chinese = (await new WixBundler(new WixBundleConfiguration
        {
            Languages = ["zh-CN"],
            StartMenuShortcut = true,
            DesktopShortcut = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(request)).Single();
        Assert(english.Path != chinese.Path && chinese.Path.EndsWith("-zh-cn.msi", StringComparison.Ordinal),
            "Localized MSI outputs collide with the English output.");
        using var en = new MsiDatabaseReader(english.Path);
        using var zh = new MsiDatabaseReader(chinese.Path);
        Assert(en.Property("ProductLanguage") == "1033" && zh.Property("ProductLanguage") == "2052" &&
               en.Property("UpgradeCode") != zh.Property("UpgradeCode"),
            "Localized product language or identity is incorrect.");
        var chineseDirectory = zh.ContainsSubstring("Directory", "DefaultDir", "com.example.wixtestfixture-x64-zh-cn");
        var dialogCount = zh.RowCount("Dialog", "Dialog");
        Assert(zh.Template.Contains("2052", StringComparison.Ordinal) && chineseDirectory && dialogCount > 0,
            $"Chinese MSI did not include isolated files and a license UI. Template={zh.Template}, directory={chineseDirectory}, dialogs={dialogCount}.");
        using var englishFixture = new WixTestFixture();
        var englishLicense = Path.Combine(englishFixture.Root, "terms.rtf");
        File.WriteAllText(englishLicense, "{\\rtf1\\ansi Example license terms.}");
        var licensedEnglish = (await englishFixture.Bundler().BuildAsync(
            englishFixture.Request(licenseFile: englishLicense))).Single();
        using var englishUi = new MsiDatabaseReader(licensedEnglish.Path);
        Assert(englishUi.Property("ProductLanguage") == "1033" &&
               englishUi.RowCount("Dialog", "Dialog") > 0,
            "English MSI did not include the configured license UI.");
    }

    static async Task SignsMsiArtifacts()
    {
        using var fixture = new WixTestFixture();
        File.WriteAllText(Path.Combine(fixture.Input, "helper.dll"), "helper");
        var signer = new MsiRecordingSigner();
        var request = fixture.Request(signingFiles: ["helper.dll"]);
        var artifact = (await new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = fixture.Cache,
            Signer = signer
        }).BuildAsync(request)).Single();
        Assert(signer.Kinds.SequenceEqual([BundleSigningArtifactKind.PayloadExecutable,
            BundleSigningArtifactKind.PayloadFile, BundleSigningArtifactKind.Installer]),
            "MSI signing order or artifact kinds are incorrect.");
        Assert(signer.Paths[0] != Path.Combine(fixture.Input, "fixture.exe") &&
               File.ReadAllText(Path.Combine(fixture.Input, "fixture.exe")) == "original payload" &&
               File.Exists(artifact.Path + ".bundler-manifest"),
            "MSI signing changed source files or omitted final output manifest.");
        var repeatedSigner = new MsiRecordingSigner();
        await ExpectAsync<IOException>(() => new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = fixture.Cache,
            Signer = repeatedSigner
        }).BuildAsync(request), "same product version");
        Assert(repeatedSigner.Kinds.Count == 0, "A rejected same-version MSI invoked the signer.");
    }

    static async Task RemovesFailedSignedMsi()
    {
        using var fixture = new WixTestFixture();
        var signer = new MsiRecordingSigner { FailInstaller = true };
        await ExpectAsync<InvalidOperationException>(() => new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = fixture.Cache,
            Signer = signer
        }).BuildAsync(fixture.Request()), "signing failed");
        var output = Path.Combine(fixture.Root, "output");
        Assert(!Directory.EnumerateFiles(output, "*.msi").Any() &&
               !Directory.EnumerateFiles(output, "*.bundler-manifest").Any(),
            "An MSI with a failed final signature remained available for publishing.");
    }

    static async Task SignsRealMsiWithTestCertificate()
    {
        using var fixture = new WixTestFixture();
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
        }), certificate.Thumbprint);
        var artifact = (await new WixBundler(options: new WixBundlerOptions
        {
            ToolCacheDirectory = fixture.Cache,
            Signer = signer
        }).BuildAsync(fixture.Request())).Single();
        Assert(signer.Kinds.SequenceEqual([BundleSigningArtifactKind.PayloadExecutable,
            BundleSigningArtifactKind.Installer]) &&
            originalHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))) &&
            File.Exists(artifact.Path + ".bundler-manifest"),
            "Real MSI signing changed input files or missed a requested signature.");
    }

    static async Task ValidatesMsiBitmapInputs()
    {
        using var fixture = new WixTestFixture();
        var wrongSize = CreateBmp(Path.Combine(fixture.Root, "wrong.bmp"), 100, 100);
        var renamedText = Path.Combine(fixture.Root, "renamed.bmp");
        await File.WriteAllTextAsync(renamedText, "not a bitmap");
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
            var settings = new WixBundleConfiguration { BannerBitmap = banner, DialogBitmap = dialog };
            await ExpectAsync<ArgumentException>(() => new WixBundler(settings,
                new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request()), "bmp");
        }
        var valid = Path.Combine(fixture.Root, "valid-banner.bmp");
        CreateBmp(valid, 493, 58);
        var validDialog = Path.Combine(fixture.Root, "valid-dialog.bmp");
        CreateBmp(validDialog, 503, 314);
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            BannerBitmap = valid,
            DialogBitmap = validDialog
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request())).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert(database.RowCount("Dialog", "Dialog") > 0,
            "Supplied UI bitmaps did not enable the interactive MSI dialog set.");
    }

    static async Task BuildsFullFeaturedMsi()
    {
        using var fixture = new WixTestFixture();
        var license = Path.Combine(fixture.Root, "terms.rtf");
        await File.WriteAllTextAsync(license, "{\\rtf1\\ansi Example license terms.}");
        var settings = new WixBundleConfiguration
        {
            StartMenuShortcut = true,
            DesktopShortcut = true,
            InstallDirectorySelection = true,
            BannerBitmap = CreateBmp(Path.Combine(fixture.Root, "banner.bmp"), 493, 58),
            DialogBitmap = CreateBmp(Path.Combine(fixture.Root, "dialog.bmp"), 503, 314),
            AddToPath = true,
            UninstallShortcut = true,
            LaunchAfterInstall = true
        };
        var artifact = (await new WixBundler(settings,
            new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(licenseFile: license))).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        foreach (var dialog in new[] { "WelcomeDlg", "LicenseAgreementDlg", "InstallDirDlg", "BrowseDlg",
                     "VerifyReadyDlg", "ProgressDlg", "ExitDialog", "MaintenanceWelcomeDlg",
                     "MaintenanceTypeDlg", "ErrorDlg", "InvalidDirDlg" })
            Assert(database.Contains("Dialog", "Dialog", dialog), "The custom MSI UI is missing dialog " + dialog + ".");
        Assert(database.Contains("Feature", "Feature", "Complete") && database.Contains("Feature", "Feature", "Shortcuts") &&
               database.Contains("Feature", "Feature", "PathEnvironment") && database.Contains("Feature", "Feature", "UninstallShortcut"),
            "Optional MSI features must be declared as selectable features.");
        Assert(database.RowCount("Component", "Component") == 6,
            "One payload file, cleanup, and each optional feature need separate components.");
        Assert(database.RowCount("Shortcut", "Shortcut") == 3 &&
               database.ContainsSubstring("Shortcut", "Arguments", "[ProductCode]"),
            "MSI shortcuts or the managed uninstall entry are incorrect.");
        Assert(database.Contains("Environment", "Name", "=-PATH") &&
               database.Contains("Environment", "Value", "[~];[INSTALLFOLDER]"),
            "The MSI PATH feature must append only the product directory.");
        Assert(database.Contains("CustomAction", "Action", "BundlerInstallDirScope") &&
               database.Contains("InstallExecuteSequence", "Action", "BundlerInstallDirScope") &&
               database.ContainsSubstring("InstallExecuteSequence", "Condition", "LocalAppDataFolder"),
            "The MSI install-directory scope check is missing from the execute sequence.");
        Assert(database.Contains("CustomAction", "Action", "BundlerLaunchAfterInstall") &&
               database.ContainsSubstring("ControlEvent", "Argument", "BundlerLaunchAfterInstall") &&
               database.ContainsSubstring("ControlEvent", "Condition", "WIXUI_EXITDIALOGOPTIONALCHECKBOX"),
            "The interactive launch checkbox is not wired to the exit dialog.");
        Assert(database.Property("WIXUI_INSTALLDIR") == "INSTALLFOLDER" &&
               database.Property("WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT") == "Launch [ProductName]" &&
               database.Property("ARPNOMODIFY") == "1" &&
               database.Property("ARPCONTACT").Length > 0 &&
               database.Contains("CustomAction", "Source", "ARPINSTALLLOCATION") &&
               database.ContainsSubstring("CustomAction", "Target", "INSTALLFOLDER"),
            "MSI UI or uninstall metadata properties are missing.");
        Assert(database.Contains("AppSearch", "Property", "INSTALLFOLDER") &&
               database.Contains("Registry", "Name", "InstallDir"),
            "MSI must restore a user-chosen install directory during upgrades.");
        Assert(database.Contains("Binary", "Name", "WixUI_Bmp_Banner") &&
               database.Contains("Binary", "Name", "WixUI_Bmp_Dialog"),
            "MSI brand bitmaps were not embedded.");
    }

    static async Task BuildsCustomUiWithoutLicense()
    {
        using var fixture = new WixTestFixture();
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            InstallDirectorySelection = true,
            LaunchAfterInstall = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request())).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert(database.Contains("Dialog", "Dialog", "InstallDirDlg") &&
               database.Contains("Dialog", "Dialog", "WelcomeDlg") &&
               database.Contains("Dialog", "Dialog", "VerifyReadyDlg") &&
               database.Contains("Dialog", "Dialog", "ExitDialog") &&
               !database.Contains("Dialog", "Dialog", "LicenseAgreementDlg"),
            "A license-free MSI must use the custom dialog sequence without a license page.");
    }

    static async Task BuildsLicensedDirectorySelection()
    {
        using var fixture = new WixTestFixture();
        var license = Path.Combine(fixture.Root, "terms.rtf");
        await File.WriteAllTextAsync(license, "{\\rtf1\\ansi Example license terms.}");
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            InstallDirectorySelection = true,
            Languages = ["zh-CN"]
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(licenseFile: license))).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert(database.Contains("Dialog", "Dialog", "LicenseAgreementDlg") &&
               database.Contains("Dialog", "Dialog", "InstallDirDlg") &&
               database.ContainsSubstring("ControlEvent", "Condition", "LicenseAccepted") &&
               database.Property("ProductLanguage") == "2052",
            "A licensed MSI must keep the license page before directory selection.");
        Assert(database.Contains("Feature", "Feature", "Complete") &&
               !database.Contains("Feature", "Feature", "Shortcuts"),
            "Optional features must not appear unless configured.");
    }

    static async Task EnforcesMsiInstallDirectoryRoot()
    {
        using var fixture = new WixTestFixture();
        foreach (var (scope, architecture, expectedRoot) in new[]
        {
            (WixInstallScope.CurrentUser, "win-x64", "LocalAppDataFolder"),
            (WixInstallScope.CurrentUser, "win-x86", "LocalAppDataFolder"),
            (WixInstallScope.PerMachine, "win-x64", "ProgramFiles64Folder"),
            (WixInstallScope.PerMachine, "win-x86", "ProgramFilesFolder")
        })
        {
            var scoped = fixture.Request(runtimeIdentifier: architecture,
                outputDirectory: Path.Combine(fixture.Root, "scope-" + scope + "-" + architecture));
            var artifact = (await new WixBundler(new WixBundleConfiguration { InstallScope = scope },
                new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(scoped)).Single();
            using var database = new MsiDatabaseReader(artifact.Path);
            var conditions = database.Values("InstallExecuteSequence", "Condition");
            Assert(conditions.Any(condition => condition.Contains(expectedRoot, StringComparison.Ordinal) &&
                   condition.Contains("INSTALLFOLDER", StringComparison.Ordinal)),
                $"The {scope}/{architecture} MSI must reject directories outside {expectedRoot}.");
        }
    }

    static async Task GeneratesLaunchCheckboxOnly()
    {
        using var fixture = new WixTestFixture();
        var license = Path.Combine(fixture.Root, "terms.rtf");
        await File.WriteAllTextAsync(license, "{\\rtf1\\ansi Example license terms.}");
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            LaunchAfterInstall = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(fixture.Request(licenseFile: license))).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert(database.Contains("CustomAction", "Action", "BundlerLaunchAfterInstall") &&
               database.ContainsSubstring("ControlEvent", "Condition", "NOT Installed"),
            "The licensed minimal UI must expose the opt-in launch checkbox without auto-start.");
        var plain = (await fixture.Bundler().BuildAsync(
            fixture.Request(outputDirectory: Path.Combine(fixture.Root, "plain")))).Single();
        using var plainDatabase = new MsiDatabaseReader(plain.Path);
        Assert(!plainDatabase.Contains("CustomAction", "Action", "BundlerLaunchAfterInstall") &&
               !plainDatabase.Contains("_Tables", "Name", "Dialog"),
            "A default MSI must not add UI or launch actions.");
    }

    static async Task BuildsEveryDeclaredMsiLanguage()
    {
        using var fixture = new WixTestFixture();
        var cultures = WixLanguageInfo.Supported.Select(language => language.Culture).ToArray();
        var artifacts = await new WixBundler(new WixBundleConfiguration
        {
            Languages = cultures,
            StartMenuShortcut = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request());
        Assert(artifacts.Count == cultures.Length,
            $"Expected {cultures.Length} MSI artifacts, got {artifacts.Count}.");
        var names = artifacts.Select(artifact => Path.GetFileName(artifact.Path)).ToArray();
        Assert(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == artifacts.Count,
            "Per-language MSI outputs must have distinct file names.");
        Assert(names.Any(name => name.EndsWith("-ja-jp.msi", StringComparison.Ordinal)) &&
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
            Assert(japanese.Property("ProductLanguage") == "1041" &&
                   traditional.Property("ProductLanguage") == "1028" &&
                   albanian.Property("ProductLanguage") == "1052",
                "Per-language MSI product languages are incorrect.");
            var codes = artifacts.Select(artifact =>
            {
                using var database = new MsiDatabaseReader(artifact.Path);
                return database.Property("ProductCode") + "|" + database.Property("UpgradeCode");
            }).ToArray();
            Assert(codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == artifacts.Count,
                "Every language MSI must keep an isolated product identity.");
        }
    }

    static async Task AppliesCallerMsiLocaleOverrides()
    {
        using var fixture = new WixTestFixture();
        var locale = Path.Combine(fixture.Root, "override.wxl");
        await File.WriteAllTextAsync(locale,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<WixLocalization xmlns=\"http://schemas.microsoft.com/wix/2006/localization\" " +
            "Culture=\"ja-JP\" Codepage=\"932\">\n" +
            "  <String Id=\"BundlerLaunchCheckboxText\">&#x8D77;&#x52D5; [ProductName]</String>\n" +
            "  <String Id=\"BundlerShortcutsFeature\">&#x30B7;&#x30E7;&#x30FC;&#x30C8;&#x30AB;&#x30C3;&#x30C8;</String>\n" +
            "</WixLocalization>\n");
        var artifact = (await new WixBundler(new WixBundleConfiguration
        {
            Languages = ["ja-JP"],
            LocaleFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ja-JP"] = locale
            },
            LaunchAfterInstall = true,
            StartMenuShortcut = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request())).Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert(database.Property("WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT") == "起動 [ProductName]",
            "Caller locale overrides did not reach the MSI property.");
        Assert(database.Contains("Feature", "Title", "ショートカット"),
            "Caller locale overrides did not reach the MSI feature title.");
        var english = (await new WixBundler(new WixBundleConfiguration
        {
            LaunchAfterInstall = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(
            fixture.Request(outputDirectory: Path.Combine(fixture.Root, "en")))).Single();
        using var englishDatabase = new MsiDatabaseReader(english.Path);
        Assert(englishDatabase.Property("WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT") == "Launch [ProductName]",
            "Caller overrides for one language must not leak into another language MSI.");
    }

    static async Task RejectsInvalidMsiLocaleInputs()
    {
        using var fixture = new WixTestFixture();
        var wrongCulture = Path.Combine(fixture.Root, "wrong.wxl");
        await File.WriteAllTextAsync(wrongCulture,
            "<?xml version=\"1.0\"?><WixLocalization xmlns=\"http://schemas.microsoft.com/wix/2006/localization\"" +
            " Culture=\"de-DE\" Codepage=\"1252\"><String Id=\"BundlerShortcutsFeature\">x</String></WixLocalization>");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["ja-JP"],
            LocaleFiles = new Dictionary<string, string> { ["ja-JP"] = wrongCulture }
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request()),
            "does not match");
        var unencodable = Path.Combine(fixture.Root, "unencodable.wxl");
        await File.WriteAllTextAsync(unencodable,
            "<?xml version=\"1.0\"?><WixLocalization xmlns=\"http://schemas.microsoft.com/wix/2006/localization\"" +
            " Culture=\"ja-JP\" Codepage=\"932\"><String Id=\"BundlerShortcutsFeature\">&#x1F600;</String></WixLocalization>");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["ja-JP"],
            LocaleFiles = new Dictionary<string, string> { ["ja-JP"] = unencodable }
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request()),
            "code page");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["ja-JP"],
            LocaleFiles = new Dictionary<string, string> { ["fr-FR"] = wrongCulture }
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request()),
            "fr-FR");
        await ExpectAsync<ArgumentException>(() => new WixBundler(new WixBundleConfiguration
        {
            Languages = ["en-US", "en-us"]
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache }).BuildAsync(fixture.Request()),
            "duplicates");
    }

    static async Task BuildsFipsMsiWithIconShortcuts()
    {
        using var fixture = new WixTestFixture();
        var icon = Path.Combine(fixture.Root, "app.ico");
        await File.WriteAllBytesAsync(icon,
        [
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
            0x10, 0x10, 0x00, 0x00, 0x01, 0x00, 0x20, 0x00, 0x68, 0x04,
            0x00, 0x00, 0x16, 0x00, 0x00, 0x00,
            .. Enumerable.Repeat((byte)0xAB, 1128)
        ]);
        var badIcon = Path.Combine(fixture.Root, "bad.ico");
        await File.WriteAllTextAsync(badIcon, "not an icon");
        var configuration = fixture.Request();
        var iconsArtifact = await new WixBundler(new WixBundleConfiguration
        {
            FipsCompliant = true,
            StartMenuShortcut = true,
            DesktopShortcut = true,
            UninstallShortcut = true
        }, new WixBundlerOptions { ToolCacheDirectory = fixture.Cache })
            .BuildAsync(WithIcon(configuration, icon));
        var artifact = iconsArtifact.Single();
        using var database = new MsiDatabaseReader(artifact.Path);
        Assert(database.RowCount("Shortcut", "Shortcut") == 3 &&
               database.Values("Shortcut", "Icon_")
                   .Count(value => value == "ProductIcon") == 3,
            "The MSI icon was not wired into every shortcut.");
        await ExpectAsync<InvalidDataException>(() => new WixBundler()
            .BuildAsync(WithIcon(fixture.Request(outputDirectory: Path.Combine(fixture.Root, "bad")), badIcon)),
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

    private sealed class MsiVerifyingSigner(IBundleSigner inner, string thumbprint) : IBundleSigner
    {
        public List<BundleSigningArtifactKind> Kinds { get; } = [];

        public async Task SignAsync(BundleSigningRequest request, CancellationToken cancellationToken = default)
        {
            await inner.SignAsync(request, cancellationToken);
            using var signed = new X509Certificate2(X509Certificate.CreateFromSignedFile(request.Path));
            Assert(signed.Thumbprint == thumbprint, "A signed MSI artifact has the wrong certificate.");
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

    private static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private static Task RunSync(Action action) { action(); return Task.CompletedTask; }
    private static BundleConfiguration ValidConfiguration(BundleTargetConfiguration target) => new()
    {
        ProductName = "ExampleApp", Identifier = "com.example.app", Version = "1.0.0",
        OutputDirectory = "artifacts", Targets = [target]
    };
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Msi.Tests", Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Root, "input");
        public string Cache => Path.Combine(Root, "cache");

        public WixTestFixture()
        {
            Directory.CreateDirectory(Input);
            File.WriteAllText(Path.Combine(Input, "fixture.exe"), "original payload");
            File.WriteAllText(Path.Combine(Root, "resource.txt"), "resource payload");
        }

        public WixBundler Bundler() => new(options: new WixBundlerOptions { ToolCacheDirectory = Cache });

        public BundleConfiguration Request(string? resourceTarget = null, string? licenseFile = null,
            IReadOnlyList<string>? signingFiles = null, string? outputDirectory = null,
            string? runtimeIdentifier = null) => new()
        {
            ProductName = "WiX test fixture",
            Identifier = "com.example.wixtestfixture",
            Version = "1.0.0",
            LicenseFile = licenseFile,
            OutputDirectory = outputDirectory ?? Path.Combine(Root, "output"),
            Resources = resourceTarget is null ? [] :
                [new BundleResourceConfiguration { Source = Path.Combine(Root, "resource.txt"), TargetPath = resourceTarget }],
            Targets = [new BundleTargetConfiguration
            {
                RuntimeIdentifier = runtimeIdentifier ?? "win-x64", InputDirectory = Input,
                MainExecutable = "fixture.exe", Formats = [PackageFormat.Msi],
                SigningFiles = signingFiles ?? []
            }]
        };

        public void Dispose()
        {
            var link = Path.Combine(Input, "linked-outside");
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link);
            DeleteOwnedTestDirectory(Root);
        }
    }

    private static void DeleteOwnedTestDirectory(string path)
    {
        var ownedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Msi.Tests"));
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(ownedParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Refusing to delete an unowned or redirected WiX test path: " + full);
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
