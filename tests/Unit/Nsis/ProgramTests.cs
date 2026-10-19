using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Signing.Windows;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;


// 从旧 tests/Bundler.Tests/ProgramTests.cs 收割：NSIS 后端 + 通用管线 + 工具缓存。
// 真实 makensis 编译腿（BuildsThroughStandaloneNsisApi、BuildsEveryBuiltInNsisLanguage、
// ValidatesCustomNsisLanguageFiles、BuildsEveryNsisCompressionMode、
// SignsBothNsisInstallerArtifacts、RemovesFailedSignedInstaller）与拉起外部签名命令的
// RunsExternalWindowsSigningProviderSafely 属 spawn，去向见 Integration/Manual 层对应文件；
// 其中可拆出的进程内断言另立 RejectsDuplicateAndUnsupportedNsisLanguages /
// RejectsUnknownNsisCompression / RejectsInvalidCustomNsisLanguageFiles。
// 判重跳过：ParsesSupportedDesktopTargets（BundleTargetTests）、
// RejectsExecutablePathEscape（MainExecutable_EscapesInput_Reports+SigningFiles_RelativeOnly）、
// RejectsMalformedUpdateSection（Update_*）、AddsAppDependencyBeforeDmg（Create_DmgWithoutApp_*）、
// RejectsUnknownTemplateVariables（Template_Render_UnknownToken_Throws）、
// SelectsEveryBundledNsisHostCompiler（CompilerRelativePath_HostMatrix）、
// ComparesSemanticVersionsForInstallerPolicy（SemVer_*）、
// RejectsInvalidAssociationsAndProtocols（FileAssociations_/UrlProtocols_）、
// RunsBackendsThroughCommonPipeline（Build_ProducesArtifacts_*+Build_Logs_*）、
// PreflightsEveryRequestedBackend（Build_NoBackend_ThrowsNotSupported）、
// RejectsArtifactPathCollisionAcrossTargets（Create_ArtifactNameCollision_Throws+Build_DuplicateArtifactPaths_Rejected）、
// ResolvesLibCSpecificBackendVariants（Build_LibCSpecificBackend_PreferredOverGeneric）、
// RejectsDuplicateBackendForSameLibC（Constructor_DuplicateSameLibC_Rejected）。
namespace Bundler.UnitTests.Nsis;

public sealed class ProgramTests : TempBase
{

