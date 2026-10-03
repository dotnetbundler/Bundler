using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.Cli;
using DotNet.Bundler.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

public static class AlpineApkTests
{

    [Fact]
    static void RejectsNonApkFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var thrown = false;
            try
            {
                new AlpineApkBundler()
                    .BuildAsync(ApkConfiguration(input, formats: [PackageFormat.Deb]))
                    .GetAwaiter().GetResult();
            }
            catch (NotSupportedException exception)
            {
                thrown = exception.Message.Contains("AlpineApk targets only");
            }
            Assert.True(thrown, "A non-apk target must be rejected with NotSupportedException.");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void ProducesValidApk()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new AlpineApkBundler()
                .BuildAsync(ApkConfiguration(input, output))
                .GetAwaiter().GetResult();
            var artifact = artifacts.Single();
            var expectedName = "example-app-1.0.0-r0.apk";
            Assert.EndsWith(Path.Combine("linux-musl-x64", "apk", expectedName), artifact.Path);

            var apk = File.ReadAllBytes(artifact.Path);
            var starts = ApkPackageReader.GzipMemberOffsets(apk);
            var segments = ApkPackageReader.SplitGzipStreams(apk);
            Assert.Equal(2, segments.Count);
            var dataGzipBytes = apk.Skip(starts[1]).ToArray();

            var control = ApkPackageReader.ReadTar(segments[0]);
            var data = ApkPackageReader.ReadTar(segments[1]);

            var pkginfo = control.SingleOrDefault(e => e.Name == ".PKGINFO");
            Assert.NotNull(pkginfo);
            Assert.True(control.All(e => !e.Name.StartsWith(".SIGN.", StringComparison.Ordinal)),
                "An unsigned package must not contain .SIGN.* members.");
            var fields = PkgInfoFields(pkginfo!.Content);
            Assert.Equal("example-app", fields["pkgname"]);
            Assert.Equal("1.0.0-r0", fields["pkgver"]);
            Assert.Equal("Example application", fields["pkgdesc"]);
            Assert.Equal("https://example.com/app", fields["url"]);
            Assert.Equal("x86_64", fields["arch"]);
            Assert.Equal("example-app", fields["origin"]);
            Assert.Equal("0", fields["builddate"]);
            Assert.Equal(Sha256Hex(dataGzipBytes), fields["datahash"]);
            var payloadSize = data.Where(e => e.TypeFlag == '0').Sum(e => (long)e.Content.Length);
            Assert.Equal(payloadSize.ToString(), fields["size"]);

            var names = data.Select(e => e.Name).ToArray();
            Assert.Contains("usr/lib/example-app/ExampleApp", names);
            Assert.Contains("usr/lib/example-app/ExampleApp.dll", names);
            var exe = data.Single(e => e.Name == "usr/lib/example-app/ExampleApp");
            Assert.True(exe.Mode == 493,
                $"The main executable must have mode 0755, got {Convert.ToString(exe.Mode, 8)}.");
            var dll = data.Single(e => e.Name == "usr/lib/example-app/ExampleApp.dll");
            Assert.True(dll.Mode == 420,
                $"Data files must have mode 0644, got {Convert.ToString(dll.Mode, 8)}.");
            Assert.True(data.Where(e => e.TypeFlag == '5').All(e => e.Mode == 493),
                "Directories must have mode 0755.");
            Assert.True(data.Where(e => e.TypeFlag == '5').All(e => e.Name.EndsWith('/')),
                "Directory entries must carry the trailing '/' marker.");
            var link = data.SingleOrDefault(e => e.Name == "usr/bin/example-app");
            Assert.True(link is { TypeFlag: '2' } && link.LinkTarget == "../lib/example-app/ExampleApp",
                $"usr/bin symlink mismatch: {link?.LinkTarget}");

            Assert.True(data.All(e =>
                    e.Pax.TryGetValue("ctime", out var ctime) && ctime == "0" &&
                    e.Pax.TryGetValue("atime", out var atime) && atime == "0"),
                "Every entry needs atime=0/ctime=0 pax records like abuild output.");
            foreach (var file in data.Where(e => e.TypeFlag is '0' or '2'))
            {
                Assert.True(file.Pax.TryGetValue("APK-TOOLS.checksum.SHA1", out var sha1),
                    $"Entry {file.Name} lacks the APK-TOOLS.checksum.SHA1 pax record.");
                using var hasher = SHA1.Create();
                var hashed = file.TypeFlag == '2'
                    ? Encoding.UTF8.GetBytes(file.LinkTarget)
                    : file.Content;
                Assert.True(Convert.ToHexString(hasher.ComputeHash(hashed)).ToLowerInvariant() == sha1,
                    $"SHA1 checksum mismatch for {file.Name}.");
            }

