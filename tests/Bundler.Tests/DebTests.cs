using DotNet.Bundler;
using DotNet.Bundler.Deb;
using System.Text;

internal static class DebTests
{
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases
    {
        get
        {
            yield return ("Rejects non-deb formats", () => RunSync(RejectsNonDebFormats));
            yield return ("Produces a structurally valid .deb", () => RunSync(ProducesValidDeb));
            yield return ("Maps SemVer to Debian versions", () => RunSync(MapsSemVerToDebianVersion));
            yield return ("Maps deb overrides", () => RunSync(MapsOverrides));
            yield return ("Disables the bin link", () => RunSync(DisablesBinLink));
            yield return ("Rejects invalid deb settings", () => RunSync(RejectsInvalidSettings));
            yield return ("Derives a kebab-case package name", () => RunSync(DerivesKebabName));
            yield return ("Writes matching md5sums", () => RunSync(Md5sumsMatch));
            yield return ("Stages resources under the install root", () => RunSync(StagesResources));
            yield return ("Produces deterministic .deb bytes", () => RunSync(DeterministicBytes));
            yield return ("Writes a correct sha256 sidecar", () => RunSync(Sha256Sidecar));
            yield return ("Maps deb settings through MSBuild", () => RunSync(MapsDebSettingsThroughMsBuild));
        }
    }

