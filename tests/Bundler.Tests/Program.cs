using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Signing.Windows;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

var tests = new (string Name, Func<Task> Test)[]
{
    ("Parses supported desktop RIDs", () => RunSync(ParsesSupportedDesktopRids)),
    ("Rejects incompatible formats", () => RunSync(RejectsIncompatibleFormats)),
    ("Adds app dependency before DMG", () => RunSync(AddsAppDependencyBeforeDmg)),
    ("Rejects executable paths outside input", () => RunSync(RejectsExecutablePathEscape)),
    ("Verifies and extracts bundled NSIS", VerifiesAndExtractsBundledNsis),
    ("Selects every bundled NSIS host compiler", () => RunSync(SelectsEveryBundledNsisHostCompiler)),
    ("Compares semantic versions for installer policy", () => RunSync(ComparesSemanticVersionsForInstallerPolicy)),
    ("Rejects invalid NSIS package versions", RejectsInvalidNsisPackageVersions),
    ("Rejects invalid legacy MSI identifiers", RejectsInvalidLegacyMsiIdentifiers),
    ("Rejects invalid associations and protocols", () => RunSync(RejectsInvalidAssociationsAndProtocols)),
    ("Builds through the standalone NSIS API", BuildsThroughStandaloneNsisApi),
    ("Signs both NSIS installer artifacts", SignsBothNsisInstallerArtifacts),
    ("Writes a valid Windows uninstall command", () => RunSync(WritesValidWindowsUninstallCommand)),
    ("Lets users choose and restore the install directory", () => RunSync(LetsUsersChooseInstallDirectory)),
    ("Uninstalls only packaged payload files", () => RunSync(UninstallsOnlyPackagedPayloadFiles)),
    ("Provides interactive NSIS safety options", () => RunSync(ProvidesInteractiveNsisSafetyOptions)),
    ("Renders the NSIS automation protocol", () => RunSync(RendersNsisAutomationProtocol)),
    ("Renders existing-version policy", () => RunSync(RendersExistingVersionPolicy)),
    ("Renders safe file and URL registrations", () => RunSync(RendersSafeFileAndUrlRegistrations)),
    ("Renders NSIS install scopes", () => RunSync(RendersNsisInstallScopes)),
    ("Renders NSIS metadata, icons, and resources", () => RunSync(RendersNsisMetadataIconsAndResources)),
    ("Rejects unknown template variables", () => RunSync(RejectsUnknownTemplateVariables)),
    ("Runs backends through the common pipeline", RunsBackendsThroughCommonPipeline),
    ("Preflights every requested backend", PreflightsEveryRequestedBackend),
    ("Signs a PE file without the Windows SDK", SignsPeFileWithoutWindowsSdk)
};

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
    string[] rids = ["win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64"];
    Assert(rids.All(rid => BundleTarget.TryParse(rid, out _)), "One or more supported RIDs failed to parse.");
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
        Formats = [PackageFormat.Nsis]
    });

    var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
    Assert(issues.Any(issue => issue.Path.EndsWith("mainExecutable", StringComparison.Ordinal)),
        "An executable outside inputDirectory should have failed validation.");
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
        Assert(File.Exists(pluginPath), "The bundled semantic-version NSIS plug-in is missing.");
        Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath))) ==
               "30A4773D22E0CFF0E9DFEDF4A62DDB1FD9878333A6B29490900D21C60ED15064",
            "The bundled semantic-version NSIS plug-in checksum changed; rebuild and update its provenance.");

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
            "Signing test"));

        using var embedded = new X509Certificate2(X509Certificate.CreateFromSignedFile(target));
        Assert(embedded.Thumbprint == certificate.Thumbprint,
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
                    InputDirectory = input,
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

static async Task SignsBothNsisInstallerArtifacts()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.NsisSigning.Tests", Guid.NewGuid().ToString("N"));
    var input = Path.Combine(root, "publish");
    Directory.CreateDirectory(input);
    await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "signed-nsis-test");
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
                    InputDirectory = input,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis]
                }
            ]
        });

        Assert(signer.ArtifactKinds.SequenceEqual(
                new[] { BundleSigningArtifactKind.Uninstaller, BundleSigningArtifactKind.Installer }),
            "NSIS signing must sign the exported uninstaller before the final installer.");
        using var embedded = new X509Certificate2(X509Certificate.CreateFromSignedFile(artifacts[0].Path));
        Assert(embedded.Thumbprint == certificate.Thumbprint,
            "The final NSIS installer did not contain the expected signing certificate.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
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
    Assert(template.Contains("UninstPage custom un.AppDataOptionsPage", StringComparison.Ordinal) &&
           template.Contains("$LOCALAPPDATA\\${PRODUCT_ID}", StringComparison.Ordinal),
        "The uninstaller should offer optional application-data deletion.");
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
                LegacyMsiUpgradeCodes = ["{5AD89AE2-9984-4B5F-937F-0DF918FE7A22}"]
            },
            item,
            "setup.exe",
            "ExampleApp");

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

    public async Task<BundleArtifact> BuildAsync(
        BundleBuildContext context,
        CancellationToken cancellationToken = default)
    {
        InvocationCount++;
        WorkDirectory = context.WorkDirectory;
        var path = Path.Combine(context.Item.OutputDirectory, "recording-installer.exe");
        await File.WriteAllTextAsync(path, "artifact", cancellationToken);
        return new BundleArtifact(Format, context.Item.Target.RuntimeIdentifier, path);
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

    public async Task SignAsync(
        BundleSigningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArtifactKinds.Add(request.ArtifactKind);
        await inner.SignAsync(request, cancellationToken);
    }
}
