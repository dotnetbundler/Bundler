using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Signing.Windows;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

if (args.Length >= 5 && args[0] == "--external-sign-fixture")
{
    await File.AppendAllTextAsync(args[4], string.Join("|", args[1], args[2], args[3]) + Environment.NewLine);
    if (args.Length > 5)
    {
        Console.Error.WriteLine(args[5]);
        return 17;
    }
    return 0;
}

var tests = new (string Name, Func<Task> Test)[]
{
    ("Parses supported desktop RIDs", () => RunSync(ParsesSupportedDesktopRids)),
    ("Rejects incompatible formats", () => RunSync(RejectsIncompatibleFormats)),
    ("Adds app dependency before DMG", () => RunSync(AddsAppDependencyBeforeDmg)),
    ("Rejects executable paths outside input", () => RunSync(RejectsExecutablePathEscape)),
    ("Verifies and extracts bundled NSIS", VerifiesAndExtractsBundledNsis),
    ("Recovers and serializes the tool cache", RecoversAndSerializesToolCache),
    ("Recovers and serializes embedded NSIS resources", RecoversAndSerializesEmbeddedNsisResources),
    ("Rejects unsafe tool archives", RejectsUnsafeToolArchives),
    ("Rejects reparse points in NSIS payloads", () => RunSync(RejectsReparsePointsInNsisPayloads)),
    ("Selects every bundled NSIS host compiler", () => RunSync(SelectsEveryBundledNsisHostCompiler)),
    ("Compares semantic versions for installer policy", () => RunSync(ComparesSemanticVersionsForInstallerPolicy)),
    ("Rejects invalid NSIS package versions", RejectsInvalidNsisPackageVersions),
    ("Rejects invalid legacy MSI identifiers", RejectsInvalidLegacyMsiIdentifiers),
    ("Rejects invalid associations and protocols", () => RunSync(RejectsInvalidAssociationsAndProtocols)),
    ("Loads complete generic bundle configuration", LoadsCompleteGenericBundleConfiguration),
    ("Builds through the standalone NSIS API", BuildsThroughStandaloneNsisApi),
    ("Builds every built-in NSIS language", BuildsEveryBuiltInNsisLanguage),
    ("Validates custom NSIS language files", ValidatesCustomNsisLanguageFiles),
    ("Builds every NSIS compression mode", BuildsEveryNsisCompressionMode),
    ("Signs both NSIS installer artifacts", SignsBothNsisInstallerArtifacts),
    ("Runs an external Windows signing provider safely", RunsExternalWindowsSigningProviderSafely),
    ("Removes a failed signed installer", RemovesFailedSignedInstaller),
    ("Rejects payload signing files without a signer", RejectsPayloadSigningFilesWithoutSigner),
    ("Writes a valid Windows uninstall command", () => RunSync(WritesValidWindowsUninstallCommand)),
    ("Lets users choose and restore the install directory", () => RunSync(LetsUsersChooseInstallDirectory)),
    ("Uninstalls only packaged payload files", () => RunSync(UninstallsOnlyPackagedPayloadFiles)),
    ("Provides interactive NSIS safety options", () => RunSync(ProvidesInteractiveNsisSafetyOptions)),
    ("Pins install recovery to the installer manifest", () => RunSync(PinsInstallRecoveryToInstallerManifest)),
    ("Renders the NSIS automation protocol", () => RunSync(RendersNsisAutomationProtocol)),
    ("Renders existing-version policy", () => RunSync(RendersExistingVersionPolicy)),
    ("Renders safe file and URL registrations", () => RunSync(RendersSafeFileAndUrlRegistrations)),
    ("Renders NSIS install scopes", () => RunSync(RendersNsisInstallScopes)),
    ("Renders every NSIS compression mode", () => RunSync(RendersEveryNsisCompressionMode)),
    ("Renders NSIS metadata, icons, and resources", () => RunSync(RendersNsisMetadataIconsAndResources)),
    ("Renders complete shortcut configuration", () => RunSync(RendersCompleteShortcutConfiguration)),
    ("Rejects unsafe shortcut configuration", RejectsUnsafeShortcutConfiguration),
    ("Maps NSIS settings through MSBuild", () => RunSync(MapsNsisSettingsThroughMsBuild)),
    ("Rejects unknown template variables", () => RunSync(RejectsUnknownTemplateVariables)),
    ("Runs backends through the common pipeline", RunsBackendsThroughCommonPipeline),
    ("Preflights every requested backend", PreflightsEveryRequestedBackend),
    ("Signs a PE file without the Windows SDK", SignsPeFileWithoutWindowsSdk)
};

tests = tests.Append(("Keeps package consumer versions aligned", () => RunSync(KeepsPackageConsumerVersionsAligned)))
    .Concat(WixTests.Cases).Concat(MacAppTests.Cases).ToArray();

var failed = 0;
foreach (var (name, test) in tests)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ParsesSupportedDesktopRids()
{
    string[] rids = ["win-x86", "win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64"];
    Assert(rids.All(rid => BundleTarget.TryParse(rid, out _)), "One or more supported RIDs failed to parse.");
    Assert(BundleTarget.TryParse("win-x86", out var x86) && x86!.Architecture == CpuArchitecture.X86,
        "Windows x86 must be a distinct public target architecture.");
    Assert(!BundleTarget.TryParse("android-arm64", out _), "A mobile RID was accepted.");
}

static void RejectsIncompatibleFormats()
{
    var configuration = ValidConfiguration(new BundleTargetConfiguration
    {
        RuntimeIdentifier = "linux-x64",
        InputDirectory = "unused",
        Formats = [PackageFormat.Msi]
    });

    var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
    Assert(issues.Any(issue => issue.Message.Contains("not supported", StringComparison.Ordinal)),
        "Linux MSI should have failed validation.");
    var x86Nsis = ValidConfiguration(new BundleTargetConfiguration
    {
        RuntimeIdentifier = "win-x86", InputDirectory = "unused", Formats = [PackageFormat.Nsis]
    });
    Assert(BundleConfigurationValidator.Validate(x86Nsis, checkFileSystem: false)
            .Any(issue => issue.Path == "targets[0].formats"),
        "Adding x86 to the shared model must not enable NSIS x86.");
    var x86Msi = ValidConfiguration(new BundleTargetConfiguration
    {
        RuntimeIdentifier = "win-x86", InputDirectory = "unused", Formats = [PackageFormat.Msi]
    });
    Assert(!BundleConfigurationValidator.Validate(x86Msi, checkFileSystem: false)
            .Any(issue => issue.Path == "targets[0].formats"),
        "The MSI backend must accept the Windows x86 target.");
}

static void AddsAppDependencyBeforeDmg()
{
    var configuration = ValidConfiguration(new BundleTargetConfiguration
    {
        RuntimeIdentifier = "osx-arm64",
        InputDirectory = "unused",
        Formats = [PackageFormat.Dmg]
    });

    var plan = BundlePlanner.Create(configuration, checkFileSystem: false);
    Assert(plan.Items.Count == 2, "DMG plan should contain an app and a DMG step.");
    Assert(plan.Items[0].Format == PackageFormat.App && plan.Items[0].Intermediate,
        "The intermediate app step must be first.");
    Assert(plan.Items[1].Format == PackageFormat.Dmg && !plan.Items[1].Intermediate,
        "The requested DMG step must be last.");
}

static void RejectsExecutablePathEscape()
{
    var configuration = ValidConfiguration(new BundleTargetConfiguration
    {
        RuntimeIdentifier = "win-x64",
        InputDirectory = "unused",
        MainExecutable = "../Other.exe",
        SigningFiles = ["../Other.dll"],
        Formats = [PackageFormat.Nsis]
    });

    var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
    Assert(issues.Any(issue => issue.Path.EndsWith("mainExecutable", StringComparison.Ordinal)),
        "An executable outside inputDirectory should have failed validation.");
    Assert(issues.Any(issue => issue.Path.Contains("signingFiles", StringComparison.Ordinal)),
        "A signing file outside inputDirectory should have failed validation.");
}

static async Task VerifiesAndExtractsBundledNsis()
{
    var repositoryRoot = RepositoryRoot();
    var archive = Path.Combine(repositoryRoot, "third_party", "nsis", "nsis-toolset-3.12-r1.zip");
    var cache = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
        {
            var entries = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (var required in new[]
            {
                "common/nsisconf.nsh",
                "hosts/win-x86/makensis.exe",
                "hosts/linux-x64/makensis",
                "hosts/linux-arm64/makensis",
                "hosts/osx-x64/makensis",
                "hosts/osx-arm64/makensis"
            })
            {
                Assert(entries.Contains(required), $"The NSIS toolset archive is missing '{required}'.");
            }

            var copyingEntry = zip.GetEntry("common/COPYING") ??
                throw new InvalidDataException("The NSIS toolset archive is missing common/COPYING.");
            using var copyingStream = copyingEntry.Open();
            var archiveLicenseHash = Convert.ToHexString(SHA256.HashData(copyingStream));
            var packagedLicenseHash = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(repositoryRoot, "third_party", "nsis", "COPYING"))));
            Assert(archiveLicenseHash == packagedLicenseHash,
                "The separately packaged NSIS license must match common/COPYING in NsisToolset.");
        }


        var pluginPath = Path.Combine(
            repositoryRoot,
            "third_party", "nsis", "plugins", "x86-unicode", "DotNetBundlerNsis.dll");
        Assert(File.Exists(pluginPath), "The bundled NSIS plug-in is missing.");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath))) ==
               "AFD65CB6BAA3C931CF9DEA4A9AB41579EFA4D23774C71CE5707E76E3AC1ADC9D",
            "The bundled NSIS plug-in checksum changed; rebuild and update its provenance.");

        var toolset = await NsisToolResolver.ResolveAsync(archive, cache);
        Assert(File.Exists(toolset.CompilerPath), "The verified NSIS toolset did not produce the host compiler.");
        Assert(toolset.DataDirectory is not null && Directory.Exists(toolset.DataDirectory),
            "The verified NSIS toolset did not produce its common data directory.");
    }
    finally
    {
        if (Directory.Exists(cache))
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}