    static void RejectsNonDebFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var thrown = false;
            try
            {
                new DebBundler()
                    .BuildAsync(DebConfiguration(input, formats: [PackageFormat.Rpm]))
                    .GetAwaiter().GetResult();
            }
            catch (NotSupportedException exception)
            {
                thrown = exception.Message.Contains("Deb targets only");
            }
            Assert(thrown, "A non-deb target must be rejected with NotSupportedException.");
        }
        finally
        {
            Cleanup(input);
        }
    }

    static void ProducesValidDeb()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new DebBundler()
                .BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult();
            var artifact = artifacts.Single();
            var expectedName = "example-app_1.0.0-1_amd64.deb";
            Assert(artifact.Path.EndsWith(
                    Path.Combine("linux-x64", "deb", expectedName), StringComparison.Ordinal),
                $"Unexpected .deb artifact path: {artifact.Path}");

            var members = DebPackageReader.ReadAr(artifact.Path);
            Assert(members.Select(m => m.Name).SequenceEqual(
                    new[] { "debian-binary", "control.tar.gz", "data.tar.gz" }),
                $"Unexpected ar members: {string.Join(',', members.Select(m => m.Name))}");
            Assert(Encoding.ASCII.GetString(members[0].Content) == "2.0\n",
                "debian-binary must be '2.0\\n'.");

            var controlEntries = DebPackageReader.ReadTar(
                DebPackageReader.Ungzip(members[1].Content));
            var control = Encoding.UTF8.GetString(
                controlEntries.Single(e => e.Name == "./control").Content);
            Assert(control.Contains("Package: example-app\n", StringComparison.Ordinal) &&
                   control.Contains("Version: 1.0.0-1\n", StringComparison.Ordinal) &&
                   control.Contains("Architecture: amd64\n", StringComparison.Ordinal) &&
                   control.Contains("Maintainer: Example Publisher\n", StringComparison.Ordinal) &&
                   control.Contains("Priority: optional\n", StringComparison.Ordinal) &&
                   control.Contains("Homepage: https://example.com/app\n", StringComparison.Ordinal) &&
                   control.Contains("Installed-Size: ", StringComparison.Ordinal) &&
                   control.Contains("Description: Example application\n", StringComparison.Ordinal),
                $"Control file is missing core fields:\n{control}");
            Assert(controlEntries.Any(e => e.Name == "./md5sums"),
                "control.tar must contain md5sums.");

            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            var names = data.Select(e => e.Name).ToArray();
            Assert(names.Contains("./usr/lib/example-app/ExampleApp") &&
                   names.Contains("./usr/lib/example-app/ExampleApp.dll") &&
                   names.Contains("./usr/bin/example-app"),
                $"data.tar is missing payload entries: {string.Join(',', names)}");
            var executable = data.Single(e => e.Name == "./usr/lib/example-app/ExampleApp");
            Assert(executable.Mode == 493 /* 0755 */,
                $"The main executable must be 0755, got {Convert.ToString(executable.Mode, 8)}.");
            var lib = data.Single(e => e.Name == "./usr/lib/example-app/ExampleApp.dll");
            Assert(lib.Mode == 420 /* 0644 */, "Payload data files must be 0644.");
            var link = data.Single(e => e.Name == "./usr/bin/example-app");
            Assert(link.Kind == TarEntryKind.Symlink &&
                   link.LinkTarget == "../lib/example-app/ExampleApp",
                $"usr/bin must be a relative symlink into the install root, got '{link.LinkTarget}'.");
            Assert(data.Any(e => e.Kind == TarEntryKind.Directory && e.Name == "./usr/lib/example-app"),
                "The install-root directory entry must be present.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsSemVerToDebianVersion()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = DebConfiguration(input, output, rid: "linux-arm64");
            configuration = new BundleConfiguration
            {
                ProductName = configuration.ProductName,
                Identifier = configuration.Identifier,
                Publisher = configuration.Publisher,
                Version = "2.5.0-beta.3+build.7",
                Homepage = configuration.Homepage,
                Description = configuration.Description,
                OutputDirectory = configuration.OutputDirectory,
                Targets = configuration.Targets
            };
            var artifacts = new DebBundler().BuildAsync(configuration).GetAwaiter().GetResult();
            var deb = artifacts.Single();
            Assert(deb.Path.EndsWith("example-app_2.5.0~beta.3+build.7-1_arm64.deb", StringComparison.Ordinal),
                $"SemVer must map to '~' prerelease and '_arm64' file naming, got: {deb.Path}");
            var members = DebPackageReader.ReadAr(deb.Path);
            var control = Encoding.UTF8.GetString(
                DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content))
                    .Single(e => e.Name == "./control").Content);
            Assert(control.Contains("Version: 2.5.0~beta.3+build.7-1\n", StringComparison.Ordinal) &&
                   control.Contains("Architecture: arm64\n", StringComparison.Ordinal),
                $"Control lacks the mapped version/arm64:\n{control}");
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
            var artifacts = new DebBundler(new DebBundleConfiguration
                {
                    PackageName = "custom-name",
                    Version = "2:9.9.9-5",
                    Architecture = "armhf",
                    Maintainer = "Custom Maintainer <m@example.com>",
                    InstallRoot = "/opt/custom-name",
                    BinLink = "custom-cli"
                })
                .BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult();
            var deb = artifacts.Single();
            // The file name drops the epoch and keeps the explicit arch/revision.
            Assert(deb.Path.EndsWith("custom-name_9.9.9-5_armhf.deb", StringComparison.Ordinal),
                $"Unexpected override artifact name: {deb.Path}");
            var members = DebPackageReader.ReadAr(deb.Path);
            var control = Encoding.UTF8.GetString(
                DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content))
                    .Single(e => e.Name == "./control").Content);
            Assert(control.Contains("Package: custom-name\n", StringComparison.Ordinal) &&
                   control.Contains("Version: 2:9.9.9-5\n", StringComparison.Ordinal) &&
                   control.Contains("Architecture: armhf\n", StringComparison.Ordinal) &&
                   control.Contains("Maintainer: Custom Maintainer <m@example.com>\n", StringComparison.Ordinal),
                $"Control lacks the overrides:\n{control}");
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            var link = data.SingleOrDefault(e => e.Name == "./usr/bin/custom-cli");
            Assert(link is not null && link.LinkTarget == "/opt/custom-name/ExampleApp",
                $"A non-usr install root must use an absolute link target, got '{link?.LinkTarget}'.");
            Assert(data.Any(e => e.Name == "./opt/custom-name/ExampleApp"),
                "The payload must land under the overridden install root.");
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
            var artifacts = new DebBundler(new DebBundleConfiguration { BinLink = "" })
                .BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult();
            var members = DebPackageReader.ReadAr(artifacts.Single().Path);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            Assert(!data.Any(e => e.Name.StartsWith("./usr/bin/", StringComparison.Ordinal)),
                "BinLink=\"\" must remove the usr/bin symlink.");
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
            var cases = new (string Name, DebBundleConfiguration Settings)[]
            {
                ("relative install root", new DebBundleConfiguration { InstallRoot = "usr/lib/x" }),
                ("traversal install root", new DebBundleConfiguration { InstallRoot = "/usr/../etc" }),
                ("invalid package name", new DebBundleConfiguration { PackageName = "Bad_Name" }),
                ("invalid version", new DebBundleConfiguration { Version = "v1.0" }),
                ("invalid revision", new DebBundleConfiguration { Revision = "1-2" }),
                ("non-numeric epoch", new DebBundleConfiguration { Epoch = "x" }),
                ("invalid bin link", new DebBundleConfiguration { BinLink = "a/b" }),
                ("empty maintainer", new DebBundleConfiguration { Maintainer = " " })
            };
            foreach (var (name, settings) in cases)
            {
                var thrown = false;
                try
                {
                    new DebBundler(settings)
                        .BuildAsync(DebConfiguration(input, output))
                        .GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    thrown = true;
                }
                Assert(thrown, $"The '{name}' case must fail validation.");
                var debDir = Path.Combine(output, "linux-x64", "deb");
                Assert(!Directory.Exists(debDir) || !Directory.EnumerateFiles(debDir, "*.deb").Any(),
                    $"The '{name}' case left a .deb artifact behind.");
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
            var configuration = DebConfiguration(input, output);
            var renamed = new BundleConfiguration
            {
                ProductName = "Hello  Deb_App!",
                Identifier = configuration.Identifier,
                Publisher = configuration.Publisher,
                Version = configuration.Version,
                Description = configuration.Description,
                OutputDirectory = configuration.OutputDirectory,
                Targets = configuration.Targets
            };
            var artifacts = new DebBundler().BuildAsync(renamed).GetAwaiter().GetResult();
            Assert(artifacts.Single().Path.EndsWith("hello-deb-app_1.0.0-1_amd64.deb", StringComparison.Ordinal),
                $"The product name must kebab-case into the package name, got: {artifacts.Single().Path}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void Md5sumsMatch()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var deb = new DebBundler()
                .BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var controlEntries = DebPackageReader.ReadTar(
                DebPackageReader.Ungzip(members[1].Content));
            var md5sums = Encoding.UTF8.GetString(
                controlEntries.Single(e => e.Name == "./md5sums").Content);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            foreach (var line in md5sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var expectedHash = line.Substring(0, 32);
                var path = "./" + line.Substring(34);
                var entry = data.Single(e => e.Name == path);
                var actual = string.Concat(
                    System.Security.Cryptography.MD5.HashData(entry.Content)
                        .Select(b => b.ToString("x2")));
                Assert(expectedHash == actual,
                    $"md5sums mismatch for {path}.");
            }
            Assert(md5sums.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .All(line => !line.Contains("usr/bin/example-app")),
                "The md5sums list must cover regular files only, not the symlink.");
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
        var extra = Path.Combine(input, "extra-note.txt");
        File.WriteAllText(extra, "extra");
        try
        {
            var configuration = DebConfiguration(input, output);
            var deb = new DebBundler().BuildAsync(new BundleConfiguration
                {
                    ProductName = configuration.ProductName,
                    Identifier = configuration.Identifier,
                    Publisher = configuration.Publisher,
                    Version = configuration.Version,
                    Description = configuration.Description,
                    OutputDirectory = configuration.OutputDirectory,
                    Resources =
                    [
                        new BundleResourceConfiguration { Source = extra, TargetPath = "docs/note.txt" }
                    ],
                    Targets = configuration.Targets
                }).GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            Assert(data.Any(e => e.Name == "./usr/lib/example-app/docs/note.txt" &&
                                 e.Mode == 420),
                "A resource must land under the install root at its TargetPath.");
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
        try
        {
            var first = new DebBundler().BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult().Single().Path;
            var secondDir = Path.Combine(output, "second");
            var second = new DebBundler().BuildAsync(DebConfiguration(input, secondDir))
                .GetAwaiter().GetResult().Single().Path;
            Assert(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),
                "Two builds of the same input must produce byte-identical .deb files.");
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
            var deb = new DebBundler().BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var sidecar = deb.Path + ".sha256";
            Assert(File.Exists(sidecar), "The .sha256 sidecar is missing.");
            var expected = DebPackageWriter.Sha256Hex(File.ReadAllBytes(deb.Path));
            var line = File.ReadAllText(sidecar).Trim();
            Assert(line == expected + "  " + Path.GetFileName(deb.Path),
                $"The sidecar must be '<sha256>  <file>', got '{line}'.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsDebSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert(targets.Contains("DebPackageName=\"$(BundlerDebPackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("DebVersion=\"$(BundlerDebVersion)\"", StringComparison.Ordinal) &&
               targets.Contains("DebRevision=\"$(BundlerDebRevision)\"", StringComparison.Ordinal) &&
               targets.Contains("DebEpoch=\"$(BundlerDebEpoch)\"", StringComparison.Ordinal) &&
               targets.Contains("DebArchitecture=\"$(BundlerDebArchitecture)\"", StringComparison.Ordinal) &&
               targets.Contains("DebMaintainer=\"$(BundlerDebMaintainer)\"", StringComparison.Ordinal) &&
               targets.Contains("DebInstallRoot=\"$(BundlerDebInstallRoot)\"", StringComparison.Ordinal) &&
               targets.Contains("DebBinLink=\"$(BundlerDebBinLink)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerDeb* properties to the task.");
        Assert(props.Contains("<BundlerDebPackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerDebInstallRoot", StringComparison.Ordinal),
            "The BundlerDeb* properties lack defaults in the .props file.");
        Assert(task.Contains("new DebBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Deb", StringComparison.Ordinal),
            "The MSBuild task does not construct the .deb backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert(msbuildProject.Contains("DotNet.Bundler.Deb.dll", StringComparison.Ordinal),
            "The MSBuild package does not pack the Deb backend assembly.");
    }

    static BundleConfiguration DebConfiguration(
        string input,
        string output = "",
        string rid = "linux-x64",
        IReadOnlyList<PackageFormat>? formats = null) => new()
        {
            ProductName = "Example App",
            Identifier = "com.example.app",
            Publisher = "Example Publisher",
            Version = "1.0.0",
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
                    Formats = formats ?? [PackageFormat.Deb]
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
