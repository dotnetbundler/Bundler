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

public static class ProgramTests
{

    [Fact]
    static void ParsesSupportedDesktopRids()
    {
        string[] rids = ["windows-i686", "windows-x86_64", "windows-arm64", "macos-universal", "macos-x86_64", "macos-arm64", "linux-x86_64", "linux-aarch64", "linux-musl-x86_64", "linux-musl-aarch64"];
        Assert.True(rids.All(rid => BundleTarget.TryParse(rid, out _)), "One or more supported RIDs failed to parse.");
        Assert.True(BundleTarget.TryParse("windows-i686", out var x86) && x86!.Architecture == CpuArchitecture.X86,
            "Windows x86 must be a distinct public target architecture.");
        Assert.True(BundleTarget.TryParse("macos-universal", out var macosUniversal) &&
                macosUniversal!.OperatingSystem == DesktopOperatingSystem.MacOS && macosUniversal.Architecture == CpuArchitecture.Universal,
            "The bare macos-universal RID must map to a universal macOS target.");
        Assert.True(BundleTarget.TryParse("linux-musl-x86_64", out var musl) &&
                musl!.OperatingSystem == DesktopOperatingSystem.LinuxMusl && musl.Architecture == CpuArchitecture.X64,
            "linux-musl-x86_64 must parse as a distinct musl target.");
        Assert.True(BundleTarget.TryParse("linux-musl-aarch64", out var muslArm) &&
                muslArm!.OperatingSystem == DesktopOperatingSystem.LinuxMusl,
            "linux-musl-aarch64 must parse as a distinct musl target.");
        Assert.False(BundleTarget.TryParse("android-arm64", out _), "A mobile RID was accepted.");
        Assert.False(BundleTarget.TryParse("linux-musl", out _), "A musl RID without an architecture was accepted.");
        Assert.False(BundleTarget.TryParse("win", out _), "An OS RID without an architecture was accepted.");
    }

