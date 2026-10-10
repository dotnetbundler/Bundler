using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Deb;
using System.Text;

public static class DebTests
{

    [Fact]
    static void RejectsNonDebFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var wrongTarget = Assert.ThrowsAny<NotSupportedException>(
                () => new DebBundler()
                    .BuildAsync(DebConfiguration(input, formats: [PackageFormat.Rpm]))
                    .GetAwaiter().GetResult());
            Assert.Contains("Deb targets only", wrongTarget.Message);
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
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
            Assert.EndsWith(Path.Combine("linux-x64", "deb", expectedName), artifact.Path);

            var members = DebPackageReader.ReadAr(artifact.Path);
            Assert.True(members.Select(m => m.Name).SequenceEqual(
                    new[] { "debian-binary", "control.tar.gz", "data.tar.gz" }),
                $"Unexpected ar members: {string.Join(',', members.Select(m => m.Name))}");
            Assert.Equal("2.0\n", Encoding.ASCII.GetString(members[0].Content));

            var controlEntries = DebPackageReader.ReadTar(
                DebPackageReader.Ungzip(members[1].Content));
            var control = Encoding.UTF8.GetString(
                controlEntries.Single(e => e.Name == "./control").Content);
            Assert.True(control.Contains("Package: example-app\n", StringComparison.Ordinal) &&
                   control.Contains("Version: 1.0.0-1\n", StringComparison.Ordinal) &&
                   control.Contains("Architecture: amd64\n", StringComparison.Ordinal) &&
                   control.Contains("Maintainer: Example Publisher\n", StringComparison.Ordinal) &&
                   control.Contains("Priority: optional\n", StringComparison.Ordinal) &&
                   control.Contains("Homepage: https://example.com/app\n", StringComparison.Ordinal) &&
                   control.Contains("Installed-Size: ", StringComparison.Ordinal) &&
                   control.Contains("Description: Example application\n", StringComparison.Ordinal),
                $"Control file is missing core fields:\n{control}");
            Assert.Contains(controlEntries, e => e.Name == "./md5sums");

            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            var names = data.Select(e => e.Name).ToArray();
            Assert.True(names.Contains("./usr/lib/example-app/ExampleApp") &&
                   names.Contains("./usr/lib/example-app/ExampleApp.dll") &&
                   names.Contains("./usr/bin/example-app"),
                $"data.tar is missing payload entries: {string.Join(',', names)}");
            var executable = data.Single(e => e.Name == "./usr/lib/example-app/ExampleApp");
            Assert.Equal(493 /* 0755 */, executable.Mode);
            var lib = data.Single(e => e.Name == "./usr/lib/example-app/ExampleApp.dll");
            Assert.Equal(420 /* 0644 */, lib.Mode);
            var link = data.Single(e => e.Name == "./usr/bin/example-app");
            Assert.True(link.Kind == TarEntryKind.Symlink &&
                   link.LinkTarget == "../lib/example-app/ExampleApp",
                $"usr/bin must be a relative symlink into the install root, got '{link.LinkTarget}'.");
            Assert.True(data.Any(e => e.Kind == TarEntryKind.Directory && e.Name == "./usr/lib/example-app"),
                "The install-root directory entry must be present.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.EndsWith("example-app_2.5.0~beta.3+build.7-1_arm64.deb", deb.Path);
            var members = DebPackageReader.ReadAr(deb.Path);
            var control = Encoding.UTF8.GetString(
                DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content))
                    .Single(e => e.Name == "./control").Content);
            Assert.True(control.Contains("Version: 2.5.0~beta.3+build.7-1\n", StringComparison.Ordinal) &&
                   control.Contains("Architecture: arm64\n", StringComparison.Ordinal),
                $"Control lacks the mapped version/arm64:\n{control}");
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
            var artifacts = new DebBundler(new DebBundleConfiguration
                {
                    PackageName = "custom-name",
                    Version = "2:9.9.9-5",
                    Architecture = "armhf",
                    Vendor = "Custom Maintainer <m@example.com>",
                    InstallRoot = "/opt/custom-name",
                    BinLink = "custom-cli"
                })
                .BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult();
            var deb = artifacts.Single();
            // The file name drops the epoch and keeps the explicit arch/revision.
            Assert.EndsWith("custom-name_9.9.9-5_armhf.deb", deb.Path);
            var members = DebPackageReader.ReadAr(deb.Path);
            var control = Encoding.UTF8.GetString(
                DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content))
                    .Single(e => e.Name == "./control").Content);
            Assert.True(control.Contains("Package: custom-name\n", StringComparison.Ordinal) &&
                   control.Contains("Version: 2:9.9.9-5\n", StringComparison.Ordinal) &&
                   control.Contains("Architecture: armhf\n", StringComparison.Ordinal) &&
                   control.Contains("Maintainer: Custom Maintainer <m@example.com>\n", StringComparison.Ordinal),
                $"Control lacks the overrides:\n{control}");
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            var link = data.SingleOrDefault(e => e.Name == "./usr/bin/custom-cli");
            Assert.True(link is not null && link.LinkTarget == "/opt/custom-name/ExampleApp",
                $"A non-usr install root must use an absolute link target, got '{link?.LinkTarget}'.");
            Assert.Contains(data, e => e.Name == "./opt/custom-name/ExampleApp");
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
            var artifacts = new DebBundler(new DebBundleConfiguration { BinLink = "" })
                .BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult();
            var members = DebPackageReader.ReadAr(artifacts.Single().Path);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            Assert.False(data.Any(e => e.Name.StartsWith("./usr/bin/", StringComparison.Ordinal)), "BinLink=\"\" must remove the usr/bin symlink.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void BinLinkNoneDisables()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new DebBundler(new DebBundleConfiguration { BinLink = "none" })
                .BuildAsync(DebConfiguration(input, output)).GetAwaiter().GetResult();
            var members = DebPackageReader.ReadAr(artifacts.Single().Path);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            Assert.False(data.Any(e => e.Name.StartsWith("./usr/bin/", StringComparison.Ordinal)),
                "BinLink=\"none\" must remove the usr/bin symlink like \"\" does.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    // 默认 BinLink 先落 usr/bin 目录条目，Files 再映同名文件——目录→文件冲突显式拒绝。
    [Fact]
    static void RejectsFileOverClaimedDirectory()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var extra = Path.Combine(input, "extra.conf");
        File.WriteAllText(extra, "k=v");
        try
        {
            Assert.ThrowsAny<InvalidOperationException>(
                () => new DebBundler(new DebBundleConfiguration
                {
                    Files = [new DebFileEntry { Source = extra, Destination = "/usr/bin" }]
                }).BuildAsync(DebConfiguration(input, output)).GetAwaiter().GetResult());
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    // ar 尺寸字段 10 位十进制：超长成员必须显式抛而非静默溢出邻域。
    [Fact]
    static void RejectsArMemberOverflowingSizeField()
    {
        var root = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var big = Path.Combine(root, "big.bin");
        try
        {
            Directory.CreateDirectory(root);
            using (var stream = new FileStream(big, FileMode.Create))
            {
                stream.SetLength(10_000_000_000L);
            }
            using var output = new MemoryStream();
            Assert.ThrowsAny<ArgumentException>(
                () => ArWriter.Write(output, [ArMember.FromFile("data.tar.gz", big)]));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
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
                ("invalid revision", new DebBundleConfiguration { Release = "1-2" }),
                ("non-numeric epoch", new DebBundleConfiguration { Epoch = "x" }),
                ("invalid bin link", new DebBundleConfiguration { BinLink = "a/b" }),
                ("empty maintainer", new DebBundleConfiguration { Vendor = " " })
            };
            foreach (var (name, settings) in cases)
            {
                var rejected = Assert.ThrowsAny<Exception>(
                    () => new DebBundler(settings)
                        .BuildAsync(DebConfiguration(input, output))
                        .GetAwaiter().GetResult());
                Assert.True(rejected is ArgumentException or InvalidOperationException,
                    $"The '{name}' case must fail validation: {rejected.GetType().Name}: {rejected.Message}");
                var debDir = Path.Combine(output, "linux-x64", "deb");
                Assert.True(!Directory.Exists(debDir) || !Directory.EnumerateFiles(debDir, "*.deb").Any(),
                    $"The '{name}' case left a .deb artifact behind.");
            }
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.EndsWith("hello-deb-app_1.0.0-1_amd64.deb", artifacts.Single().Path);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
                Assert.True(expectedHash == actual,
                    $"md5sums mismatch for {path}.");
            }
            Assert.True(md5sums.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .All(line => !line.Contains("usr/bin/example-app")),
                "The md5sums list must cover regular files only, not the symlink.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void WritesDesktopIntegration()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var icon = Path.Combine(input, "..", Guid.NewGuid().ToString("N") + "-icon.png");
        var icon2x = Path.Combine(input, "..", Guid.NewGuid().ToString("N") + "-icon@2x.png");
        File.WriteAllBytes(icon, PngBytes(48, 48));
        File.WriteAllBytes(icon2x, PngBytes(96, 96));
        // The hicolor slot comes from the @2x file stem, not the generated name.
        var namedIcon = Path.Combine(Path.GetDirectoryName(icon2x)!,
            "app@2x.png");
        File.Move(icon2x, namedIcon);
        try
        {
            var configuration = DebConfiguration(input, output);
            var deb = new DebBundler(new DebBundleConfiguration
                {
                    Categories = ["Utility", "Development"]
                })
                .BuildAsync(new BundleConfiguration
                {
                    ProductName = configuration.ProductName,
                    Identifier = configuration.Identifier,
                    Publisher = configuration.Publisher,
                    Version = configuration.Version,
                    Description = configuration.Description,
                    OutputDirectory = configuration.OutputDirectory,
                    Icons = [icon, namedIcon],
                    FileAssociations =
                    [
                        new BundleFileAssociationConfiguration
                        {
                            Extensions = [".bdl"],
                            MimeType = "application/x-bundle"
                        }
                    ],
                    UrlProtocols =
                    [
                        new BundleUrlProtocolConfiguration { Schemes = ["bdl"] }
                    ],
                    Targets = configuration.Targets
                }).GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));

            var desktop = Encoding.UTF8.GetString(
                data.Single(e => e.Name == "./usr/share/applications/example-app.desktop").Content);
            Assert.True(desktop.Contains("Type=Application\n", StringComparison.Ordinal) &&
                   desktop.Contains("Name=Example App\n", StringComparison.Ordinal) &&
                   desktop.Contains("Comment=Example application\n", StringComparison.Ordinal) &&
                   desktop.Contains("Exec=example-app %u\n", StringComparison.Ordinal) &&
                   desktop.Contains("Icon=example-app\n", StringComparison.Ordinal) &&
                   desktop.Contains("Terminal=false\n", StringComparison.Ordinal) &&
                   desktop.Contains("Categories=Utility;Development;\n", StringComparison.Ordinal) &&
                   desktop.Contains("MimeType=application/x-bundle;x-scheme-handler/bdl;\n", StringComparison.Ordinal),
                $"The generated .desktop content is wrong:\n{desktop}");

            Assert.True(data.Any(e => e.Name == "./usr/share/icons/hicolor/48x48/apps/example-app.png") &&
                   data.Any(e => e.Name == "./usr/share/icons/hicolor/48x48@2/apps/example-app.png"),
                "Icons must land under hicolor <WxH> and <WxH>@2 directories.");
        }
        finally
        {
            Cleanup(input, output);
            File.Delete(icon);
            File.Delete(namedIcon);
        }
    }

    [Fact]
    static void WritesRelationAndDocFields()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var filesDir = Path.Combine(output, "..", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(filesDir);
        var changelog = Path.Combine(filesDir, "CHANGELOG.md");
        var licenseFile = Path.Combine(filesDir, "LICENSE.txt");
        var metainfo = Path.Combine(filesDir, "app.metainfo.xml");
        var extraConf = Path.Combine(filesDir, "defaults.conf");
        File.WriteAllText(changelog, "# Changelog\n");
        File.WriteAllText(licenseFile, "MIT\n");
        File.WriteAllText(metainfo, "<component type=\"desktop-application\"/>\n");
        File.WriteAllText(extraConf, "key=value\n");
        try
        {
            var configuration = DebConfiguration(input, output);
            var deb = new DebBundler(new DebBundleConfiguration
                {
                    Depends = ["libc6 (>= 2.35)", "libssl3"],
                    Recommends = ["ca-certificates"],
                    Provides = ["virtual-example"],
                    Conflicts = ["legacy-example"],
                    Replaces = ["legacy-example"],
                    Section = "utils",
                    Priority = "extra",
                    MetainfoFile = metainfo,
                    ChangelogFile = changelog,
                    Files =
                    [
                        new DebFileEntry { Source = extraConf, Destination = "/etc/example-app/defaults.conf" }
                    ]
                })
                .BuildAsync(new BundleConfiguration
                {
                    ProductName = configuration.ProductName,
                    Identifier = configuration.Identifier,
                    Publisher = configuration.Publisher,
                    Version = configuration.Version,
                    Description = configuration.Description,
                    LicenseFile = licenseFile,
                    OutputDirectory = configuration.OutputDirectory,
                    Targets = configuration.Targets
                }).GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var control = Encoding.UTF8.GetString(
                DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content))
                    .Single(e => e.Name == "./control").Content);
            foreach (var field in new[]
            {
                "Depends: libc6 (>= 2.35), libssl3\n",
                "Recommends: ca-certificates\n",
                "Provides: virtual-example\n",
                "Conflicts: legacy-example\n",
                "Replaces: legacy-example\n",
                "Section: utils\n",
                "Priority: extra\n"
            })
            {
                Assert.Contains(field, control);
            }
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            Assert.Contains(data, e => e.Name == "./usr/share/metainfo/example-app.metainfo.xml");
            var copyright = data.SingleOrDefault(e => e.Name == "./usr/share/doc/example-app/copyright");
            Assert.True(copyright is not null && Encoding.UTF8.GetString(copyright.Content) == "MIT\n",
                "LicenseFile must land at usr/share/doc/<pkg>/copyright.");
            var gz = data.Single(e => e.Name == "./usr/share/doc/example-app/changelog.gz");
            Assert.Equal("# Changelog\n", Encoding.UTF8.GetString(DebPackageReader.Ungzip(gz.Content)));
            var conf = data.SingleOrDefault(e => e.Name == "./etc/example-app/defaults.conf");
            Assert.True(conf is not null && Encoding.UTF8.GetString(conf.Content) == "key=value\n",
                "DebFile entries must land at their absolute destination.");
        }
        finally
        {
            Cleanup(input, output, filesDir);
        }
    }

    [Fact]
    static void RejectsInvalidMetadata()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var filesDir = Path.Combine(output, "..", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(filesDir);
        var anyFile = Path.Combine(filesDir, "x.txt");
        var notPng = Path.Combine(filesDir, "icon.png");
        File.WriteAllText(anyFile, "x");
        File.WriteAllBytes(notPng, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        try
        {
            var cases = new (string Name, DebBundleConfiguration Settings, string[] Icons)[]
            {
                ("bad priority", new DebBundleConfiguration { Priority = "ultra" }, []),
                ("bad section", new DebBundleConfiguration { Section = "Bad Section!" }, []),
                ("newline in Depends", new DebBundleConfiguration { Depends = ["libc6\nbad"] }, []),
                ("relative file destination",
                    new DebBundleConfiguration { Files = [new DebFileEntry { Source = anyFile, Destination = "etc/x.conf" }] }, []),
                ("traversal file destination",
                    new DebBundleConfiguration { Files = [new DebFileEntry { Source = anyFile, Destination = "/etc/../x.conf" }] }, []),
                ("non-PNG icon", new DebBundleConfiguration(), [notPng]),
                ("missing DesktopFile", new DebBundleConfiguration { DesktopFile = Path.Combine(filesDir, "nope.desktop") }, []),
                ("bad categories", new DebBundleConfiguration { Categories = ["Not A Category!"] }, [])
            };
            foreach (var (name, settings, icons) in cases)
            {
                var configuration = DebConfiguration(input, output);
                var rejected = Assert.ThrowsAny<Exception>(
                    () => new DebBundler(settings).BuildAsync(new BundleConfiguration
                        {
                            ProductName = configuration.ProductName,
                            Identifier = configuration.Identifier,
                            Publisher = configuration.Publisher,
                            Version = configuration.Version,
                            Description = configuration.Description,
                            OutputDirectory = configuration.OutputDirectory,
                            Icons = icons,
                            Targets = configuration.Targets
                        }).GetAwaiter().GetResult());
                Assert.True(rejected is ArgumentException or FileNotFoundException or InvalidOperationException,
                    $"The '{name}' case must fail validation: {rejected.GetType().Name}: {rejected.Message}");
            }
        }
        finally
        {
            Cleanup(input, output, filesDir);
        }
    }

    [Fact]
    static void DesktopFileOverride()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var filesDir = Path.Combine(output, "..", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(filesDir);
        var desktop = Path.Combine(filesDir, "custom.desktop");
        const string custom = "[Desktop Entry]\nType=Application\nName=Custom\nExec=/opt/x/run\n";
        File.WriteAllText(desktop, custom);
        try
        {
            var deb = new DebBundler(new DebBundleConfiguration { DesktopFile = desktop })
                .BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            var entry = data.Single(e => e.Name == "./usr/share/applications/example-app.desktop");
            Assert.Equal(custom, Encoding.UTF8.GetString(entry.Content));
        }
        finally
        {
            Cleanup(input, output, filesDir);
        }
    }

    [Fact]
    static void MaintainerScriptsAndConffiles()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var filesDir = Path.Combine(output, "..", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(filesDir);
        var postinst = Path.Combine(filesDir, "postinst.sh");
        var prerm = Path.Combine(filesDir, "prerm.sh");
        var conf = Path.Combine(filesDir, "defaults.conf");
        File.WriteAllText(postinst, "#!/bin/sh\nset -e\ntouch /tmp/marker\n");
        File.WriteAllText(prerm, "#!/bin/sh\nexit 0\n");
        File.WriteAllText(conf, "key=value\n");
        try
        {
            var configuration = DebConfiguration(input, output);
            var deb = new DebBundler(new DebBundleConfiguration
                {
                    PostInstallFile = postinst,
                    PreUninstallFile = prerm,
                    ConfigFiles = ["/usr/lib/example-app/libplaceholder.conf"],
                    Files = [new DebFileEntry { Source = conf, Destination = "/etc/example-app/defaults.conf" },
                             new DebFileEntry { Source = conf, Destination = "/usr/lib/example-app/libplaceholder.conf" }]
                })
                .BuildAsync(new BundleConfiguration
                {
                    ProductName = configuration.ProductName,
                    Identifier = configuration.Identifier,
                    Publisher = configuration.Publisher,
                    Version = configuration.Version,
                    Description = configuration.Description,
                    OutputDirectory = configuration.OutputDirectory,
                    Targets = configuration.Targets
                }).GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var controlEntries = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content));

            var postinstEntry = controlEntries.Single(e => e.Name == "./postinst");
            Assert.Equal(493, postinstEntry.Mode);
            Assert.Contains("touch /tmp/marker", Encoding.UTF8.GetString(postinstEntry.Content));
            Assert.True(controlEntries.Any(e => e.Name == "./prerm" && e.Mode == 493),
                "prerm must be present with mode 0755.");
            Assert.True(!controlEntries.Any(e => e.Name == "./preinst" || e.Name == "./postrm"),
                "Unset scripts must not be packed.");

            var conffiles = controlEntries.SingleOrDefault(e => e.Name == "./conffiles");
            Assert.NotNull(conffiles);
            var list = Encoding.UTF8.GetString(conffiles!.Content);
            Assert.Equal("/etc/example-app/defaults.conf\n/usr/lib/example-app/libplaceholder.conf\n", list);
        }
        finally
        {
            Cleanup(input, output, filesDir);
        }
    }

    [Fact]
    static void SystemdUnitDaemonReload()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var filesDir = Path.Combine(output, "..", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(filesDir);
        var unit = Path.Combine(filesDir, "fixture.service");
        var postinst = Path.Combine(filesDir, "postinst.sh");
        File.WriteAllText(unit, "[Unit]\nDescription=Fixture\n[Service]\nExecStart=/usr/bin/example-app\n");
        File.WriteAllText(postinst, "#!/bin/sh\nset -e\nldconfig\n");
        try
        {
            var configuration = DebConfiguration(input, output);
            var deb = new DebBundler(new DebBundleConfiguration
                {
                    SystemdServiceFile = unit,
                    PostInstallFile = postinst
                })
                .BuildAsync(new BundleConfiguration
                {
                    ProductName = configuration.ProductName,
                    Identifier = configuration.Identifier,
                    Publisher = configuration.Publisher,
                    Version = configuration.Version,
                    Description = configuration.Description,
                    OutputDirectory = configuration.OutputDirectory,
                    Targets = configuration.Targets
                }).GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var controlEntries = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content));
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));

            var unitEntry = data.SingleOrDefault(e => e.Name == "./usr/lib/systemd/system/example-app.service");
            Assert.NotNull(unitEntry);
            Assert.Contains("ExecStart=/usr/bin/example-app", Encoding.UTF8.GetString(unitEntry!.Content));

            var postinstEntry = controlEntries.Single(e => e.Name == "./postinst");
            var postinstText = Encoding.UTF8.GetString(postinstEntry.Content);
            Assert.True(postinstText.Contains("ldconfig", StringComparison.Ordinal) &&
                   postinstText.Contains("systemctl daemon-reload || true", StringComparison.Ordinal),
                $"postinst must merge the caller script with daemon-reload:\n{postinstText}");
            Assert.Equal(493, postinstEntry.Mode);

            // Unit only (no caller postinst) must still synthesize a valid script.
            var deb2 = new DebBundler(new DebBundleConfiguration { SystemdServiceFile = unit })
                .BuildAsync(configuration).GetAwaiter().GetResult().Single();
            var control2 = DebPackageReader.ReadTar(
                DebPackageReader.Ungzip(DebPackageReader.ReadAr(deb2.Path)[1].Content));
            var soloPostinst = control2.Single(e => e.Name == "./postinst");
            var soloText = Encoding.UTF8.GetString(soloPostinst.Content);
            Assert.True(soloText.StartsWith("#!/bin/sh\n", StringComparison.Ordinal) &&
                   soloText.Contains("systemctl daemon-reload || true", StringComparison.Ordinal),
                $"The synthesized postinst must be self-contained:\n{soloText}");
        }
        finally
        {
            Cleanup(input, output, filesDir);
        }
    }

    [Fact]
    static void RejectsInvalidDeb3Knobs()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var filesDir = Path.Combine(output, "..", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(filesDir);
        var noShebang = Path.Combine(filesDir, "bad.sh");
        var crlf = Path.Combine(filesDir, "crlf.sh");
        var conf = Path.Combine(filesDir, "x.conf");
        File.WriteAllText(noShebang, "echo hi\n");
        File.WriteAllText(crlf, "#!/bin/sh\r\nexit 0\r\n");
        File.WriteAllText(conf, "x=1\n");
        try
        {
            var cases = new (string Name, DebBundleConfiguration Settings)[]
            {
                ("script without shebang", new DebBundleConfiguration { PostInstallFile = noShebang }),
                ("script with CRLF", new DebBundleConfiguration { PreUninstallFile = crlf }),
                ("conffile not in payload", new DebBundleConfiguration { ConfigFiles = ["/etc/absent.conf"] }),
                ("conffile relative path", new DebBundleConfiguration { ConfigFiles = ["etc/x.conf"] }),
                ("conffile traversal", new DebBundleConfiguration { ConfigFiles = ["/etc/../x.conf"] })
            };
            foreach (var (name, settings) in cases)
            {
                var rejected = Assert.ThrowsAny<Exception>(
                    () => new DebBundler(settings).BuildAsync(DebConfiguration(input, output))
                        .GetAwaiter().GetResult());
                Assert.True(rejected is ArgumentException or FileNotFoundException,
                    $"The '{name}' case must fail validation: {rejected.GetType().Name}: {rejected.Message}");
            }
        }
        finally
        {
            Cleanup(input, output, filesDir);
        }
    }

    static byte[] PngBytes(int width, int height)
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        png[11] = 0x0D; // IHDR length
        png[12] = (byte)'I'; png[13] = (byte)'H'; png[14] = (byte)'D'; png[15] = (byte)'R';
        png[16] = (byte)(width >> 24); png[17] = (byte)(width >> 16);
        png[18] = (byte)(width >> 8); png[19] = (byte)width;
        png[20] = (byte)(height >> 24); png[21] = (byte)(height >> 16);
        png[22] = (byte)(height >> 8); png[23] = (byte)height;
        return png;
    }

    [Fact]
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
                        new BundleResourceConfiguration { Source = extra, Destination = "docs/note.txt" }
                    ],
                    Targets = configuration.Targets
                }).GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            Assert.True(data.Any(e => e.Name == "./usr/lib/example-app/docs/note.txt" &&
                                 e.Mode == 420),
                "A resource must land under the install root at its TargetPath.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void WritesPackagingChangelog()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var deb = new DebBundler().BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(deb.Path);
            var control = Encoding.UTF8.GetString(
                DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[1].Content))
                    .Single(e => e.Name == "./control").Content);
            Assert.Contains(" Packaged with DotNet.Bundler.\n", control);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            var changelog = data.SingleOrDefault(
                e => e.Name == "./usr/share/doc/example-app/changelog.Debian.gz");
            Assert.NotNull(changelog);
            var text = Encoding.UTF8.GetString(DebPackageReader.Ungzip(changelog!.Content));
            Assert.True(text.StartsWith("example-app (1.0.0-1) unstable; urgency=low", StringComparison.Ordinal) &&
                   text.Contains(" -- Example Publisher  Tue, 01 Jan 1980", StringComparison.Ordinal),
                $"changelog.Debian.gz lacks the expected stanza:\n{text}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
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
            var deb = new DebBundler().BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var sidecar = deb.Path + ".sha256";
            Assert.True(File.Exists(sidecar), "The .sha256 sidecar is missing.");
            var expected = DebPackageWriter.Sha256Hex(File.ReadAllBytes(deb.Path));
            var line = File.ReadAllText(sidecar).Trim();
            Assert.Equal(expected + "  " + Path.GetFileName(deb.Path), line);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void MapsDebSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(targets.Contains("DebPackageName=\"$(BundlerDebPackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("DebVersion=\"$(BundlerDebVersion)\"", StringComparison.Ordinal) &&
               targets.Contains("DebRelease=\"$(BundlerDebRelease)\"", StringComparison.Ordinal) &&
               targets.Contains("DebEpoch=\"$(BundlerDebEpoch)\"", StringComparison.Ordinal) &&
               targets.Contains("DebArchitecture=\"$(BundlerDebArchitecture)\"", StringComparison.Ordinal) &&
               targets.Contains("DebVendor=\"$(BundlerDebVendor)\"", StringComparison.Ordinal) &&
               targets.Contains("DebInstallRoot=\"$(BundlerDebInstallRoot)\"", StringComparison.Ordinal) &&
               targets.Contains("DebBinLink=\"$(BundlerDebBinLink)\"", StringComparison.Ordinal) &&
               targets.Contains("DebDepends=\"$(BundlerDebDepends)\"", StringComparison.Ordinal) &&
               targets.Contains("DebRecommends=\"$(BundlerDebRecommends)\"", StringComparison.Ordinal) &&
               targets.Contains("DebProvides=\"$(BundlerDebProvides)\"", StringComparison.Ordinal) &&
               targets.Contains("DebConflicts=\"$(BundlerDebConflicts)\"", StringComparison.Ordinal) &&
               targets.Contains("DebReplaces=\"$(BundlerDebReplaces)\"", StringComparison.Ordinal) &&
               targets.Contains("DebSection=\"$(BundlerDebSection)\"", StringComparison.Ordinal) &&
               targets.Contains("DebPriority=\"$(BundlerDebPriority)\"", StringComparison.Ordinal) &&
               targets.Contains("DebCategories=\"$(BundlerDebCategories)\"", StringComparison.Ordinal) &&
               targets.Contains("DebDesktopFile=\"$(BundlerDebDesktopFile)\"", StringComparison.Ordinal) &&
               targets.Contains("DebMetainfoFile=\"$(BundlerDebMetainfoFile)\"", StringComparison.Ordinal) &&
               targets.Contains("DebChangelogFile=\"$(BundlerDebChangelogFile)\"", StringComparison.Ordinal) &&
               targets.Contains("DebPreInstallFile=\"$(BundlerDebPreInstallFile)\"", StringComparison.Ordinal) &&
               targets.Contains("DebPostInstallFile=\"$(BundlerDebPostInstallFile)\"", StringComparison.Ordinal) &&
               targets.Contains("DebPreUninstallFile=\"$(BundlerDebPreUninstallFile)\"", StringComparison.Ordinal) &&
               targets.Contains("DebPostUninstallFile=\"$(BundlerDebPostUninstallFile)\"", StringComparison.Ordinal) &&
               targets.Contains("DebSystemdServiceFile=\"$(BundlerDebSystemdServiceFile)\"", StringComparison.Ordinal) &&
               targets.Contains("DebConfigFiles=\"$(BundlerDebConfigFiles)\"", StringComparison.Ordinal) &&
               targets.Contains("DebFiles=\"@(BundlerDebFile)\"", StringComparison.Ordinal) &&
               task.Contains("DebFiles.Select(item => new DebFileEntry", StringComparison.Ordinal),
            "MSBuild does not map the BundlerDeb* properties to the task.");
        Assert.True(props.Contains("<BundlerDebPackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerDebInstallRoot", StringComparison.Ordinal) &&
               props.Contains("<BundlerDebDepends", StringComparison.Ordinal) &&
               props.Contains("<BundlerDebCategories", StringComparison.Ordinal) &&
               props.Contains("<BundlerDebChangelogFile", StringComparison.Ordinal) &&
               props.Contains("<BundlerDebPostInstallFile", StringComparison.Ordinal) &&
               props.Contains("<BundlerDebSystemdServiceFile", StringComparison.Ordinal) &&
               props.Contains("<BundlerDebConfigFiles", StringComparison.Ordinal),
            "The BundlerDeb* properties lack defaults in the .props file.");
        Assert.True(task.Contains("new DebBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Deb", StringComparison.Ordinal),
            "The MSBuild task does not construct the .deb backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert.Contains("DotNet.Bundler.Deb.dll", msbuildProject);
    }

    // A unix socket (or any other non-regular file) inside the input must be
    // skipped rather than packaged.
    [Fact]
    static void SkipsNonRegularPayloadFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "requires a non-Windows host");
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var bindPath = Path.Combine(Path.GetTempPath(), "bt" + Guid.NewGuid().ToString("N")[..8]);
            using (var socket = new System.Net.Sockets.Socket(
                       System.Net.Sockets.AddressFamily.Unix,
                       System.Net.Sockets.SocketType.Stream,
                       System.Net.Sockets.ProtocolType.Unspecified))
            {
                socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(bindPath));
                // .NET unlinks the bound path on dispose; move the node while bound
                File.Move(bindPath, Path.Combine(input, "agent.sock"));
            }
            var artifact = new DebBundler().BuildAsync(DebConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var members = DebPackageReader.ReadAr(artifact.Path);
            var data = DebPackageReader.ReadTar(DebPackageReader.Ungzip(members[2].Content));
            var names = data.Select(e => e.Name).ToArray();
            Assert.Contains("./usr/lib/example-app/ExampleApp", names);
            Assert.False(names.Any(name => name.EndsWith("agent.sock", StringComparison.Ordinal)), $"a unix socket must not be packaged: {string.Join(',', names)}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void RejectsUnderivablePackageName()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => DebName.Sanitize("中文应用"));
        Assert.Contains("Cannot derive a Debian package name", ex.Message);
    }

    [Fact]
    static void RejectsNonDigitEpoch()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => DebVersion.Map("1.0.0", new DebBundleConfiguration { Epoch = "abc" }));
        Assert.Contains("not a valid Debian epoch", ex.Message);
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


}
