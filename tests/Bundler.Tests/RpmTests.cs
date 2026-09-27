using DotNet.Bundler;
using DotNet.Bundler.Rpm;
using System.Text;

internal static class RpmTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Rejects non-rpm formats", () => RunSync(RejectsNonRpmFormats));
            yield return ("Produces a structurally valid .rpm", () => RunSync(ProducesValidRpm));
            yield return ("Maps SemVer to RPM version/release", () => RunSync(MapsSemVerToRpmVersion));
            yield return ("Maps rpm overrides", () => RunSync(MapsOverrides));
            yield return ("Disables the bin link", () => RunSync(DisablesBinLink));
            yield return ("Rejects invalid rpm settings", () => RunSync(RejectsInvalidSettings));
            yield return ("Derives a kebab-case package name", () => RunSync(DerivesKebabName));
            yield return ("Stages resources under the install root", () => RunSync(StagesResources));
            yield return ("Owns explicit directory entries", () => RunSync(OwnsDirectoryEntries));
            yield return ("Produces deterministic .rpm bytes", () => RunSync(DeterministicBytes));
            yield return ("Writes a correct sha256 sidecar", () => RunSync(Sha256Sidecar));
            yield return ("Maps rpm settings through MSBuild", () => RunSync(MapsRpmSettingsThroughMsBuild));
        }
    }

    static void RejectsNonRpmFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var thrown = false;
            try
            {
                new RpmBundler()
                    .BuildAsync(RpmConfiguration(input, formats: [PackageFormat.Deb]))
                    .GetAwaiter().GetResult();
            }
            catch (NotSupportedException exception)
            {
                thrown = exception.Message.Contains("Rpm targets only");
            }
            Assert(thrown, "A non-rpm target must be rejected with NotSupportedException.");
        }
        finally
        {
            Cleanup(input);
        }
    }

    static void ProducesValidRpm()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new RpmBundler()
                .BuildAsync(RpmConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var expectedName = "example-app-1.0.0-1.x86_64.rpm";
            Assert(artifact.Path.EndsWith(
                    Path.Combine("linux-x64", "rpm", expectedName), StringComparison.Ordinal),
                $"Unexpected .rpm artifact path: {artifact.Path}");

            var package = RpmPackageReader.Read(artifact.Path);
            Assert(package.Main.Text(1000) == "example-app", "NAME tag must be the package name.");
            Assert(package.Main.Text(1001) == "1.0.0", "VERSION tag must be '1.0.0'.");
            Assert(package.Main.Text(1002) == "1", "RELEASE tag must be '1'.");
            Assert(package.Main.Text(1021) == "linux", "OS tag must be 'linux'.");
            Assert(package.Main.Text(1022) == "x86_64", "ARCH tag must be 'x86_64'.");
            Assert(package.Main.Text(1124) == "cpio", "PAYLOADFORMAT must be 'cpio'.");
            Assert(package.Main.Text(1125) == "gzip", "PAYLOADCOMPRESSOR must be 'gzip'.");
            Assert(package.Main.Ints(5011).Single() == 8, "FILEDIGESTALGO must be sha256 (8).");
            Assert(package.Signature.Tag(1000) > 0, "RPMSIGTAG_SIZE must be present.");
            Assert(package.Signature.Tag(1007) > 0, "RPMSIGTAG_PAYLOADSIZE must be present.");
            Assert(package.Signature.Text(273).Length == 64, "SHA256HEADER must be a hex digest.");
            Assert(package.Signature.Text(269).Length == 40, "SHA1HEADER must be a hex digest.");

            // rpmlib self-dependencies.
            var requires = package.Main.Strings(1049);
            Assert(requires.Contains("rpmlib(CompressedFileNames)"), "rpmlib deps must be declared.");

            // Self provides.
            var provides = package.Main.Strings(1047);
            Assert(provides.Contains("example-app") && provides.Contains("example-app(x86_64)"),
                "The package must provide its own name and arch-qualified name.");

            // Payload contents.
            var paths = package.Payload.Select(e => e.Path).ToArray();
            Assert(paths.Contains("/usr/lib/example-app/ExampleApp"), "Payload must carry the executable.");
            Assert(paths.Contains("/usr/bin/example-app"), "Payload must carry the bin symlink.");
            var link = package.Payload.Single(e => e.Path == "/usr/bin/example-app");
            Assert((link.Mode & 0xF000) == 0xA000, "The bin entry must be a symlink.");
            Assert(Encoding.UTF8.GetString(link.Data) == "../lib/example-app/ExampleApp",
                "The bin symlink must target the install root.");
            var exe = package.Payload.Single(e => e.Path == "/usr/lib/example-app/ExampleApp");
            Assert((exe.Mode & 511) == 493 /* 0755 */, "The main executable must be 0755.");
            Assert(Encoding.UTF8.GetString(exe.Data) == "fake executable", "Payload bytes must round-trip.");

            // File list consistency with cpio entries.
            var basenames = package.Main.Strings(1117);
            var dirnames = package.Main.Strings(1118);
            var dirIndexes = package.Main.Ints(1116);
            var fileModes = package.Main.Ints(1030);
            Assert(basenames.Length == package.Payload.Count,
                "Header file count must match the cpio entries.");
            var expectedPaths = basenames.Select((b, i) => dirnames[dirIndexes[i]] + b).ToArray();
            Assert(paths.SequenceEqual(expectedPaths),
                "BASENAMES+DIRNAMES must reconstruct the payload paths.");
            var digests = package.Main.Strings(1035);
            Assert(digests.SequenceEqual(package.Payload.Select(e =>
                (e.Mode & 0xF000) == 0x4000 ? "" : Sha256(e.Data))),
                "FILEDIGESTS must hold sha256 of every non-directory entry.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsSemVerToRpmVersion()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cases = new (string SemVer, string Version, string Release)[]
            {
                ("1.0.0", "1.0.0", "1"),
                ("2.3.4", "2.3.4", "1"),
                ("1.0.0-alpha.1", "1.0.0", "0.1.alpha.1"),
                ("1.0.0-beta", "1.0.0", "0.1.beta"),
                ("1.0.0+build42", "1.0.0", "1+build42")
            };
            foreach (var (semVer, version, release) in cases)
            {
                var artifact = new RpmBundler()
                    .BuildAsync(RpmConfiguration(input, output, version: semVer))
                    .GetAwaiter().GetResult().Single();
                var package = RpmPackageReader.Read(artifact.Path);
                Assert(package.Main.Text(1001) == version && package.Main.Text(1002) == release,
                    $"SemVer {semVer} must map to {version}-{release} (got {package.Main.Text(1001)}-{package.Main.Text(1002)}).");
            }
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
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                PackageName = "custom-name",
                Version = "9.9",
                Release = "7.el9",
                Epoch = "2",
                Architecture = "noarch",
                InstallRoot = "/opt/custom"
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            Assert(artifact.Path.EndsWith("custom-name-9.9-7.el9.noarch.rpm", StringComparison.Ordinal),
                $"Unexpected file name: {artifact.Path}");
            var package = RpmPackageReader.Read(artifact.Path);
            Assert(package.Main.Text(1000) == "custom-name", "NAME override.");
            Assert(package.Main.Text(1001) == "9.9" && package.Main.Text(1002) == "7.el9",
                "Version/Release override.");
            Assert(package.Main.Ints(1003).Single() == 2, "EPOCH override must land in tag 1003.");
            Assert(package.Main.Text(1022) == "noarch", "ARCH override.");
            Assert(package.Payload.Any(e => e.Path == "/opt/custom/ExampleApp"),
                "InstallRoot override must move the payload.");
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
            var artifact = new RpmBundler(new RpmBundleConfiguration { BinLink = "none" })
                .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            Assert(package.Payload.All(e => (e.Mode & 0xF000) != 0xA000),
                "BinLink='none' must emit no symlink.");
            Assert(package.Payload.All(e => !e.Path.StartsWith("/usr/bin", StringComparison.Ordinal)),
                "BinLink='none' must emit nothing under /usr/bin.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void RejectsInvalidSettings()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var cases = new (string Name, RpmBundleConfiguration Settings, string Version)[]
            {
                ("bad package name", new RpmBundleConfiguration { PackageName = "Has Space" }, "1.0.0"),
                ("version with dash", new RpmBundleConfiguration { Version = "1.0-0" }, "1.0.0"),
                ("release with dash", new RpmBundleConfiguration { Release = "1-2" }, "1.0.0"),
                ("semver with no leading digit", new RpmBundleConfiguration(), "v1.0"),
                ("non-numeric epoch", new RpmBundleConfiguration { Epoch = "x" }, "1.0.0"),
                ("relative install root", new RpmBundleConfiguration { InstallRoot = "opt/x" }, "1.0.0"),
                ("install root traversal", new RpmBundleConfiguration { InstallRoot = "/opt/../x" }, "1.0.0"),
                ("bad bin link", new RpmBundleConfiguration { BinLink = "a/b" }, "1.0.0"),
                ("bad arch", new RpmBundleConfiguration { Architecture = "x86-64" }, "1.0.0"),
                ("empty vendor", new RpmBundleConfiguration { Vendor = " " }, "1.0.0")
            };
            foreach (var (name, settings, version) in cases)
            {
                var thrown = false;
                try
                {
                    new RpmBundler(settings)
                        .BuildAsync(RpmConfiguration(input, output, version: version))
                        .GetAwaiter().GetResult();
                }
                catch (Exception) { thrown = true; }
                Assert(thrown, $"Invalid setting must be rejected: {name}.");
            }
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void DerivesKebabName()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new RpmBundler()
                .BuildAsync(RpmConfiguration(input, output, productName: "My Cool_App!"))
                .GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            Assert(package.Main.Text(1000) == "my-cool-app",
                $"Product name must normalize to kebab-case (got {package.Main.Text(1000)}).");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void StagesResources()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var resourcesDir = Path.Combine(output, "..", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resourcesDir);
        var resourceFile = Path.Combine(resourcesDir, "note.txt");
        File.WriteAllText(resourceFile, "hello rpm");
        try
        {
            var configuration = RpmConfiguration(input, output);
            configuration = new BundleConfiguration
            {
                ProductName = configuration.ProductName,
                Identifier = configuration.Identifier,
                Publisher = configuration.Publisher,
                Version = configuration.Version,
                OutputDirectory = configuration.OutputDirectory,
                Targets = configuration.Targets,
                Resources = [new BundleResourceConfiguration
                {
                    Source = resourceFile,
                    TargetPath = "docs/note.txt"
                }]
            };
            var artifact = new RpmBundler().BuildAsync(configuration).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var resource = package.Payload.SingleOrDefault(
                e => e.Path == "/usr/lib/example-app/docs/note.txt");
            Assert(resource is not null, "Resources must stage under the install root.");
            Assert(Encoding.UTF8.GetString(resource!.Data) == "hello rpm",
                "Resource content must round-trip.");
            Assert(package.Payload.Any(e => e.Path == "/usr/lib/example-app/docs" &&
                (e.Mode & 0xF000) == 0x4000), "Resource parent dirs must be owned entries.");
        }
        finally
        {
            Cleanup(input, output, resourcesDir);
        }
    }

    static void OwnsDirectoryEntries()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(input, "sub"));
        File.WriteAllText(Path.Combine(input, "sub", "inner.txt"), "nested");
        try
        {
            var artifact = new RpmBundler()
                .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var dirs = package.Payload.Where(e => (e.Mode & 0xF000) == 0x4000)
                .Select(e => e.Path).ToArray();
            Assert(dirs.Contains("/usr/lib/example-app"), "The install root must be an owned dir entry.");
            Assert(dirs.Contains("/usr/lib/example-app/sub"), "Nested dirs must be owned entries.");
            Assert(!dirs.Contains("/usr") && !dirs.Contains("/usr/lib") && !dirs.Contains("/usr/bin"),
                "System dirs above the install root must not be owned.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void DeterministicBytes()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var second = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new RpmBundler().BuildAsync(RpmConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var next = new RpmBundler().BuildAsync(RpmConfiguration(input, second))
                .GetAwaiter().GetResult().Single();
            Assert(File.ReadAllBytes(first.Path).SequenceEqual(File.ReadAllBytes(next.Path)),
                "Identical inputs must produce identical .rpm bytes.");
        }
        finally
        {
            Cleanup(input, output, second);
        }
    }

    static void Sha256Sidecar()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new RpmBundler().BuildAsync(RpmConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var sidecar = File.ReadAllText(artifact.Path + ".sha256").Trim();
            var parts = sidecar.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Assert(parts.Length == 2 && parts[0].Length == 64 &&
                   parts[1] == Path.GetFileName(artifact.Path),
                $"The sha256 sidecar must be '<digest>  <filename>', got '{sidecar}'.");
            Assert(parts[0] == Sha256(File.ReadAllBytes(artifact.Path)),
                "The sidecar digest must match the file bytes.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsRpmSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert(targets.Contains("RpmPackageName=\"$(BundlerRpmPackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmVersion=\"$(BundlerRpmVersion)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmRelease=\"$(BundlerRpmRelease)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmEpoch=\"$(BundlerRpmEpoch)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmArchitecture=\"$(BundlerRpmArchitecture)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmVendor=\"$(BundlerRpmVendor)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmInstallRoot=\"$(BundlerRpmInstallRoot)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmBinLink=\"$(BundlerRpmBinLink)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerRpm* properties to the task.");
        Assert(props.Contains("<BundlerRpmPackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmVersion", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmRelease", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmEpoch", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmArchitecture", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmVendor", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmInstallRoot", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmBinLink", StringComparison.Ordinal),
            "The BundlerRpm* properties lack defaults in the .props file.");
        Assert(task.Contains("new RpmBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Rpm", StringComparison.Ordinal),
            "The MSBuild task does not construct the .rpm backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert(msbuildProject.Contains("DotNet.Bundler.Rpm.dll", StringComparison.Ordinal),
            "The MSBuild package does not pack the Rpm backend assembly.");
        // The multi-format fanout is the RPM-1 contract.
        Assert(task.Contains("foreach (var format in formats.Distinct())", StringComparison.Ordinal) &&
               task.Contains("artifacts.AddRange(produced)", StringComparison.Ordinal),
            "The MSBuild dispatch must fan out per format.");
    }

    static BundleConfiguration RpmConfiguration(
        string input,
        string output = "",
        string rid = "linux-x64",
        string? productName = null,
        string version = "1.0.0",
        IReadOnlyList<PackageFormat>? formats = null) => new()
        {
            ProductName = productName ?? "Example App",
            Identifier = "com.example.app",
            Publisher = "Example Publisher",
            Version = version,
            Homepage = "https://example.com/app",
            Description = "Example application",
            OutputDirectory = output.Length == 0 ? input + ".artifacts" : output,
            Targets =
            [
                new BundleTargetConfiguration
                {
                    RuntimeIdentifier = rid,
                    InputDirectory = input,
                    MainExecutable = "ExampleApp",
                    Formats = formats ?? [PackageFormat.Rpm]
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

    static string Sha256(byte[] bytes)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var builder = new StringBuilder();
        foreach (var b in sha256.ComputeHash(bytes))
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
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
