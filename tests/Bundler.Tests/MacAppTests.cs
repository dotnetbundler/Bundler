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
            yield return ("Rejects features owned by later MAC stages", RejectsLaterStageFeatures);
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

    static async Task RejectsLaterStageFeatures()
    {
        var input = CreateInputDirectory();
        try
        {
            await AssertThrows<NotSupportedException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(
                    input,
                    fileAssociations: [new BundleFileAssociationConfiguration { Extensions = ["hello"] }])),
                "File associations must be rejected until MAC-APP-2.");
            await AssertThrows<NotSupportedException>(
                () => new MacAppBundler().BuildAsync(MacConfiguration(
                    input,
                    urlProtocols: [new BundleUrlProtocolConfiguration { Schemes = ["hello"] }])),
                "URL schemes must be rejected until MAC-APP-2.");
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
                     "MacAppMinimumSystemVersion", "MacAppCategory", "MacAppIconName"
                 })
        {
            Assert(targets.Contains(property + "=\"$(Bundler" + property + ")\"", StringComparison.Ordinal),
                $"MSBuild does not map Bundler{property} to the task.");
        }
        Assert(targets.Contains("MacContents=\"@(BundlerMacContent)\"", StringComparison.Ordinal) &&
               targets.Contains("MacFrameworks=\"@(BundlerMacFramework)\"", StringComparison.Ordinal),
            "MSBuild does not map BundlerMacContent/BundlerMacFramework item groups.");
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
            var artifacts = await new MacAppBundler().BuildAsync(MacConfiguration(input, output));
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
            Assert(process.ExitCode == 0, $"plutil -lint failed: {process.StandardError.ReadToEnd()}");
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

    static byte[] FakeMachO()
    {
        var bytes = new byte[64];
        bytes[0] = 0xCF; bytes[1] = 0xFA; bytes[2] = 0xED; bytes[3] = 0xFE;
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