static void WritesValidWindowsUninstallCommand()
{
    var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(input);
    try
    {
        File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "test");
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = input,
            MainExecutable = "ExampleApp.exe",
            Formats = [PackageFormat.Nsis]
        });
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            input,
            "ExampleApp.exe",
            "output",
            false);

        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var script = NsisBundleBackend.CreateScript(template, configuration, new NsisBundleConfiguration(), item, "setup.exe", "ExampleApp");
        Assert(script.Contains("\"UninstallString\" '\"$INSTDIR\\Uninstall.exe\"'", StringComparison.Ordinal),
            "UninstallString must contain ordinary quotes around the executable path.");
        Assert(!script.Contains("'$\"$INSTDIR", StringComparison.Ordinal),
            "UninstallString must not write NSIS escape markers into the registry.");
    }
    finally
    {
        Directory.Delete(input, recursive: true);
    }
}

static void RejectsUnknownTemplateVariables()
{
    try
    {
        TemplateRenderer.Render("{{known}} {{missing}}", new Dictionary<string, string> { ["known"] = "value" });
        throw new InvalidOperationException("An unknown template variable should have failed rendering.");
    }
    catch (InvalidDataException exception)
    {
        Assert(exception.Message.Contains("missing", StringComparison.Ordinal),
            "The template error should identify the missing variable.");
    }
}

static void LetsUsersChooseInstallDirectory()
{
    var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
    Assert(template.Contains("!insertmacro MUI_PAGE_DIRECTORY", StringComparison.Ordinal),
        "The installer must display the NSIS directory selection page.");
    Assert(template.Contains("ReadRegStr $0 SHCTX \"${UNINSTALL_KEY}\" \"InstallLocation\"", StringComparison.Ordinal) &&
           template.Contains("MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_VALUENAME \"InstallLocation\"", StringComparison.Ordinal) &&
           template.Contains("${If} $INSTDIR == \"placeholder\\${INSTALL_FOLDER}\"", StringComparison.Ordinal),
        "Fixed and selectable install scopes should restore their previously selected install directories.");
}

static async Task RecoversAndSerializesToolCache()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "DotNet.Bundler.Cache.Tests",
        "Unicode-工具缓存",
        new string('a', 70),
        new string('b', 70),
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var archivePath = Path.Combine(root, "tool.zip");
    try
    {
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "bin/tool.exe", "trusted executable");
            WriteZipEntry(zip, "data/说明.txt", "trusted data");
        }
        var archive = new ZipToolArchive(
            "fixture",
            "1.0.0",
            HashFile(archivePath),
            "bin/tool.exe",
            ["data/说明.txt"]);
        var cache = Path.Combine(root, "共享缓存");
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => ZipToolCache.ResolveToolAsync(archivePath, cache, archive)));
        Assert(results.Select(result => result.DirectoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
            "Concurrent tool resolution did not converge on one cache entry.");

        var executable = results[0].ExecutablePath;
        await File.WriteAllTextAsync(executable, "corrupted");
        await File.WriteAllTextAsync(Path.Combine(results[0].DirectoryPath, "unexpected.txt"), "corrupted");
        var repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive);
        Assert(await File.ReadAllTextAsync(repaired.ExecutablePath) == "trusted executable",
            "A modified cached executable was not restored from the trusted archive.");
        Assert(!File.Exists(Path.Combine(repaired.DirectoryPath, "unexpected.txt")),
            "Unexpected cache content was not removed during recovery.");

        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var link = Path.Combine(repaired.DirectoryPath, "linked-outside");
        Directory.CreateSymbolicLink(link, outside);
        repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive);
        Assert(File.Exists(sentinel) && !Directory.Exists(link),
            "Cache recovery followed or retained a directory reparse point.");
        Directory.Delete(repaired.DirectoryPath, recursive: true);
        await File.WriteAllTextAsync(repaired.DirectoryPath, "directory replaced by a file");
        repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive);
        Assert(Directory.Exists(repaired.DirectoryPath) && await File.ReadAllTextAsync(repaired.ExecutablePath) == "trusted executable",
            "A cache directory replaced by a file was not rebuilt.");

        var linkedCache = Path.Combine(root, "linked-cache");
        Directory.CreateSymbolicLink(linkedCache, outside);
        try
        {
            await ZipToolCache.ResolveToolAsync(archivePath, linkedCache, archive);
            throw new InvalidOperationException("A reparse-point tool cache root was accepted.");
        }
        catch (InvalidDataException)
        {
        }
        Directory.Delete(linkedCache);
        Assert(File.Exists(sentinel), "Tool cache root validation followed a reparse point.");
        Assert(File.Exists(Path.Combine(repaired.DirectoryPath, ".bundler-tool-manifest")),
            "A verified cache manifest was not persisted.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task RejectsUnsafeToolArchives()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.UnsafeZip.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        foreach (var (name, configure) in new (string Name, Action<ZipArchive> Configure)[]
        {
            ("traversal", zip => WriteZipEntry(zip, "../escaped.exe", "escape")),
            ("symlink", zip =>
            {
                var entry = zip.CreateEntry("tool-link");
                entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write("outside");
            })
        })
        {
            var archivePath = Path.Combine(root, name + ".zip");
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                configure(zip);
            }
            var descriptor = new ZipToolArchive(name, "1", HashFile(archivePath),
                name == "traversal" ? "escaped.exe" : "tool-link");
            try
            {
                await ZipToolCache.ResolveToolAsync(archivePath, Path.Combine(root, "cache"), descriptor);
                throw new InvalidOperationException($"Unsafe {name} archive was accepted.");
            }
            catch (InvalidDataException)
            {
            }
        }
        Assert(!File.Exists(Path.Combine(root, "escaped.exe")),
            "Archive traversal wrote outside the cache staging directory.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RecoversAndSerializesEmbeddedNsisResources()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Resource.Tests", "资源", Guid.NewGuid().ToString("N"));
    try
    {
        var resources = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => NsisEmbeddedResources.MaterializeAsync(root, CancellationToken.None)));
        Assert(resources.Select(item => item.TemplatePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
            "Concurrent embedded resource materialization did not converge on one immutable path.");
        var expected = await File.ReadAllTextAsync(resources[0].TemplatePath);
        await File.WriteAllTextAsync(resources[0].TemplatePath, "corrupted");
        var repaired = await NsisEmbeddedResources.MaterializeAsync(root, CancellationToken.None);
        Assert(await File.ReadAllTextAsync(repaired.TemplatePath) == expected,
            "A corrupted embedded NSIS resource was not restored.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void RejectsReparsePointsInNsisPayloads()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Reparse.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "input");
    var outside = Path.Combine(root, "outside");
    Directory.CreateDirectory(input);
    Directory.CreateDirectory(outside);
    try
    {
        File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "fixture");
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "must not package");
        Directory.CreateSymbolicLink(Path.Combine(input, "linked-outside"), outside);
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = input,
            MainExecutable = "ExampleApp.exe",
            Formats = [PackageFormat.Nsis]
        });
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            input,
            "ExampleApp.exe",
            Path.Combine(root, "output"),
            false);
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        try
        {
            NsisBundleBackend.CreateScript(
                template,
                configuration,
                new NsisBundleConfiguration(),
                item,
                Path.Combine(root, "setup.exe"),
                "ExampleApp");
            throw new InvalidOperationException("An NSIS payload directory reparse point was accepted.");
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void WriteZipEntry(ZipArchive archive, string path, string contents)
{
    var entry = archive.CreateEntry(path);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(contents);
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static async Task SignsPeFileWithoutWindowsSdk()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test process path is available.");
        var target = Path.Combine(root, "signed-test.exe");
        File.Copy(executable, target);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=DotNet.Bundler test certificate",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.3") },
            critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.Now.AddMinutes(-5),
            DateTimeOffset.Now.AddDays(1));
        const string password = "integration-only-password";
        var pfx = Path.Combine(root, "test-signing.pfx");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));

        var signer = new WindowsAuthenticodeSigner(new WindowsAuthenticodeSigningOptions
        {
            PfxFile = pfx,
            PfxPassword = password
        });
        await signer.SignAsync(new BundleSigningRequest(
            target,
            BundleSigningArtifactKind.Installer,
            "Signing test",
            "win-x64"));

        Assert(SignedFileCertificates.EmbeddedSignatureContains(target, certificate),
            "The signed PE file did not contain the expected test certificate.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void SelectsEveryBundledNsisHostCompiler()
{
    Assert(NsisToolResolver.GetCompilerRelativePath(OSPlatform.Windows, Architecture.X64)
            .Replace('\\', '/') == "hosts/win-x86/makensis.exe",
        "Windows hosts should use the portable x86 NSIS compiler.");
    Assert(NsisToolResolver.GetCompilerRelativePath(OSPlatform.Linux, Architecture.X64)
            .Replace('\\', '/') == "hosts/linux-x64/makensis",
        "Linux x64 host compiler selection failed.");
    Assert(NsisToolResolver.GetCompilerRelativePath(OSPlatform.Linux, Architecture.Arm64)
            .Replace('\\', '/') == "hosts/linux-arm64/makensis",
        "Linux arm64 host compiler selection failed.");
    Assert(NsisToolResolver.GetCompilerRelativePath(OSPlatform.OSX, Architecture.X64)
            .Replace('\\', '/') == "hosts/osx-x64/makensis",
        "macOS x64 host compiler selection failed.");
    Assert(NsisToolResolver.GetCompilerRelativePath(OSPlatform.OSX, Architecture.Arm64)
            .Replace('\\', '/') == "hosts/osx-arm64/makensis",
        "macOS arm64 host compiler selection failed.");
}

static void ComparesSemanticVersionsForInstallerPolicy()
{
    static int Compare(string left, string right)
    {
        Assert(SemanticVersion.TryParse(left, out var leftVersion), $"Could not parse '{left}'.");
        Assert(SemanticVersion.TryParse(right, out var rightVersion), $"Could not parse '{right}'.");
        return Math.Sign(leftVersion!.CompareTo(rightVersion));
    }

    Assert(Compare("1.0.0", "1.0.0") == 0, "Equal releases should compare equal.");
    Assert(Compare("1.0.0+build.2", "1.0.0+build.1") == 0,
        "Build metadata must not affect precedence.");
    Assert(Compare("1.0.0", "1.0.0-rc.1") > 0, "A release must be newer than its prerelease.");
    Assert(Compare("1.0.0-beta.11", "1.0.0-beta.2") > 0,
        "Numeric prerelease identifiers must compare numerically.");
    Assert(Compare("2.0.0-alpha", "10.0.0-alpha") < 0,
        "Core numeric identifiers must compare numerically.");
    Assert(!SemanticVersion.TryParse("1.0", out _), "SemVer requires major, minor, and patch.");
    Assert(!SemanticVersion.TryParse("1.0.0-01", out _),
        "Numeric prerelease identifiers must reject leading zeroes.");
    Assert(SemanticVersion.TryParse("65536.0.0", out var oversized) &&
           !oversized!.TryGetWindowsNumericVersion(out _),
        "Windows version resources must reject components above 65535.");
}

static Task RejectsInvalidNsisPackageVersions()
{
    foreach (var version in new[] { "1.0", "1.0.0-01", "65536.0.0" })
    {
        try
        {
            new NsisBundler().BuildAsync(new BundleConfiguration { Version = version })
                .GetAwaiter().GetResult();
            throw new InvalidOperationException($"Invalid NSIS version '{version}' was accepted.");
        }
        catch (ArgumentException exception)
        {
            Assert(exception.Message.Contains(version, StringComparison.Ordinal),
                "The version validation error should identify the rejected value.");
        }
    }

    return Task.CompletedTask;
}

static async Task RejectsInvalidLegacyMsiIdentifiers()
{
    var bundler = new NsisBundler(new NsisBundleConfiguration
    {
        LegacyMsiProductCodes = ["not-a-guid"]
    });
    try
    {
        await bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" });
        throw new InvalidOperationException("An invalid legacy MSI product code was accepted.");
    }
    catch (ArgumentException exception)
    {
        Assert(exception.Message.Contains("not-a-guid", StringComparison.Ordinal),
            "The validation error should identify the invalid MSI GUID.");
    }

    bundler = new NsisBundler(new NsisBundleConfiguration
    {
        LegacyMsiUpgradeCodes = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid().ToString()).ToArray()
    });
    try
    {
        await bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" });
        throw new InvalidOperationException("An oversized legacy MSI identifier list was accepted.");
    }
    catch (ArgumentException exception)
    {
        Assert(exception.Message.Contains("string limit", StringComparison.Ordinal),
            "The validation error should explain the NSIS runtime string limit.");
    }
}