            // Signature/control segments carry no trailing zero blocks; the data
            // segment ends the archive and keeps the two 512-byte zero records.
            Assert.False(EndsWithZeroBlocks(segments[0]), "The control segment must not end with zero blocks.");
            Assert.True(EndsWithZeroBlocks(segments[1]),
                "The data segment must end with the two 512-byte zero records.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void MapsOverrides()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new AlpineApkBundler(new AlpineApkBundleConfiguration
            {
                PackageName = "custom-pkg",
                Version = "2.5.0",
                Architecture = "aarch64",
                Origin = "custom-origin",
                Description = "Custom description",
                Url = "https://example.com/custom",
                BinLink = "custom-cli"
            }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            var artifact = artifacts.Single();
            Assert.EndsWith("custom-pkg-2.5.0-r0.apk", artifact.Path);

            var segments = ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(artifact.Path));
            var fields = PkgInfoFields(
                ApkPackageReader.ReadTar(segments[0]).Single(e => e.Name == ".PKGINFO").Content);
            Assert.True(fields["pkgver"] == "2.5.0-r0" && fields["arch"] == "aarch64" &&
                fields["origin"] == "custom-origin" && fields["pkgdesc"] == "Custom description" &&
                fields["url"] == "https://example.com/custom",
                $"Override fields missing: {string.Join(',', fields.Select(kv => kv.Key + '=' + kv.Value))}");
            var data = ApkPackageReader.ReadTar(segments[1]);
            Assert.Contains(data, e => e.Name == "usr/lib/custom-pkg/ExampleApp");
            var link = data.Single(e => e.Name == "usr/bin/custom-cli");
            Assert.Equal("../lib/custom-pkg/ExampleApp", link.LinkTarget);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void DisablesBinLink()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new AlpineApkBundler(new AlpineApkBundleConfiguration { BinLink = "" })
                .BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            var data = ApkPackageReader.ReadTar(
                ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(artifacts.Single().Path))[1]);
            Assert.False(data.Any(e => e.Name.StartsWith("usr/bin", StringComparison.Ordinal)), "An empty BinLink must not create a usr/bin entry.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void RejectsInvalidSettings()
    {
        var input = CreateInputDirectory();
        try
        {
            foreach (var (settings, label) in new (AlpineApkBundleConfiguration, string)[]
            {
                (new AlpineApkBundleConfiguration { PackageName = "-bad-name-" }, "package name"),
                (new AlpineApkBundleConfiguration { PackageName = "bad name" }, "package name"),
                (new AlpineApkBundleConfiguration { Version = "1.0$bad" }, "version"),
                (new AlpineApkBundleConfiguration { Architecture = "AMD64" }, "architecture"),
                (new AlpineApkBundleConfiguration { BinLink = "a/b" }, "bin link")
            })
            {
                var thrown = false;
                try
                {
                    new AlpineApkBundler(settings)
                        .BuildAsync(ApkConfiguration(input)).GetAwaiter().GetResult();
                }
                catch (ArgumentException)
                {
                    thrown = true;
                }
                Assert.True(thrown, $"Invalid {label} must be rejected: {settings}");
            }
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void DerivesKebabName()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = ApkConfiguration(input, output, productName: "My Fancy App!");
            var artifacts = new AlpineApkBundler().BuildAsync(configuration).GetAwaiter().GetResult();
            Assert.EndsWith("my-fancy-app-1.0.0-r0.apk", artifacts.Single().Path);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void MapsMuslArchitectures()
    {
        foreach (var (rid, arch) in new[] { ("linux-musl-x64", "x86_64"), ("linux-musl-arm64", "aarch64") })
        {
            var input = CreateInputDirectory();
            var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                var artifacts = new AlpineApkBundler()
                    .BuildAsync(ApkConfiguration(input, output, rid)).GetAwaiter().GetResult();
                var segments = ApkPackageReader.SplitGzipStreams(
                    File.ReadAllBytes(artifacts.Single().Path));
                var fields = PkgInfoFields(
                    ApkPackageReader.ReadTar(segments[0]).Single(e => e.Name == ".PKGINFO").Content);
                Assert.True(fields["arch"] == arch,
                    $"RID {rid} must map to arch {arch}, got {fields["arch"]}");
            }
            finally
            {
                Cleanup(input, output);
            }
        }
    }

    [Fact]
    static void RejectsNonMuslTargets()
    {
        var input = CreateInputDirectory();
        try
        {
            foreach (var rid in new[] { "linux-x64", "win-x64", "osx-arm64" })
            {
                var thrown = false;
                try
                {
                    new AlpineApkBundler()
                        .BuildAsync(ApkConfiguration(input, rid: rid)).GetAwaiter().GetResult();
                }
                catch (NotSupportedException exception)
                {
                    thrown = exception.Message.Contains("No backend is registered");
                }
                catch (ArgumentException exception)
                {
                    thrown = exception.Message.Contains("validation error");
                }
                catch (BundleValidationException)
                {
                    thrown = true;
                }
                Assert.True(thrown, $"apk must be refused on non-musl rid {rid}.");
            }

            // The matrix keeps the validator symmetric: apk on a glibc Linux
            // target is a configuration issue, not just a missing backend.
            var glibc = ApkConfiguration(input, rid: "linux-x64");
            Assert.Contains(BundleConfigurationValidator.Validate(glibc, checkFileSystem: false), issue => issue.Path == "targets[0].formats");
            var musl = ApkConfiguration(input, rid: "linux-musl-arm64");
            Assert.False(BundleConfigurationValidator.Validate(musl, checkFileSystem: false) .Any(issue => issue.Path == "targets[0].formats"), "The validator must accept AlpineApk on linux-musl-arm64.");
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void StagesResources()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var resourceDir = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(resourceDir);
            File.WriteAllText(Path.Combine(resourceDir, "readme.txt"), "resource content");
            var configuration = ApkConfiguration(input, output, resources:
            [
                new BundleResourceConfiguration
                {
                    Source = resourceDir,
                    TargetPath = "docs"
                }
            ]);
            var artifacts = new AlpineApkBundler().BuildAsync(configuration).GetAwaiter().GetResult();
            var data = ApkPackageReader.ReadTar(
                ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(artifacts.Single().Path))[1]);
            var doc = data.SingleOrDefault(e => e.Name == "usr/lib/example-app/docs/readme.txt");
            Assert.True(doc is not null && Encoding.UTF8.GetString(doc.Content) == "resource content",
                "Resource directory must land under the install root.");
        }
        finally
        {
            Cleanup(input, output, resourceDir);
        }
    }

    [Fact]
    static void DeterministicBytes()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new AlpineApkBundler().BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            var second = new AlpineApkBundler().BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            Assert.Equal(File.ReadAllBytes(first.Single().Path), File.ReadAllBytes(second.Single().Path));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void Sha256Sidecar()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new AlpineApkBundler()
                .BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var sidecarPath = artifact.Path + ".sha256";
            Assert.True(File.Exists(sidecarPath), "The .sha256 sidecar is missing.");
            var content = File.ReadAllText(sidecarPath);
            using var sha256 = SHA256.Create();
            var hash = Hex(sha256.ComputeHash(File.ReadAllBytes(artifact.Path)));
            Assert.Equal(hash + "  " + Path.GetFileName(artifact.Path) + "\n", content);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void ParsesAlpineApkThroughCli()
    {
        var formats = CliProgram.ParseFormats("alpineapk,zip", "linux-musl-x64");
        Assert.Equal(new[] { PackageFormat.AlpineApk, PackageFormat.Zip }, formats);

        var all = CliProgram.ParseFormats("all", "linux-musl-x64");
        Assert.True(all.Contains(PackageFormat.AlpineApk) && all.Contains(PackageFormat.Zip) &&
            all.Contains(PackageFormat.TarGz) && all.Contains(PackageFormat.AppImage) && all.Count == 4,
            $"musl 'all' must expand to zip/targz/alpineapk/appimage: {string.Join(',', all)}");
        var glibcAll = CliProgram.ParseFormats("all", "linux-x64");
        Assert.DoesNotContain(PackageFormat.AlpineApk, glibcAll);

        // plan output uses the 'apk' subdirectory name.
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = CliProgram.Run(
                ["bundle", "--input-dir", input, "--rid", "linux-musl-x64", "--formats", "alpineapk",
                 "--product-name", "CliFixture", "--identifier", "dev.example.cli",
                 "--package-version", "1.0.0", "--main-executable", "ExampleApp",
                 "--output-dir", output],
                stdout, stderr);
            Assert.True(code == 0, $"cli bundle must exit 0, got {code}: {stderr}");
            Assert.True(File.Exists(Path.Combine(output, "linux-musl-x64", "apk", "clifixture-1.0.0-r0.apk")),
                $"CLI-produced .apk missing: {stdout}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void MapsApkSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(targets.Contains("AlpineApkPackageName=\"$(BundlerAlpineApkPackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkVersion=\"$(BundlerAlpineApkVersion)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkArchitecture=\"$(BundlerAlpineApkArchitecture)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkOrigin=\"$(BundlerAlpineApkOrigin)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkDescription=\"$(BundlerAlpineApkDescription)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkUrl=\"$(BundlerAlpineApkUrl)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkBinLink=\"$(BundlerAlpineApkBinLink)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkRelease=\"$(BundlerAlpineApkRelease)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkLicense=\"$(BundlerAlpineApkLicense)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkBuildDate=\"$(BundlerAlpineApkBuildDate)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkDepends=\"$(BundlerAlpineApkDepends)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkProvides=\"$(BundlerAlpineApkProvides)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkTriggers=\"$(BundlerAlpineApkTriggers)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkPreInstallScript=\"$(BundlerAlpineApkPreInstallScript)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkPostInstallScript=\"$(BundlerAlpineApkPostInstallScript)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkPreDeinstallScript=\"$(BundlerAlpineApkPreDeinstallScript)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkPostDeinstallScript=\"$(BundlerAlpineApkPostDeinstallScript)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkPreUpgradeScript=\"$(BundlerAlpineApkPreUpgradeScript)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkPostUpgradeScript=\"$(BundlerAlpineApkPostUpgradeScript)\"", StringComparison.Ordinal) &&
               targets.Contains("AlpineApkFiles=\"@(BundlerAlpineApkFile)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerAlpineApk* properties to the task.");
        Assert.True(props.Contains("<BundlerAlpineApkPackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerAlpineApkBinLink", StringComparison.Ordinal),
            "The BundlerAlpineApk* properties lack defaults in the .props file.");
        Assert.True(task.Contains("new AlpineApkBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.AlpineApk", StringComparison.Ordinal),
            "The MSBuild task does not construct the .apk backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert.Contains("DotNet.Bundler.AlpineApk.dll", msbuildProject);
    }

    [Fact]
    static void WritesReleaseLicenseAndRelations()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new AlpineApkBundler(new AlpineApkBundleConfiguration
            {
                Release = "3",
                License = "MIT",
                BuildDate = "1700000000",
                Depends = ["busybox", "so:libc.musl-x86_64.so.1>=1.2"],
                Provides = ["virtual-example", "example-shim=1.0"],
                Triggers = ["/usr/share/example", "/usr/lib/example-triggers"],
                ExtraPkgInfo = new Dictionary<string, string> { ["install_if"] = "example-gui" }
            }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single();
            Assert.Equal("example-app-1.0.0-r3.apk", Path.GetFileName(artifact.Path));

            var control = ApkPackageReader.ReadTar(
                ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(artifact.Path))[0]);
            var text = Encoding.UTF8.GetString(
                control.Single(e => e.Name == ".PKGINFO").Content);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("pkgver = 1.0.0-r3", lines);
            Assert.Contains("license = MIT", lines);
            Assert.Contains("builddate = 1700000000", lines);
            Assert.True(lines.Count(l => l == "depend = busybox" ||
                l == "depend = so:libc.musl-x86_64.so.1>=1.2") == 2,
                $"depend lines missing: {text}");
            Assert.True(lines.Count(l => l == "provides = virtual-example" ||
                l == "provides = example-shim=1.0") == 2,
                $"provides lines missing: {text}");
            Assert.True(lines.Contains("triggers = /usr/share/example /usr/lib/example-triggers"),
                $"triggers must be a single space-joined line: {text}");
            Assert.True(lines.Contains("install_if = example-gui"),
                $"ExtraPkgInfo escape hatch missing: {text}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void WritesInstallScripts()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scripts);
            string[] scriptFiles =
            [
                "pre-install", "post-install", "pre-deinstall", "post-deinstall",
                "pre-upgrade", "post-upgrade"
            ];
            foreach (var entryName in scriptFiles)
            {
                File.WriteAllText(Path.Combine(scripts, entryName + ".sh"),
                    "#!/bin/sh\necho " + entryName + "\n");
            }
            var all = new AlpineApkBundleConfiguration
            {
                PreInstallScript = Path.Combine(scripts, "pre-install.sh"),
                PostInstallScript = Path.Combine(scripts, "post-install.sh"),
                PreDeinstallScript = Path.Combine(scripts, "pre-deinstall.sh"),
                PostDeinstallScript = Path.Combine(scripts, "post-deinstall.sh"),
                PreUpgradeScript = Path.Combine(scripts, "pre-upgrade.sh"),
                PostUpgradeScript = Path.Combine(scripts, "post-upgrade.sh")
            };
            var artifact = new AlpineApkBundler(all)
                .BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var control = ApkPackageReader.ReadTar(
                ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(artifact.Path))[0]);
            foreach (var name in scriptFiles.Select(n => "." + n))
            {
                var entry = control.SingleOrDefault(e => e.Name == name);
                Assert.True(entry is not null && entry.Mode == 493 &&
                    Encoding.UTF8.GetString(entry.Content).Contains(name.TrimStart('.')),
                    $"Script {name} missing from the control segment.");
            }
        }
        finally
        {
            Cleanup(input, output, scripts);
        }
    }

    [Fact]
    static void MapsArbitraryFileDestinations()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(extra);
            var conf = Path.Combine(extra, "settings.conf");
            File.WriteAllText(conf, "key=value");
            var artifact = new AlpineApkBundler(new AlpineApkBundleConfiguration
            {
                Files = [new AlpineApkFileEntry { Source = conf, Destination = "/etc/example-app/settings.conf" }]
            }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var data = ApkPackageReader.ReadTar(
                ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(artifact.Path))[1]);
            var entry = data.SingleOrDefault(e => e.Name == "etc/example-app/settings.conf");
            Assert.True(entry is not null && entry.Mode == 420 &&
                Encoding.UTF8.GetString(entry.Content) == "key=value",
                $"Mapped file must land at its absolute destination: {string.Join(',', data.Select(e => e.Name))}");
            Assert.True(entry!.Pax.ContainsKey("APK-TOOLS.checksum.SHA1"),
                "Mapped files need the sha1 pax record like any payload file.");
            Assert.True(data.Any(e => e.Name == "etc/" && e.TypeFlag == '5') &&
                data.Any(e => e.Name == "etc/example-app/" && e.TypeFlag == '5'),
                "Parent directories of mapped files must be claimed: " +
                string.Join(',', data.Select(e => e.Name + "(" + e.TypeFlag + ")")));
        }
        finally
        {
            Cleanup(input, output, extra);
        }
    }

    [Fact]
    static void RejectsInvalidMetadataKnobs()
    {
        var input = CreateInputDirectory();
        try
        {
            foreach (var (settings, label) in new (AlpineApkBundleConfiguration, string)[]
            {
                (new AlpineApkBundleConfiguration { Release = "abc" }, "release"),
                (new AlpineApkBundleConfiguration { Release = "-1" }, "release"),
                (new AlpineApkBundleConfiguration { BuildDate = "soon" }, "builddate"),
                (new AlpineApkBundleConfiguration { Depends = ["libc dev"] }, "depend"),
                (new AlpineApkBundleConfiguration { Provides = [""] }, "provides"),
                (new AlpineApkBundleConfiguration { Triggers = ["relative/path"] }, "triggers"),
                (new AlpineApkBundleConfiguration
                {
                    ExtraPkgInfo = new Dictionary<string, string> { ["pkgname"] = "x" }
                }, "extra pkginfo"),
                (new AlpineApkBundleConfiguration
                {
                    ExtraPkgInfo = new Dictionary<string, string> { ["bad key"] = "x" }
                }, "extra pkginfo")
            })
            {
                var thrown = false;
                try
                {
                    new AlpineApkBundler(settings)
                        .BuildAsync(ApkConfiguration(input)).GetAwaiter().GetResult();
                }
                catch (ArgumentException)
                {
                    thrown = true;
                }
                Assert.True(thrown, $"Invalid {label} must be rejected.");
            }
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
    static void RejectsInvalidScriptAndFileInputs()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            // CRLF script is rejected.
            Directory.CreateDirectory(output);
            var script = Path.Combine(output, "post.sh");
            File.WriteAllText(script, "#!/bin/sh\r\necho hi\r\n");
            var thrown = false;
            try
            {
                new AlpineApkBundler(new AlpineApkBundleConfiguration { PostInstallScript = script })
                    .BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            }
            catch (ArgumentException) { thrown = true; }
            Assert.True(thrown, "A CRLF script must be rejected.");

            // Missing script file is rejected.
            thrown = false;
            try
            {
                new AlpineApkBundler(new AlpineApkBundleConfiguration
                {
                    PreInstallScript = Path.Combine(output, "missing.sh")
                }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            }
            catch (FileNotFoundException) { thrown = true; }
            Assert.True(thrown, "A missing script file must be rejected.");

            // Relative destination and '..' segments are rejected.
            var extra = Path.Combine(output, "conf.txt");
            File.WriteAllText(extra, "x");
            foreach (var destination in new[] { "etc/x.conf", "/etc/../x.conf", "/etc/" })
            {
                thrown = false;
                try
                {
                    new AlpineApkBundler(new AlpineApkBundleConfiguration
                    {
                        Files = [new AlpineApkFileEntry { Source = extra, Destination = destination }]
                    }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
                }
                catch (ArgumentException) { thrown = true; }
                Assert.True(thrown, $"Destination '{destination}' must be rejected.");
            }
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void SignsAndVerifiesSignatureSegment()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var keyPath = WriteKey(output, "testkey.rsa", TestPrivateKeyPem);
            var artifact = new AlpineApkBundler(new AlpineApkBundleConfiguration
            {
                SigningKeyFile = keyPath
            }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single();

            var apk = File.ReadAllBytes(artifact.Path);
            var members = ApkPackageReader.GzipMemberOffsets(apk);
            Assert.Equal(3, members.Count);
            var segments = ApkPackageReader.SplitGzipStreams(apk);
            Assert.False(EndsWithZeroBlocks(segments[0]), "The signature tar has no end-of-archive blocks.");
            Assert.False(EndsWithZeroBlocks(segments[1]), "The control tar has no end-of-archive blocks.");
            Assert.True(EndsWithZeroBlocks(segments[2]), "The data tar keeps its end-of-archive blocks.");

            var sig = ApkPackageReader.ReadTar(segments[0]);
            Assert.True(sig.Count == 1 && sig[0].Name == ".SIGN.RSA.testkey.rsa.rsa.pub" &&
                sig[0].TypeFlag == '0' && sig[0].Mode == 420,
                $"Signature member mismatch: {string.Join(',', sig.Select(e => e.Name))}");
            var control = ApkPackageReader.ReadTar(segments[1]);
            Assert.Contains(control, e => e.Name == ".PKGINFO");

            // PKCS1v15 RSA-SHA1 over the raw control gzip stream.
            var controlGzip = apk.Skip(members[1]).Take(members[2] - members[1]).ToArray();
            using var rsa = RSA.Create();
            rsa.ImportFromPem(TestPublicKeyPem);
            Assert.True(rsa.VerifyData(controlGzip, sig[0].Content,
                    HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1),
                "The .SIGN.RSA blob must be a valid RSA-SHA1 signature of the control stream.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void SignsWithEncryptedKey()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var keyPath = WriteKey(output, "enckey.rsa", EncryptedPrivateKeyPem);
            var artifact = new AlpineApkBundler(new AlpineApkBundleConfiguration
            {
                SigningKeyFile = keyPath,
                SigningKeyPassphrase = "test-pass"
            }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single();

            var apk = File.ReadAllBytes(artifact.Path);
            var members = ApkPackageReader.GzipMemberOffsets(apk);
            var sig = ApkPackageReader.ReadTar(ApkPackageReader.SplitGzipStreams(apk)[0]);
            var controlGzip = apk.Skip(members[1]).Take(members[2] - members[1]).ToArray();
            using var rsa = RSA.Create();
            rsa.ImportFromPem(EncryptedPublicKeyPem);
            Assert.True(sig.Count == 1 && sig[0].Name == ".SIGN.RSA.enckey.rsa.rsa.pub" &&
                rsa.VerifyData(controlGzip, sig[0].Content,
                    HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1),
                "The encrypted key must produce a verifiable signature.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void RejectsInvalidSigningInputs()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            // Passphrase without a key file is a half configuration.
            var thrown = false;
            try
            {
                new AlpineApkBundler(new AlpineApkBundleConfiguration
                {
                    SigningKeyPassphrase = "x"
                }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            }
            catch (ArgumentException) { thrown = true; }
            Assert.True(thrown, "A passphrase without a key must be rejected.");

            // Missing key file.
            thrown = false;
            try
            {
                new AlpineApkBundler(new AlpineApkBundleConfiguration
                {
                    SigningKeyFile = Path.Combine(output, "missing.rsa")
                }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            }
            catch (FileNotFoundException) { thrown = true; }
            Assert.True(thrown, "A missing key file must be rejected.");

            // Non-PEM key content.
            var garbage = WriteKey(output, "garbage.rsa", "definitely not a pem");
            thrown = false;
            try
            {
                new AlpineApkBundler(new AlpineApkBundleConfiguration
                {
                    SigningKeyFile = garbage
                }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            }
            catch (ArgumentException) { thrown = true; }
            Assert.True(thrown, "A non-PEM key must be rejected.");

            // Wrong passphrase on an encrypted key fails (crypto layer, any exception).
            var enc = WriteKey(output, "enckey.rsa", EncryptedPrivateKeyPem);
            thrown = false;
            try
            {
                new AlpineApkBundler(new AlpineApkBundleConfiguration
                {
                    SigningKeyFile = enc,
                    SigningKeyPassphrase = "wrong"
                }).BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            }
            catch (Exception) { thrown = true; }
            Assert.True(thrown, "A wrong passphrase must fail.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void DeterministicSignedBytes()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var keyPath = WriteKey(output, "testkey.rsa", TestPrivateKeyPem);
            var settings = new AlpineApkBundleConfiguration { SigningKeyFile = keyPath };
            var first = new AlpineApkBundler(settings)
                .BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single().Path;
            var second = new AlpineApkBundler(settings)
                .BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single().Path;
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static string WriteKey(string directory, string name, string pem)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, pem);
        return path;
    }

    const string TestPrivateKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIIEvwIBADANBgkqhkiG9w0BAQEFAASCBKkwggSlAgEAAoIBAQDDR4h8wDPZOzmt
        5aGtxe6Ue9K5J9vQzmmTsjvcUZJc24D6tmFrVlzbpnRlZnA+YAKLyx5z5I/cI5Ij
        3DMYH3/vkDzq8+OTjp1WLxlSuSU4Q5nCGupCX/o71DwMGrwyS00Ruyd+CdkTz706
        9VlY4dPgNM8XmAW+I/I32WrvqHzlqilSJnYZIdn1CnKBPXktxnO19ylz7aG3Lc3X
        asX6ZXfOME7LLr3O4thQ0KIhpvY9eSV0Cmq0wxmf/EbBG8s08ovm7E8oLSesSyJb
        VBIcUi9dQR8f5mGWFV+GdOcYHJ9gitjIcQuSQYK2EvaUQCdNh0PlU/S325HcGxIM
        PTh4/63VAgMBAAECggEABz5dCruacMFop1GwSKDh87IQI/wdhEZT1j2zSL3h3v3p
        b+NaA8BFW4R2JtjA6x9mmMblD0l4KKNNJXVik89/UGSaTeDUHUIaBftjRhVGEys2
        xeN3sxSaVKPPwmcvefIfHrxBf8RfwANhspEtSkW+NT/gOrDR7bapona3J8KpN1+i
        251CTxBU9zMAc8gxSG9Z4gP1Vcg+n1/wTurzo3NZ89KuSah9mNBFQFGUkkUlG71j
        5K+nv4kjGwbVIpQqlt/RuNFZ9sqpsskYzWt+rWivuKRwqaFkWIzeSV60FRkLI7oQ
        i9s3GrS1af62G+CwtCYdpM+EX32VsPVH6WSCvEDoYQKBgQDxkna8/J9Kj45dkSFi
        YM6EFpwXnbcjwNy+ZUJ8iWm67ejo9D2+1Gd85Ux/yLE1wpBmflafbeyhEmX+lzEy
        D6YXX7TbuaJSotMUxySwY6rZJjmiipOYjEV3wJYs/NbwwYUZwF7pXy8jySoodZWo
        GEJyqFEV8DGoPzF//U9BqY9BWQKBgQDO8UYA5Xj5YlQ1FERqIyXQ8JBRXSeZ6JFU
        pbrrkifsj8IRdBT75HydLKp6MFdvuOHVl7YQp0+BfKOjhsPT5wT+n4jgMsR9DxJe
        0i3lrjCkh0F6eEDHx94jj9lPB+HukP7EaRVMA7Qb4F1eU93uN9Cvpz+c23c9shT4
        c1jxJgnk3QKBgQCMfWN0sW5qTGa9X8QMlMRF6WhKC70Qm/9E81rhVoEY53fG0xR9
        wMWWyzvcLPlyjH6yPNNf0OwHGM4cbA1+Ub+EZHKoPqN6b5tWwCJEOxKHS0XFk9YW
        p61W4bf03e6bAdDIkyofiu29YCaWdRveMI2kZOMTYSdf87B0APtw8o2PsQKBgQDD
        Z4m9cPERMqrmz/Nl8ThVGcJ8QaUSLEuGjVN5+zFdq8UJa/4gd/i/BR0YcasuYHpG
        kJGnGgT19PYjhC5HWf4aXBQH94gXunKTPI2AMkHWKa1HcmNhAbYdCEie2oeZGCqo
        1bz5YQnhxLMFTdXiiauxIRDtEUJ/7Dbm/yv90PhItQKBgQCMRSCPn3zfjiSnhPR8
        f7Kq/XcpgaJ/x4oa+EbG9Eh7pN/k9pT809xv9pO9KdKZQNQ+doXDkTB16D/LHLsf
        VEvc+QsfaW33yUsCbBvDcjGW7VaGd4hnTDxXRCVMrhlFaHcnQmhmFyNHmtDR0tS6
        3ShG2udEmvoxxusmmu5AOTrgFg==
        -----END PRIVATE KEY-----
        """;

    const string TestPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAw0eIfMAz2Ts5reWhrcXu
        lHvSuSfb0M5pk7I73FGSXNuA+rZha1Zc26Z0ZWZwPmACi8sec+SP3COSI9wzGB9/
        75A86vPjk46dVi8ZUrklOEOZwhrqQl/6O9Q8DBq8MktNEbsnfgnZE8+9OvVZWOHT
        4DTPF5gFviPyN9lq76h85aopUiZ2GSHZ9QpygT15LcZztfcpc+2hty3N12rF+mV3
        zjBOyy69zuLYUNCiIab2PXkldApqtMMZn/xGwRvLNPKL5uxPKC0nrEsiW1QSHFIv
        XUEfH+ZhlhVfhnTnGByfYIrYyHELkkGCthL2lEAnTYdD5VP0t9uR3BsSDD04eP+t
        1QIDAQAB
        -----END PUBLIC KEY-----
        """;

    const string EncryptedPrivateKeyPem = """
        -----BEGIN ENCRYPTED PRIVATE KEY-----
        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
        -----END ENCRYPTED PRIVATE KEY-----
        """;

    const string EncryptedPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAooTzdNRsnRZIiZCnlNQu
        H4uJEYAO+j9SnztXEA4zsCXzEnybfP4dtpOtOLVOMoYtAnzqwMOy3UgwvrHOBmcL
        RUYD0r7SJkR5hGxklmEZmzLOHvoPh9KQmthaQdPaLe/ol5mAAVcOWm18x07INgvm
        RdCY7S4jSsNoNIp1yDg3lrQG13g4Ab8gY5QgNi9XF8pjOpbODK9/nXyeomjZzV1R
        edvuxVYb6LebiwsQ/EUzdN0ZVq4eugeij3ZwY3FMdxbdQEQAafglG7btXwYuqSmd
        iLHSQLt+xtV81Fn3Xm/FEbDY6yP0uiXURDRJH+lE+YFo55B7kvZjIm2bNussaAsI
        jwIDAQAB
        -----END PUBLIC KEY-----
        """;

    static bool EndsWithZeroBlocks(byte[] tar)
    {
        if (tar.Length < 1024)
        {
            return false;
        }
        for (var i = tar.Length - 1024; i < tar.Length; i++)
        {
            if (tar[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    static Dictionary<string, string> PkgInfoFields(byte[] pkginfo) => Encoding.UTF8
        .GetString(pkginfo)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => !line.StartsWith('#'))
        .Select(line => line.Split(" = ", 2))
        .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    static string Sha256Hex(byte[] content)
    {
        using var sha256 = SHA256.Create();
        return Hex(sha256.ComputeHash(content));
    }

    static string Hex(byte[] hash)
    {
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    static BundleConfiguration ApkConfiguration(
        string input,
        string output = "",
        string rid = "linux-musl-x64",
        IReadOnlyList<PackageFormat>? formats = null,
        string productName = "Example App",
        IReadOnlyList<BundleResourceConfiguration>? resources = null) => new()
        {
            ProductName = productName,
            Identifier = "com.example.app",
            Publisher = "Example Publisher",
            Version = "1.0.0",
            Homepage = "https://example.com/app",
            Description = "Example application",
            OutputDirectory = output.Length == 0 ? input + ".artifacts" : output,
            Resources = resources ?? [],
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = rid,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.AlpineApk]
                }
            ]
        };

    static string CreateInputDirectory()
    {
        var input = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "ExampleApp"), "fake executable");
        File.WriteAllText(Path.Combine(input, "ExampleApp.dll"), "payload");
        return input;
    }

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

    static string RepositoryRoot() => Path.GetFullPath("../../../../../", AppContext.BaseDirectory);


}
