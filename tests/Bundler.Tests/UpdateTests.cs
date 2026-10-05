using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Bundler;
using DotNet.Bundler.Core.Update;
using DotNet.Bundler.Update;

public static class UpdateTests
{
    [Fact]
    static void KeyMaterial_RoundTrips()
    {
        var directory = CreateTempDirectory();
        try
        {
            var material = UpdateKeyMaterial.Generate();
            var path = Path.Combine(directory, "key.json");
            material.Save(path);

            var loaded = UpdateKeyMaterial.Load(path);
            Assert.Equal(material.X, loaded.X);
            Assert.Equal(material.Y, loaded.Y);
            Assert.Equal(material.D, loaded.D);
            Assert.Equal(material.PublicPointBase64(), loaded.PublicPointBase64());

            // 公钥点 = 0x04 || X(32B) || Y(32B)，未压缩 P-256 点。
            var point = Convert.FromBase64String(loaded.PublicPointBase64());
            Assert.Equal(65, point.Length);
            Assert.Equal(4, point[0]);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Signer_ProducesCanonicalP1363()
    {
        var material = UpdateKeyMaterial.Generate();
        var payload = Encoding.UTF8.GetBytes("bundler update payload");

        var signature = EcdsaSigner.Sign(payload, material);
        Assert.Equal(64, signature.Length);
        Assert.True(EcdsaSigner.Verify(payload, signature, material));
    }

    [Fact]
    static void Signer_VerifiesDerAndP1363()
    {
        // OpenSsl 直接签出 DER、我们的 .sig 是规范 P1363——验签必须两种编码都认。
        var material = UpdateKeyMaterial.Generate();
        var payload = Encoding.UTF8.GetBytes("cross-format signature check");
        using var ecdsa = ECDsa.Create(material.ToPrivateParameters());
        var der = ecdsa.SignData(payload, HashAlgorithmName.SHA256);
        var p1363 = EcdsaSigner.Sign(payload, material);

        Assert.True(EcdsaSigner.Verify(payload, der, material));
        Assert.True(EcdsaSigner.Verify(payload, p1363, material));
    }

    [Fact]
    static void Signer_RejectsTamperedAndForeignKey()
    {
        var material = UpdateKeyMaterial.Generate();
        var payload = Encoding.UTF8.GetBytes("signed payload");
        var signature = EcdsaSigner.Sign(payload, material);

        var tampered = (byte[])signature.Clone();
        tampered[10] ^= 0xFF;
        Assert.False(EcdsaSigner.Verify(payload, tampered, material));
        Assert.False(EcdsaSigner.Verify(Encoding.UTF8.GetBytes("other payload"), signature,
            material));
        Assert.False(EcdsaSigner.Verify(payload, signature, UpdateKeyMaterial.Generate()));
        // 非 64 字节输入（非签名/非 DER 的随机字节）不抛异常、判定为不合法。
        Assert.False(EcdsaSigner.Verify(payload, [1, 2, 3], material));
    }

    [Fact]
    static void Emitter_WritesSigSidecarsAndFeed()
    {
        var directory = CreateTempDirectory();
        try
        {
            var keyPath = Path.Combine(directory, "key.json");
            var material = UpdateKeyMaterial.Generate();
            material.Save(keyPath);

            var zipArtifact = WriteDummyArtifact(directory, "app-1.0.0-linux-x64.zip");
            var debArtifact = WriteDummyArtifact(directory, "app_1.0.0_amd64.deb");
            var configuration = new BundleConfiguration
            {
                ProductName = "App",
                Identifier = "com.example.app",
                Version = "1.0.0",
                OutputDirectory = directory,
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://example.test/updates",
                    Channel = "stable",
                    SigningKeyFile = keyPath,
                    Notes = "first feed"
                }
            };
            var artifacts = new[]
            {
                new BundleArtifact(PackageFormat.Zip, "linux-x64", zipArtifact),
                // deb 不在自更新适配清单里——不产生 .sig、不进 feed。
                new BundleArtifact(PackageFormat.Deb, "linux-x64", debArtifact)
            };

            var produced = UpdateManifestEmitter.EmitAsync(configuration, artifacts)
                .GetAwaiter().GetResult();

            var sigPath = zipArtifact + ".sig";
            Assert.True(File.Exists(sigPath));
            Assert.Equal(64, new FileInfo(sigPath).Length);
            Assert.False(File.Exists(debArtifact + ".sig"));
            var feedPath = Path.Combine(directory, "bundler-update-feed.stable.json");
            Assert.Contains(feedPath, produced);
            Assert.Contains(sigPath, produced);

            using var document = JsonDocument.Parse(File.ReadAllText(feedPath));
            var root = document.RootElement;
            Assert.Equal("1.0.0", root.GetProperty("version").GetString());
            Assert.Equal("stable", root.GetProperty("channel").GetString());
            Assert.Equal("first feed", root.GetProperty("notes").GetString());
            var entries = root.GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.Single(entries);
            var entry = entries[0];
            Assert.Equal("linux-x64", entry.GetProperty("rid").GetString());
            Assert.Equal("zip", entry.GetProperty("format").GetString());
            Assert.Equal("app-1.0.0-linux-x64.zip", entry.GetProperty("file").GetString());
            Assert.Equal("https://example.test/updates/app-1.0.0-linux-x64.zip",
                entry.GetProperty("url").GetString());
            Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(sigPath)),
                entry.GetProperty("sig").GetString());
            Assert.Equal(new FileInfo(zipArtifact).Length, entry.GetProperty("size").GetInt64());
            Assert.Equal(64, entry.GetProperty("sha256").GetString()!.Length);

            // 清单内嵌签名与 .sig 旁车对同一内容可验——交叉验证闭环。
            var signature = File.ReadAllBytes(sigPath);
            Assert.True(EcdsaSigner.VerifyFile(zipArtifact, signature, material));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Emitter_RequiresSigningKey()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new BundleConfiguration
            {
                ProductName = "App",
                Identifier = "com.example.app",
                Version = "1.0.0",
                OutputDirectory = directory,
                Update = new UpdateBundleConfiguration { FeedUrl = "https://example.test" }
            };
            var artifacts = new[]
            {
                new BundleArtifact(PackageFormat.Zip, "linux-x64",
                    WriteDummyArtifact(directory, "app.zip"))
            };
            Assert.Throws<InvalidOperationException>(() =>
                UpdateManifestEmitter.EmitAsync(configuration, artifacts).GetAwaiter().GetResult());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void ArchiveBackend_InjectsSidecarIntoZip()
    {
        // 集成断言：真实后端产 zip → 归档顶层目录内带 bundler-update.json。
        var directory = CreateTempDirectory();
        try
        {
            var input = Path.Combine(directory, "input");
            Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(input, "app.bin"), "payload");
            var output = Path.Combine(directory, "out");

            var artifacts = new DotNet.Bundler.Archive.ArchiveBundler(
                new DotNet.Bundler.Archive.ArchiveBundleConfiguration())
                .BuildAsync(new BundleConfiguration
                {
                    ProductName = "App",
                    Identifier = "com.example.app",
                    Version = "1.0.0",
                    OutputDirectory = output,
                    Update = new UpdateBundleConfiguration { PublicKey = "cHVibGljLWtleQ==" },
                    Targets = [new BundleTargetConfiguration
                    {
                        RuntimeIdentifier = "linux-x64",
                        InputDirectory = input,
                        MainExecutable = "app.bin",
                        Formats = [PackageFormat.Zip]
                    }]
                }).GetAwaiter().GetResult();

            var zip = artifacts.Single().Path;
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            var sidecar = archive.Entries.SingleOrDefault(
                e => e.FullName.EndsWith("/" + UpdateIdentitySidecar.FileName));
            Assert.NotNull(sidecar);
            using var reader = new StreamReader(sidecar!.Open());
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            Assert.Equal("zip", document.RootElement.GetProperty("format").GetString());
            Assert.Equal("linux-x64", document.RootElement.GetProperty("rid").GetString());
            Assert.Equal("cHVibGljLWtleQ==",
                document.RootElement.GetProperty("publicKey").GetString());
            Assert.False(File.Exists(Path.Combine(input, UpdateIdentitySidecar.FileName)));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Bootstrapper_ResolvesPerRidThenPosix()
    {
        var directory = CreateTempDirectory();
        try
        {
            var tools = Path.Combine(directory, "tools");
            Directory.CreateDirectory(Path.Combine(tools, "linux-x64"));
            Directory.CreateDirectory(Path.Combine(tools, "posix"));
            File.WriteAllText(Path.Combine(tools, "linux-x64", "bundler-updater"), "elf-binary");
            File.WriteAllText(Path.Combine(tools, "posix", "bundler-updater.sh"), "#!/bin/sh\n");

            var update = new UpdateBundleConfiguration { BootstrapperDirectory = tools };
            // per-RID 件命中优先。
            Assert.True(UpdateBootstrapper.TryResolve(update, "linux-x64", out var resolved));
            Assert.EndsWith(Path.Combine("linux-x64", "bundler-updater"), resolved);
            // 无 per-RID 件（osx-arm64 未构建）→ 降级 posix 脚本。
            Assert.True(UpdateBootstrapper.TryResolve(update, "osx-arm64", out resolved));
            Assert.EndsWith("bundler-updater.sh", resolved);
            // 工具目录缺位→不注入（安全降级，非错误）。
            var empty = new UpdateBundleConfiguration { BootstrapperDirectory = Path.Combine(tools, "none") };
            Assert.False(UpdateBootstrapper.TryResolve(empty, "linux-x64", out _));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_SwapsAndBacksUp()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            File.WriteAllText(Path.Combine(payload, "app"), "v2");

            var lines = new List<string>();
            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    KeepPayload = true
                }, lines.Add);

            Assert.Equal(0, rc);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
            Assert.Equal("v1", File.ReadAllText(
                Path.Combine(install + ".bundler-backup", "app")));
            // --keep-payload 下暂存目录保留（调试与组合器用法）。
            Assert.True(Directory.Exists(payload));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_RejectsMissingAndNestedDirs()
    {
        var directory = CreateTempDirectory();
        var install = Path.Combine(directory, "install");
        var payload = Path.Combine(directory, "payload");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        var lines = new List<string>();

        // 缺失目录 → 用法错误。
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = Path.Combine(directory, "missing"),
                    PayloadDirectory = payload
                }, lines.Add));