        [Fact]
    public void RejectsIncompatibleFormats()
    {
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "linux-x86_64",
            InputDirectory = "unused",
            Formats = [PackageFormat.Msi]
        });

        var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
        Assert.Contains(issues, issue => issue.Message.Contains("not supported", StringComparison.Ordinal));
        var x86Nsis = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "windows-i686", InputDirectory = "unused", Formats = [PackageFormat.Nsis]
        });
        Assert.False(BundleConfigurationValidator.Validate(x86Nsis, checkFileSystem: false) .Any(issue => issue.Path == "targets[0].formats"), "The NSIS backend must accept the Windows x86 target.");
        var x86Archive = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "windows-i686", InputDirectory = "unused",
            Formats = [PackageFormat.Zip, PackageFormat.TarGz]
        });
        Assert.False(BundleConfigurationValidator.Validate(x86Archive, checkFileSystem: false) .Any(issue => issue.Path == "targets[0].formats"), "Zip/TarGz must accept the Windows x86 target.");
        var x86Msi = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "windows-i686", InputDirectory = "unused", Formats = [PackageFormat.Msi]
        });
        Assert.False(BundleConfigurationValidator.Validate(x86Msi, checkFileSystem: false) .Any(issue => issue.Path == "targets[0].formats"), "The MSI backend must accept the Windows x86 target.");
        var osxFormats = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "macos-universal", InputDirectory = "unused",
            Formats = [PackageFormat.App, PackageFormat.Dmg, PackageFormat.Pkg, PackageFormat.Zip, PackageFormat.TarGz]
        });
        Assert.False(BundleConfigurationValidator.Validate(osxFormats, checkFileSystem: false) .Any(issue => issue.Path == "targets[0].formats"), "The universal macos-universal target must accept the macOS format set.");
        var osxNsis = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "macos-universal", InputDirectory = "unused", Formats = [PackageFormat.Nsis]
        });
        Assert.Contains(BundleConfigurationValidator.Validate(osxNsis, checkFileSystem: false), issue => issue.Message.Contains("not supported", StringComparison.Ordinal));
        foreach (var target in new[] { "linux-musl-x86_64", "linux-musl-aarch64" })
        {
            var muslArchive = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = target, InputDirectory = "unused",
                Formats = [PackageFormat.Zip, PackageFormat.TarGz, PackageFormat.AlpineApk, PackageFormat.AppImage]
            });
            Assert.False(BundleConfigurationValidator.Validate(muslArchive, checkFileSystem: false) .Any(issue => issue.Path == "targets[0].formats"), $"Zip/TarGz/AlpineApk/AppImage must accept the musl target {target}.");
            var muslGlibc = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = target, InputDirectory = "unused",
                Formats = [PackageFormat.Deb, PackageFormat.Rpm]
            });
            var muslIssues = BundleConfigurationValidator.Validate(muslGlibc, checkFileSystem: false)
                .Where(issue => issue.Path == "targets[0].formats").ToArray();
            Assert.True(muslIssues.Length == 2,
                $"Deb/Rpm must each be rejected for the musl target {target} (glibc-distro semantics).");
        }
    }

                [Fact]
    public void RejectsMismatchedUpdateKeys()
    {
        // 旁车嵌的 publicKey 与 signingKeyFile 私钥不配对 = 装出来的客户端验签全拒。
        var directory = Temp("bundler-test-").FullName;
        Directory.CreateDirectory(directory);
        try
        {
            var keyPath = Path.Combine(directory, "key.json");
            UpdateKeyMaterial.Generate().Save(keyPath);
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = "linux-x86_64",
                InputDirectory = "unused",
                Formats = [PackageFormat.Zip]
            }, update: new UpdateBundleConfiguration
            {
                FeedUrl = "https://example.test/updates",
                SigningKeyFile = keyPath,
                // 另一把密钥的公点——各自合法但不配对。
                PublicKey = UpdateKeyMaterial.Generate().PublicPointBase64(),
            });

            var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: true);
            Assert.Contains(issues, issue =>
                issue.Path == "update.publicKey" && issue.Message.Contains("signingKeyFile"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifiesAndExtractsBundledNsis()
    {
        var repositoryRoot = RepoRoot();
        var archive = Path.Combine(repositoryRoot, "third_party", "nsis", "nsis-toolset-3.12-r1.zip");
        var cache = Temp("DotNet.Bundler.Tests").FullName;
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
                    Assert.Contains(required, entries);
                }

                var copyingEntry = zip.GetEntry("common/COPYING") ??
                    throw new InvalidDataException("The NSIS toolset archive is missing common/COPYING.");
                using var copyingStream = copyingEntry.Open();
                var archiveLicense = new StreamReader(copyingStream).ReadToEnd().Replace("\r\n", "\n");
                var packagedLicense = File.ReadAllText(
                        Path.Combine(repositoryRoot, "third_party", "nsis", "COPYING"))
                    .Replace("\r\n", "\n");
                Assert.Equal(packagedLicense, archiveLicense);
            }


            var pluginPath = Path.Combine(
                repositoryRoot,
                "third_party", "nsis", "plugins", "x86-unicode", "DotNetBundlerNsis.dll");
            Assert.True(File.Exists(pluginPath), "The bundled NSIS plug-in is missing.");
            Assert.Equal("C18232C2940F76BB9E367C3AE6F1AC5BEBBBD71B1134658FB814FBF83285F981",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath))));

            var toolset = await NsisToolResolver.ResolveAsync(archive, cache, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(toolset.CompilerPath), "The verified NSIS toolset did not produce the host compiler.");
            Assert.True(toolset.DataDirectory is not null && Directory.Exists(toolset.DataDirectory),
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

    [Fact]
    public void WritesValidWindowsUninstallCommand()
    {
        var input = Temp("DotNet.Bundler.Tests").FullName;
        Directory.CreateDirectory(input);
        try
        {
            File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "test");
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = "windows-x86_64",
                InputDirectory = input,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                input,
                "ExampleApp.exe",
                "output",
                false);

            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var script = NsisBundleBackend.CreateScript(template, configuration, new NsisBundleConfiguration(), item, "setup.exe", "ExampleApp");
            Assert.Contains("\"UninstallString\" '\"$INSTDIR\\Uninstall.exe\"'", script);
            Assert.DoesNotContain("'$\"$INSTDIR", script);
        }
        finally
        {
            Directory.Delete(input, recursive: true);
        }
    }

        [Fact]
    public void LetsUsersChooseInstallDirectory()
    {
        var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
        Assert.Contains("!insertmacro MUI_PAGE_DIRECTORY", template);
        Assert.True(template.Contains("ReadRegStr $0 SHCTX \"${UNINSTALL_KEY}\" \"InstallRoot\"", StringComparison.Ordinal) &&
               template.Contains("MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_VALUENAME \"InstallRoot\"", StringComparison.Ordinal) &&
               template.Contains("${If} $INSTDIR == \"placeholder\\${INSTALL_FOLDER}\"", StringComparison.Ordinal),
            "Fixed and selectable install scopes should restore their previously selected install directories.");
    }

    [Fact]
    public async Task RecoversAndSerializesToolCache()
    {
        var root = Path.Combine(
            Temp("cache-tests").FullName,
            "Unicode-工具缓存",
            new string('a', 70),
            new string('b', 70));
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
                Enumerable.Range(0, 8).Select(_ => ZipToolCache.ResolveToolAsync(archivePath, cache, archive, TestContext.Current.CancellationToken)));
            Assert.True(results.Select(result => result.DirectoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
                "Concurrent tool resolution did not converge on one cache entry.");

            var executable = results[0].ExecutablePath;
            await File.WriteAllTextAsync(executable, "corrupted", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(results[0].DirectoryPath, "unexpected.txt"), "corrupted", TestContext.Current.CancellationToken);
            var repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive, TestContext.Current.CancellationToken);
            Assert.Equal("trusted executable", await File.ReadAllTextAsync(repaired.ExecutablePath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(repaired.DirectoryPath, "unexpected.txt")), "Unexpected cache content was not removed during recovery.");

            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "keep", TestContext.Current.CancellationToken);
            var link = Path.Combine(repaired.DirectoryPath, "linked-outside");
            Directory.CreateSymbolicLink(link, outside);
            repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(sentinel) && !Directory.Exists(link),
                "Cache recovery followed or retained a directory reparse point.");
            Directory.Delete(repaired.DirectoryPath, recursive: true);
            await File.WriteAllTextAsync(repaired.DirectoryPath, "directory replaced by a file", TestContext.Current.CancellationToken);
            repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive, TestContext.Current.CancellationToken);
            Assert.True(Directory.Exists(repaired.DirectoryPath) && await File.ReadAllTextAsync(repaired.ExecutablePath, TestContext.Current.CancellationToken) == "trusted executable",
                "A cache directory replaced by a file was not rebuilt.");

            var linkedCache = Path.Combine(root, "linked-cache");
            Directory.CreateSymbolicLink(linkedCache, outside);
            await Assert.ThrowsAnyAsync<InvalidDataException>(
                () => ZipToolCache.ResolveToolAsync(archivePath, linkedCache, archive, TestContext.Current.CancellationToken));
            Directory.Delete(linkedCache);
            Assert.True(File.Exists(sentinel), "Tool cache root validation followed a reparse point.");
            Assert.True(File.Exists(Path.Combine(repaired.DirectoryPath, ".bundler-tool-manifest")),
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

    [Fact]
    public async Task RejectsUnsafeToolArchives()
    {
        var root = Temp("DotNet.Bundler.UnsafeZip.Tests").FullName;
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
                await Assert.ThrowsAnyAsync<InvalidDataException>(
                    () => ZipToolCache.ResolveToolAsync(archivePath, Path.Combine(root, "cache"), descriptor, TestContext.Current.CancellationToken));
            }
            Assert.False(File.Exists(Path.Combine(root, "escaped.exe")), "Archive traversal wrote outside the cache staging directory.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoversAndSerializesEmbeddedNsisResources()
    {
        var root = Path.Combine(Temp("resource-tests").FullName, "资源");
        try
        {
            var resources = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => NsisEmbeddedResources.MaterializeAsync(root, TestContext.Current.CancellationToken)));
            Assert.True(resources.Select(item => item.TemplatePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
                "Concurrent embedded resource materialization did not converge on one immutable path.");
            var expected = await File.ReadAllTextAsync(resources[0].TemplatePath, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(resources[0].TemplatePath, "corrupted", TestContext.Current.CancellationToken);
            var repaired = await NsisEmbeddedResources.MaterializeAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(expected, await File.ReadAllTextAsync(repaired.TemplatePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsReparsePointsInNsisPayloads()
    {
        var root = Temp("DotNet.Bundler.Reparse.Tests").FullName;
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
                Target = "windows-x86_64",
                InputDirectory = input,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                input,
                "ExampleApp.exe",
                Path.Combine(root, "output"),
                false);
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var reparsePoint = Assert.ThrowsAny<InvalidDataException>(() => NsisBundleBackend.CreateScript(
                template,
                configuration,
                new NsisBundleConfiguration(),
                item,
                Path.Combine(root, "setup.exe"),
                "ExampleApp"));
            Assert.Contains("reparse point", reparsePoint.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsInvalidWindowsPayloadNames()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "Windows hosts cannot materialize these payload names");
        foreach (var badName in new[] { "con.dll", "a|b.txt", "ends." })
        {
            var root = Temp("DotNet.Bundler.Tests").FullName;
            var input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            try
            {
                File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "fixture");
                File.WriteAllText(Path.Combine(input, badName), "payload");
                var configuration = ValidConfiguration(new BundleTargetConfiguration
                {
                    Target = "windows-x86_64",
                    InputDirectory = input,
                    MainExecutable = "ExampleApp.exe",
                    Formats = [PackageFormat.Nsis]
                });
                var item = new BundlePlanItem(
                    new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                    PackageFormat.Nsis,
                    input,
                    "ExampleApp.exe",
                    Path.Combine(root, "output"),
                    false);
                var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
                var rejected = Assert.ThrowsAny<InvalidDataException>(() => NsisBundleBackend.CreateScript(
                    template,
                    configuration,
                    new NsisBundleConfiguration(),
                    item,
                    Path.Combine(root, "setup.exe"),
                    "ExampleApp"));
                Assert.Contains("not valid on Windows", rejected.Message);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsInvalidWindowsResourceTargets()
    {
        var root = Temp("DotNet.Bundler.Tests").FullName;
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        try
        {
            File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "fixture");
            var resource = Path.Combine(root, "resource.txt");
            File.WriteAllText(resource, "resource");
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                input,
                "ExampleApp.exe",
                Path.Combine(root, "output"),
                false);
            foreach (var targetPath in new[] { "docs/con.txt", "docs/a|b.txt", "docs/trail. " })
            {
                var configuration = new BundleConfiguration
                {
                    ProductName = "ExampleApp",
                    Identifier = "com.example.app",
                    Version = "1.0.0",
                    OutputDirectory = "artifacts",
                    Resources = [new BundleResourceConfiguration { Source = resource, Destination = targetPath }],
                    Targets =
                    [
                        new BundleTargetConfiguration
                        {
                            Target = "windows-x86_64",
                            InputDirectory = input,
                            MainExecutable = "ExampleApp.exe",
                            Formats = [PackageFormat.Nsis]
                        }
                    ]
                };
                var rejected = Assert.ThrowsAny<InvalidOperationException>(() => NsisBundleBackend.CreateScript(
                    template,
                    configuration,
                    new NsisBundleConfiguration(),
                    item,
                    Path.Combine(root, "setup.exe"),
                    "ExampleApp"));
                Assert.Contains("not valid on Windows", rejected.Message);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsPercentInUninstallerFinalizeDestination()
    {
        var rejected = Assert.ThrowsAny<InvalidOperationException>(
            () => NsisBundleBackend.CreateUninstallerFinalizeCommand("C:\\work%NAME%dir\\uninstaller.exe"));
        Assert.Contains("percent", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uninstaller.exe",
            NsisBundleBackend.CreateUninstallerFinalizeCommand(Path.Combine("work", "uninstaller.exe")));
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

    [Fact]
    public async Task SignsPeFileWithoutWindowsSdk()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");

        var root = Temp("DotNet.Bundler.Tests").FullName;
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
                "windows-x86_64"), TestContext.Current.CancellationToken);

            Assert.True(SignedFileCertificates.EmbeddedSignatureContains(target, certificate),
                "The signed PE file did not contain the expected test certificate.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

            [Fact]
    public async Task RejectsInvalidNsisPackageVersions()
    {
        foreach (var version in new[] { "1.0", "1.0.0-01", "65536.0.0" })
        {
            var exception = await Assert.ThrowsAnyAsync<ArgumentException>(
                () => new NsisBundler().BuildAsync(
                    new BundleConfiguration { Version = version },
                    TestContext.Current.CancellationToken));
            Assert.Contains(version, exception.Message);
        }
    }

    [Fact]
    public async Task RejectsInvalidLegacyMsiIdentifiers()
    {
        var bundler = new NsisBundler(new NsisBundleConfiguration
        {
            LegacyMsiProductCodes = ["not-a-guid"]
        });
        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" }, TestContext.Current.CancellationToken));
        Assert.Contains("not-a-guid", exception.Message);

        bundler = new NsisBundler(new NsisBundleConfiguration
        {
            LegacyMsiUpgradeCodes = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid().ToString()).ToArray()
        });
        var exception2 = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" }, TestContext.Current.CancellationToken));
        Assert.Contains("string limit", exception2.Message);
    }

        [Fact]
    public async Task LoadsCompleteGenericBundleConfiguration()
    {
        var root = Temp("DotNet.Bundler.Configuration.Tests").FullName;
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
                      "target": "windows-x86_64",
                      "inputDirectory": "publish",
                      "mainExecutable": "Configured.exe",
                      "signingFiles": ["Helper.dll"],
                      "formats": ["nsis"]
                    }
                  ]
                }
                """, TestContext.Current.CancellationToken);

            var configuration = await BundleConfigurationLoader.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.True(configuration.FileAssociations.Count == 1 &&
                   configuration.FileAssociations[0].Extensions.SequenceEqual(["configured"]) &&
                   configuration.FileAssociations[0].MimeType == "application/x-configured",
                "The configuration loader dropped file-association metadata.");
            Assert.True(configuration.UrlProtocols.Count == 1 &&
                   configuration.UrlProtocols[0].Schemes.SequenceEqual(["configured-app"]) &&
                   configuration.UrlProtocols[0].Name == "Configured link",
                "The configuration loader dropped URL-protocol metadata.");
            Assert.Equal(Path.Combine(root, "publish"), configuration.Targets[0].InputDirectory);
            Assert.Equal(configuration.Targets[0].SigningFiles, ["Helper.dll"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

            // 自定义语言文件整份替换内置语言，必须覆盖内置模板声明的全部 LangString 键——
    // 新增必填键时样品的 Assets/nsis/*.nsh 忘同步会让样品 nsis 腿直接拒产。
    [Fact]
    public void SampleCustomNsisLanguageFilesCoverBuiltInKeys()
    {
        var requiredKeys = File.ReadLines(Path.Combine(
                RepoRoot(), "src", "Bundler.Nsis", "templates", "languages", "English.nsh"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("LangString ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(requiredKeys);

        // 只校验样品声明为 BundlerNsisLanguageFile 的文件——同目录的
        // installer-hooks.nsh 是钩入文件不是语言文件。
        var props = XDocument.Load(Path.Combine(
            RepoRoot(), "samples", "HelloBundlerApp", "formats", "Nsis.props"));
        var customFiles = props.Descendants("BundlerNsisLanguageFile")
            .Select(item => (string?)item.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => Path.GetFullPath(include!
                .Replace("$(HelloBundlerAppAssets)",
                    Path.Combine(RepoRoot(), "samples", "HelloBundlerApp", "Assets") + Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)))
            .ToArray();
        Assert.NotEmpty(customFiles);
        foreach (var file in customFiles)
        {
            var defined = File.ReadLines(file)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("LangString ", StringComparison.Ordinal))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
                .ToHashSet(StringComparer.Ordinal);
            var missing = requiredKeys.Where(key => !defined.Contains(key)).ToArray();
            Assert.True(missing.Length == 0,
                $"{Path.GetFileName(file)} is missing required LangString keys: {string.Join(", ", missing)}");
        }
    }

                    [Fact]
    public async Task RejectsPayloadSigningFilesWithoutSigner()
    {
        var unsignedSigner = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => new NsisBundler().BuildAsync(new BundleConfiguration
            {
                ProductName = "Unsigned App",
                Identifier = "com.example.unsigned",
                Version = "1.0.0",
                Targets =
                [
                    new BundleTargetConfiguration
                    {
                        Target = "windows-x86_64",
                        InputDirectory = "unused",
                        SigningFiles = ["Helper.dll"],
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            }, TestContext.Current.CancellationToken));
        Assert.Contains("no bundle signer", unsignedSigner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallsOnlyPackagedPayloadFiles()
    {
        var root = Temp("DotNet.Bundler.Tests").FullName;
        var nested = Path.Combine(root, "assets");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
        File.WriteAllText(Path.Combine(nested, "data.txt"), "test");

        try
        {
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = "windows-x86_64",
                InputDirectory = root,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var script = NsisBundleBackend.CreateScript(template, configuration, new NsisBundleConfiguration(), item, "setup.exe", "ExampleApp");

            Assert.Contains("Delete /REBOOTOK \"$INSTDIR\\ExampleApp.exe\"", script);
            Assert.Contains("Delete /REBOOTOK \"$INSTDIR\\assets\\data.txt\"", script);
            Assert.True(script.Contains("${If} $DeleteAppData == 1", StringComparison.Ordinal) &&
                   script.Contains("RMDir /r /REBOOTOK \"$INSTDIR\"", StringComparison.Ordinal),
                "Choosing application-data deletion should remove the complete program directory.");
            Assert.True(script.IndexOf("${Else}", StringComparison.Ordinal) <
                   script.IndexOf("Delete /REBOOTOK \"$INSTDIR\\ExampleApp.exe\"", StringComparison.Ordinal),
                "Payload-only deletion should remain in the branch used when application data is preserved.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProvidesInteractiveNsisSafetyOptions()
    {
        var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
        Assert.Contains("Page custom ShortcutOptionsPage", template);
        Assert.True(template.Contains("Function ValidateInstallDirectory", StringComparison.Ordinal) &&
               template.Contains("${INSTALL_MARKER}", StringComparison.Ordinal),
            "The installer should distinguish its own directory from another non-empty directory.");
        Assert.True(template.Contains("Call EnsureAppClosed", StringComparison.Ordinal) &&
               template.Contains("Call un.EnsureAppClosed", StringComparison.Ordinal),
            "Install and uninstall should both check the running application.");
        Assert.True(template.Contains("DotNetBundlerNsis::GetLockingProcessCount \"${SHORTCUT_OWNED_TARGETS}\"", StringComparison.Ordinal) &&
               template.Contains("DotNetBundlerNsis::ShutdownLockingProcesses \"${SHORTCUT_OWNED_TARGETS}\"", StringComparison.Ordinal) &&
               !template.Contains("taskkill.exe", StringComparison.Ordinal) &&
               !template.Contains("tasklist.exe", StringComparison.Ordinal),
            "Running-application coordination should use the exact installed executable path.");
        Assert.True(template.Contains("Function PrepareInstallTransaction", StringComparison.Ordinal) &&
               template.Contains("DotNetBundlerNsis::ActivateInstallTransaction", StringComparison.Ordinal) &&
               template.Contains("DotNetBundlerNsis::ValidateTransactionSnapshotSet", StringComparison.Ordinal) &&
               template.Contains("DotNetBundlerNsis::ValidateTransactionSnapshotIntegrity", StringComparison.Ordinal) &&
               template.Contains("DotNetBundlerNsis::BeginInstallTransactionRecovery", StringComparison.Ordinal) &&
               template.Contains("DotNetBundlerNsis::CompleteInstallTransactionRecovery", StringComparison.Ordinal) &&
               !template.Contains("DotNetBundlerNsis::RollbackInstallTransaction", StringComparison.Ordinal) &&
               template.Contains("Call CommitInstallTransaction", StringComparison.Ordinal),
            "The installer should snapshot, activate, recover from its manifest, and commit persistent changes without exposing journal-directed rollback.");
        Assert.True(template.Contains("DOTNET_BUNDLER_TEST_AFTER_TRANSACTION_BEGIN", StringComparison.Ordinal) &&
               template.Contains("DOTNET_BUNDLER_TEST_BEFORE_TRANSACTION_ACTIVATE", StringComparison.Ordinal),
            "The repository fixtures should be able to inject pre-activation transaction failures.");
        Assert.True(template.Contains("BeginUninstallTransaction", StringComparison.Ordinal) &&
               template.Contains("GetUninstallRecoveryHash", StringComparison.Ordinal) &&
               template.Contains("BundlerRecoverySha256", StringComparison.Ordinal) &&
               template.Contains("RecoverUninstallTransaction", StringComparison.Ordinal) &&
               template.Contains("MarkUninstallTransactionFinalizing", StringComparison.Ordinal) &&
               template.Contains("CommitUninstallTransaction", StringComparison.Ordinal),
            "The uninstaller should journal, resume, finalize, and commit forward deletion.");
        Assert.True(template.Contains("uninstall_directory_owned", StringComparison.Ordinal) &&
               template.Contains("IfFileExists \"$INSTDIR\\${INSTALL_MARKER}\"", StringComparison.Ordinal),
            "un.onInit should refuse a foreign $INSTDIR before any deletion (copied-uninstaller guard).");
        Assert.True(template.Contains("UninstPage custom un.AppDataOptionsPage", StringComparison.Ordinal) &&
               template.Contains("$LOCALAPPDATA\\${PRODUCT_ID}", StringComparison.Ordinal),
            "The uninstaller should offer optional application-data deletion.");
    }

    [Fact]
    public void PinsInstallRecoveryToInstallerManifest()
    {
        var root = Temp("DotNet.Bundler.Tests").FullName;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
        try
        {
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = "windows-x86_64",
                InputDirectory = root,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
                Assert.Contains($"DotNetBundlerNsis::{action} \"$TransactionDirectory\" \"key-000\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\" \"${{UNINSTALL_KEY}}\"", script);
            }

            Assert.True(script.Contains("ValidateTransactionSnapshotSet \"$TransactionDirectory\" \"3\" \"2\"", StringComparison.Ordinal) &&
                   script.Contains("ValidateTransactionSnapshotIntegrity \"$TransactionDirectory\" \"$TransactionRegistryRoot\" \"$TransactionRegistryView\"", StringComparison.Ordinal),
                "Recovery should reject a different snapshot set and changed snapshot content before restoring the payload.");
            Assert.True(script.Contains("/RECOVERONLY", StringComparison.Ordinal) &&
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
                Assert.True(command is not null && command.Contains("\"$DESKTOP\\ExampleApp.lnk\"", StringComparison.Ordinal),
                    $"{action} should use the same installer-defined file snapshot name and target.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RendersNsisAutomationProtocol()
    {
        var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
        foreach (var option in new[] { "/P", "/UPDATE", "/NS", "/R", "/ARGS" })
        {
            Assert.Contains($"$CMDLINE \"{option}\"", template);
        }

        Assert.True(template.Contains("!define EXIT_INVALID_ARGUMENTS 3", StringComparison.Ordinal) &&
               template.Contains("!define EXIT_VERSION_BLOCKED 4", StringComparison.Ordinal) &&
               template.Contains("!define EXIT_APP_CLOSE_FAILED 5", StringComparison.Ordinal) &&
               template.Contains("!define EXIT_REBOOT_REQUIRED 3010", StringComparison.Ordinal),
            "The documented automation exit codes are missing from the NSIS template.");
        Assert.True(template.Contains("Function un.onUninstSuccess", StringComparison.Ordinal) &&
               template.Contains("IfRebootFlag un_reboot_required un_no_reboot_required", StringComparison.Ordinal) &&
               template.Contains("SetErrorLevel ${EXIT_REBOOT_REQUIRED}", StringComparison.Ordinal),
            "Install and uninstall success paths should expose reboot-required status as exit code 3010.");
        Assert.DoesNotContain("ExecWait '$InstalledUninstaller /S _?=$InstalledDirectory'", template);
        Assert.True(template.Contains("GetTempFileName $1", StringComparison.Ordinal) &&
               template.Contains("CopyFiles /SILENT \"$InstalledDirectory\\Uninstall.exe\" \"$1\"", StringComparison.Ordinal) &&
               template.Contains("ExecWait '\"$1\" /S _?=$InstalledDirectory' $0", StringComparison.Ordinal),
            "Upgrade should synchronously run an explicit temporary copy of the old uninstaller.");
        var installSection = template.Substring(template.IndexOf("Section \"Install\" MainSection", StringComparison.Ordinal));
        Assert.True(installSection.Contains("Call CommitInstallTransaction", StringComparison.Ordinal) &&
               template.Contains("Function .onInstSuccess", StringComparison.Ordinal),
            "A successful reboot-required install must commit its transaction before the success callback reports exit code 3010.");
        var payloadStart = installSection.IndexOf("SetOverwrite try", StringComparison.Ordinal);
        var payloadOutput = installSection.IndexOf("SetOutPath \"$INSTDIR\"", StringComparison.Ordinal);
        var payloadFailure = installSection.IndexOf("SetOverwrite on", StringComparison.Ordinal);
        Assert.True(payloadStart >= 0 && payloadOutput > payloadStart && payloadFailure > payloadOutput &&
                installSection.IndexOf("MessageBox MB_ICONSTOP|MB_OK \"$(PayloadWriteFailed)\"", payloadFailure, StringComparison.Ordinal) > payloadFailure &&
                installSection.IndexOf("Call FailInstallTransaction", payloadFailure, StringComparison.Ordinal) > payloadFailure,
            "Payload extraction should explain interactive write failures and then fail transactionally.");
        foreach (var language in new[] { "English.nsh", "SimpChinese.nsh" })
        {
            var strings = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "languages", language));
            Assert.True(strings.Contains("LangString PayloadWriteFailed", StringComparison.Ordinal),
                $"{language} should define the payload-write failure message.");
        }
        var shortcutWrite = installSection.IndexOf("Call ConfigureShortcuts", payloadFailure, StringComparison.Ordinal);
        var registryWrite = installSection.IndexOf("WriteRegStr SHCTX \"${UNINSTALL_KEY}\" \"DisplayName\"", shortcutWrite, StringComparison.Ordinal);
        var postInstallHook = installSection.IndexOf("!ifmacrodef NSIS_HOOK_POSTINSTALL", registryWrite, StringComparison.Ordinal);
        Assert.True(shortcutWrite > payloadFailure && registryWrite > shortcutWrite && postInstallHook > registryWrite &&
               installSection.LastIndexOf("Call FailInstallTransaction", registryWrite, StringComparison.Ordinal) > shortcutWrite &&
               installSection.IndexOf("Call FailInstallTransaction", registryWrite, StringComparison.Ordinal) < postInstallHook,
            "Shortcut and registry persistence errors should fail before the install transaction commits.");
        Assert.True(template.Contains("Function SkipIfPassive", StringComparison.Ordinal) &&
               template.Contains("Function ValidateAutomatedInstallDirectory", StringComparison.Ordinal) &&
               template.Contains("DotNetBundlerNsis::RunAsUser", StringComparison.Ordinal),
            "The passive-mode safety or unelevated launch flow is missing.");
    }

    [Fact]
    public void RendersExistingVersionPolicy()
    {
        var root = Temp("DotNet.Bundler.Tests").FullName;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
        try
        {
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = "windows-x86_64",
                InputDirectory = root,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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

            Assert.True(script.Contains("DotNetBundlerNsis::SemverCompare", StringComparison.Ordinal) &&
                   script.Contains("!define ALLOW_DOWNGRADES \"true\"", StringComparison.Ordinal),
                "The script must use the bundled SemVer plug-in and render downgrade policy.");
            Assert.True(script.Contains("Function DetectExistingInstall", StringComparison.Ordinal) &&
                   script.Contains("Function ApplyAutomatedExistingInstallPolicy", StringComparison.Ordinal) &&
                   script.Contains("Function UninstallExistingInstallation", StringComparison.Ordinal),
                "The script must detect and replace existing installations in interactive and automated modes.");
            Assert.True(script.Contains("!define LEGACY_MSI_PRODUCT_CODES \"{1D1A6B03-2BDA-4D18-B12C-574145D9CFA0}\"", StringComparison.Ordinal) &&
                   script.Contains("!define LEGACY_MSI_UPGRADE_CODES \"{5AD89AE2-9984-4B5F-937F-0DF918FE7A22}\"", StringComparison.Ordinal) &&
                   script.Contains("DotNetBundlerNsis::FindMsiProduct", StringComparison.Ordinal) &&
                   script.Contains("Function UninstallLegacyMsiInstallations", StringComparison.Ordinal),
                "The script must render exact legacy MSI identifiers and the migration flow.");
            Assert.True(script.Contains("!define LEGACY_MSI_AUTODETECT_NAME \"ExampleApp\"", StringComparison.Ordinal) &&
                   script.Contains("!define LEGACY_MSI_AUTODETECT_PUBLISHER \"ExampleApp\"", StringComparison.Ordinal) &&
                   script.Contains("\"${LEGACY_MSI_AUTODETECT_NAME}\" \"${LEGACY_MSI_AUTODETECT_PUBLISHER}\"", StringComparison.Ordinal),
                "The script must pass opt-in name/publisher auto-detection to the plug-in calls.");
            Assert.Contains("${ElseIf} $0 == 1602", script);
            Assert.True(defaultScript.Contains("!define LEGACY_MSI_AUTODETECT_NAME \"\"", StringComparison.Ordinal) &&
                   defaultScript.Contains("!define LEGACY_MSI_AUTODETECT_PUBLISHER \"\"", StringComparison.Ordinal),
                "Auto-detection must render empty name/publisher unless explicitly enabled.");
            Assert.True(script.Contains("; 打包时无法知道用户已安装的版本", StringComparison.Ordinal) &&
                   script.Contains("; 静默和被动安装不会显示现有安装处理页面", StringComparison.Ordinal),
                "Non-trivial NSIS policy branches must retain Chinese explanatory comments.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RendersSafeFileAndUrlRegistrations()
    {
        var root = Temp("DotNet.Bundler.Tests").FullName;
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
                        Target = "windows-x86_64",
                        InputDirectory = root,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            };
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var script = NsisBundleBackend.CreateScript(
                template,
                configuration,
                new NsisBundleConfiguration(),
                item,
                "setup.exe",
                "ExampleApp");

            Assert.True(script.Contains("Software\\Classes\\.example\\OpenWithProgids", StringComparison.Ordinal) &&
                   script.Contains("${PRODUCT_ID}.File.example.1", StringComparison.Ordinal) &&
                   script.Contains("${CAPABILITIES_KEY}\\FileAssociations", StringComparison.Ordinal),
                "File associations should register a private ProgID, Open With candidate, and application capability.");
            Assert.DoesNotContain("WriteRegStr SHCTX \"Software\\Classes\\.example\" \"\"", script);
            Assert.True(script.Contains("Software\\Classes\\example-app\\shell\\open\\command", StringComparison.Ordinal) &&
                   script.Contains("${CAPABILITIES_KEY}\\UrlAssociations", StringComparison.Ordinal),
                "URL protocols should be directly launchable and visible to the default-apps model.");
            Assert.Contains("${If} $0 == '\"$INSTDIR\\ExampleApp.exe\" \"%1\"'", script);
            Assert.True(script.Contains("SHChangeNotify", StringComparison.Ordinal) &&
                   script.Contains("; 只有协议仍指向本次安装的程序时才删除", StringComparison.Ordinal),
                "The association cache refresh and the ownership rule should remain explicit in Chinese comments.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RendersNsisInstallScopes()
    {
        var root = Temp("DotNet.Bundler.Tests").FullName;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");

        try
        {
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);

            string Render(NsisInstallScope mode)
            {
                var configuration = ValidConfiguration(new BundleTargetConfiguration
                {
                    Target = "windows-x86_64",
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
                    new NsisBundleConfiguration { InstallScope = mode },
                    item,
                    "setup.exe",
                    "ExampleApp");
            }

            var currentUser = Render(NsisInstallScope.CurrentUser);
            Assert.True(currentUser.Contains("!define INSTALL_MODE \"currentUser\"", StringComparison.Ordinal) &&
                   currentUser.Contains("RequestExecutionLevel user", StringComparison.Ordinal) &&
                   currentUser.Contains("SetShellVarContext current", StringComparison.Ordinal) &&
                   currentUser.Contains("$LOCALAPPDATA\\Programs\\${INSTALL_FOLDER}", StringComparison.Ordinal),
                "Current-user mode must use user execution, HKCU shell context, and a per-user directory.");

            var perMachine = Render(NsisInstallScope.PerMachine);
            Assert.True(perMachine.Contains("!define INSTALL_MODE \"perMachine\"", StringComparison.Ordinal) &&
                   perMachine.Contains("RequestExecutionLevel admin", StringComparison.Ordinal) &&
                   perMachine.Contains("SetShellVarContext all", StringComparison.Ordinal) &&
                   perMachine.Contains("$PROGRAMFILES64\\${INSTALL_FOLDER}", StringComparison.Ordinal),
                "Per-machine mode must elevate and use the all-users shell context and Program Files.");

            var both = Render(NsisInstallScope.Both);
            Assert.True(both.Contains("!define INSTALL_MODE \"both\"", StringComparison.Ordinal) &&
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

    [Fact]
    public void RendersEveryNsisCompressionMode()
    {
        var root = Temp("DotNet.Bundler.Compression.Rendering.Tests").FullName;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
        try
        {
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = "windows-x86_64",
                InputDirectory = root,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
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
                Assert.Contains(pair.Value, script);
                Assert.DoesNotContain("{{compression_directive}}", script);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RendersNsisMetadataIconsAndResources()
    {
        var root = Temp("DotNet.Bundler.Tests").FullName;
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "test");
        var icon = Path.Combine(root, "app.ico");
        var uninstallerIconFile = Path.Combine(root, "uninstall.ico");
        var headerFile = Path.Combine(root, "header.bmp");
        var sidebarFile = Path.Combine(root, "sidebar.bmp");
        var licenseFile = Path.Combine(root, "license.rtf");
        var hooksFile = Path.Combine(root, "hooks.nsh");
        var resource = Path.Combine(root, "license.txt");
        File.WriteAllText(icon, "icon");
        File.WriteAllText(uninstallerIconFile, "icon");
        File.WriteAllText(headerFile, "bitmap");
        File.WriteAllText(sidebarFile, "bitmap");
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
                        Destination = "docs/license.txt"
                    }
                ],
                Targets =
                [
                    new BundleTargetConfiguration
                    {
                        Target = "windows-x86_64",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            };
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                input,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var nsisConfiguration = new NsisBundleConfiguration
            {
                InstallerIconFile = icon,
                UninstallerIconFile = uninstallerIconFile,
                HeaderFile = headerFile,
                SidebarFile = sidebarFile,
                InstallerHooksFile = hooksFile
            };
            var script = NsisBundleBackend.CreateScript(
                template,
                configuration,
                nsisConfiguration,
                item,
                "setup.exe",
                "ExampleApp");

            Assert.Contains("!define PRODUCT_DESCRIPTION \"Example Description\"", script);
            Assert.True(script.Contains("!define MUI_ICON", StringComparison.Ordinal) &&
                   script.Contains("!define MUI_UNICON", StringComparison.Ordinal),
                "The configured .ico should apply to the installer and uninstaller.");
            Assert.True(script.Contains(uninstallerIconFile.Replace("$", "$$"), StringComparison.Ordinal) &&
                   script.Contains("!define MUI_HEADERIMAGE_BITMAP", StringComparison.Ordinal) &&
                   script.Contains("!define MUI_WELCOMEFINISHPAGE_BITMAP", StringComparison.Ordinal),
                "NSIS-specific installer, uninstaller, header, and sidebar artwork should be rendered.");
            Assert.True(script.Contains("MUI_PAGE_LICENSE", StringComparison.Ordinal) &&
                   script.Contains("URLInfoAbout\" \"https://example.com/app", StringComparison.Ordinal) &&
                   script.Contains("VIAddVersionKey \"LegalCopyright\" \"${PRODUCT_COPYRIGHT}\"", StringComparison.Ordinal),
                "License, homepage, and extended version metadata should be rendered.");
            Assert.True(script.Contains($"!include \"{hooksFile}\"", StringComparison.Ordinal) &&
                   script.Contains("!insertmacro NSIS_HOOK_PREINSTALL", StringComparison.Ordinal) &&
                   script.Contains("!insertmacro NSIS_HOOK_POSTUNINSTALL", StringComparison.Ordinal),
                "The hook file and all four lifecycle hook points should be rendered.");
            Assert.True(script.Contains("SetOutPath \"$INSTDIR\\docs\"", StringComparison.Ordinal) &&
                   script.Contains("/oname=license.txt", StringComparison.Ordinal),
                "The external resource should be installed at its configured target path.");
            Assert.Contains("Delete /REBOOTOK \"$INSTDIR\\docs\\license.txt\"", script);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

                        [Fact]
    public void RendersCompleteShortcutConfiguration()
    {
        var root = Temp("DotNet.Bundler.Shortcut.Tests").FullName;
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
                Targets = [new BundleTargetConfiguration { Target = "windows-x86_64", InputDirectory = input, MainExecutable = "NewApp.exe", Formats = [PackageFormat.Nsis] }]
            };
            var item = new BundlePlanItem(new BundleTarget("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64), PackageFormat.Nsis, input, "NewApp.exe", "output", false);
            var settings = new NsisBundleConfiguration
            {
                Shortcuts = new NsisShortcutConfiguration
                {
                    Desktop = false, StartMenu = true, Arguments = "--profile demo", WorkingDirectory = "data",
                    Icon = "assets/shortcut.ico", AppUserModelId = "com.example.newapp.desktop",
                    StartMenuFolder = "Example Publisher", LegacyProductNames = ["Old App"], LegacyMainExecutables = ["OldApp.exe"]
                }
            };
            var template = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var script = NsisBundleBackend.CreateScript(template, configuration, settings, item, "setup.exe", "New App");

            Assert.True(script.Contains("StrCpy $CreateDesktopShortcut 0", StringComparison.Ordinal) && script.Contains("StrCpy $CreateStartMenuShortcut 1", StringComparison.Ordinal), "Configured shortcut defaults were not rendered.");
            Assert.True(script.Contains("!define SHORTCUT_ARGUMENTS \"--profile demo\"", StringComparison.Ordinal) &&
                   script.Contains("!define SHORTCUT_WORKING_DIRECTORY \"$INSTDIR\\data\"", StringComparison.Ordinal) &&
                   script.Contains("!define SHORTCUT_ICON \"$INSTDIR\\assets\\shortcut.ico\"", StringComparison.Ordinal) &&
                   script.Contains("!define SHORTCUT_APP_USER_MODEL_ID \"com.example.newapp.desktop\"", StringComparison.Ordinal),
                "Shortcut arguments, working directory, icon, or AppUserModelID were not rendered.");
            Assert.Contains("$SMPROGRAMS\\Example Publisher\\New App.lnk", script);
            Assert.True(script.Contains("DotNetBundlerNsis::CreateShortcut", StringComparison.Ordinal) &&
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

    [Fact]
    public async Task RejectsUnsafeShortcutConfiguration()
    {
        var bundler = new NsisBundler(new NsisBundleConfiguration { Shortcuts = new NsisShortcutConfiguration { WorkingDirectory = "..\\outside" } });
        var unsafeShortcut = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" }, TestContext.Current.CancellationToken));
        Assert.Contains("relative", unsafeShortcut.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapsNsisSettingsThroughMsBuild()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var targets = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var task = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        foreach (var property in new[]
                 {
                     "NsisCompression", "NsisShortcutDesktop", "NsisShortcutStartMenu", "NsisShortcutArguments",
                     "NsisShortcutWorkingDirectory", "NsisShortcutIcon", "NsisShortcutAppUserModelId",
                     "NsisShortcutStartMenuFolder", "NsisShortcutLegacyProductNames", "NsisShortcutLegacyMainExecutables",
                     "NsisLegacyMsiProductCodes", "NsisLegacyMsiUpgradeCodes", "NsisLegacyMsiAutoDetect",
                     "WindowsSigningCommand"
                 })
        {
            Assert.Contains(property + "=\"$(Bundler" + property + ")\"", targets);
        }
        Assert.Contains("<BundlerNsisCompression Condition=\"'$(BundlerNsisCompression)' == ''\">lzma</BundlerNsisCompression>", props);
        Assert.Contains("<BundlerNsisLegacyMsiAutoDetect Condition=\"'$(BundlerNsisLegacyMsiAutoDetect)' == ''\">false</BundlerNsisLegacyMsiAutoDetect>", props);
        Assert.True(task.Contains("Compression = ParseCompression()", StringComparison.Ordinal) &&
               task.Contains("BundlerNsisCompression must be lzma, zlib, bzip2, or none.", StringComparison.Ordinal),
            "The MSBuild task does not parse and validate NSIS compression.");
        Assert.True(targets.Contains("WindowsSigningFiles=\"@(BundlerWindowsSigningFile)\"", StringComparison.Ordinal) &&
               targets.Contains("WindowsSigningCommandArguments=\"@(BundlerWindowsSigningCommandArgument)\"", StringComparison.Ordinal) &&
               task.Contains("new WindowsExternalCommandSigner", StringComparison.Ordinal),
            "MSBuild does not expose explicit payload files and external signing command arguments.");
    }

    [Fact]
    public void PassesValidationIssueDetailsThroughMsBuild()
    {
        var task = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(task.Contains("catch (BundleValidationException", StringComparison.Ordinal) &&
               task.Contains("issue.Path") && task.Contains("issue.Message"),
            "The MSBuild task must log each validation issue's path and message, not only the count.");
    }

    [Fact]
    public void KeepsPackageConsumerVersionsAligned()
    {
        var root = RepoRoot();
        var version = XDocument.Load(Path.Combine(root, "Directory.Build.props"))
            .Descendants("BundlerPackageVersion").Single().Value;
        var consumerProps = XDocument.Load(Path.Combine(root, "Bundler.LocalPackages.props"));
        Assert.Empty(consumerProps.Descendants("BundlerPackageVersion"));
        Assert.True(consumerProps.Descendants("RestoreAdditionalProjectSources").Single().Value == "$(BundlerPackageSource)" &&
               !consumerProps.Descendants("RestoreSources").Any(),
            "The standalone-fixture props must append the script-supplied package source without owning the restore source list.");
        var wiring = XDocument.Load(Path.Combine(root, "Bundler.ProjectReference.targets"));
        Assert.True(wiring.Descendants("ProjectReference").Any(item =>
                   ((string?)item.Attribute("Include"))?.Contains("Bundler.MSBuild.csproj", StringComparison.Ordinal) == true) &&
               wiring.Descendants("_BundlerTaskAssembly").Any() &&
               wiring.Descendants("Import").Any(item =>
                   ((string?)item.Attribute("Project"))?.Contains("DotNet.Bundler.MSBuild.targets", StringComparison.Ordinal) == true),
            "The shared wiring must reference Bundler.MSbuild, locate its task assembly, and import its targets.");
        foreach (var path in new[]
        {
            Path.Combine(root, "samples", "HelloBundlerApp", "HelloBundlerApp.csproj"),
            Path.Combine(root, "tests", "Fixtures", "Nsis", "Fixture", "BundlerNsisIntegrationFixture.csproj"),
            Path.Combine(root, "tests", "Fixtures", "Deb", "BundlerDebIntegrationFixture.csproj"),
            Path.Combine(root, "tests", "Fixtures", "Rpm", "BundlerRpmIntegrationFixture.csproj")
        })
        {
            var project = XDocument.Load(path);
            Assert.True(!project.Descendants("PackageReference").Any(item =>
                       ((string?)item.Attribute("Include"))?.StartsWith("DotNet.Bundler", StringComparison.Ordinal) == true) &&
                   project.Descendants("Import").Any(item =>
                       ((string?)item.Attribute("Project"))?.Contains("Bundler.ProjectReference.targets", StringComparison.Ordinal) == true),
                "The internal consumer must use the shared project-reference wiring, not a package: " + path);
        }
        var mergedSample = XDocument.Load(Path.Combine(root, "samples", "HelloBundlerApp", "HelloBundlerApp.csproj"));
        Assert.True(mergedSample.Descendants("AssemblyName").Single().Value == "HelloBundlerApp" &&
               mergedSample.Descendants().Any(item => item.Name.LocalName == "BundlerFormats") &&
               !mergedSample.Descendants().Any(item => item.Name.LocalName.StartsWith("HelloBundledApp", StringComparison.Ordinal)),
            "The merged sample must expose the executable name and per-target format selection without legacy pass-throughs.");
        var formatPrefixes = new Dictionary<string, string>
        {
            ["Nsis"] = "HelloBundlerNsis",
            ["Msi"] = "HelloBundlerMsi",
            ["MacApp"] = "HelloBundlerMacApp",
            ["MacDmg"] = "HelloBundlerDmg",
            ["MacPkg"] = "HelloBundlerPkg",
            ["Deb"] = "HelloBundlerDeb",
            ["Rpm"] = "HelloBundlerRpm",
            ["AppImage"] = "HelloBundlerAppImage",
            ["Archive"] = "HelloBundlerArchive",
            ["AlpineApk"] = "HelloBundlerApk",
        };
        foreach (var (format, prefix) in formatPrefixes)
        {
            var propsPath = Path.Combine(root, "samples", "HelloBundlerApp", "formats", format + ".props");
            Assert.Contains(prefix, File.ReadAllText(propsPath));
            Assert.True(mergedSample.Descendants("Import").Any(item =>
                   ((string?)item.Attribute("Project"))?.Contains("formats/" + format + ".props", StringComparison.Ordinal) == true),
                "The merged sample must import formats/" + format + ".props with a host-portable forward slash.");
        }
        Assert.Equal("BundlerIntegrationFixture",
            XDocument.Load(Path.Combine(root, "tests", "Fixtures", "Nsis", "Fixture", "BundlerNsisIntegrationFixture.csproj"))
            .Descendants("AssemblyName").Single().Value);
        var apiTests = XDocument.Load(Path.Combine(root, "tests", "Api", "Bundler.ApiTests.csproj"));
        Assert.True(!apiTests.Descendants("BundlerPackageVersion").Any() &&
               !apiTests.Descendants("PackageReference").Any(item =>
                   ((string?)item.Attribute("Include"))?.StartsWith("DotNet.Bundler", StringComparison.Ordinal) == true),
            "The API test project must not consume any DotNet.Bundler package.");
        foreach (var backend in new[] { "Bundler.Nsis", "Bundler.Wix", "Bundler.Deb", "Bundler.Rpm", "Bundler.AppImage", "Bundler.Archive", "Bundler.AlpineApk", "Bundler.MacApp", "Bundler.MacDmg", "Bundler.MacPkg" })
        {
            Assert.True(apiTests.Descendants("ProjectReference").Any(item =>
                   ((string?)item.Attribute("Include"))?.Contains("src\\" + backend + "\\" + backend + ".csproj", StringComparison.Ordinal) == true),
                "The API test project must reference its backend project: " + backend);
        }
        // 独立包消费 fixture 在新测试结构下暂不存在（tests/Package 为占位腿）；
        // 原 LocalPackages fixture 断言段随之移除，其余接线断言保留。
        var layout = File.ReadAllText(Path.Combine(
            root, "tests", "Shared", "Tooling", "RepositoryLayout.cs"));
        Assert.Contains("Directory.Build.props", layout, StringComparison.Ordinal);
        Assert.Contains("BundlerPackageVersion", layout, StringComparison.Ordinal);
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

    // BuildsEveryBuiltInNsisLanguage 的进程内腿：重复/不支持语言在 Validate 段拒绝，
    // 不触 makensis；真实编译腿属于 spawn，去向见 Integration/Manual 层对应文件。
    [Fact]
    public async Task RejectsDuplicateAndUnsupportedNsisLanguages()
    {
        var duplicateLanguage = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => new NsisBundler(new NsisBundleConfiguration { Languages = ["English", "english"] })
                .BuildAsync(new BundleConfiguration { Version = "1.0.0" }, TestContext.Current.CancellationToken));
        Assert.Contains("more than once", duplicateLanguage.Message, StringComparison.Ordinal);

        var unsupportedLanguage = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => new NsisBundler(new NsisBundleConfiguration { Languages = ["Klingon"] })
                .BuildAsync(new BundleConfiguration { Version = "1.0.0" }, TestContext.Current.CancellationToken));
        Assert.Contains("Unsupported NSIS language", unsupportedLanguage.Message, StringComparison.Ordinal);
    }

    // BuildsEveryNsisCompressionMode 的进程内腿：未知压缩枚举在 Validate 段拒绝。
    [Fact]
    public async Task RejectsUnknownNsisCompression()
    {
        var unknownCompression = await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
            () => new NsisBundler(new NsisBundleConfiguration { Compression = (NsisCompression)999 })
                .BuildAsync(new BundleConfiguration { Version = "1.0.0" }, TestContext.Current.CancellationToken));
        Assert.Contains("compression", unknownCompression.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ValidatesCustomNsisLanguageFiles 的进程内腿：自定义语言文件校验先于 makensis
    // 编译段运行；完整替换+真实编译腿属于 spawn，去向见 Integration/Manual 层对应文件。
    [Fact]
    public async Task RejectsInvalidCustomNsisLanguageFiles()
    {
        var root = Temp("custom-language").FullName;
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "custom-language-test", TestContext.Current.CancellationToken);
        var english = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "Bundler.Nsis", "templates", "languages", "English.nsh"), TestContext.Current.CancellationToken);
        var englishLines = english.Split('\n');
        var cases = new Dictionary<string, string>
        {
            ["missing"] = string.Join(Environment.NewLine, englishLines.Skip(1)),
            ["duplicate"] = english + Environment.NewLine + englishLines[0].TrimEnd('\r'),
            ["unknown"] = english.Replace("ShortcutPageTitle", "UnexpectedMessage", StringComparison.Ordinal),
            ["wrong-language"] = english.Replace("${LANG_ENGLISH}", "${LANG_GERMAN}", StringComparison.Ordinal)
        };
        foreach (var (name, content) in cases)
        {
            var customFile = Path.Combine(root, name + ".nsh");
            await File.WriteAllTextAsync(customFile, content, TestContext.Current.CancellationToken);
            var bundler = new NsisBundler(
                new NsisBundleConfiguration
                {
                    Languages = ["English"],
                    CustomLanguageFiles = new Dictionary<string, string> { ["english"] = customFile }
                },
                new NsisBundlerOptions { ToolCacheDirectory = Path.Combine(root, "shared-tools") });
            await Assert.ThrowsAnyAsync<InvalidDataException>(
                () => bundler.BuildAsync(LanguageTestConfiguration(input, Path.Combine(root, "artifacts", name)), TestContext.Current.CancellationToken));
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
                Target = "windows-x86_64",
                InputDirectory = input,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            }
        ]
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
}




file static class SignedFileCertificates
{
    private const uint CertQueryObjectFile = 0x00000001;
    private const uint CertQueryContentFlagPkcs7SignedEmbed = 0x00000400;
    private const uint CertQueryFormatFlagBinary = 0x00000002;

    public static bool EmbeddedSignatureContains(string path, X509Certificate2 certificate)
    {
        var signature = ReadEmbeddedSignature(path);
        return signature.AsSpan().IndexOf(certificate.RawData) >= 0;
    }

    // CryptQueryObject 对内嵌签名的返回口径随版本不同：ppvContext 直接给 PKCS7
    // blob；有的宿主只填 phCertStore，此时逐张枚举证书——证书 DER 会原样出现在
    // PKCS7 blob 中，拼接后仍可用同一处子串断言。
    private static byte[] ReadEmbeddedSignature(string path)
    {
        var queried = CryptQueryObject(
            CertQueryObjectFile,
            path,
            CertQueryContentFlagPkcs7SignedEmbed,
            CertQueryFormatFlagBinary,
            0,
            out _,
            out _,
            out _,
            out var certStore,
            out var message,
            out var context);
        try
        {
            if (!queried)
            {
                throw new InvalidOperationException($"Embedded Authenticode signature was not found in '{path}'.");
            }
            if (context != IntPtr.Zero)
            {
                var blob = Marshal.PtrToStructure<CryptoApiBlob>(context);
                var bytes = new byte[checked((int)blob.DataLength)];
                Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            if (certStore != IntPtr.Zero)
            {
                return ReadStoreCertificates(certStore);
            }
            throw new InvalidOperationException($"Embedded Authenticode signature was not found in '{path}'.");
        }
        finally
        {
            if (context != IntPtr.Zero) CryptMemFree(context);
            if (message != IntPtr.Zero) CryptMsgClose(message);
            if (certStore != IntPtr.Zero) CertCloseStore(certStore, 0);
        }
    }

    private static byte[] ReadStoreCertificates(IntPtr certStore)
    {
        using var stream = new MemoryStream();
        var previous = IntPtr.Zero;
        try
        {
            while (true)
            {
                var current = CertEnumCertificatesInStore(certStore, previous);
                previous = current;
                if (current == IntPtr.Zero)
                {
                    break;
                }
                var context = Marshal.PtrToStructure<CertContext>(current);
                var certificate = new byte[checked((int)context.EncodedLength)];
                Marshal.Copy(context.EncodedData, certificate, 0, certificate.Length);
                stream.Write(certificate, 0, certificate.Length);
            }
        }
        finally
        {
            if (previous != IntPtr.Zero) CertFreeCertificateContext(previous);
        }
        if (stream.Length == 0)
        {
            throw new InvalidOperationException("The embedded signature contains no certificates.");
        }
        return stream.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptoApiBlob
    {
        public uint DataLength;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertContext
    {
        public uint EncodingType;
        public IntPtr EncodedData;
        public uint EncodedLength;
        public IntPtr CertInfo;
        public IntPtr CertStore;
    }

    [DllImport("crypt32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptQueryObject(
        uint objectType,
        string objectName,
        uint expectedContentTypeFlags,
        uint expectedFormatTypeFlags,
        uint flags,
        out uint encodingType,
        out uint contentType,
        out uint formatType,
        out IntPtr certStore,
        out IntPtr message,
        out IntPtr context);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern void CryptMemFree(IntPtr buffer);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern bool CryptMsgClose(IntPtr message);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern IntPtr CertEnumCertificatesInStore(IntPtr certStore, IntPtr previousCertificateContext);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern bool CertFreeCertificateContext(IntPtr certificateContext);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern bool CertCloseStore(IntPtr certStore, uint flags);
}