static void RejectsInvalidAssociationsAndProtocols()
{
    var target = new BundleTargetConfiguration
    {
        RuntimeIdentifier = "win-x64",
        InputDirectory = "unused",
        Formats = [PackageFormat.Nsis]
    };
    var configuration = new BundleConfiguration
    {
        ProductName = "ExampleApp",
        Identifier = "com.example.app",
        Version = "1.0.0",
        OutputDirectory = "artifacts",
        FileAssociations =
        [
            new BundleFileAssociationConfiguration { Extensions = [".safe", "bad\\key"] },
            new BundleFileAssociationConfiguration { Extensions = ["SAFE"] }
        ],
        UrlProtocols =
        [
            new BundleUrlProtocolConfiguration { Schemes = ["1invalid"] },
            new BundleUrlProtocolConfiguration { Schemes = ["example", "EXAMPLE"] }
        ],
        Targets = [target]
    };

    var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
    Assert(issues.Any(issue => issue.Path.Contains("fileAssociations", StringComparison.Ordinal) &&
                               issue.Message.Contains("Duplicate", StringComparison.Ordinal)),
        "File extensions should be unique without regard to case or an optional leading dot.");
    Assert(issues.Any(issue => issue.Path.Contains("fileAssociations", StringComparison.Ordinal) &&
                               issue.Message.Contains("1-64", StringComparison.Ordinal)),
        "Unsafe file-extension registry paths should be rejected.");
    Assert(issues.Any(issue => issue.Path.Contains("urlProtocols", StringComparison.Ordinal) &&
                               issue.Message.Contains("beginning with a letter", StringComparison.Ordinal)),
        "URL schemes should follow URI scheme syntax.");
    Assert(issues.Any(issue => issue.Path.Contains("urlProtocols", StringComparison.Ordinal) &&
                               issue.Message.Contains("Duplicate", StringComparison.Ordinal)),
        "URL schemes should be unique without regard to case.");
}

