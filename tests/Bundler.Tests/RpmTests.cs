using DotNet.Bundler;
using DotNet.Bundler.Rpm;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Bcpg.Sig;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;
using System.Text;

public static class RpmTests
{

    [Fact]
    static void SignsPackage()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var keyFile = Path.Combine(input, "test-signing-key.asc");
        var publicKey = GenerateTestKey(keyFile, "test-passphrase");
        var unsignedOutput = output + "-unsigned";
        try
        {
            var signed = new RpmBundler(new RpmBundleConfiguration
            {
                Signing = new KeyFileSigningConfiguration { KeyFile = keyFile, Passphrase = "test-passphrase" }
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();

            var package = RpmPackageReader.Read(signed.Path);
            Assert.True(package.Signature.Tags[1002] is byte[],
                "RPMSIGTAG_PGP (1002) must carry the OpenPGP signature packet.");
            Assert.True(package.Signature.Tags[268] is byte[],
                "RPMSIGTAG_RSA (268) must carry the header-only signature packet.");
            var sigPacket = (byte[])package.Signature.Tags[1002];
            var rsaPacket = (byte[])package.Signature.Tags[268];

            var rpmBytes = File.ReadAllBytes(signed.Path);
            var headerStart = SignedDataOffset(rpmBytes);
            var headerLength = HeaderLength(rpmBytes, headerStart);

            // rpm signs main header + payload: bytes from the main header to EOF.
            var signature = ((PgpSignatureList)new PgpObjectFactory(sigPacket).NextPgpObject())[0];
            signature.InitVerify(publicKey);
            signature.Update(rpmBytes, headerStart, rpmBytes.Length - headerStart);
            Assert.True(signature.Verify(),
                "The embedded signature must verify over header+payload bytes.");

            // RPMSIGTAG_RSA signs the main header alone — what zypper checks.
            var rsaSignature = ((PgpSignatureList)new PgpObjectFactory(rsaPacket).NextPgpObject())[0];
            rsaSignature.InitVerify(publicKey);
            rsaSignature.Update(rpmBytes, headerStart, headerLength);
            Assert.True(rsaSignature.Verify(),
                "The RPMSIGTAG_RSA signature must verify over the main header bytes.");

            var unsigned = new RpmBundler(new RpmBundleConfiguration())
                .BuildAsync(RpmConfiguration(input, unsignedOutput)).GetAwaiter().GetResult().Single();
            Assert.False(RpmPackageReader.Read(unsigned.Path).Signature.Tags.ContainsKey(1002), "No RPMSIGTAG_PGP when signing is not configured.");
            Assert.False(RpmPackageReader.Read(unsigned.Path).Signature.Tags.ContainsKey(268), "No RPMSIGTAG_RSA when signing is not configured.");
        }
        finally
        {
            Cleanup(input, output, unsignedOutput);
        }
    }

    [Fact]
    static void RejectsIncompleteSigning()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (caseName, settings) in new (string, RpmBundleConfiguration)[]
            {
                ("passphrase without key file",
                    new RpmBundleConfiguration { Signing = new KeyFileSigningConfiguration { Passphrase = "x"  }}),
                ("missing key file",
                    new RpmBundleConfiguration { Signing = new KeyFileSigningConfiguration { KeyFile = Path.Combine(input, "missing.asc") } }),
            })
            {
                Assert.ThrowsAny<ArgumentException>(
                    () => new RpmBundler(settings).BuildAsync(RpmConfiguration(input, output))
                        .GetAwaiter().GetResult());
            }
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void RejectsWrongPassphrase()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        var keyFile = Path.Combine(input, "test-signing-key.asc");
        GenerateTestKey(keyFile, "right-passphrase");
        try
        {
            var wrongPassphrase = Assert.ThrowsAny<InvalidOperationException>(
                () => new RpmBundler(new RpmBundleConfiguration
                {
                    Signing = new KeyFileSigningConfiguration { KeyFile = keyFile, Passphrase = "wrong-passphrase" }
                }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult());
            Assert.Contains("passphrase", wrongPassphrase.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    private static int SignedDataOffset(byte[] rpm)
    {
        int At(int i) => (rpm[i] << 24) | (rpm[i + 1] << 16) | (rpm[i + 2] << 8) | rpm[i + 3];
        var indexCount = At(96 + 8);
        var storeSize = At(96 + 12);
        var signatureEnd = 96 + 16 + indexCount * 16 + storeSize;
        return (signatureEnd + 7) & ~7;
    }

    private static int HeaderLength(byte[] rpm, int offset)
    {
        int At(int i) => (rpm[i] << 24) | (rpm[i + 1] << 16) | (rpm[i + 2] << 8) | rpm[i + 3];
        return 16 + At(offset + 8) * 16 + At(offset + 12);
    }

    private static PgpPublicKey GenerateTestKey(string keyFile, string passphrase)
    {
        var keyPairs = new RsaKeyPairGenerator();
        keyPairs.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral, keyPairs.GenerateKeyPair(), DateTime.UtcNow);
        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetKeyFlags(false, KeyFlags.SignData | KeyFlags.CertifyOther);
        var ringGenerator = new PgpKeyRingGenerator(
            PgpSignature.DefaultCertification, pair, "Bundler Test <test@example.com>",
            SymmetricKeyAlgorithmTag.Aes256, false, passphrase.ToCharArray(), true,
            hashed.Generate(), new PgpSignatureSubpacketGenerator().Generate(),
            new SecureRandom());
        var secretRing = ringGenerator.GenerateSecretKeyRing();
        var publicKey = secretRing.GetSecretKey().PublicKey;
        using (var file = File.Create(keyFile))
        using (var armored = new ArmoredOutputStream(file))
        {
            secretRing.Encode(armored);
        }
        return publicKey;
    }

    [Fact]
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
            Assert.Equal("echo hello\n", package.Main.Text(1023));
            Assert.Equal("/bin/bash", package.Main.Text(1085));
            // No shebang and no program -> /bin/sh default.
            Assert.Equal("echo post\n", package.Main.Text(1024));
            Assert.Equal("/usr/bin/python3", package.Main.Text(1086));
            // Unset scriptlets stay absent.
            Assert.True(!package.Main.Tags.ContainsKey(1025) && !package.Main.Tags.ContainsKey(1087),
                "No PREUN/PREUNPROG when unset.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.Contains(package.Payload, e => e.Path == "/usr/lib/systemd/system/example-app.service");
            var post = package.Main.Text(1024);
            Assert.True(post.Contains("echo custom") && post.Contains("daemon-reload"),
                "POSTIN must merge the caller body with the daemon-reload epilogue.");
            Assert.Equal("/bin/sh", package.Main.Text(1086));
            var postun = package.Main.Text(1026);
            Assert.True(postun.Contains("daemon-reload") && package.Main.Text(1088) == "/bin/sh",
                "POSTUN must be synthesized for daemon-reload too.");
            Assert.True(!package.Main.Tags.ContainsKey(1023) && !package.Main.Tags.ContainsKey(1025),
                "PREIN/PREUN stay absent with only a unit knob set.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
                ConfigLocations = ["/opt/example/extra.conf"]
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
            Assert.True((etc & 1) != 0 && (etc & 16) != 0,
                "/etc files must be %config(noreplace) (flags 1|16).");
            var opt = Flag("/opt/example/extra.conf");
            Assert.True((opt & 1) != 0 && (opt & 16) != 0,
                "Explicit ConfigLocations entries must be %config(noreplace) too.");
            var bin = Flag("/usr/lib/example-app/ExampleApp");
            Assert.Equal(0, bin & 17);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
                (new RpmBundleConfiguration { ConfigLocations = ["/usr/lib/example-app/nowhere.conf"] },
                    "ConfigLocations must reference a real payload file")
            })
            {
                var rejected = Assert.ThrowsAny<Exception>(
                    () => new RpmBundler(cfg).BuildAsync(RpmConfiguration(input, output))
                        .GetAwaiter().GetResult());
                Assert.True(rejected is ArgumentException or FileNotFoundException,
                    $"{label}: {rejected.GetType().Name}: {rejected.Message}");
            }
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void RejectsNonRpmFormats()
    {
        var input = CreateInputDirectory();
        try
        {
            var wrongTarget = Assert.ThrowsAny<NotSupportedException>(
                () => new RpmBundler()
                    .BuildAsync(RpmConfiguration(input, formats: [PackageFormat.Deb]))
                    .GetAwaiter().GetResult());
            Assert.Contains("Rpm targets only", wrongTarget.Message);
        }
        finally
        {
            Cleanup(input);
        }
    }

    [Fact]
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
            Assert.EndsWith(expectedName, artifact.Path);

            var package = RpmPackageReader.Read(artifact.Path);
            Assert.Equal("example-app", package.Main.Text(1000));
            Assert.Equal("1.0.0", package.Main.Text(1001));
            Assert.Equal("1", package.Main.Text(1002));
            Assert.Equal("linux", package.Main.Text(1021));
            Assert.Equal("x86_64", package.Main.Text(1022));
            Assert.Equal("cpio", package.Main.Text(1124));
            Assert.Equal("gzip", package.Main.Text(1125));
            Assert.Equal(8, package.Main.Ints(5011).Single());
            Assert.True(package.Signature.Tag(1000) > 0, "RPMSIGTAG_SIZE must be present.");
            Assert.True(package.Signature.Tag(1007) > 0, "RPMSIGTAG_PAYLOADSIZE must be present.");
            Assert.Equal(64, package.Signature.Text(273).Length);
            Assert.Equal(40, package.Signature.Text(269).Length);

            // rpmlib self-dependencies.
            var requires = package.Main.Strings(1049);
            Assert.Contains("rpmlib(CompressedFileNames)", requires);

            // Self provides.
            var provides = package.Main.Strings(1047);
            Assert.True(provides.Contains("example-app") && provides.Contains("example-app(x86_64)"),
                "The package must provide its own name and arch-qualified name.");

            // Payload contents.
            var paths = package.Payload.Select(e => e.Path).ToArray();
            Assert.Contains("/usr/lib/example-app/ExampleApp", paths);
            Assert.Contains("/usr/bin/example-app", paths);
            var link = package.Payload.Single(e => e.Path == "/usr/bin/example-app");
            Assert.Equal(0xA000, (link.Mode & 0xF000));
            Assert.Equal("../lib/example-app/ExampleApp", Encoding.UTF8.GetString(link.Data));
            var exe = package.Payload.Single(e => e.Path == "/usr/lib/example-app/ExampleApp");
            Assert.Equal(493 /* 0755 */, (exe.Mode & 511));
            Assert.Equal("fake executable", Encoding.UTF8.GetString(exe.Data));

            // File list consistency with cpio entries.
            var basenames = package.Main.Strings(1117);
            var dirnames = package.Main.Strings(1118);
            var dirIndexes = package.Main.Ints(1116);
            var fileModes = package.Main.Ints(1030);
            Assert.Equal(package.Payload.Count, basenames.Length);
            var expectedPaths = basenames.Select((b, i) => dirnames[dirIndexes[i]] + b).ToArray();
            Assert.Equal(paths, expectedPaths);
            var digests = package.Main.Strings(1035);
            Assert.True(digests.SequenceEqual(package.Payload.Select(e =>
                (e.Mode & 0xF000) == 0x4000 ? "" : Sha256(e.Data))),
                "FILEDIGESTS must hold sha256 of every non-directory entry.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
                Assert.True(package.Main.Text(1001) == version && package.Main.Text(1002) == release,
                    $"SemVer {semVer} must map to {version}-{release} (got {package.Main.Text(1001)}-{package.Main.Text(1002)}).");
            }
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
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                PackageName = "custom-name",
                Version = "9.9",
                Release = "7.el9",
                Epoch = "2",
                Architecture = "noarch",
                InstallRoot = "/opt/custom"
            }).BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            Assert.EndsWith("custom-name-9.9-7.el9.noarch.rpm", artifact.Path);
            var package = RpmPackageReader.Read(artifact.Path);
            Assert.Equal("custom-name", package.Main.Text(1000));
            Assert.True(package.Main.Text(1001) == "9.9" && package.Main.Text(1002) == "7.el9",
                "Version/Release override.");
            Assert.Equal(2, package.Main.Ints(1003).Single());
            Assert.Equal("noarch", package.Main.Text(1022));
            Assert.Contains(package.Payload, e => e.Path == "/opt/custom/ExampleApp");
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
            var artifact = new RpmBundler(new RpmBundleConfiguration { BinLink = "none" })
                .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            Assert.True(package.Payload.All(e => (e.Mode & 0xF000) != 0xA000),
                "BinLink='none' must emit no symlink.");
            Assert.True(package.Payload.All(e => !e.Path.StartsWith("/usr/bin", StringComparison.Ordinal)),
                "BinLink='none' must emit nothing under /usr/bin.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    // rpm FILESIZES 与 cpio newc 都是 int32 尺寸字段：超 2GiB 截断产坏包，显式拒绝。
    [Fact]
    static void RejectsPayloadFileOverTwoGiB()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(Path.Combine(input, "big.bin"), FileMode.Create))
            {
                stream.SetLength((long)int.MaxValue + 1);
            }
            Assert.ThrowsAny<ArgumentException>(
                () => new RpmBundler()
                    .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult());
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
                Assert.ThrowsAny<Exception>(
                    () => new RpmBundler(settings)
                        .BuildAsync(RpmConfiguration(input, output, version: version))
                        .GetAwaiter().GetResult());
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
            var artifact = new RpmBundler()
                .BuildAsync(RpmConfiguration(input, output, productName: "My Cool_App!"))
                .GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            Assert.Equal("my-cool-app", package.Main.Text(1000));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
                    Destination = "docs/note.txt"
                }]
            };
            var artifact = new RpmBundler().BuildAsync(configuration).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var resource = package.Payload.SingleOrDefault(
                e => e.Path == "/usr/lib/example-app/docs/note.txt");
            Assert.NotNull(resource);
            Assert.Equal("hello rpm", Encoding.UTF8.GetString(resource!.Data));
            Assert.True(package.Payload.Any(e => e.Path == "/usr/lib/example-app/docs" &&
                (e.Mode & 0xF000) == 0x4000), "Resource parent dirs must be owned entries.");
        }
        finally
        {
            Cleanup(input, output, resourcesDir);
        }
    }

    [Fact]
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
            Assert.Contains("/usr/lib/example-app", dirs);
            Assert.Contains("/usr/lib/example-app/sub", dirs);
            Assert.True(!dirs.Contains("/usr") && !dirs.Contains("/usr/lib") && !dirs.Contains("/usr/bin"),
                "System dirs above the install root must not be owned.");
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
        var second = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new RpmBundler().BuildAsync(RpmConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var next = new RpmBundler().BuildAsync(RpmConfiguration(input, second))
                .GetAwaiter().GetResult().Single();
            Assert.Equal(File.ReadAllBytes(first.Path), File.ReadAllBytes(next.Path));
        }
        finally
        {
            Cleanup(input, output, second);
        }
    }

    [Fact]
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
            Assert.True(parts.Length == 2 && parts[0].Length == 64 &&
                   parts[1] == Path.GetFileName(artifact.Path),
                $"The sha256 sidecar must be '<digest>  <filename>', got '{sidecar}'.");
            Assert.Equal(Sha256(File.ReadAllBytes(artifact.Path)), parts[0]);
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void MapsRpmSettingsThroughMsBuild()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.targets"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "buildTransitive", "DotNet.Bundler.MSBuild.props"));
        var task = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "BundleDesktopApplication.cs"));
        Assert.True(targets.Contains("RpmPackageName=\"$(BundlerRpmPackageName)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmVersion=\"$(BundlerRpmVersion)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmRelease=\"$(BundlerRpmRelease)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmEpoch=\"$(BundlerRpmEpoch)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmArchitecture=\"$(BundlerRpmArchitecture)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmVendor=\"$(BundlerRpmVendor)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmInstallRoot=\"$(BundlerRpmInstallRoot)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmBinLink=\"$(BundlerRpmBinLink)\"", StringComparison.Ordinal) &&
               targets.Contains("RpmDepends=\"$(BundlerRpmDepends)\"", StringComparison.Ordinal) &&
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
        Assert.True(props.Contains("<BundlerRpmPackageName", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmVersion", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmRelease", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmEpoch", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmArchitecture", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmVendor", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmInstallRoot", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmBinLink", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmDepends", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmLicense", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmCategories", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmDesktopFile", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmMetainfoFile", StringComparison.Ordinal) &&
               props.Contains("<BundlerRpmChangelogFile", StringComparison.Ordinal),
            "The BundlerRpm* properties lack defaults in the .props file.");
        Assert.True(task.Contains("new RpmBundler(", StringComparison.Ordinal) &&
               task.Contains("PackageFormat.Rpm", StringComparison.Ordinal),
            "The MSBuild task does not construct the .rpm backend.");
        var msbuildProject = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bundler.MSBuild", "Bundler.MSBuild.csproj"));
        Assert.Contains("DotNet.Bundler.Rpm.dll", msbuildProject);
        // The multi-format fanout is the RPM-1 contract.
        Assert.True(task.Contains("foreach (var format in formats.Distinct())", StringComparison.Ordinal) &&
               task.Contains("artifacts.AddRange(produced)", StringComparison.Ordinal),
            "The MSBuild dispatch must fan out per format.");
    }

    [Fact]
    static void MapsRelationTags()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new RpmBundler(new RpmBundleConfiguration
            {
                Depends = ["libc.so.6", "libfoo >= 1.2-3"],
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
            Assert.True(at >= 3, "Caller Requires must be appended after the rpmlib clauses.");
            Assert.True(requireFlags[at] == (4 | 8) && requireVersions[at] == "1.2-3",
                "'libfoo >= 1.2-3' must parse to GREATER|EQUAL + '1.2-3'.");
            at = Array.IndexOf(requireNames, "libc.so.6");
            Assert.True(requireFlags[at] == 0 && requireVersions[at] == "",
                "A bare Requires name must carry no flags or version.");

            var provideNames = package.Main.Strings(1047);
            var provideFlags = package.Main.Ints(1112);
            var provideVersions = package.Main.Strings(1113);
            at = Array.IndexOf(provideNames, "example-plugin");
            Assert.True(at == 2 && provideFlags[at] == 8 && provideVersions[at] == "2.0",
                "Caller Provides must be appended after the self-provides.");

            Assert.True(package.Main.Strings(1054).SequenceEqual(new[] { "old-example" }) &&
                   package.Main.Ints(1053).SequenceEqual(new[] { 2 }) &&
                   package.Main.Strings(1055).SequenceEqual(new[] { "1.0" }),
                "Conflicts must land in CONFLICTNAME/FLAGS/VERSION (1054/1053/1055).");
            Assert.True(package.Main.Strings(1090).SequenceEqual(new[] { "example-legacy" }) &&
                   package.Main.Ints(1114).SequenceEqual(new[] { 0 }),
                "Obsoletes must land in OBSOLETENAME/FLAGS (1090/1114).");
            Assert.True(package.Main.Strings(5046).SequenceEqual(new[] { "example-extra" }) &&
                   package.Main.Ints(5048).SequenceEqual(new[] { 4 | 8 }) &&
                   package.Main.Strings(5047).SequenceEqual(new[] { "0.5" }),
                "Recommends must land in RECOMMENDNAME/VERSION/FLAGS (5046/5047/5048).");
            Assert.Equal(new[] { "example-docs" }, package.Main.Strings(5049));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.Equal("MIT OR Apache-2.0", package.Main.Text(1014));
            Assert.Equal("Applications/Engineering", package.Main.Text(1016));
            Assert.Equal("https://example.com/rpm-override", package.Main.Text(1020));

            // Defaults: License 'Unspecified', Group 'Unspecified', URL = Homepage.
            var defaults = RpmPackageReader.Read(new RpmBundler()
                .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single().Path);
            Assert.Equal("Unspecified", defaults.Main.Text(1014));
            Assert.Equal("Unspecified", defaults.Main.Text(1016));
            Assert.Equal("https://example.com/app", defaults.Main.Text(1020));

            // Explicit empty Url/Group omit the tags entirely.
            var omitted = RpmPackageReader.Read(new RpmBundler(
                    new RpmBundleConfiguration { Url = "", Group = "" })
                .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult().Single().Path);
            Assert.Empty(omitted.Main.Strings(1020));
            Assert.Empty(omitted.Main.Strings(1016));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
                Categories = ["Utility", "Development"],
                MetainfoFile = metainfo,
                ChangelogFile = changelog
            }).BuildAsync(config).GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var paths = package.Payload.Select(e => e.Path).ToArray();

            var desktop = package.Payload.Single(e => e.Path == "/usr/share/applications/example-app.desktop");
            var content = Encoding.UTF8.GetString(desktop.Data);
            Assert.True(content.Contains("Type=Application") &&
                   content.Contains("Exec=example-app %u") &&
                   content.Contains("Icon=example-app") &&
                   content.Contains("Categories=Utility;Development;") &&
                   content.Contains("MimeType=x-scheme-handler/example;"),
                "The generated .desktop must carry exec/icon/categories/protocol mime entries.");
            Assert.Contains("/usr/share/icons/hicolor/48x48/apps/example-app.png", paths);
            Assert.Contains("/usr/share/metainfo/example-app.metainfo.xml", paths);
            Assert.Contains("/usr/share/doc/example-app/changelog.gz", paths);
            Assert.Contains("/usr/share/licenses/example-app/LICENSE.txt", paths);

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
                    Assert.Equal(128, flags[i]);
                }
                else if (path == "/usr/share/doc/example-app/changelog.gz")
                {
                    Assert.Equal(2, flags[i]);
                }
            }

            // Owned dirs: the license/doc package dirs, but never shared parents.
            var dirs = package.Payload.Where(e => (e.Mode & 0xF000) == 0x4000)
                .Select(e => e.Path).ToArray();
            Assert.True(dirs.Contains("/usr/share/licenses/example-app") &&
                   dirs.Contains("/usr/share/doc/example-app"),
                "Package-owned leaf dirs must be claimed.");
            Assert.True(!dirs.Contains("/usr/share") && !dirs.Contains("/usr/share/applications") &&
                   !dirs.Contains("/usr/share/icons") && !dirs.Contains("/usr/share/doc"),
                "Shared system dirs must not be owned.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.Contains("Name=Custom", Encoding.UTF8.GetString(desktop.Data));
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
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
            Assert.Equal("key=value", Encoding.UTF8.GetString(entry.Data));
            var dirs = package.Payload.Where(e => (e.Mode & 0xF000) == 0x4000)
                .Select(e => e.Path).ToArray();
            Assert.True(dirs.Contains("/etc/example") && !dirs.Contains("/etc"),
                "New non-shared parents are owned; /etc itself is not.");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    [Fact]
    static void RejectsInvalidDependencyClauses()
    {
        var input = CreateInputDirectory();
        var output = Path.Combine(Path.GetTempPath(), "DotNet.Bundler.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var clause in new[] { "foo != 1.0", "foo bar", "", "foo =" })
            {
                Assert.ThrowsAny<ArgumentException>(
                    () => new RpmBundler(new RpmBundleConfiguration { Depends = [clause] })
                        .BuildAsync(RpmConfiguration(input, output)).GetAwaiter().GetResult());
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
            var artifact = new RpmBundler().BuildAsync(RpmConfiguration(input, output))
                .GetAwaiter().GetResult().Single();
            var package = RpmPackageReader.Read(artifact.Path);
            var paths = package.Payload.Select(e => e.Path).ToArray();
            Assert.Contains("/usr/lib/example-app/ExampleApp", paths);
            Assert.False(paths.Any(path => path.EndsWith("agent.sock", StringComparison.Ordinal)), $"a unix socket must not be packaged: {string.Join(',', paths)}");
        }
        finally
        {
            Cleanup(input, output);
        }
    }

    static BundleConfiguration RpmConfiguration(
        string input,
        string output = "",
        string rid = "linux-x86_64",
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
                    Target = rid,
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


}
