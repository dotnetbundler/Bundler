using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.Cli;
using DotNet.Bundler.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal static class AlpineApkTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Rejects non-apk formats", () => RunSync(RejectsNonApkFormats));
            yield return ("Produces a structurally valid .apk", () => RunSync(ProducesValidApk));
            yield return ("Maps overrides", () => RunSync(MapsOverrides));
            yield return ("Disables the bin link", () => RunSync(DisablesBinLink));
            yield return ("Rejects invalid apk settings", () => RunSync(RejectsInvalidSettings));
            yield return ("Derives a kebab-case package name", () => RunSync(DerivesKebabName));
            yield return ("Maps musl architectures", () => RunSync(MapsMuslArchitectures));
            yield return ("Rejects apk on non-musl targets", () => RunSync(RejectsNonMuslTargets));
            yield return ("Stages resources under the install root", () => RunSync(StagesResources));
            yield return ("Produces deterministic .apk bytes", () => RunSync(DeterministicBytes));
            yield return ("Writes a correct sha256 sidecar", () => RunSync(Sha256Sidecar));
            yield return ("Parses alpineapk through the CLI", () => RunSync(ParsesAlpineApkThroughCli));
            yield return ("Maps apk settings through MSBuild", () => RunSync(MapsApkSettingsThroughMsBuild));
            yield return ("Writes release, license and metadata knobs", () => RunSync(WritesReleaseLicenseAndRelations));
            yield return ("Writes the six install scripts", () => RunSync(WritesInstallScripts));
            yield return ("Maps arbitrary file destinations", () => RunSync(MapsArbitraryFileDestinations));
            yield return ("Rejects invalid apk metadata knobs", () => RunSync(RejectsInvalidMetadataKnobs));
            yield return ("Rejects invalid script and file inputs", () => RunSync(RejectsInvalidScriptAndFileInputs));
        }
    }

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
            Assert(thrown, "A non-apk target must be rejected with NotSupportedException.");
        }
        finally
        {
            Cleanup(input);
        }
    }

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
            Assert(artifact.Path.EndsWith(
                    Path.Combine("linux-musl-x64", "apk", expectedName), StringComparison.Ordinal),
                $"Unexpected .apk artifact path: {artifact.Path}");

            var apk = File.ReadAllBytes(artifact.Path);
            var starts = ApkPackageReader.GzipMemberOffsets(apk);
            var segments = ApkPackageReader.SplitGzipStreams(apk);
            Assert(segments.Count == 2,
                $"An unsigned .apk must hold exactly 2 gzip streams (control+data), got {segments.Count}.");
            var dataGzipBytes = apk.Skip(starts[1]).ToArray();

            var control = ApkPackageReader.ReadTar(segments[0]);
            var data = ApkPackageReader.ReadTar(segments[1]);

            var pkginfo = control.SingleOrDefault(e => e.Name == ".PKGINFO");
            Assert(pkginfo is not null, "The control segment lacks .PKGINFO.");
            Assert(control.All(e => !e.Name.StartsWith(".SIGN.", StringComparison.Ordinal)),
                "An unsigned package must not contain .SIGN.* members.");
            var fields = PkgInfoFields(pkginfo!.Content);
            Assert(fields["pkgname"] == "example-app", $"pkgname mismatch: {fields["pkgname"]}");
            Assert(fields["pkgver"] == "1.0.0-r0", $"pkgver mismatch: {fields["pkgver"]}");
            Assert(fields["pkgdesc"] == "Example application", $"pkgdesc mismatch: {fields["pkgdesc"]}");
            Assert(fields["url"] == "https://example.com/app", $"url mismatch: {fields["url"]}");
            Assert(fields["arch"] == "x86_64", $"arch mismatch: {fields["arch"]}");
            Assert(fields["origin"] == "example-app", $"origin mismatch: {fields["origin"]}");
            Assert(fields["builddate"] == "0", $"builddate must default to 0: {fields["builddate"]}");
            Assert(fields["datahash"] == Sha256Hex(dataGzipBytes),
                "datahash must be the sha256 of the data gzip stream.");
            var payloadSize = data.Where(e => e.TypeFlag == '0').Sum(e => (long)e.Content.Length);
            Assert(fields["size"] == payloadSize.ToString(),
                $"size must be the installed payload size: {fields["size"]} != {payloadSize}");

            var names = data.Select(e => e.Name).ToArray();
            Assert(names.Contains("usr/lib/example-app/ExampleApp"),
                $"Payload lacks the main executable: {string.Join(',', names)}");
            Assert(names.Contains("usr/lib/example-app/ExampleApp.dll"),
                $"Payload lacks the library: {string.Join(',', names)}");
            var exe = data.Single(e => e.Name == "usr/lib/example-app/ExampleApp");
            Assert(exe.Mode == 493,
                $"The main executable must have mode 0755, got {Convert.ToString(exe.Mode, 8)}.");
            var dll = data.Single(e => e.Name == "usr/lib/example-app/ExampleApp.dll");
            Assert(dll.Mode == 420,
                $"Data files must have mode 0644, got {Convert.ToString(dll.Mode, 8)}.");
            Assert(data.Where(e => e.TypeFlag == '5').All(e => e.Mode == 493),
                "Directories must have mode 0755.");
            Assert(data.Where(e => e.TypeFlag == '5').All(e => e.Name.EndsWith('/')),
                "Directory entries must carry the trailing '/' marker.");
            var link = data.SingleOrDefault(e => e.Name == "usr/bin/example-app");
            Assert(link is { TypeFlag: '2' } && link.LinkTarget == "../lib/example-app/ExampleApp",
                $"usr/bin symlink mismatch: {link?.LinkTarget}");

            foreach (var file in data.Where(e => e.TypeFlag == '0'))
            {
                Assert(file.Pax.TryGetValue("APK-TOOLS.checksum.SHA1", out var sha1),
                    $"File {file.Name} lacks the APK-TOOLS.checksum.SHA1 pax record.");
                using var hasher = SHA1.Create();
                Assert(Convert.ToBase64String(hasher.ComputeHash(file.Content)) == sha1,
                    $"SHA1 checksum mismatch for {file.Name}.");
            }

            // Signature/control segments carry no trailing zero blocks; the data
            // segment ends the archive and keeps the two 512-byte zero records.
            Assert(!EndsWithZeroBlocks(segments[0]),
                "The control segment must not end with zero blocks.");
            Assert(EndsWithZeroBlocks(segments[1]),
                "The data segment must end with the two 512-byte zero records.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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
            Assert(artifact.Path.EndsWith("custom-pkg-2.5.0-r0.apk", StringComparison.Ordinal),
                $"Override file name mismatch: {artifact.Path}");

            var segments = ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(artifact.Path));
            var fields = PkgInfoFields(
                ApkPackageReader.ReadTar(segments[0]).Single(e => e.Name == ".PKGINFO").Content);
            Assert(fields["pkgver"] == "2.5.0-r0" && fields["arch"] == "aarch64" &&
                fields["origin"] == "custom-origin" && fields["pkgdesc"] == "Custom description" &&
                fields["url"] == "https://example.com/custom",
                $"Override fields missing: {string.Join(',', fields.Select(kv => kv.Key + '=' + kv.Value))}");
            var data = ApkPackageReader.ReadTar(segments[1]);
            Assert(data.Any(e => e.Name == "usr/lib/custom-pkg/ExampleApp"),
                "Payload must land under /usr/lib/<package-name>.");
            var link = data.Single(e => e.Name == "usr/bin/custom-cli");
            Assert(link.LinkTarget == "../lib/custom-pkg/ExampleApp",
                $"BinLink target mismatch: {link.LinkTarget}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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
            Assert(!data.Any(e => e.Name.StartsWith("usr/bin", StringComparison.Ordinal)),
                "An empty BinLink must not create a usr/bin entry.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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
                Assert(thrown, $"Invalid {label} must be rejected: {settings}");
            }
        }
        finally
        {
            Cleanup(input);
        }
    }

    static void DerivesKebabName()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = ApkConfiguration(input, output, productName: "My Fancy App!");
            var artifacts = new AlpineApkBundler().BuildAsync(configuration).GetAwaiter().GetResult();
            Assert(artifacts.Single().Path.EndsWith("my-fancy-app-1.0.0-r0.apk", StringComparison.Ordinal),
                $"Kebab-case name derivation failed: {artifacts.Single().Path}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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
                Assert(fields["arch"] == arch,
                    $"RID {rid} must map to arch {arch}, got {fields["arch"]}");
            }
            finally
            {
                Cleanup(input, output);
            }
        }
    }

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
                Assert(thrown, $"apk must be refused on non-musl rid {rid}.");
            }

            // The matrix keeps the validator symmetric: apk on a glibc Linux
            // target is a configuration issue, not just a missing backend.
            var glibc = ApkConfiguration(input, rid: "linux-x64");
            Assert(BundleConfigurationValidator.Validate(glibc, checkFileSystem: false)
                    .Any(issue => issue.Path == "targets[0].formats"),
                "The validator must reject AlpineApk on linux-x64.");
            var musl = ApkConfiguration(input, rid: "linux-musl-arm64");
            Assert(!BundleConfigurationValidator.Validate(musl, checkFileSystem: false)
                    .Any(issue => issue.Path == "targets[0].formats"),
                "The validator must accept AlpineApk on linux-musl-arm64.");
        }
        finally
        {
            Cleanup(input);
        }
    }

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
            Assert(doc is not null && Encoding.UTF8.GetString(doc.Content) == "resource content",
                "Resource directory must land under the install root.");
        }
        finally
        {
            Cleanup(input, output, resourceDir);
        }
    }

    static void DeterministicBytes()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new AlpineApkBundler().BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            var second = new AlpineApkBundler().BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult();
            Assert(File.ReadAllBytes(first.Single().Path).SequenceEqual(
                    File.ReadAllBytes(second.Single().Path)),
                "Two identical builds must produce byte-identical .apk files.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void Sha256Sidecar()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new AlpineApkBundler()
                .BuildAsync(ApkConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var sidecarPath = artifact.Path + ".sha256";
            Assert(File.Exists(sidecarPath), "The .sha256 sidecar is missing.");
            var content = File.ReadAllText(sidecarPath);
            using var sha256 = SHA256.Create();
            var hash = Hex(sha256.ComputeHash(File.ReadAllBytes(artifact.Path)));
            Assert(content == hash + "  " + Path.GetFileName(artifact.Path) + "\n",
                $"Sidecar mismatch: {content}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void ParsesAlpineApkThroughCli()
    {
        var formats = CliProgram.ParseFormats("alpineapk,zip", "linux-musl-x64");
        Assert(formats.SequenceEqual(new[] { PackageFormat.AlpineApk, PackageFormat.Zip }),
            $"--formats alpineapk must parse: {string.Join(',', formats)}");

        var all = CliProgram.ParseFormats("all", "linux-musl-x64");
        Assert(all.Contains(PackageFormat.AlpineApk) && all.Contains(PackageFormat.Zip) &&
            all.Contains(PackageFormat.TarGz) && all.Count == 3,
            $"musl 'all' must expand to zip/targz/alpineapk: {string.Join(',', all)}");
        var glibcAll = CliProgram.ParseFormats("all", "linux-x64");
        Assert(!glibcAll.Contains(PackageFormat.AlpineApk),
            "glibc 'all' must not include alpineapk.");

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
            Assert(code == 0, $"cli bundle must exit 0, got {code}: {stderr}");
            Assert(File.Exists(Path.Combine(output, "linux-musl-x64", "apk", "clifixture-1.0.0-r0.apk")),
                $"CLI-produced .apk missing: {stdout}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsApkSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert(targets.Contains("AlpineApkPackageName=\"$(BundlerAlpineApkPackageName)\"", StringComparison.Ordinal) &&
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
        Assert(props.Contains("<BundlerAlpineApkPackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerAlpineApkBinLink", StringComparison.Ordinal),
            "The BundlerAlpineApk* properties lack defaults in the .props file.");
        Assert(task.Contains("new AlpineApkBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.AlpineApk", StringComparison.Ordinal),
            "The MSBuild task does not construct the .apk backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert(msbuildProject.Contains("DotNet.Bundler.AlpineApk.dll", StringComparison.Ordinal),
            "The MSBuild package does not pack the AlpineApk backend assembly.");
    }

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
            Assert(Path.GetFileName(artifact.Path) == "example-app-1.0.0-r3.apk",
                $"Release must enter the file name: {artifact.Path}");

            var control = ApkPackageReader.ReadTar(
                ApkPackageReader.SplitGzipStreams(File.ReadAllBytes(artifact.Path))[0]);
            var text = Encoding.UTF8.GetString(
                control.Single(e => e.Name == ".PKGINFO").Content);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert(lines.Contains("pkgver = 1.0.0-r3"), "pkgver must carry the release.");
            Assert(lines.Contains("license = MIT"), "license field missing.");
            Assert(lines.Contains("builddate = 1700000000"), "builddate override missing.");
            Assert(lines.Count(l => l == "depend = busybox" ||
                l == "depend = so:libc.musl-x86_64.so.1>=1.2") == 2,
                $"depend lines missing: {text}");
            Assert(lines.Count(l => l == "provides = virtual-example" ||
                l == "provides = example-shim=1.0") == 2,
                $"provides lines missing: {text}");
            Assert(lines.Contains("triggers = /usr/share/example /usr/lib/example-triggers"),
                $"triggers must be a single space-joined line: {text}");
            Assert(lines.Contains("install_if = example-gui"),
                $"ExtraPkgInfo escape hatch missing: {text}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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
                Assert(entry is not null && entry.Mode == 493 &&
                    Encoding.UTF8.GetString(entry.Content).Contains(name.TrimStart('.')),
                    $"Script {name} missing from the control segment.");
            }
        }
        finally
        {
            Cleanup(input, output, scripts);
        }
    }

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
            Assert(entry is not null && entry.Mode == 420 &&
                Encoding.UTF8.GetString(entry.Content) == "key=value",
                $"Mapped file must land at its absolute destination: {string.Join(',', data.Select(e => e.Name))}");
            Assert(entry!.Pax.ContainsKey("APK-TOOLS.checksum.SHA1"),
                "Mapped files need the sha1 pax record like any payload file.");
            Assert(data.Any(e => e.Name == "etc/" && e.TypeFlag == '5') &&
                data.Any(e => e.Name == "etc/example-app/" && e.TypeFlag == '5'),
                "Parent directories of mapped files must be claimed: " +
                string.Join(',', data.Select(e => e.Name + "(" + e.TypeFlag + ")")));
        }
        finally
        {
            Cleanup(input, output, extra);
        }
    }

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
                Assert(thrown, $"Invalid {label} must be rejected.");
            }
        }
        finally
        {
            Cleanup(input);
        }
    }

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
            Assert(thrown, "A CRLF script must be rejected.");

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
            Assert(thrown, "A missing script file must be rejected.");

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
                Assert(thrown, $"Destination '{destination}' must be rejected.");
            }
        }
        finally
        {
            Cleanup(input, output);
        }
    }

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

    static Task RunSync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Assertion failed: " + message);
        }
    }
}