    [Fact]
    static void RejectsIncompatibleFormats()
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
        foreach (var rid in new[] { "linux-musl-x86_64", "linux-musl-aarch64" })
        {
            var muslArchive = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = rid, InputDirectory = "unused",
                Formats = [PackageFormat.Zip, PackageFormat.TarGz, PackageFormat.AlpineApk, PackageFormat.AppImage]
            });
            Assert.False(BundleConfigurationValidator.Validate(muslArchive, checkFileSystem: false) .Any(issue => issue.Path == "targets[0].formats"), $"Zip/TarGz/AlpineApk/AppImage must accept the musl target {rid}.");
            var muslGlibc = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = rid, InputDirectory = "unused",
                Formats = [PackageFormat.Deb, PackageFormat.Rpm]
            });
            var muslIssues = BundleConfigurationValidator.Validate(muslGlibc, checkFileSystem: false)
                .Where(issue => issue.Path == "targets[0].formats").ToArray();
            Assert.True(muslIssues.Length == 2,
                $"Deb/Rpm must each be rejected for the musl target {rid} (glibc-distro semantics).");
        }
    }

    [Fact]
    static void AddsAppDependencyBeforeDmg()
    {
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "macos-arm64",
            InputDirectory = "unused",
            Formats = [PackageFormat.Dmg]
        });

        var plan = BundlePlanner.Create(configuration, checkFileSystem: false);
        Assert.Equal(2, plan.Items.Count);
        Assert.True(plan.Items[0].Format == PackageFormat.App && plan.Items[0].Intermediate,
            "The intermediate app step must be first.");
        Assert.True(plan.Items[1].Format == PackageFormat.Dmg && !plan.Items[1].Intermediate,
            "The requested DMG step must be last.");
    }

    [Fact]
    static void RejectsExecutablePathEscape()
    {
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "windows-x86_64",
            InputDirectory = "unused",
            MainExecutable = "../Other.exe",
            SigningFiles = ["../Other.dll"],
            Formats = [PackageFormat.Nsis]
        });

        var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
        Assert.Contains(issues, issue => issue.Path.EndsWith("mainExecutable", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path.Contains("signingFiles", StringComparison.Ordinal));
    }

    [Fact]
    static void RejectsMalformedUpdateSection()
    {
        var configuration = ValidConfiguration(new BundleTargetConfiguration
        {
            Target = "linux-x86_64",
            InputDirectory = "unused",
            Formats = [PackageFormat.Zip]
        }, update: new UpdateBundleConfiguration
        {
            FeedUrl = "",
            Channel = "../escape",
            PublicKey = "not-a-point",
            SigningKeyFile = null,
        });

        var issues = BundleConfigurationValidator.Validate(configuration, checkFileSystem: false);
        Assert.Contains(issues, issue => issue.Path == "update.feedUrl");
        Assert.Contains(issues, issue => issue.Path == "update.channel");
        Assert.Contains(issues, issue => issue.Path == "update.signingKeyFile");
        Assert.Contains(issues, issue => issue.Path == "update.publicKey");
    }

    [Fact]
    static void RejectsMismatchedUpdateKeys()
    {
        // 旁车嵌的 publicKey 与 signingKeyFile 私钥不配对 = 装出来的客户端验签全拒。
        var directory = Path.Combine(Path.GetTempPath(), "bundler-test-" + Guid.NewGuid().ToString("N"));
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
            Assert.Equal("F0F5B0E81317B8600CE4D5B2BEAC3A08A51DD808FD7596FE5190F3B861455D54",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath))));

            var toolset = await NsisToolResolver.ResolveAsync(archive, cache);
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
    static void WritesValidWindowsUninstallCommand()
    {
        var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                input,
                "ExampleApp.exe",
                "output",
                false);

            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
    static void RejectsUnknownTemplateVariables()
    {
        var exception = Assert.ThrowsAny<InvalidDataException>(
            () => TemplateRenderer.Render("{{known}} {{missing}}", new Dictionary<string, string> { ["known"] = "value" }));
        Assert.Contains("missing", exception.Message);
    }

    [Fact]
    static void LetsUsersChooseInstallDirectory()
    {
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
        Assert.Contains("!insertmacro MUI_PAGE_DIRECTORY", template);
        Assert.True(template.Contains("ReadRegStr $0 SHCTX \"${UNINSTALL_KEY}\" \"InstallRoot\"", StringComparison.Ordinal) &&
               template.Contains("MULTIUSER_INSTALLMODE_DEFAULT_REGISTRY_VALUENAME \"InstallRoot\"", StringComparison.Ordinal) &&
               template.Contains("${If} $INSTDIR == \"placeholder\\${INSTALL_FOLDER}\"", StringComparison.Ordinal),
            "Fixed and selectable install scopes should restore their previously selected install directories.");
    }

    [Fact]
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
            Assert.True(results.Select(result => result.DirectoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
                "Concurrent tool resolution did not converge on one cache entry.");

            var executable = results[0].ExecutablePath;
            await File.WriteAllTextAsync(executable, "corrupted");
            await File.WriteAllTextAsync(Path.Combine(results[0].DirectoryPath, "unexpected.txt"), "corrupted");
            var repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive);
            Assert.Equal("trusted executable", await File.ReadAllTextAsync(repaired.ExecutablePath));
            Assert.False(File.Exists(Path.Combine(repaired.DirectoryPath, "unexpected.txt")), "Unexpected cache content was not removed during recovery.");

            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "keep");
            var link = Path.Combine(repaired.DirectoryPath, "linked-outside");
            Directory.CreateSymbolicLink(link, outside);
            repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive);
            Assert.True(File.Exists(sentinel) && !Directory.Exists(link),
                "Cache recovery followed or retained a directory reparse point.");
            Directory.Delete(repaired.DirectoryPath, recursive: true);
            await File.WriteAllTextAsync(repaired.DirectoryPath, "directory replaced by a file");
            repaired = await ZipToolCache.ResolveToolAsync(archivePath, cache, archive);
            Assert.True(Directory.Exists(repaired.DirectoryPath) && await File.ReadAllTextAsync(repaired.ExecutablePath) == "trusted executable",
                "A cache directory replaced by a file was not rebuilt.");

            var linkedCache = Path.Combine(root, "linked-cache");
            Directory.CreateSymbolicLink(linkedCache, outside);
            await Assert.ThrowsAnyAsync<InvalidDataException>(
                () => ZipToolCache.ResolveToolAsync(archivePath, linkedCache, archive));
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
                await Assert.ThrowsAnyAsync<InvalidDataException>(
                    () => ZipToolCache.ResolveToolAsync(archivePath, Path.Combine(root, "cache"), descriptor));
            }
            Assert.False(File.Exists(Path.Combine(root, "escaped.exe")), "Archive traversal wrote outside the cache staging directory.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    static async Task RecoversAndSerializesEmbeddedNsisResources()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Resource.Tests", "资源", Guid.NewGuid().ToString("N"));
        try
        {
            var resources = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => NsisEmbeddedResources.MaterializeAsync(root, CancellationToken.None)));
            Assert.True(resources.Select(item => item.TemplatePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
                "Concurrent embedded resource materialization did not converge on one immutable path.");
            var expected = await File.ReadAllTextAsync(resources[0].TemplatePath);
            await File.WriteAllTextAsync(resources[0].TemplatePath, "corrupted");
            var repaired = await NsisEmbeddedResources.MaterializeAsync(root, CancellationToken.None);
            Assert.Equal(expected, await File.ReadAllTextAsync(repaired.TemplatePath));
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
                Target = "windows-x86_64",
                InputDirectory = input,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                input,
                "ExampleApp.exe",
                Path.Combine(root, "output"),
                false);
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
    static void RejectsInvalidWindowsPayloadNames()
    {
        Assert.SkipWhen(TestPlatform.IsWindows, "Windows hosts cannot materialize these payload names");
        foreach (var badName in new[] { "con.dll", "a|b.txt", "ends." })
        {
            var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                    new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
                    PackageFormat.Nsis,
                    input,
                    "ExampleApp.exe",
                    Path.Combine(root, "output"),
                    false);
                var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
    static void RejectsInvalidWindowsResourceTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        try
        {
            File.WriteAllText(Path.Combine(input, "ExampleApp.exe"), "fixture");
            var resource = Path.Combine(root, "resource.txt");
            File.WriteAllText(resource, "resource");
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
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
    static void RejectsPercentInUninstallerFinalizeDestination()
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
    static async Task SignsPeFileWithoutWindowsSdk()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");

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
                "windows-x86_64"));

            Assert.True(SignedFileCertificates.EmbeddedSignatureContains(target, certificate),
                "The signed PE file did not contain the expected test certificate.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    static void SelectsEveryBundledNsisHostCompiler()
    {
        Assert.Equal("hosts/win-x86/makensis.exe",
            NsisToolResolver.GetCompilerRelativePath(OSPlatform.Windows, Architecture.X64).Replace('\\', '/'));
        Assert.Equal("hosts/linux-x64/makensis",
            NsisToolResolver.GetCompilerRelativePath(OSPlatform.Linux, Architecture.X64).Replace('\\', '/'));
        Assert.Equal("hosts/linux-arm64/makensis",
            NsisToolResolver.GetCompilerRelativePath(OSPlatform.Linux, Architecture.Arm64).Replace('\\', '/'));
        Assert.Equal("hosts/osx-x64/makensis",
            NsisToolResolver.GetCompilerRelativePath(OSPlatform.OSX, Architecture.X64).Replace('\\', '/'));
        Assert.Equal("hosts/osx-arm64/makensis",
            NsisToolResolver.GetCompilerRelativePath(OSPlatform.OSX, Architecture.Arm64).Replace('\\', '/'));
    }

    [Fact]
    static void ComparesSemanticVersionsForInstallerPolicy()
    {
        static int Compare(string left, string right)
        {
            Assert.True(SemanticVersion.TryParse(left, out var leftVersion), $"Could not parse '{left}'.");
            Assert.True(SemanticVersion.TryParse(right, out var rightVersion), $"Could not parse '{right}'.");
            return Math.Sign(leftVersion!.CompareTo(rightVersion));
        }

        Assert.Equal(0, Compare("1.0.0", "1.0.0"));
        Assert.Equal(0, Compare("1.0.0+build.2", "1.0.0+build.1"));
        Assert.True(Compare("1.0.0", "1.0.0-rc.1") > 0, "A release must be newer than its prerelease.");
        Assert.True(Compare("1.0.0-beta.11", "1.0.0-beta.2") > 0,
            "Numeric prerelease identifiers must compare numerically.");
        Assert.True(Compare("2.0.0-alpha", "10.0.0-alpha") < 0,
            "Core numeric identifiers must compare numerically.");
        Assert.False(SemanticVersion.TryParse("1.0", out _), "SemVer requires major, minor, and patch.");
        Assert.False(SemanticVersion.TryParse("1.0.0-01", out _), "Numeric prerelease identifiers must reject leading zeroes.");
        Assert.True(SemanticVersion.TryParse("65536.0.0", out var oversized) &&
               !oversized!.TryGetWindowsNumericVersion(out _),
            "Windows version resources must reject components above 65535.");
    }

    [Fact]
    static Task RejectsInvalidNsisPackageVersions()
    {
        foreach (var version in new[] { "1.0", "1.0.0-01", "65536.0.0" })
        {
            var exception = Assert.ThrowsAny<ArgumentException>(
                () => new NsisBundler().BuildAsync(new BundleConfiguration { Version = version })
                    .GetAwaiter().GetResult());
            Assert.Contains(version, exception.Message);
        }

        return Task.CompletedTask;
    }

    [Fact]
    static async Task RejectsInvalidLegacyMsiIdentifiers()
    {
        var bundler = new NsisBundler(new NsisBundleConfiguration
        {
            LegacyMsiProductCodes = ["not-a-guid"]
        });
        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" }));
        Assert.Contains("not-a-guid", exception.Message);

        bundler = new NsisBundler(new NsisBundleConfiguration
        {
            LegacyMsiUpgradeCodes = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid().ToString()).ToArray()
        });
        var exception2 = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" }));
        Assert.Contains("string limit", exception2.Message);
    }

    [Fact]
    static void RejectsInvalidAssociationsAndProtocols()
    {
        var target = new BundleTargetConfiguration
        {
            Target = "windows-x86_64",
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
        Assert.True(issues.Any(issue => issue.Path.Contains("fileAssociations", StringComparison.Ordinal) &&
                                   issue.Message.Contains("Duplicate", StringComparison.Ordinal)),
            "File extensions should be unique without regard to case or an optional leading dot.");
        Assert.True(issues.Any(issue => issue.Path.Contains("fileAssociations", StringComparison.Ordinal) &&
                                   issue.Message.Contains("1-64", StringComparison.Ordinal)),
            "Unsafe file-extension registry paths should be rejected.");
        Assert.True(issues.Any(issue => issue.Path.Contains("urlProtocols", StringComparison.Ordinal) &&
                                   issue.Message.Contains("beginning with a letter", StringComparison.Ordinal)),
            "URL schemes should follow URI scheme syntax.");
        Assert.True(issues.Any(issue => issue.Path.Contains("urlProtocols", StringComparison.Ordinal) &&
                                   issue.Message.Contains("Duplicate", StringComparison.Ordinal)),
            "URL schemes should be unique without regard to case.");
    }

    [Fact]
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
                      "target": "windows-x86_64",
                      "inputDirectory": "publish",
                      "mainExecutable": "Configured.exe",
                      "signingFiles": ["Helper.dll"],
                      "formats": ["nsis"]
                    }
                  ]
                }
                """);

            var configuration = await BundleConfigurationLoader.LoadAsync(path);
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

    [Fact]
    static async Task BuildsThroughStandaloneNsisApi()
    {
        // 内嵌 makensis 是 glibc 链接产物，musl 宿主上无法执行
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
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
                        Target = "windows-x86_64",
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
            Assert.True(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
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

    [Fact]
    static async Task BuildsEveryBuiltInNsisLanguage()
    {
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
        string[] expectedLanguages =
        [
            "Arabic", "Bulgarian", "Dutch", "English", "French", "German", "Italian", "Japanese",
            "Korean", "Norwegian", "Persian", "Portuguese", "PortugueseBR", "Russian", "SimpChinese",
            "Spanish", "SpanishInternational", "Swedish", "TradChinese", "Turkish", "Ukrainian", "Vietnamese"
        ];
        Assert.Equal(NsisBundler.SupportedLanguages, expectedLanguages);

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
                        Target = "windows-x86_64",
                        InputDirectory = input,
                        MainExecutable = "示例应用.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            });
            Assert.True(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
                "NSIS did not compile one installer containing every built-in language.");

            var duplicateLanguage = await Assert.ThrowsAnyAsync<ArgumentException>(
                () => new NsisBundler(new NsisBundleConfiguration { Languages = ["English", "english"] })
                    .BuildAsync(new BundleConfiguration { Version = "1.0.0" }));
            Assert.Contains("more than once", duplicateLanguage.Message, StringComparison.Ordinal);

            var unsupportedLanguage = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => new NsisBundler(new NsisBundleConfiguration { Languages = ["Klingon"] })
                    .BuildAsync(new BundleConfiguration { Version = "1.0.0" }));
            Assert.Contains("Unsupported NSIS language", unsupportedLanguage.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // 自定义语言文件整份替换内置语言，必须覆盖内置模板声明的全部 LangString 键——
    // 新增必填键时样品的 Assets/nsis/*.nsh 忘同步会让样品 nsis 腿直接拒产。
    [Fact]
    static void SampleCustomNsisLanguageFilesCoverBuiltInKeys()
    {
        var requiredKeys = File.ReadLines(Path.Combine(
                RepositoryRoot(), "src", "Bundler.Nsis", "templates", "languages", "English.nsh"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("LangString ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(requiredKeys);

        // 只校验样品声明为 BundlerNsisLanguageFile 的文件——同目录的
        // installer-hooks.nsh 是钩入文件不是语言文件。
        var props = XDocument.Load(Path.Combine(
            RepositoryRoot(), "samples", "HelloBundlerApp", "formats", "Nsis.props"));
        var customFiles = props.Descendants("BundlerNsisLanguageFile")
            .Select(item => (string?)item.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => Path.GetFullPath(include!
                .Replace("$(HelloBundlerAppAssets)",
                    Path.Combine(RepositoryRoot(), "samples", "HelloBundlerApp", "Assets") + Path.DirectorySeparatorChar)
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
    static async Task ValidatesCustomNsisLanguageFiles()
    {
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.CustomLanguage.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "custom-language-test");
        var english = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "languages", "English.nsh"));
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
                await Assert.ThrowsAnyAsync<InvalidDataException>(
                    () => bundler.BuildAsync(LanguageTestConfiguration(input, Path.Combine(root, "artifacts", name))));
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
            Assert.True(validArtifacts.Count == 1 && File.Exists(validArtifacts[0].Path),
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
                Target = "windows-x86_64",
                InputDirectory = input,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            }
        ]
    };

    [Fact]
    static async Task BuildsEveryNsisCompressionMode()
    {
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
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
                            Target = "windows-x86_64",
                            InputDirectory = input,
                            MainExecutable = "ExampleApp.exe",
                            Formats = [PackageFormat.Nsis]
                        }
                    ]
                });
                Assert.True(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
                    $"NSIS failed to compile an installer with {compression} compression.");
            }

            var unknownCompression = await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(
                () => new NsisBundler(new NsisBundleConfiguration { Compression = (NsisCompression)999 })
                    .BuildAsync(new BundleConfiguration { Version = "1.0.0" }));
            Assert.Contains("compression", unknownCompression.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    static async Task SignsBothNsisInstallerArtifacts()
    {
        Assert.SkipUnless(TestPlatform.IsWindows, "requires a Windows host");

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
                        Target = "windows-x86_64",
                        InputDirectory = input + Path.DirectorySeparatorChar,
                        MainExecutable = "ExampleApp.exe",
                        SigningFiles = ["Sidecar.dll"],
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            });

            Assert.Equal(new[] { BundleSigningArtifactKind.PayloadExecutable, BundleSigningArtifactKind.PayloadFile, BundleSigningArtifactKind.NativeComponent, BundleSigningArtifactKind.Uninstaller, BundleSigningArtifactKind.Installer }, signer.ArtifactKinds);
            Assert.True(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mainExecutable))) == originalMainHash &&
                   Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecar))) == originalSidecarHash,
                "NSIS signing modified the caller's input directory instead of a staging copy.");
            Assert.True(signer.Paths[0].Contains("signed-payload", StringComparison.OrdinalIgnoreCase) &&
                   signer.Paths[2].Contains("signed-plugins", StringComparison.OrdinalIgnoreCase),
                "Payload or Bundler native components were not signed from private staging directories.");
            var cachedPlugin = Directory.EnumerateFiles(
                Path.Combine(root, "shared-tools"),
                "DotNetBundlerNsis.dll",
                SearchOption.AllDirectories).Single();
            var repositoryPlugin = Path.Combine(RepositoryRoot(), "third_party", "nsis", "plugins", "x86-unicode", "DotNetBundlerNsis.dll");
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(cachedPlugin)), SHA256.HashData(File.ReadAllBytes(repositoryPlugin)));
            Assert.True(SignedFileCertificates.EmbeddedSignatureContains(artifacts[0].Path, certificate),
                "The final NSIS installer did not contain the expected signing certificate.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
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
                "windows-x86_64"));
            var record = await File.ReadAllTextAsync(log);
            Assert.Equal($"{payload}|PayloadExecutable|windows-x86_64", record.Trim());

            const string secret = "must-not-leak-provider-secret";
            var failingArguments = arguments.Concat([secret]).ToArray();
            var failingSigner = new WindowsExternalCommandSigner(new WindowsExternalCommandSigningOptions
            {
                Command = processPath,
                Arguments = failingArguments
            });
            var signingFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => failingSigner.SignAsync(new BundleSigningRequest(
                    payload,
                    BundleSigningArtifactKind.Installer,
                    "External signing fixture",
                    "windows-x86_64")));
            Assert.Contains("exit code 17", signingFailure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, signingFailure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    static async Task RemovesFailedSignedInstaller()
    {
        Assert.SkipWhen(TestPlatform.IsMusl, "bundled makensis is glibc-linked and cannot run on musl hosts");
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
            var finalSigningFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => bundler.BuildAsync(new BundleConfiguration
                {
                    ProductName = "Failed Signing App",
                    Identifier = "com.example.failed-signing",
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
                }));
            Assert.Equal("fixture signing failure", finalSigningFailure.Message);
            var installer = Path.Combine(output, "Failed Signing App-1.0.0-setup.exe");
            Assert.False(File.Exists(installer), "A signing failure left a final installer that could be mistaken for success.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    static async Task RejectsPayloadSigningFilesWithoutSigner()
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
            }));
        Assert.Contains("no bundle signer", unsignedSigner.Message, StringComparison.Ordinal);
    }

    [Fact]
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
                Target = "windows-x86_64",
                InputDirectory = root,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
    static void ProvidesInteractiveNsisSafetyOptions()
    {
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
        Assert.True(template.Contains("UninstPage custom un.AppDataOptionsPage", StringComparison.Ordinal) &&
               template.Contains("$LOCALAPPDATA\\${PRODUCT_ID}", StringComparison.Ordinal),
            "The uninstaller should offer optional application-data deletion.");
    }

    [Fact]
    static void PinsInstallRecoveryToInstallerManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
    static void RendersNsisAutomationProtocol()
    {
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
            var strings = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "languages", language));
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
    static void RendersExistingVersionPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
                        Target = "windows-x86_64",
                        InputDirectory = root,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            };
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                root,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
    static void RendersNsisInstallScopes()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");

        try
        {
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
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
    static void RendersEveryNsisCompressionMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Compression.Rendering.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ExampleApp.exe"), "test");
        try
        {
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
            var configuration = ValidConfiguration(new BundleTargetConfiguration
            {
                Target = "windows-x86_64",
                InputDirectory = root,
                MainExecutable = "ExampleApp.exe",
                Formats = [PackageFormat.Nsis]
            });
            var item = new BundlePlanItem(
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
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
    static void RendersNsisMetadataIconsAndResources()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
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
                new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
                PackageFormat.Nsis,
                input,
                "ExampleApp.exe",
                "output",
                false);
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
                        Target = "windows-x86_64",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            };

            var artifacts = await new BundlePipeline([backend], logger).BuildAsync(configuration);
            Assert.True(artifacts.Count == 1 && File.Exists(artifacts[0].Path),
                "The common pipeline should return an existing backend artifact.");
            Assert.True(backend.WorkDirectory is not null && !Directory.Exists(backend.WorkDirectory),
                "The common pipeline should clean its backend work directory.");
            Assert.Contains(logger.Messages, message => message.Contains("Created", StringComparison.Ordinal));
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
                        Target = "windows-x86_64",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis, PackageFormat.Msi]
                    }
                ]
            };

            var missingBackend = await Assert.ThrowsAnyAsync<NotSupportedException>(
                () => new BundlePipeline([backend]).BuildAsync(configuration));
            Assert.Contains("Msi", missingBackend.Message);
            Assert.Equal(0, backend.InvocationCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    static async Task RejectsArtifactPathCollisionAcrossTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "publish");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "ExampleApp.exe"), "test");

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
                        Target = "windows-x86_64",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    },
                    new BundleTargetConfiguration
                    {
                        Target = "windows-i686",
                        InputDirectory = input,
                        MainExecutable = "ExampleApp.exe",
                        Formats = [PackageFormat.Nsis]
                    }
                ]
            };

            var backend = new RecordingBackend();
            var collision = await Assert.ThrowsAnyAsync<ArgumentException>(
                () => new BundlePipeline([backend]).BuildAsync(configuration));
            Assert.Contains("collision", collision.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
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
                Targets = [new BundleTargetConfiguration { Target = "windows-x86_64", InputDirectory = input, MainExecutable = "NewApp.exe", Formats = [PackageFormat.Nsis] }]
            };
            var item = new BundlePlanItem(new BundleTarget("windows-x86_64", DesktopOperatingSystem.Windows, CpuArchitecture.X64), PackageFormat.Nsis, input, "NewApp.exe", "output", false);
            var settings = new NsisBundleConfiguration
            {
                Shortcuts = new NsisShortcutConfiguration
                {
                    Desktop = false, StartMenu = true, Arguments = "--profile demo", WorkingDirectory = "data",
                    Icon = "assets/shortcut.ico", AppUserModelId = "com.example.newapp.desktop",
                    StartMenuFolder = "Example Publisher", LegacyProductNames = ["Old App"], LegacyMainExecutables = ["OldApp.exe"]
                }
            };
            var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.Nsis", "templates", "installer.nsi"));
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
    static async Task RejectsUnsafeShortcutConfiguration()
    {
        var bundler = new NsisBundler(new NsisBundleConfiguration { Shortcuts = new NsisShortcutConfiguration { WorkingDirectory = "..\\outside" } });
        var unsafeShortcut = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => bundler.BuildAsync(new BundleConfiguration { Version = "1.0.0" }));
        Assert.Contains("relative", unsafeShortcut.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    static void MapsNsisSettingsThroughMsBuild()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
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
    static void PassesValidationIssueDetailsThroughMsBuild()
    {
        var task = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(task.Contains("catch (BundleValidationException", StringComparison.Ordinal) &&
               task.Contains("issue.Path") && task.Contains("issue.Message"),
            "The MSBuild task must log each validation issue's path and message, not only the count.");
    }

    [Fact]
    static void KeepsPackageConsumerVersionsAligned()
    {
        var root = RepositoryRoot();
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
            Path.Combine(root, "tests", "Bundler.IntegrationTests", "Fixtures", "Nsis", "Fixture", "BundlerNsisIntegrationFixture.csproj"),
            Path.Combine(root, "tests", "Bundler.IntegrationTests", "Fixtures", "Deb", "BundlerDebIntegrationFixture.csproj"),
            Path.Combine(root, "tests", "Bundler.IntegrationTests", "Fixtures", "Rpm", "BundlerRpmIntegrationFixture.csproj")
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
            "The merged sample must expose the executable name and per-RID format selection without legacy pass-throughs.");
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
                   ((string?)item.Attribute("Project"))?.Contains("formats\\" + format + ".props", StringComparison.Ordinal) == true),
                "The merged sample must import formats/" + format + ".props.");
        }
        Assert.Equal("BundlerIntegrationFixture",
            XDocument.Load(Path.Combine(root, "tests", "Bundler.IntegrationTests", "Fixtures", "Nsis", "Fixture",
                "BundlerNsisIntegrationFixture.csproj"))
            .Descendants("AssemblyName").Single().Value);
        var apiTests = XDocument.Load(Path.Combine(root, "tests", "Bundler.ApiTests", "Bundler.ApiTests.csproj"));
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
        foreach (var path in new[]
        {
            Path.Combine(root, "tests", "Bundler.LocalPackagesTests", "Fixtures", "Msi", "Fixture", "BundlerMsiSmoke.csproj"),
            Path.Combine(root, "tests", "Bundler.LocalPackagesTests", "Fixtures", "Msi", "Standalone", "Msi.Api.PackageFixture.csproj")
        })
        {
            var project = XDocument.Load(path);
            Assert.True(project.Descendants("PackageReference").Any(item =>
                       ((string?)item.Attribute("Include"))?.StartsWith("DotNet.Bundler", StringComparison.Ordinal) == true &&
                       (string?)item.Attribute("VersionOverride") == "$(BundlerPackageVersion)") &&
                   project.Descendants("Import").Any(item =>
                       ((string?)item.Attribute("Project"))?.Contains("Bundler.LocalPackages.props", StringComparison.Ordinal) == true),
                "The standalone package-consumption fixture must keep its package reference: " + path);
        }
        var layout = File.ReadAllText(Path.Combine(
            root, "tests", "Shared", "Tooling", "RepositoryLayout.cs"));
        Assert.Contains("Directory.Build.props", layout, StringComparison.Ordinal);
        Assert.Contains("BundlerPackageVersion", layout, StringComparison.Ordinal);
    }

    static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);


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
        return [new BundleArtifact(Format, context.Item.Target.Target, path)];
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