        // 备份目录嵌进安装目录 → 拒绝（会把自身也备份进去）。
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    BackupDirectory = Path.Combine(install, "backup")
                }, lines.Add));
    }

    [Fact]
    static void Sidecar_WritesIdentityJson()
    {
        var directory = CreateTempDirectory();
        try
        {
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(payload);
            UpdateIdentitySidecar.WriteIfEnabled(payload, new UpdateBundleConfiguration
            {
                FeedUrl = "https://example.test/feed.json",
                Channel = "beta",
                PublicKey = "cHVibGljLWtleQ=="
            }, PackageFormat.AppImage, "linux-x64");

            var sidecar = Path.Combine(payload, UpdateIdentitySidecar.FileName);
            Assert.True(File.Exists(sidecar));
            using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = document.RootElement;
            Assert.Equal("appimage", root.GetProperty("format").GetString());
            Assert.Equal("linux-x64", root.GetProperty("rid").GetString());
            Assert.Equal("beta", root.GetProperty("channel").GetString());
            Assert.Equal("https://example.test/feed.json", root.GetProperty("feedUrl").GetString());
            Assert.Equal("cHVibGljLWtleQ==", root.GetProperty("publicKey").GetString());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Sidecar_SkipsWhenUpdateDisabled()
    {
        var directory = CreateTempDirectory();
        try
        {
            UpdateIdentitySidecar.WriteIfEnabled(
                directory, null, PackageFormat.Zip, "linux-x64");
            Assert.False(File.Exists(Path.Combine(directory, UpdateIdentitySidecar.FileName)));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Staging_CopiesPayloadAndInjectsSidecar()
    {
        var directory = CreateTempDirectory();
        try
        {
            var input = Path.Combine(directory, "input");
            Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(input, "app.bin"), "payload-bytes");
            var work = Path.Combine(directory, "work");
            Directory.CreateDirectory(work);

            var item = new BundlePlanItem(
                BundleTarget.TryParse("linux-x64", out var target) ? target! : throw new InvalidOperationException(),
                PackageFormat.Zip, input, "app.bin", directory, false);
            var context = new BundleBuildContext(
                new BundleConfiguration
                {
                    ProductName = "App",
                    Identifier = "com.example.app",
                    Version = "1.0.0",
                    OutputDirectory = directory,
                    Update = new UpdateBundleConfiguration { PublicKey = "cHVibGljLWtleQ==" }
                },
                item, work, new SilentLogger());

            var staged = UpdatePayloadStaging.EnsureStaged(context, item);
            Assert.NotEqual(input, staged.InputDirectory);
            Assert.True(File.Exists(Path.Combine(
                staged.InputDirectory, UpdateIdentitySidecar.FileName)));
            Assert.True(File.Exists(Path.Combine(staged.InputDirectory, "app.bin")));
            // 用户发布目录必须不被触碰——旁车只出现在 staging 拷贝里。
            Assert.False(File.Exists(Path.Combine(input, UpdateIdentitySidecar.FileName)));

            // 已在工作目录内的 staging（如 signed-payload）直接就地写旁车、不再二次复制。
            var alreadyStaged = Path.Combine(work, "signed-payload");
            Directory.CreateDirectory(alreadyStaged);
            var second = UpdatePayloadStaging.EnsureStaged(
                context, item with { InputDirectory = alreadyStaged });
            Assert.Equal(alreadyStaged, second.InputDirectory);
            Assert.True(File.Exists(Path.Combine(alreadyStaged, UpdateIdentitySidecar.FileName)));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private sealed class SilentLogger : IBundleLogger
    {
        public void Log(BundleLogLevel level, string message)
        {
        }
    }

    static string WriteDummyArtifact(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(name + " contents"));
        return path;
    }

    static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bundler-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
