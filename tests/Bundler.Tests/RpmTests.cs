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
            yield return ("Maps rpm relation clauses to tag triples", () => RunSync(MapsRelationTags));
            yield return ("Maps license, group and URL overrides", () => RunSync(MapsLicenseGroupUrl));
            yield return ("Stages freedesktop and doc files", () => RunSync(StagesFreedesktopFiles));
            yield return ("Honours a caller-supplied .desktop file", () => RunSync(DesktopFileOverride));
            yield return ("Maps arbitrary absolute destinations", () => RunSync(MapsArbitraryFiles));
            yield return ("Rejects invalid dependency clauses", () => RunSync(RejectsInvalidDependencyClauses));
            yield return ("Stages scriptlets with interpreter tags", () => RunSync(StagesScriptlets));
            yield return ("Synthesizes daemon-reload scriptlets for systemd units", () => RunSync(SystemdScriptletSynthesis));
            yield return ("Marks /etc files and explicit paths as %config(noreplace)", () => RunSync(ConfigFileFlags));
            yield return ("Rejects invalid scriptlets and compression", () => RunSync(RejectsInvalidScriptlets));
        }
    }

    static void StagesScriptlets()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var script = Path.Combine(input, "pre.sh");
        File.WriteAllText(script, "#!/bin/bash\necho hello\n");
        var plain = Path.Combine(input, "post.sh");
        File.WriteAllText(plain, "echo post\n");
        try
        {
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                PreInstallFile = script,
                PostInstallFile = plain,
                PostInstallProgram = "/usr/bin/python3"
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            // Shebang stripped from body; interpreter comes from the shebang.
            Assert(package.Main.Text(1023) == "echo hello\n",
                "PREIN must carry the body without the shebang line.");
            Assert(package.Main.Text(1085) == "/bin/bash",
                "PREINPROG must come from the script's shebang.");
            // No shebang and no program -> /bin/sh default.
            Assert(package.Main.Text(1024) == "echo post\n", "POSTIN body.");
            Assert(package.Main.Text(1086) == "/usr/bin/python3",
                "Explicit *Program overrides the default interpreter.");
            // Unset scriptlets stay absent.
            Assert(!package.Main.Tags.ContainsKey(1025) && !package.Main.Tags.ContainsKey(1087),
                "No PREUN/PREUNPROG when unset.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void SystemdScriptletSynthesis()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var unit = Path.Combine(input, "app.service");
        File.WriteAllText(unit, "[Service]\nExecStart=/bin/true\n");
        var postin = Path.Combine(input, "post.sh");
        File.WriteAllText(postin, "echo custom\n");
        try
        {
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                SystemdServiceFile = unit,
                PostInstallFile = postin
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            Assert(package.Payload.Any(e =>
                    e.Path == "/usr/lib/systemd/system/example-app.service"),
                "The unit must land under /usr/lib/systemd/system/<pkg>.service.");
            var post = package.Main.Text(1024);
            Assert(post.Contains("echo custom") && post.Contains("daemon-reload"),
                "POSTIN must merge the caller body with the daemon-reload epilogue.");
            Assert(package.Main.Text(1086) == "/bin/sh",
                "Synthesized POSTIN defaults to /bin/sh.");
            var postun = package.Main.Text(1026);
            Assert(postun.Contains("daemon-reload") && package.Main.Text(1088) == "/bin/sh",
                "POSTUN must be synthesized for daemon-reload too.");
            Assert(!package.Main.Tags.ContainsKey(1023) && !package.Main.Tags.ContainsKey(1025),
                "PREIN/PREUN stay absent with only a unit knob set.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void ConfigFileFlags()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(input, "defaults.conf");
        File.WriteAllText(config, "k=v");
        var extra = Path.Combine(input, "extra.conf");
        File.WriteAllText(extra, "e=1");
        try
        {
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                Files =
                [
                    new RpmFileEntry { Source = config, Destination = "/etc/example/defaults.conf" },
                    new RpmFileEntry { Source = extra, Destination = "/opt/example/extra.conf" }
                ],
                ConfigFiles = ["/opt/example/extra.conf"]
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var dirnames = package.Main.Strings(1118);
            var dirindexes = package.Main.Ints(1116);
            var basenames = package.Main.Strings(1117);
            var flags = package.Main.Ints(1037);
            string Full(int i) => dirnames[dirindexes[i]] + basenames[i];
            int Flag(string path)
            {
                for (var i = 0; i < basenames.Length; i++)
                {
                    if (Full(i) == path + "/") return flags[i]; // dirnames keep trailing '/'
                    if (Full(i).TrimEnd('/') == path) return flags[i];
                }
                throw new InvalidOperationException("missing " + path);
            }
            var etc = Flag("/etc/example/defaults.conf");
            Assert((etc & 1) != 0 && (etc & 16) != 0,
                "/etc files must be %config(noreplace) (flags 1|16).");
            var opt = Flag("/opt/example/extra.conf");
            Assert((opt & 1) != 0 && (opt & 16) != 0,
                "Explicit ConfigFiles entries must be %config(noreplace) too.");
            var bin = Flag("/usr/lib/example-app/ExampleApp");
            Assert((bin & 17) == 0, "Non-config files must not get config flags.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void RejectsInvalidScriptlets()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var crlf = Path.Combine(input, "crlf.sh");
        File.WriteAllText(crlf, "#!/bin/sh\r\necho hi\r\n");
        var lonelyCr = Path.Combine(input, "cr.sh");
        File.WriteAllText(lonelyCr, "#!/bin/sh\necho a\rb\n");
        var emptyShebang = Path.Combine(input, "empty.sh");
        File.WriteAllText(emptyShebang, "#!\necho hi\n");
        try
        {
            foreach (var (cfg, label) in new (RpmBundleConfiguration, string)[]
            {
                (new RpmBundleConfiguration { PreInstallFile = crlf }, "CRLF body must be rejected"),
                (new RpmBundleConfiguration { PreInstallFile = lonelyCr }, "lone CR must be rejected"),
                (new RpmBundleConfiguration { PreInstallFile = emptyShebang }, "empty shebang must be rejected"),
                (new RpmBundleConfiguration { PreInstallFile = input + "/missing.sh" }, "missing file must be rejected"),
                (new RpmBundleConfiguration { Compression = "xz" }, "xz compression must be rejected"),
                (new RpmBundleConfiguration { ConfigFiles = ["/usr/lib/example-app/nowhere.conf"] },
                    "ConfigFiles must reference a real payload file")
            })
            {
                var thrown = false;
                try
                {
                    new RpmBundler(cfg).BuildAsync(RpmConfiguration(input, output))
                        .GetAwaiter().GetResult();
                }
                catch (Exception e) when (e is ArgumentException or FileNotFoundException)
                {
                    thrown = true;
                }
                Assert(thrown, label);
            }
        }
        finally
        {
            Cleanup(input, output);
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
               targets.Contains("RpmBinLink=\"$(BundlerRpmBinLink)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmRequires=\"$(BundlerRpmRequires)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmProvides=\"$(BundlerRpmProvides)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmConflicts=\"$(BundlerRpmConflicts)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmObsoletes=\"$(BundlerRpmObsoletes)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmRecommends=\"$(BundlerRpmRecommends)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmSuggests=\"$(BundlerRpmSuggests)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmLicense=\"$(BundlerRpmLicense)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmGroup=\"$(BundlerRpmGroup)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmUrl=\"$(BundlerRpmUrl)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmCategories=\"$(BundlerRpmCategories)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmDesktopFile=\"$(BundlerRpmDesktopFile)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmMetainfoFile=\"$(BundlerRpmMetainfoFile)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmChangelogFile=\"$(BundlerRpmChangelogFile)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmFiles=\"@(BundlerRpmFile)\"", StringComparison.Ordinal),
            "MSBuild does not map the BundlerRpm* properties to the task.");
        Assert(props.Contains("<BundlerRpmPackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmVersion", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmRelease", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmEpoch", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmArchitecture", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmVendor", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmInstallRoot", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmBinLink", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmRequires", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmLicense", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmCategories", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmDesktopFile", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmMetainfoFile", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmChangelogFile", StringComparison.Ordinal),
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

    static void MapsRelationTags()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                Requires = ["libc.so.6", "libfoo >= 1.2-3"],
                Provides = ["example-plugin = 2.0"],
                Conflicts = ["old-example < 1.0"],
                Obsoletes = ["example-legacy"],
                Recommends = ["example-extra >= 0.5"],
                Suggests = ["example-docs"]
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);

            var requireNames = package.Main.Strings(1049);
            var requireFlags = package.Main.Ints(1048);
            var requireVersions = package.Main.Strings(1050);
            var at = Array.IndexOf(requireNames, "libfoo");
            Assert(at >= 3, "Caller Requires must be appended after the rpmlib clauses.");
            Assert(requireFlags[at] == (4 | 8) && requireVersions[at] == "1.2-3",
                "'libfoo >= 1.2-3' must parse to GREATER|EQUAL + '1.2-3'.");
            at = Array.IndexOf(requireNames, "libc.so.6");
            Assert(requireFlags[at] == 0 && requireVersions[at] == "",
                "A bare Requires name must carry no flags or version.");

            var provideNames = package.Main.Strings(1047);
            var provideFlags = package.Main.Ints(1112);
            var provideVersions = package.Main.Strings(1113);
            at = Array.IndexOf(provideNames, "example-plugin");
            Assert(at == 2 && provideFlags[at] == 8 && provideVersions[at] == "2.0",
                "Caller Provides must be appended after the self-provides.");

            Assert(package.Main.Strings(1054).SequenceEqual(new[] { "old-example" }) &&
                   package.Main.Ints(1053).SequenceEqual(new[] { 2 }) &&
                   package.Main.Strings(1055).SequenceEqual(new[] { "1.0" }),
                "Conflicts must land in CONFLICTNAME/FLAGS/VERSION (1054/1053/1055).");
            Assert(package.Main.Strings(1090).SequenceEqual(new[] { "example-legacy" }) &&
                   package.Main.Ints(1114).SequenceEqual(new[] { 0 }),
                "Obsoletes must land in OBSOLETENAME/FLAGS (1090/1114).");
            Assert(package.Main.Strings(5046).SequenceEqual(new[] { "example-extra" }) &&
                   package.Main.Ints(5048).SequenceEqual(new[] { 4 | 8 }) &&
                   package.Main.Strings(5047).SequenceEqual(new[] { "0.5" }),
                "Recommends must land in RECOMMENDNAME/VERSION/FLAGS (5046/5047/5048).");
            Assert(package.Main.Strings(5049).SequenceEqual(new[] { "example-docs" }),
                "Suggests must land in SUGGESTNAME (5049).");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsLicenseGroupUrl()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                License = "MIT OR Apache-2.0",
                Group = "Applications/Engineering",
                Url = "https://example.com/rpm-override"
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            Assert(package.Main.Text(1014) == "MIT OR Apache-2.0", "LICENSE must take the SPDX string.");
            Assert(package.Main.Text(1016) == "Applications/Engineering", "GROUP override.");
            Assert(package.Main.Text(1020) == "https://example.com/rpm-override",
                "URL must prefer the explicit knob over Homepage.");

            // Defaults: License 'Unspecified', Group 'Unspecified', URL = Homepage.
            var defaults = RpmPackageReader.Read(new RpmBundler()
                .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single().Path);
            Assert(defaults.Main.Text(1014) == "Unspecified", "Default LICENSE.");
            Assert(defaults.Main.Text(1016) == "Unspecified", "Default GROUP.");
            Assert(defaults.Main.Text(1020) == "https://example.com/app", "Default URL = Homepage.");

            // Explicit empty Url/Group omit the tags entirely.
            var omitted = RpmPackageReader.Read(new RpmBundler(
                    new RpmBundleConfiguration { Url = "", Group = "" })
                .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single().Path);
            Assert(omitted.Main.Strings(1020).Length == 0, "Url=\"\" must omit the URL tag.");
            Assert(omitted.Main.Strings(1016).Length == 0, "Group=\"\" must omit the GROUP tag.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void StagesFreedesktopFiles()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var icon = Path.Combine(input, "icon.png");
        File.WriteAllBytes(icon, Png48x48());
        var metainfo = Path.Combine(input, "app.metainfo.xml");
        File.WriteAllText(metainfo, "<component/>");
        var changelog = Path.Combine(input, "CHANGELOG.md");
        File.WriteAllText(changelog, "# changes");
        var license = Path.Combine(input, "LICENSE.txt");
        File.WriteAllText(license, "MIT");
        try
        {
            var config = RpmConfiguration(input, output);
            config = new BundleConfiguration
            {
                ProductName = config.ProductName,
                Identifier = config.Identifier,
                Publisher = config.Publisher,
                Version = config.Version,
                Homepage = config.Homepage,
                Description = config.Description,
                LicenseFile = license,
                OutputDirectory = config.OutputDirectory,
                Icons = [icon],
                UrlProtocols = [new BundleUrlProtocolConfiguration { Schemes = ["example"], Name = "Example" }],
                Targets = config.Targets
            };
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                Categories = "Utility;Development",
                MetainfoFile = metainfo,
                ChangelogFile = changelog
            }).BuildAsync(config).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var paths = package.Payload.Select(e => e.Path).ToArray();

            var desktop = package.Payload.Single(e => e.Path == "/usr/share/applications/example-app.desktop");
            var content = Encoding.UTF8.GetString(desktop.Data);
            Assert(content.Contains("Type=Application") &&
                   content.Contains("Exec=example-app %u") &&
                   content.Contains("Icon=example-app") &&
                   content.Contains("Categories=Utility;Development;") &&
                   content.Contains("MimeType=x-scheme-handler/example;"),
                "The generated .desktop must carry exec/icon/categories/protocol mime entries.");
            Assert(paths.Contains("/usr/share/icons/hicolor/48x48/apps/example-app.png"),
                "A 48px PNG must land in the hicolor tree.");
            Assert(paths.Contains("/usr/share/metainfo/example-app.metainfo.xml"),
                "Metainfo must land in /usr/share/metainfo.");
            Assert(paths.Contains("/usr/share/doc/example-app/changelog.gz"),
                "The changelog must land gzipped in /usr/share/doc/<pkg>.");
            Assert(paths.Contains("/usr/share/licenses/example-app/LICENSE.txt"),
                "LicenseFile must land in /usr/share/licenses/<pkg>.");

            // File flags: %license=128 on license files, %doc=2 on doc/man files.
            var basenames = package.Main.Strings(1117);
            var dirnames = package.Main.Strings(1118);
            var dirindexes = package.Main.Ints(1116);
            var flags = package.Main.Ints(1037);
            string FullPath(int i) => dirnames[dirindexes[i]] + basenames[i];
            for (var i = 0; i < basenames.Length; i++)
            {
                var path = FullPath(i);
                if (path == "/usr/share/licenses/example-app/LICENSE.txt")
                {
                    Assert(flags[i] == 128, "License files must carry RPMFILE_LICENSE (128).");
                }
                else if (path == "/usr/share/doc/example-app/changelog.gz")
                {
                    Assert(flags[i] == 2, "Doc files must carry RPMFILE_DOC (2).");
                }
            }

            // Owned dirs: the license/doc package dirs, but never shared parents.
            var dirs = package.Payload.Where(e => (e.Mode & 0xF000) == 0x4000)
                .Select(e => e.Path).ToArray();
            Assert(dirs.Contains("/usr/share/licenses/example-app") &&
                   dirs.Contains("/usr/share/doc/example-app"),
                "Package-owned leaf dirs must be claimed.");
            Assert(!dirs.Contains("/usr/share") && !dirs.Contains("/usr/share/applications") &&
                   !dirs.Contains("/usr/share/icons") && !dirs.Contains("/usr/share/doc"),
                "Shared system dirs must not be owned.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void DesktopFileOverride()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var custom = Path.Combine(input, "custom.desktop");
        File.WriteAllText(custom, "[Desktop Entry]\nName=Custom\n");
        try
        {
            var artifact = new RpmBundler(
                    new RpmBundleConfiguration { DesktopFile = custom })
                .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var desktop = package.Payload.Single(e => e.Path == "/usr/share/applications/example-app.desktop");
            Assert(Encoding.UTF8.GetString(desktop.Data).Contains("Name=Custom"),
                "DesktopFile must replace the generated .desktop verbatim.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void MapsArbitraryFiles()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(input, "defaults.conf");
        File.WriteAllText(config, "key=value");
        try
        {
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                Files = [new RpmFileEntry { Source = config, Destination = "/etc/example/defaults.conf" }]
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var entry = package.Payload.Single(e => e.Path == "/etc/example/defaults.conf");
            Assert(Encoding.UTF8.GetString(entry.Data) == "key=value",
                "RpmFile entries must land at their absolute destination.");
            var dirs = package.Payload.Where(e => (e.Mode & 0xF000) == 0x4000)
                .Select(e => e.Path).ToArray();
            Assert(dirs.Contains("/etc/example") && !dirs.Contains("/etc"),
                "New non-shared parents are owned; /etc itself is not.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static void RejectsInvalidDependencyClauses()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var clause in new[] { "foo != 1.0", "foo bar", "", "foo =" })
            {
                var thrown = false;
                try
                {
                    new RpmBundler(new RpmBundleConfiguration { Requires = [clause] })
                        .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult();
                }
                catch (ArgumentException)
                {
                    thrown = true;
                }
                Assert(thrown, $"The clause '{clause}' must be rejected.");
            }
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static byte[] Png48x48()
    {
        // Minimal valid PNG header whose IHDR declares 48x48.
        var png = new byte[33];
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        magic.CopyTo(png, 0);
        png[12] = 0x49; png[13] = 0x48; png[14] = 0x44; png[15] = 0x52; // "IHDR"
        png[19] = 48; png[23] = 48;                                    // width/height 48
        return png;
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
