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
               targets.Contains("AlpineApkBinLink=\"$(BundlerAlpineApkBinLink)\"", StringComparison.Ordinal),
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