static async Task LoadsCompleteGenericBundleConfiguration()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Configuration.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "bundle.json");
    try
    {
        await File.WriteAllTextAsync(path, """
            {
              "productName": "Configured App",
              "identifier": "com.example.configured",
              "version": "1.0.0",
              "outputDirectory": "artifacts",
              "fileAssociations": [
                {
                  "extensions": ["configured"],
                  "name": "Configured document",
                  "description": "Configured document type",
                  "mimeType": "application/x-configured"
                }
              ],
              "urlProtocols": [
                {
                  "schemes": ["configured-app"],
                  "name": "Configured link"
                }
              ],
              "targets": [
                {
                  "runtimeIdentifier": "win-x64",
                  "inputDirectory": "publish",
                  "mainExecutable": "Configured.exe",
                  "signingFiles": ["Helper.dll"],
                  "formats": ["nsis"]
                }
              ]
            }
            """);

        var configuration = await BundleConfigurationLoader.LoadAsync(path);
        Assert(configuration.FileAssociations.Count == 1 &&
               configuration.FileAssociations[0].Extensions.SequenceEqual(["configured"]) &&
               configuration.FileAssociations[0].MimeType == "application/x-configured",
            "The configuration loader dropped file-association metadata.");
        Assert(configuration.UrlProtocols.Count == 1 &&
               configuration.UrlProtocols[0].Schemes.SequenceEqual(["configured-app"]) &&
               configuration.UrlProtocols[0].Name == "Configured link",
            "The configuration loader dropped URL-protocol metadata.");
        Assert(configuration.Targets[0].InputDirectory == Path.Combine(root, "publish"),
            "The configuration loader did not resolve target paths relative to the configuration file.");
        Assert(configuration.Targets[0].SigningFiles.SequenceEqual(["Helper.dll"]),
            "The configuration loader dropped target signing files.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task BuildsThroughStandaloneNsisApi()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Api.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "standalone-api-test");
    try
    {
        var configuration = new BundleConfiguration
        {
            ProductName = "Standalone API App",
            Identifier = "com.example.standalone",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = input + Path.DirectorySeparatorChar,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        };
        var bundler = new NsisBundler(
            new NsisBundleConfiguration { Languages = ["English", "SimpChinese"] },
            new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") });

        var artifacts = await bundler.BuildAsync(configuration);
        Assert(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
            "The standalone NSIS API should materialize embedded resources and create an installer.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task BuildsEveryBuiltInNsisLanguage()
{
    string[] expectedLanguages =
    [
        "Arabic", "Bulgarian", "Dutch", "English", "French", "German", "Italian", "Japanese",
        "Korean", "Norwegian", "Persian", "Portuguese", "PortugueseBR", "Russian", "SimpChinese",
        "Spanish", "SpanishInternational", "Swedish", "TradChinese", "Turkish", "Ukrainian", "Vietnamese"
    ];
    Assert(NsisBundler.SupportedLanguages.SequenceEqual(expectedLanguages),
        "The public NSIS language catalog does not match the supported Tauri language set.");

    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Language.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "发布内容");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "示例应用.exe"), "language-test");
    try
    {
        var bundler = new NsisBundler(
            new NsisBundleConfiguration
            {
                Languages = expectedLanguages,
                DisplayLanguageSelector = true
            },
            new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") });
        var artifacts = await bundler.BuildAsync(new BundleConfiguration
        {
            ProductName = "多语言 اختبار برنامه",
            Identifier = "com.example.languages",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "安装程序"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = input,
                    MainExecutable = "示例应用.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        });
        Assert(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
            "NSIS did not compile one installer containing every built-in language.");

        try
        {
            await new NsisBundler(new NsisBundleConfiguration { Languages = ["English", "english"] })
                .BuildAsync(new BundleConfiguration { Version = "1.0.0" });
            throw new InvalidOperationException("A duplicate NSIS language was accepted.");
        }
        catch (ArgumentException exception) when (exception.Message.Contains("more than once", StringComparison.Ordinal))
        {
        }

        try
        {
            await new NsisBundler(new NsisBundleConfiguration { Languages = ["Klingon"] })
                .BuildAsync(new BundleConfiguration { Version = "1.0.0" });
            throw new InvalidOperationException("An unsupported NSIS language was accepted.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Unsupported NSIS language", StringComparison.Ordinal))
        {
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task ValidatesCustomNsisLanguageFiles()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.CustomLanguage.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "custom-language-test");
    var english = await File.ReadAllTextAsync(
        Path.Combine(RepositoryRoot(), "templates", "nsis", "languages", "English.nsh"));
    try
    {
        var cases = new Dictionary<string, string>
        {
            ["missing"] = string.Join(Environment.NewLine, english.Split(["\r\n", "\n"], StringSplitOptions.None).Skip(1)),
            ["duplicate"] = english + Environment.NewLine + english.Split(["\r\n", "\n"], StringSplitOptions.None)[0],
            ["unknown"] = english.Replace("ShortcutPageTitle", "UnexpectedMessage", StringComparison.Ordinal),
            ["wrong-language"] = english.Replace("${LANG_ENGLISH}", "${LANG_GERMAN}", StringComparison.Ordinal)
        };
        foreach (var (name, content) in cases)
        {
            var customFile = Path.Combine(root, name + ".nsh");
            await File.WriteAllTextAsync(customFile, content);
            var bundler = new NsisBundler(
                new NsisBundleConfiguration
                {
                    Languages = ["English"],
                    CustomLanguageFiles = new Dictionary<string, string> { ["english"] = customFile }
                },
                new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") });
            try
            {
                await bundler.BuildAsync(LanguageTestConfiguration(input, Path.Combine(root, "artifacts", name)));
                throw new InvalidOperationException($"The invalid custom language case '{name}' was accepted.");
            }
            catch (InvalidDataException)
            {
            }
        }

        var validCustomFile = Path.Combine(root, "complete.nsh");
        await File.WriteAllTextAsync(
            validCustomFile,
            english.Replace("Shortcut options", "Custom shortcut options", StringComparison.Ordinal));
        var validArtifacts = await new NsisBundler(
            new NsisBundleConfiguration
            {
                Languages = ["english"],
                CustomLanguageFiles = new Dictionary<string, string> { ["English"] = validCustomFile }
            },
            new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") })
            .BuildAsync(LanguageTestConfiguration(input, Path.Combine(root, "artifacts", "valid")));
        Assert(validArtifacts.Count == 1 && File.Exists(validArtifacts[0].Path),
            "A complete custom language file did not override and compile in place of the built-in file.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static BundleConfiguration LanguageTestConfiguration(string input, string output) => new()
{
    ProductName = "Custom Language App",
    Identifier = "com.example.custom-language",
    Version = "1.0.0",
    OutputDirectory = output,
    Targets =
    [
        new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = input,
            MainExecutable = "ExampleApp.exe",
            Formats = [PackageFormat.Nsis]
        }
    ]
};

static async Task BuildsEveryNsisCompressionMode()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Compression.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "compression-test");
    try
    {
        foreach (var compression in Enum.GetValues<NsisCompression>())
        {
            var bundler = new NsisBundler(
                new NsisBundleConfiguration { Compression = compression },
                new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") });
            var artifacts = await bundler.BuildAsync(new BundleConfiguration
            {
                ProductName = $"Compression {compression}",
                Identifier = $"com.example.compression.{compression.ToString().ToLowerInvariant()}",
                Version = "1.0.0",
                OutputDirectory = Path.Combine(root, "artifacts", compression.ToString()),
                Targets =
                [
                    new BundleTargetConfiguration
                    {
                        RuntimeIdentifier = "win-x64",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            });
            Assert(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
                $"NSIS failed to compile an installer with {compression} compression.");
        }

        try
        {
            await new NsisBundler(new NsisBundleConfiguration { Compression = (NsisCompression)999 })
                .BuildAsync(new BundleConfiguration { Version = "1.0.0" });
            throw new InvalidOperationException("An unknown NSIS compression mode was accepted.");
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.Message.Contains("compression", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task SignsBothNsisInstallerArtifacts()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.NsisSigning.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(input);
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test process path is available.");
    var mainExecutable = Path.Combine(input, "ExampleApp.exe");
    var sidecar = Path.Combine(input, "Sidecar.dll");
    File.Copy(executable, mainExecutable);
    File.Copy(executable, sidecar);
    var originalMainHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mainExecutable)));
    var originalSidecarHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecar)));
    try
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=DotNet.Bundler NSIS test certificate",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.3") },
            critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.Now.AddMinutes(-5),
            DateTimeOffset.Now.AddDays(1));
        const string password = "nsis-integration-password";
        var pfx = Path.Combine(root, "test-signing.pfx");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
        var signer = new RecordingSigner(new WindowsAuthenticodeSigner(new WindowsAuthenticodeSigningOptions
        {
            PfxFile = pfx,
            PfxPassword = password
        }));
        var bundler = new NsisBundler(options: new NsisBundlerOptions
        {
            ToolCacheDirectory = Path.Combine(root, "shared-tools"),
            Signer = signer
        });
        var artifacts = await bundler.BuildAsync(new BundleConfiguration
        {
            ProductName = "Signed NSIS App",
            Identifier = "com.example.signed-nsis",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = input + Path.DirectorySeparatorChar,
                    MainExecutable = "ExampleApp.exe",
                    SigningFiles = ["Sidecar.dll"],
                    Formats = [PackageFormat.Nsis]
                }
            ]
        });

        Assert(signer.ArtifactKinds.SequenceEqual(
                new[]
                {
                    BundleSigningArtifactKind.PayloadExecutable,
                    BundleSigningArtifactKind.PayloadFile,
                    BundleSigningArtifactKind.NativeComponent,
                    BundleSigningArtifactKind.Uninstaller,
                    BundleSigningArtifactKind.Installer
                }),
            "NSIS signing did not process staged payload, native component, uninstaller, and installer in order.");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mainExecutable))) == originalMainHash &&
               Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecar))) == originalSidecarHash,
            "NSIS signing modified the caller's input directory instead of a staging copy.");
        Assert(signer.Paths[0].Contains("signed-payload", StringComparison.OrdinalIgnoreCase) &&
               signer.Paths[2].Contains("signed-plugins", StringComparison.OrdinalIgnoreCase),
            "Payload or Bundler native components were not signed from private staging directories.");
        var cachedPlugin = Directory.EnumerateFiles(
            Path.Combine(root, "shared-tools"),
            "DotNetBundlerNsis.dll",
            SearchOption.AllDirectories).Single();
        var repositoryPlugin = Path.Combine(RepositoryRoot(), "third_party", "nsis", "plugins", "x86-unicode", "DotNetBundlerNsis.dll");
        Assert(SHA256.HashData(File.ReadAllBytes(cachedPlugin)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(repositoryPlugin))),
            "Signing modified the shared cached NSIS plugin.");
        Assert(SignedFileCertificates.EmbeddedSignatureContains(artifacts[0].Path, certificate),
            "The final NSIS installer did not contain the expected signing certificate.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RunsExternalWindowsSigningProviderSafely()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.ExternalSigning.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var payload = Path.Combine(root, "payload with spaces.exe");
        var log = Path.Combine(root, "signing.log");
        await File.WriteAllTextAsync(payload, "fixture");
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No test process path is available.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        }
        arguments.AddRange(["--external-sign-fixture", "{path}", "{artifactKind}", "{target}", log]);
        var signer = new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
        {
            Command = processPath,
            Arguments = arguments
        });
        await signer.SignAsync(new BundleSigningRequest(
            payload,
            BundleSigningArtifactKind.PayloadExecutable,
            "External signing fixture",
            "win-x64"));
        var record = await File.ReadAllTextAsync(log);
        Assert(record.Trim() == $"{payload}|PayloadExecutable|win-x64", "External signer placeholders were not passed as isolated arguments.");

        const string secret = "must-not-leak-provider-secret";
        var failingArguments = arguments.Concat([secret]).ToArray();
        var failingSigner = new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
        {
            Command = processPath,
            Arguments = failingArguments
        });
        try
        {
            await failingSigner.SignAsync(new BundleSigningRequest(
                payload,
                BundleSigningArtifactKind.Installer,
                "External signing fixture",
                "win-x64"));
            throw new InvalidOperationException("A failing external signing provider was accepted.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("exit code 17", StringComparison.Ordinal) &&
            !exception.Message.Contains(secret, StringComparison.Ordinal))
        {
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RemovesFailedSignedInstaller()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.SigningFailure.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "fixture");
    var output = Path.Combine(root, "artifacts");
    try
    {
        var bundler = new NsisBundler(options: new NsisBundlerOptions
        {
            ToolCacheDirectory = Path.Combine(root, "tools"),
            Signer = new FailingInstallerSigner()
        });
        try
        {
            await bundler.BuildAsync(new BundleConfiguration
            {
                ProductName = "Failed Signing App",
                Identifier = "com.example.failed-signing",
                Version = "1.0.0",
                OutputDirectory = output,
                Targets =
                [
                    new BundleTargetConfiguration
                    {
                        RuntimeIdentifier = "win-x64",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            });
            throw new InvalidOperationException("A final signing failure was accepted.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "fixture signing failure")
        {
        }
        var installer = Path.Combine(output, "win-x64", "nsis", "Failed Signing App-1.0.0-setup.exe");
        Assert(!File.Exists(installer), "A signing failure left a final installer that could be mistaken for success.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RejectsPayloadSigningFilesWithoutSigner()
{
    try
    {
        await new NsisBundler().BuildAsync(new BundleConfiguration
        {
            ProductName = "Unsigned App",
            Identifier = "com.example.unsigned",
            Version = "1.0.0",
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = "unused",
                    SigningFiles = ["Helper.dll"],
                    Formats = [PackageFormat.Nsis]
                }
            ]
        });
        throw new InvalidOperationException("Signing files without a signer were silently ignored.");
    }
    catch (ArgumentException exception) when (exception.Message.Contains("no bundle signer", StringComparison.Ordinal))
    {
    }
}

static void UninstallsOnlyPackagedPayloadFiles()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    var nested = Path.Combine(root, "assets");
    Directory.CreateDirectory(nested);
    File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
    File.WriteAllText(Path.Combine(nested, "data.txt"), "test");

    try
    {
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = root,
            MainExecutable = "ExampleApp.exe",
            Formats = [PackageFormat.Nsis]
        });
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            root,
            "ExampleApp.exe",
            "output",
            false);
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var script = NsisBundleBackend.CreateScript(template, configuration, new NsisBundleConfiguration(), item, "setup.exe", "ExampleApp");

        Assert(script.Contains("Delete /REBOOTOK \"$INSTDIR\\ExampleApp.exe\"", StringComparison.Ordinal),
            "The uninstaller should delete the packaged executable explicitly.");
        Assert(script.Contains("Delete /REBOOTOK \"$INSTDIR\\assets\\data.txt\"", StringComparison.Ordinal),
            "The uninstaller should delete packaged nested files explicitly.");
        Assert(script.Contains("${If} $DeleteAppData == 1", StringComparison.Ordinal) &&
               script.Contains("RMDir /r /REBOOTOK \"$INSTDIR\"", StringComparison.Ordinal),
            "Choosing application-data deletion should remove the complete program directory.");
        Assert(script.IndexOf("${Else}", StringComparison.Ordinal) <
               script.IndexOf("Delete /REBOOTOK \"$INSTDIR\\ExampleApp.exe\"", StringComparison.Ordinal),
            "Payload-only deletion should remain in the branch used when application data is preserved.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void ProvidesInteractiveNsisSafetyOptions()
{
    var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
    Assert(template.Contains("Page custom ShortcutOptionsPage", StringComparison.Ordinal),
        "The installer should provide shortcut selection controls.");
    Assert(template.Contains("Function ValidateInstallDirectory", StringComparison.Ordinal) &&
           template.Contains("${INSTALL_MARKER}", StringComparison.Ordinal),
        "The installer should distinguish its own directory from another non-empty directory.");
    Assert(template.Contains("Call EnsureAppClosed", StringComparison.Ordinal) &&
           template.Contains("Call un.EnsureAppClosed", StringComparison.Ordinal),
        "Install and uninstall should both check the running application.");
    Assert(template.Contains("DotNetBundlerNsis::GetLockingProcessCount \"${SHORTCUT_OWNED_TARGETS}\"", StringComparison.Ordinal) &&
           template.Contains("DotNetBundlerNsis::ShutdownLockingProcesses \"${SHORTCUT_OWNED_TARGETS}\"", StringComparison.Ordinal) &&
           !template.Contains("taskkill.exe", StringComparison.Ordinal) &&
           !template.Contains("tasklist.exe", StringComparison.Ordinal),
        "Running-application coordination should use the exact installed executable path.");
    Assert(template.Contains("Function PrepareInstallTransaction", StringComparison.Ordinal) &&
           template.Contains("DotNetBundlerNsis::ActivateInstallTransaction", StringComparison.Ordinal) &&
           template.Contains("DotNetBundlerNsis::ValidateTransactionSnapshotSet", StringComparison.Ordinal) &&
           template.Contains("DotNetBundlerNsis::ValidateTransactionSnapshotIntegrity", StringComparison.Ordinal) &&
           template.Contains("DotNetBundlerNsis::BeginInstallTransactionRecovery", StringComparison.Ordinal) &&
           template.Contains("DotNetBundlerNsis::CompleteInstallTransactionRecovery", StringComparison.Ordinal) &&
           !template.Contains("DotNetBundlerNsis::RollbackInstallTransaction", StringComparison.Ordinal) &&
           template.Contains("Call CommitInstallTransaction", StringComparison.Ordinal),
        "The installer should snapshot, activate, recover from its manifest, and commit persistent changes without exposing journal-directed rollback.");
    Assert(template.Contains("DOTNET_BUNDLER_TEST_AFTER_TRANSACTION_BEGIN", StringComparison.Ordinal) &&
           template.Contains("DOTNET_BUNDLER_TEST_BEFORE_TRANSACTION_ACTIVATE", StringComparison.Ordinal),
        "The repository fixtures should be able to inject pre-activation transaction failures.");
    Assert(template.Contains("BeginUninstallTransaction", StringComparison.Ordinal) &&
           template.Contains("GetUninstallRecoveryHash", StringComparison.Ordinal) &&
           template.Contains("BundlerRecoverySha256", StringComparison.Ordinal) &&
           template.Contains("RecoverUninstallTransaction", StringComparison.Ordinal) &&
           template.Contains("MarkUninstallTransactionFinalizing", StringComparison.Ordinal) &&
           template.Contains("CommitUninstallTransaction", StringComparison.Ordinal),
        "The uninstaller should journal, resume, finalize, and commit forward deletion.");
    Assert(template.Contains("UninstPage custom un.AppDataOptionsPage", StringComparison.Ordinal) &&
           template.Contains("$LOCALAPPDATA\\${PRODUCT_ID}", StringComparison.Ordinal),
        "The uninstaller should offer optional application-data deletion.");
}

static void PinsInstallRecoveryToInstallerManifest()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
    try
    {
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = root,
            MainExecutable = "ExampleApp.exe",
            Formats = [PackageFormat.Nsis]
        });
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            root,
            "ExampleApp.exe",
            "output",
            false);
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var script = NsisBundleBackend.CreateScript(
            template,
            configuration,
            new NsisBundleConfiguration(),
            item,
            "setup.exe",
            "ExampleApp");

        foreach (var action in new[]
        {
            "BackupTransactionRegistryKey",
            "ValidateTransactionRegistryKeySnapshot",
            "RestoreTransactionRegistryKey"
        })
        {
            Assert(script.Contains($"DotNetBundlerNsis::{action} \"$TransactionDirectory\" \"key-000\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\" \"${{UNINSTALL_KEY}}\"", StringComparison.Ordinal),
                $"{action} should use the same installer-defined registry snapshot name and target.");
        }

        Assert(script.Contains("ValidateTransactionSnapshotSet \"$TransactionDirectory\" \"3\" \"2\"", StringComparison.Ordinal) &&
               script.Contains("ValidateTransactionSnapshotIntegrity \"$TransactionDirectory\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\"", StringComparison.Ordinal),
            "Recovery should reject a different snapshot set and changed snapshot content before restoring the payload.");
        Assert(script.Contains("/RECOVERONLY", StringComparison.Ordinal) &&
               script.Contains("EXIT_RECOVERY_MANIFEST_MISMATCH 6", StringComparison.Ordinal),
            "A different installer manifest should have a recovery-only path and a dedicated automation error.");

        foreach (var action in new[]
        {
            "BackupTransactionFile",
            "ValidateTransactionFileSnapshot",
            "RestoreTransactionFile"
        })
        {
            var command = script.Split('\n').SingleOrDefault(line =>
                line.Contains($"DotNetBundlerNsis::{action}", StringComparison.Ordinal) &&
                line.Contains("\"file-003\"", StringComparison.Ordinal));
            Assert(command is not null && command.Contains("\"$DESKTOP\\ExampleApp.lnk\"", StringComparison.Ordinal),
                $"{action} should use the same installer-defined file snapshot name and target.");
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RendersNsisAutomationProtocol()
{
    var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
    foreach (var option in new[] { "/P", "/UPDATE", "/NS", "/R", "/ARGS" })
    {
        Assert(template.Contains($"$CMDLINE \"{option}\"", StringComparison.Ordinal),
            $"The NSIS template does not parse {option}.");
    }

    Assert(template.Contains("!define EXIT_INVALID_ARGUMENTS 3", StringComparison.Ordinal) &&
           template.Contains("!define EXIT_VERSION_BLOCKED 4", StringComparison.Ordinal) &&
           template.Contains("!define EXIT_APP_CLOSE_FAILED 5", StringComparison.Ordinal) &&
           template.Contains("!define EXIT_REBOOT_REQUIRED 3010", StringComparison.Ordinal),
        "The documented automation exit codes are missing from the NSIS template.");
    Assert(template.Contains("Function un.onUninstSuccess", StringComparison.Ordinal) &&
           template.Contains("IfRebootFlag un_reboot_required un_no_reboot_required", StringComparison.Ordinal) &&
           template.Contains("SetErrorLevel ${EXIT_REBOOT_REQUIRED}", StringComparison.Ordinal),
        "Install and uninstall success paths should expose reboot-required status as exit code 3010.");
    Assert(!template.Contains("ExecWait '$InstalledUninstaller /S _?=$InstalledDirectory'", StringComparison.Ordinal),
        "Upgrade must not run the installed uninstaller in place because that queues deletion of itself.");
    Assert(template.Contains("GetTempFileName $1", StringComparison.Ordinal) &&
           template.Contains("CopyFiles /SILENT \"$InstalledDirectory\\Uninstall.exe\" \"$1\"", StringComparison.Ordinal) &&
           template.Contains("ExecWait '\"$1\" /S _?=$InstalledDirectory' $0", StringComparison.Ordinal),
        "Upgrade should synchronously run an explicit temporary copy of the old uninstaller.");
    var installSection = template.Substring(template.IndexOf("Section \"Install\" MainSection", StringComparison.Ordinal));
    Assert(installSection.Contains("Call CommitInstallTransaction", StringComparison.Ordinal) &&
           template.Contains("Function .onInstSuccess", StringComparison.Ordinal),
        "A successful reboot-required install must commit its transaction before the success callback reports exit code 3010.");
    var payloadStart = installSection.IndexOf("SetOverwrite try", StringComparison.Ordinal);
    var payloadOutput = installSection.IndexOf("SetOutPath \"$INSTDIR\"", StringComparison.Ordinal);
    var payloadFailure = installSection.IndexOf("SetOverwrite on", StringComparison.Ordinal);
    Assert(payloadStart >= 0 && payloadOutput > payloadStart && payloadFailure > payloadOutput &&
            installSection.IndexOf("MessageBox MB_ICONSTOP|MB_OK \"$(PayloadWriteFailed)\"", payloadFailure, StringComparison.Ordinal) > payloadFailure &&
            installSection.IndexOf("Call FailInstallTransaction", payloadFailure, StringComparison.Ordinal) > payloadFailure,
        "Payload extraction should explain interactive write failures and then fail transactionally.");
    foreach (var language in new[] { "English.nsh", "SimpChinese.nsh" })
    {
        var strings = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "languages", language));
        Assert(strings.Contains("LangString PayloadWriteFailed", StringComparison.Ordinal),
            $"{language} should define the payload-write failure message.");
    }
    var shortcutWrite = installSection.IndexOf("Call ConfigureShortcuts", payloadFailure, StringComparison.Ordinal);
    var registryWrite = installSection.IndexOf("WriteRegStr SHCTX \"${UNINSTALL_KEY}\" \"DisplayName\"", shortcutWrite, StringComparison.Ordinal);
    var postInstallHook = installSection.IndexOf("!ifmacrodef NSIS_HOOK_POSTINSTALL", registryWrite, StringComparison.Ordinal);
    Assert(shortcutWrite > payloadFailure && registryWrite > shortcutWrite && postInstallHook > registryWrite &&
           installSection.LastIndexOf("Call FailInstallTransaction", registryWrite, StringComparison.Ordinal) > shortcutWrite &&
           installSection.IndexOf("Call FailInstallTransaction", registryWrite, StringComparison.Ordinal) < postInstallHook,
        "Shortcut and registry persistence errors should fail before the install transaction commits.");
    Assert(template.Contains("Function SkipIfPassive", StringComparison.Ordinal) &&
           template.Contains("Function ValidateAutomatedInstallDirectory", StringComparison.Ordinal) &&
           template.Contains("DotNetBundlerNsis::RunAsUser", StringComparison.Ordinal),
        "The passive-mode safety or unelevated launch flow is missing.");
}

static void RendersExistingVersionPolicy()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
    try
    {
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = root,
            MainExecutable = "ExampleApp.exe",
            Formats = [PackageFormat.Nsis]
        });
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            root,
            "ExampleApp.exe",
            "output",
            false);
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var script = NsisBundleBackend.CreateScript(
            template,
            configuration,
            new NsisBundleConfiguration
            {
                AllowDowngrades = true,
                LegacyMsiProductCodes = ["1D1A6B03-2BDA-4D18-B12C-574145D9CFA0"],
                LegacyMsiUpgradeCodes = ["{5AD89AE2-9984-4B5F-937F-0DF918FE7A22}"],
                LegacyMsiAutoDetect = true
            },
            item,
            "setup.exe",
            "ExampleApp");
        var defaultScript = NsisBundleBackend.CreateScript(
            template, configuration, new NsisBundleConfiguration(), item, "setup.exe", "ExampleApp");

        Assert(script.Contains("DotNetBundlerNsis::SemverCompare", StringComparison.Ordinal) &&
               script.Contains("!define ALLOW_DOWNGRADES \"true\"", StringComparison.Ordinal),
            "The script must use the bundled SemVer plug-in and render downgrade policy.");
        Assert(script.Contains("Function DetectExistingInstall", StringComparison.Ordinal) &&
               script.Contains("Function ApplyAutomatedExistingInstallPolicy", StringComparison.Ordinal) &&
               script.Contains("Function UninstallExistingInstallation", StringComparison.Ordinal),
            "The script must detect and replace existing installations in interactive and automated modes.");
        Assert(script.Contains("!define LEGACY_MSI_PRODUCT_CODES \"{1D1A6B03-2BDA-4D18-B12C-574145D9CFA0}\"", StringComparison.Ordinal) &&
               script.Contains("!define LEGACY_MSI_UPGRADE_CODES \"{5AD89AE2-9984-4B5F-937F-0DF918FE7A22}\"", StringComparison.Ordinal) &&
               script.Contains("DotNetBundlerNsis::FindMsiProduct", StringComparison.Ordinal) &&
               script.Contains("Function UninstallLegacyMsiInstallations", StringComparison.Ordinal),
            "The script must render exact legacy MSI identifiers and the migration flow.");
        Assert(script.Contains("!define LEGACY_MSI_AUTODETECT_NAME \"ExampleApp\"", StringComparison.Ordinal) &&
               script.Contains("!define LEGACY_MSI_AUTODETECT_PUBLISHER \"ExampleApp\"", StringComparison.Ordinal) &&
               script.Contains("\"${LEGACY_MSI_AUTODETECT_NAME}\" \"${LEGACY_MSI_AUTODETECT_PUBLISHER}\"", StringComparison.Ordinal),
            "The script must pass opt-in name/publisher auto-detection to the plug-in calls.");
        Assert(script.Contains("${ElseIf} $0 == 1602", StringComparison.Ordinal),
            "The migration flow must treat a cancelled MSI uninstall as an abort, not a hard failure.");
        Assert(defaultScript.Contains("!define LEGACY_MSI_AUTODETECT_NAME \"\"", StringComparison.Ordinal) &&
               defaultScript.Contains("!define LEGACY_MSI_AUTODETECT_PUBLISHER \"\"", StringComparison.Ordinal),
            "Auto-detection must render empty name/publisher unless explicitly enabled.");
        Assert(script.Contains("; 打包时无法知道用户已安装的版本", StringComparison.Ordinal) &&
               script.Contains("; 静默和被动安装不会显示现有安装处理页面", StringComparison.Ordinal),
            "Non-trivial NSIS policy branches must retain Chinese explanatory comments.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RendersSafeFileAndUrlRegistrations()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
    try
    {
        var configuration = new BundleConfiguration
        {
            ProductName = "ExampleApp",
            Identifier = "com.example.app",
            Version = "1.0.0",
            Description = "Example application",
            OutputDirectory = "artifacts",
            FileAssociations =
            [
                new BundleFileAssociationConfiguration
                {
                    Extensions = [".example"],
                    Description = "Example document",
                    MimeType = "application/x-example"
                }
            ],
            UrlProtocols =
            [
                new BundleUrlProtocolConfiguration { Schemes = ["example-app"], Name = "Example link" }
            ],
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = root,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        };
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            root,
            "ExampleApp.exe",
            "output",
            false);
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var script = NsisBundleBackend.CreateScript(
            template,
            configuration,
            new NsisBundleConfiguration(),
            item,
            "setup.exe",
            "ExampleApp");

        Assert(script.Contains("Software\\Classes\\.example\\OpenWithProgids", StringComparison.Ordinal) &&
               script.Contains("${PRODUCT_ID}.File.example.1", StringComparison.Ordinal) &&
               script.Contains("${CAPABILITIES_KEY}\\FileAssociations", StringComparison.Ordinal),
            "File associations should register a private ProgID, Open With candidate, and application capability.");
        Assert(!script.Contains("WriteRegStr SHCTX \"Software\\Classes\\.example\" \"\"", StringComparison.Ordinal),
            "Packaging must not force the extension's default ProgID.");
        Assert(script.Contains("Software\\Classes\\example-app\\shell\\open\\command", StringComparison.Ordinal) &&
               script.Contains("${CAPABILITIES_KEY}\\UrlAssociations", StringComparison.Ordinal),
            "URL protocols should be directly launchable and visible to the default-apps model.");
        Assert(script.Contains("${If} $0 == '\"$INSTDIR\\ExampleApp.exe\" \"%1\"'", StringComparison.Ordinal),
            "Protocol uninstall should verify that this installation still owns the command.");
        Assert(script.Contains("SHChangeNotify", StringComparison.Ordinal) &&
               script.Contains("; 只有协议仍指向本次安装的程序时才删除", StringComparison.Ordinal),
            "The association cache refresh and the ownership rule should remain explicit in Chinese comments.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RendersNsisInstallScopes()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");

    try
    {
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            root,
            "ExampleApp.exe",
            "output",
            false);

        string Render(NsisInstallMode mode)
        {
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                RuntimeIdentifier = "win-x64",
                InputDirectory = root,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            configuration = new BundleConfiguration
            {
                ProductName = configuration.ProductName,
                Identifier = configuration.Identifier,
                Version = configuration.Version,
                OutputDirectory = configuration.OutputDirectory,
                Targets = configuration.Targets
            };
            return NsisBundleBackend.CreateScript(
                template,
                configuration,
                new NsisBundleConfiguration { InstallMode = mode },
                item,
                "setup.exe",
                "ExampleApp");
        }

        var currentUser = Render(NsisInstallMode.CurrentUser);
        Assert(currentUser.Contains("!define INSTALL_MODE \"currentUser\"", StringComparison.Ordinal) &&
               currentUser.Contains("RequestExecutionLevel user", StringComparison.Ordinal) &&
               currentUser.Contains("SetShellVarContext current", StringComparison.Ordinal) &&
               currentUser.Contains("$LOCALAPPDATA\\Programs\\${INSTALL_FOLDER}", StringComparison.Ordinal),
            "Current-user mode must use user execution, HKCU shell context, and a per-user directory.");

        var perMachine = Render(NsisInstallMode.PerMachine);
        Assert(perMachine.Contains("!define INSTALL_MODE \"perMachine\"", StringComparison.Ordinal) &&
               perMachine.Contains("RequestExecutionLevel admin", StringComparison.Ordinal) &&
               perMachine.Contains("SetShellVarContext all", StringComparison.Ordinal) &&
               perMachine.Contains("$PROGRAMFILES64\\${INSTALL_FOLDER}", StringComparison.Ordinal),
            "Per-machine mode must elevate and use the all-users shell context and Program Files.");

        var both = Render(NsisInstallMode.Both);
        Assert(both.Contains("!define INSTALL_MODE \"both\"", StringComparison.Ordinal) &&
               both.Contains("!define MULTIUSER_MUI", StringComparison.Ordinal) &&
               both.Contains("!insertmacro MULTIUSER_PAGE_INSTALLMODE", StringComparison.Ordinal) &&
               both.Contains("!insertmacro MULTIUSER_INIT", StringComparison.Ordinal) &&
               both.Contains("WriteRegStr SHCTX", StringComparison.Ordinal),
            "Both mode must delegate scope selection and registry routing to NSIS MultiUser support.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RendersEveryNsisCompressionMode()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Compression.Rendering.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
    try
    {
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            RuntimeIdentifier = "win-x64",
            InputDirectory = root,
            MainExecutable = "ExampleApp.exe",
            Formats = [PackageFormat.Nsis]
        });
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            root,
            "ExampleApp.exe",
            "output",
            false);
        var expected = new Dictionary<NsisCompression, string>
        {
            [NsisCompression.Lzma] = "SetCompressor /SOLID lzma",
            [NsisCompression.Zlib] = "SetCompressor /SOLID zlib",
            [NsisCompression.Bzip2] = "SetCompressor /SOLID bzip2",
            [NsisCompression.None] = "SetCompress off"
        };

        foreach (var pair in expected)
        {
            var script = NsisBundleBackend.CreateScript(
                template,
                configuration,
                new NsisBundleConfiguration { Compression = pair.Key },
                item,
                "setup.exe",
                "ExampleApp");
            Assert(script.Contains(pair.Value, StringComparison.Ordinal),
                $"The {pair.Key} compression directive was not rendered.");
            Assert(!script.Contains("{{compression_directive}}", StringComparison.Ordinal),
                "The compression template variable was not replaced.");
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RendersNsisMetadataIconsAndResources()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(input);
    File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "test");
    var icon = Path.Combine(root, "app.ico");
    var uninstallerIcon = Path.Combine(root, "uninstall.ico");
    var headerImage = Path.Combine(root, "header.bmp");
    var sidebarImage = Path.Combine(root, "sidebar.bmp");
    var licenseFile = Path.Combine(root, "license.rtf");
    var hooksFile = Path.Combine(root, "hooks.nsh");
    var resource = Path.Combine(root, "license.txt");
    File.WriteAllText(icon, "icon");
    File.WriteAllText(uninstallerIcon, "icon");
    File.WriteAllText(headerImage, "bitmap");
    File.WriteAllText(sidebarImage, "bitmap");
    File.WriteAllText(licenseFile, "license");
    File.WriteAllText(hooksFile, "!macro NSIS_HOOK_PREINSTALL$\n!macroend");
    File.WriteAllText(resource, "license");

    try
    {
        var configuration = new BundleConfiguration
        {
            ProductName = "ExampleApp",
            Identifier = "com.example.app",
            Version = "1.0.0",
            Publisher = "Example Publisher",
            Description = "Example Description",
            Homepage = "https://example.com/app",
            Copyright = "Copyright Example",
            LicenseFile = licenseFile,
            OutputDirectory = "artifacts",
            Icons = [icon],
            Resources =
            [
                new BundleResourceConfiguration
                {
                    Source = resource,
                    TargetPath = "docs/license.txt"
                }
            ],
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = input,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        };
        var item = new BundlePlanItem(
            new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            PackageFormat.Nsis,
            input,
            "ExampleApp.exe",
            "output",
            false);
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var nsisConfiguration = new NsisBundleConfiguration
        {
            InstallerIcon = icon,
            UninstallerIcon = uninstallerIcon,
            HeaderImage = headerImage,
            SidebarImage = sidebarImage,
            InstallerHooks = hooksFile
        };
        var script = NsisBundleBackend.CreateScript(
            template,
            configuration,
            nsisConfiguration,
            item,
            "setup.exe",
            "ExampleApp");

        Assert(script.Contains("!define PRODUCT_DESCRIPTION \"Example Description\"", StringComparison.Ordinal),
            "The configured description should be rendered into installer metadata.");
        Assert(script.Contains("!define MUI_ICON", StringComparison.Ordinal) &&
               script.Contains("!define MUI_UNICON", StringComparison.Ordinal),
            "The configured .ico should apply to the installer and uninstaller.");
        Assert(script.Contains(uninstallerIcon.Replace("$", "$$"), StringComparison.Ordinal) &&
               script.Contains("!define MUI_HEADERIMAGE_BITMAP", StringComparison.Ordinal) &&
               script.Contains("!define MUI_WELCOMEFINISHPAGE_BITMAP", StringComparison.Ordinal),
            "NSIS-specific installer, uninstaller, header, and sidebar artwork should be rendered.");
        Assert(script.Contains("MUI_PAGE_LICENSE", StringComparison.Ordinal) &&
               script.Contains("URLInfoAbout\" \"https://example.com/app", StringComparison.Ordinal) &&
               script.Contains("VIAddVersionKey \"LegalCopyright\" \"${PRODUCT_COPYRIGHT}\"", StringComparison.Ordinal),
            "License, homepage, and extended version metadata should be rendered.");
        Assert(script.Contains($"!include \"{hooksFile}\"", StringComparison.Ordinal) &&
               script.Contains("!insertmacro NSIS_HOOK_PREINSTALL", StringComparison.Ordinal) &&
               script.Contains("!insertmacro NSIS_HOOK_POSTUNINSTALL", StringComparison.Ordinal),
            "The hook file and all four lifecycle hook points should be rendered.");
        Assert(script.Contains("SetOutPath \"$INSTDIR\\docs\"", StringComparison.Ordinal) &&
               script.Contains("/oname=license.txt", StringComparison.Ordinal),
            "The external resource should be installed at its configured target path.");
        Assert(script.Contains("Delete /REBOOTOK \"$INSTDIR\\docs\\license.txt\"", StringComparison.Ordinal),
            "The external resource should be included in the safe uninstall manifest.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RunsBackendsThroughCommonPipeline()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    var output = Path.Combine(root, "artifacts");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "test");

    var backend = new RecordingBackend();
    var logger = new RecordingLogger();
    try
    {
        var configuration = new BundleConfiguration
        {
            ProductName = "ExampleApp",
            Identifier = "com.example.app",
            Version = "1.0.0",
            OutputDirectory = output,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = input,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        };

        var artifacts = await new BundlePipeline([backend], logger).BuildAsync(configuration);
        Assert(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
            "The common pipeline should return an existing backend artifact.");
        Assert(backend.WorkDirectory is not null && !Directory.Exists(backend.WorkDirectory),
            "The common pipeline should clean its backend work directory.");
        Assert(logger.Messages.Any(message => message.Contains("Created", StringComparison.Ordinal)),
            "The common pipeline should report progress through the public logging contract.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task PreflightsEveryRequestedBackend()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "test");
    var backend = new RecordingBackend();

    try
    {
        var configuration = new BundleConfiguration
        {
            ProductName = "ExampleApp",
            Identifier = "com.example.app",
            Version = "1.0.0",
            OutputDirectory = Path.Combine(root, "artifacts"),
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = "win-x64",
                    InputDirectory = input,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis, PackageFormat.Msi]
                }
            ]
        };

        try
        {
            await new BundlePipeline([backend]).BuildAsync(configuration);
            throw new InvalidOperationException("A missing MSI backend should have failed preflight.");
        }
        catch (NotSupportedException exception)
        {
            Assert(exception.Message.Contains("Msi", StringComparison.Ordinal),
                "The preflight error should identify the missing format backend.");
            Assert(backend.InvocationCount == 0,
                "No backend should run until every planned format has passed preflight.");
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RendersCompleteShortcutConfiguration()
{
    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Shortcut.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(Path.Combine(input, "data"));
    Directory.CreateDirectory(Path.Combine(input, "assets"));
    File.WriteAllText(Path.Combine(input, "NewApp.exe"), "test");
    File.WriteAllText(Path.Combine(input, "assets", "shortcut.ico"), "icon");
    try
    {
        var configuration = new BundleConfiguration
        {
            ProductName = "New App", Identifier = "com.example.newapp", Version = "1.0.0", OutputDirectory = "artifacts",
            Targets = [new BundleTargetConfiguration { RuntimeIdentifier = "win-x64", InputDirectory = input, MainExecutable = "NewApp.exe", Formats = [PackageFormat.Nsis] }]
        };
        var item = new BundlePlanItem(new BundleTarget("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64), PackageFormat.Nsis, input, "NewApp.exe", "output", false);
        var settings = new NsisBundleConfiguration
        {
            Shortcuts = new NsisShortcutConfiguration
            {
                Desktop = false, StartMenu = true, Arguments = "--profile demo", WorkingDirectory = "data",
                Icon = "assets/shortcut.ico", AppUserModelId = "com.example.newapp.desktop",
                StartMenuFolder = "Example Publisher", LegacyProductNames = ["Old App"], LegacyMainExecutables = ["OldApp.exe"]
            }
        };
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "templates", "nsis", "installer.nsi"));
        var script = NsisBundleBackend.CreateScript(template, configuration, settings, item, "setup.exe", "New App");

        Assert(script.Contains("StrCpy $CreateDesktopShortcut 0", StringComparison.Ordinal) && script.Contains("StrCpy $CreateStartMenuShortcut 1", StringComparison.Ordinal), "Configured shortcut defaults were not rendered.");
        Assert(script.Contains("!define SHORTCUT_ARGUMENTS \"--profile demo\"", StringComparison.Ordinal) &&
               script.Contains("!define SHORTCUT_WORKING_DIRECTORY \"$INSTDIR\\data\"", StringComparison.Ordinal) &&
               script.Contains("!define SHORTCUT_ICON \"$INSTDIR\\assets\\shortcut.ico\"", StringComparison.Ordinal) &&
               script.Contains("!define SHORTCUT_APP_USER_MODEL_ID \"com.example.newapp.desktop\"", StringComparison.Ordinal),
            "Shortcut arguments, working directory, icon, or AppUserModelID were not rendered.");
        Assert(script.Contains("$SMPROGRAMS\\Example Publisher\\New App.lnk", StringComparison.Ordinal), "The configured Start Menu folder was not rendered.");
        Assert(script.Contains("DotNetBundlerNsis::CreateShortcut", StringComparison.Ordinal) &&
               script.Contains("DotNetBundlerNsis::UpdateShortcutIfOwned", StringComparison.Ordinal) &&
               script.Contains("DotNetBundlerNsis::MoveShortcutIfOwned", StringComparison.Ordinal) &&
               script.Contains("DotNetBundlerNsis::DeleteShortcutIfOwned", StringComparison.Ordinal) &&
               script.Contains("$INSTDIR\\OldApp.exe", StringComparison.Ordinal),
            "Safe shortcut creation, update, migration, and removal were not rendered.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RejectsUnsafeShortcutConfiguration()
{
    var bundler = new NsisBundler(new NsisBundleConfiguration { Shortcuts = new NsisShortcutConfiguration { WorkingDirectory = "..\\outside" } });
    try
    {
        await bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" });
        throw new InvalidOperationException("A shortcut path outside the installation directory was accepted.");
    }
    catch (ArgumentException exception) when (exception.Message.Contains("relative", StringComparison.OrdinalIgnoreCase))
    {
    }
}

static void MapsNsisSettingsThroughMsBuild()
{
    var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
    var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
    var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
    foreach (var property in new[]
             {
                 "NsisCompression", "NsisShortcutDesktop", "NsisShortcutStartMenu", "NsisShortcutArguments",
                 "NsisShortcutWorkingDirectory", "NsisShortcutIcon", "NsisShortcutAppUserModelId",
                 "NsisShortcutStartMenuFolder", "NsisShortcutLegacyProductNames", "NsisShortcutLegacyMainExecutables",
                 "NsisLegacyMsiProductCodes", "NsisLegacyMsiUpgradeCodes", "NsisLegacyMsiAutoDetect",
                 "WindowsSigningCommand"
             })
    {
        Assert(targets.Contains(property + "=\"$(Bundler" + property + ")\"", StringComparison.Ordinal), $"MSBuild does not map Bundler{property} to the task.");
    }
    Assert(props.Contains("<BundlerNsisCompression Condition=\"'$(BundlerNsisCompression)' == ''\">lzma</BundlerNsisCompression>", StringComparison.Ordinal),
        "MSBuild does not define the documented LZMA compression default.");
    Assert(props.Contains("<BundlerNsisLegacyMsiAutoDetect Condition=\"'$(BundlerNsisLegacyMsiAutoDetect)' == ''\">false</BundlerNsisLegacyMsiAutoDetect>", StringComparison.Ordinal),
        "MSBuild must default legacy MSI auto-detection to disabled.");
    Assert(task.Contains("Compression = ParseCompression()", StringComparison.Ordinal) &&
           task.Contains("BundlerNsisCompression must be lzma, zlib, bzip2, or none.", StringComparison.Ordinal),
        "The MSBuild task does not parse and validate NSIS compression.");
    Assert(targets.Contains("WindowsSigningFiles=\"@(BundlerWindowsSigningFile)\"", StringComparison.Ordinal) &&
           targets.Contains("WindowsSigningCommandArguments=\"@(BundlerWindowsSigningCommandArgument)\"", StringComparison.Ordinal) &&
           task.Contains("new WindowsExternalCommandSigner", StringComparison.Ordinal),
        "MSBuild does not expose explicit payload files and external signing command arguments.");
}

static void KeepsPackageConsumerVersionsAligned()
{
    var root = RepositoryRoot();
    var version = XDocument.Load(Path.Combine(root, "Directory.Build.props"))
        .Descendants("BundlerPackageVersion").Single().Value;
    var consumerProps = XDocument.Load(Path.Combine(root, "Bundler.LocalPackages.props"));
    Assert(!consumerProps.Descendants("BundlerPackageVersion").Any(),
        "The local-package props must not duplicate the repository package version.");
    Assert(consumerProps.Descendants("BundlerPackageSource").Single().Value.Contains("artifacts", StringComparison.Ordinal) &&
           consumerProps.Descendants("RestoreSources").Single().Value == "$(BundlerPackageSource)",
        "The shared props must define the repository source and fixture restore source.");
    foreach (var path in new[]
    {
        Path.Combine(root, "samples", "HelloNsisApp", "HelloNsisApp.csproj"),
        Path.Combine(root, "samples", "HelloMsiApp", "HelloMsiApp.csproj")
    })
    {
        var project = XDocument.Load(path);
        var reference = project.Descendants("PackageReference")
            .Single(item => (string?)item.Attribute("Include") == "DotNet.Bundler");
        Assert((string?)reference.Attribute("Version") == "$(BundlerPackageVersion)" &&
               project.Descendants("BundlerUseRepositoryPackageSource").Single().Value == "true",
            "The public sample does not use the shared package version and source: " + path);
        Assert(project.Descendants("Import").Any(item =>
                   ((string?)item.Attribute("Project"))?.Contains("Bundler.LocalPackages.props", StringComparison.Ordinal) == true) &&
               !project.Descendants("RestoreSources").Any(),
            "The public sample must import the shared local-package props: " + path);
    }
    var nsisSample = XDocument.Load(Path.Combine(root, "samples", "HelloNsisApp", "HelloNsisApp.csproj"));
    Assert(nsisSample.Descendants("AssemblyName").Single().Value == "HelloBundledApp" &&
           nsisSample.Descendants("HelloNsisAppInstallMode").Any() &&
           !nsisSample.Descendants().Any(item => item.Name.LocalName.StartsWith("HelloBundledApp", StringComparison.Ordinal)),
        "The NSIS sample must use format-specific build properties while preserving its executable name.");
    Assert(XDocument.Load(Path.Combine(root, "tests", "Windows.Nsis.Integration", "Fixture",
            "BundlerNsisIntegrationFixture.csproj"))
        .Descendants("AssemblyName").Single().Value == "BundlerIntegrationFixture",
        "Renaming the NSIS test project must preserve its fixture executable name.");
    foreach (var name in new[] { "Nsis", "Msi" })
    {
        var project = XDocument.Load(Path.Combine(root, "tests", name + ".Api.PackageFixture",
            name + ".Api.PackageFixture.csproj"));
        Assert(!project.Descendants("BundlerPackageVersion").Any(),
            "The standalone API package fixture duplicates the current version: " + name);
    }
    foreach (var path in new[]
    {
        Path.Combine(root, "tests", "Nsis.Api.PackageFixture", "Nsis.Api.PackageFixture.csproj"),
        Path.Combine(root, "tests", "Msi.Api.PackageFixture", "Msi.Api.PackageFixture.csproj"),
        Path.Combine(root, "tests", "Windows.Nsis.Integration", "Fixture", "BundlerNsisIntegrationFixture.csproj"),
        Path.Combine(root, "tests", "Windows.Msi.Integration", "Fixture", "BundlerMsiSmoke.csproj")
    })
    {
        var project = XDocument.Load(path);
        Assert(project.Descendants("Import").Any(item =>
                   ((string?)item.Attribute("Project"))?.Contains("Bundler.LocalPackages.props", StringComparison.Ordinal) == true) &&
               !project.Descendants("RestoreSources").Any(),
            "The fixture must import the shared local-package props: " + path);
    }
    foreach (var path in new[]
    {
        Path.Combine(root, "tests", "Windows.Nsis.Integration", "Verify.ps1"),
        Path.Combine(root, "tests", "Windows.Msi.Integration", "Verify.ps1"),
        Path.Combine(root, "tests", "Windows.Msi.Integration", "VerifyLifecycle.ps1"),
        Path.Combine(root, "tests", "Windows.Msi.Integration", "VerifyMaintenance.ps1"),
        Path.Combine(root, "tests", "Windows.Msi.Integration", "VerifyPublicSample.ps1")
    })
    {
        var script = File.ReadAllText(path);
        Assert(script.Contains("Get-BundlerPackageVersion -Repository", StringComparison.Ordinal) &&
               !script.Contains(version, StringComparison.Ordinal),
            "The integration script must read its default package version from Directory.Build.props: " + path);
    }
}

static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

static Task RunSync(Action action)
{
    action();
    return Task.CompletedTask;
}

static BundleConfiguration ValidConfiguration(BundleTargetConfiguration target) => new()
{
    ProductName = "ExampleApp",
    Identifier = "com.example.app",
    Version = "1.0.0",
    OutputDirectory = "artifacts",
    Targets = [target]
};

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class RecordingBackend : IBundleBackend
{
    public PackageFormat Format => PackageFormat.Nsis;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Windows;
    public string? WorkDirectory { get; private set; }
    public int InvocationCount { get; private set; }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context,
        CancellationToken cancellationToken = default)
    {
        InvocationCount++;
        WorkDirectory = context.WorkDirectory;
        var path = Path.Combine(context.Item.OutputDirectory, "recording-installer.exe");
        await File.WriteAllTextAsync(path, "artifact", cancellationToken);
        return [new BundleArtifact(Format, context.Item.Target.RuntimeIdentifier, path)];
    }
}

file sealed class RecordingLogger : IBundleLogger
{
    public List<string> Messages { get; } = [];

    public void Log(BundleLogLevel level, string message) => Messages.Add($"{level}: {message}");
}

file sealed class RecordingSigner(IBundleSigner inner) : IBundleSigner
{
    public List<BundleSigningArtifactKind> ArtifactKinds { get; } = [];
    public List<string> Paths { get; } = [];

    public async Task SignAsync(
        BundleSigningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArtifactKinds.Add(request.ArtifactKind);
        Paths.Add(request.Path);
        await inner.SignAsync(request, cancellationToken);
    }
}

file sealed class FailingInstallerSigner : IBundleSigner
{
    public Task SignAsync(BundleSigningRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ArtifactKind == BundleSigningArtifactKind.Installer)
        {
            throw new InvalidOperationException("fixture signing failure");
        }
        return Task.CompletedTask;
    }
}
