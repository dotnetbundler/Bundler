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

            var zipArtifact = WriteDummyArtifact(directory, "app-1.0.0-linux-x86_64.zip");
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
                new BundleArtifact(PackageFormat.Zip, "linux-x86_64", zipArtifact),
                // deb 不在自更新适配清单里——不产生 .sig、不进 feed。
                new BundleArtifact(PackageFormat.Deb, "linux-x86_64", debArtifact)
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
            Assert.Equal("linux-x86_64", entry.GetProperty("rid").GetString());
            Assert.Equal("zip", entry.GetProperty("format").GetString());
            Assert.Equal("app-1.0.0-linux-x86_64.zip", entry.GetProperty("file").GetString());
            // url 恒为裸文件名——消费端按清单文件所在目录解析。
            Assert.Equal("app-1.0.0-linux-x86_64.zip", entry.GetProperty("url").GetString());
            Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(sigPath)),
                entry.GetProperty("sig").GetString());
            Assert.Equal(new FileInfo(zipArtifact).Length, entry.GetProperty("size").GetInt64());
            Assert.Equal(64, entry.GetProperty("sha256").GetString()!.Length);

            // 清单内嵌签名与 .sig 旁车对同一内容可验——交叉验证闭环。
            var signature = File.ReadAllBytes(sigPath);
            Assert.True(EcdsaSigner.VerifyFile(zipArtifact, signature, material));
            // 清单本身也带 .sig 签名——客户端先验清单再信里面的版本号。
            var feedSig = feedPath + ".sig";
            Assert.Contains(feedSig, produced);
            Assert.True(EcdsaSigner.VerifyFile(feedPath, File.ReadAllBytes(feedSig), material));
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
                new BundleArtifact(PackageFormat.Zip, "linux-x86_64",
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
            var keyPath = Path.Combine(directory, "key.json");
            var material = UpdateKeyMaterial.Generate();
            material.Save(keyPath);

            var artifacts = new DotNet.Bundler.Archive.ArchiveBundler(
                new DotNet.Bundler.Archive.ArchiveBundleConfiguration())
                .BuildAsync(new BundleConfiguration
                {
                    ProductName = "App",
                    Identifier = "com.example.app",
                    Version = "1.0.0",
                    OutputDirectory = output,
                    Update = new UpdateBundleConfiguration
                    {
                        FeedUrl = "https://example.test/updates",
                        SigningKeyFile = keyPath,
                        PublicKey = material.PublicPointBase64(),
                    },
                    Targets = [new BundleTargetConfiguration
                    {
                        Target = "linux-x86_64",
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
            Assert.Equal("linux-x86_64", document.RootElement.GetProperty("rid").GetString());
            Assert.Equal(material.PublicPointBase64(),
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
            Directory.CreateDirectory(Path.Combine(tools, "linux-x86_64"));
            Directory.CreateDirectory(Path.Combine(tools, "posix"));
            File.WriteAllText(Path.Combine(tools, "linux-x86_64", "bundler-updater"), "elf-binary");
            File.WriteAllText(Path.Combine(tools, "posix", "bundler-updater.sh"), "#!/bin/sh\n");

            var update = new UpdateBundleConfiguration { BootstrapperDirectory = tools };
            // per-RID 件命中优先。
            Assert.True(UpdateBootstrapper.TryResolve(update, "linux-x86_64", out var resolved));
            Assert.EndsWith(Path.Combine("linux-x86_64", "bundler-updater"), resolved);
            // 无 per-RID 件（macos-arm64 未构建）→ 降级 posix 脚本。
            Assert.True(UpdateBootstrapper.TryResolve(update, "macos-arm64", out resolved));
            Assert.EndsWith("bundler-updater.sh", resolved);
            // 工具目录缺位→不注入（安全降级，非错误）。
            var empty = new UpdateBundleConfiguration { BootstrapperDirectory = Path.Combine(tools, "none") };
            Assert.False(UpdateBootstrapper.TryResolve(empty, "linux-x86_64", out _));
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
            // 默认不保留回滚点：换包成功后兄弟位瞬备被清掉。
            Assert.False(Directory.Exists(install + ".bundler-backup"));
            // --keep-payload 下暂存目录保留（调试与组合器用法）。
            Assert.True(Directory.Exists(payload));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_RecoversInterruptedSwap()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            // 模拟崩在换包中途：marker 留痕 + 安装目录是半成品 + 备份完整。
            File.WriteAllText(Path.Combine(install, "app"), "corrupt-partial");
            File.WriteAllText(Path.Combine(install + ".bundler-swap"), "swap in progress");
            Directory.CreateDirectory(install + ".bundler-backup");
            File.WriteAllText(Path.Combine(install + ".bundler-backup", "app"), "v1-good");
            File.WriteAllText(Path.Combine(payload, "app"), "v2");

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
            // marker 清干净；默认不保留回滚点——恢复+v2 就位后瞬备删除。
            Assert.False(File.Exists(install + ".bundler-swap"));
            Assert.False(Directory.Exists(install + ".bundler-backup"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_RetainBackupTo_RetainsAndRollbackRestores()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            var retain = Path.Combine(directory, "backups", "install-abc");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            File.WriteAllText(Path.Combine(payload, "app"), "v2");

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    RetainBackupDirectory = retain,
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
            // 兄弟位瞬备清走，备份迁到保留目录当回滚点。
            Assert.False(Directory.Exists(install + ".bundler-backup"));
            Assert.Equal("v1", File.ReadAllText(Path.Combine(retain, "app")));

            rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    BackupDirectory = retain,
                    Rollback = true,
                }, _ => { });
            Assert.Equal(0, rc);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(install, "app")));
            Assert.Equal("v1", File.ReadAllText(Path.Combine(retain, "app")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_KeepPayload_PreservesSymlinkAndExecBit()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux-only leg.");
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            var payloadExe = Path.Combine(payload, "hello");
            File.WriteAllText(payloadExe, "#!/bin/sh\necho hi\n");
            // CA1416 分析器不认 SkipWhen——守卫保留给编译期平台判断。
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(payloadExe, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            File.CreateSymbolicLink(Path.Combine(payload, "AppRun"), "hello");

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    KeepPayload = true,
                }, _ => { });

            Assert.Equal(0, rc);
            // 软链按链接重建而非解引用成普通文件；执行位随文件走。
            var appRun = new FileInfo(Path.Combine(install, "AppRun"));
            Assert.Equal("hello", appRun.LinkTarget);
            if (OperatingSystem.IsLinux())
            {
                Assert.True(
                    (File.GetUnixFileMode(Path.Combine(install, "hello")) &
                     UnixFileMode.UserExecute) != 0);
            }
            // keep-payload 保留源载荷。
            Assert.True(File.Exists(payloadExe));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_KeepPayload_PreservesDirectoryLinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 建链需权限，POSIX 腿已实测覆盖。");
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            var outside = Path.Combine(directory, "outside-dir");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            File.WriteAllText(Path.Combine(outside, "lib.so"), "lib");
            File.WriteAllText(Path.Combine(payload, "app"), "v2");
            // .app/Frameworks 类目录软链：cp -a 语义必须按链接重建——
            // 被当目录遍历实体化会复制出一份实体目录并丢失目录级属性。
            Directory.CreateSymbolicLink(Path.Combine(payload, "Frameworks"), outside);

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    KeepPayload = true,
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal(outside, new DirectoryInfo(Path.Combine(install, "Frameworks")).LinkTarget);
            Assert.Equal("lib", File.ReadAllText(Path.Combine(install, "Frameworks", "lib.so")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_Rollback_PreservesDirectoryLinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 建链需权限，POSIX 腿已实测覆盖。");
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            var outside = Path.Combine(directory, "outside-dir");
            var retain = Path.Combine(directory, "backups", "install-abc");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            File.WriteAllText(Path.Combine(outside, "lib.so"), "lib");
            File.WriteAllText(Path.Combine(payload, "app"), "v2");
            Directory.CreateSymbolicLink(Path.Combine(install, "Frameworks"), outside);

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    RetainBackupDirectory = retain,
                }, _ => { });
            Assert.Equal(0, rc);

            rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    BackupDirectory = retain,
                    Rollback = true,
                }, _ => { });
            Assert.Equal(0, rc);
            // 回滚经复制路径还原——目录软链必须还是链接而非实体目录。
            Assert.Equal(outside, new DirectoryInfo(Path.Combine(install, "Frameworks")).LinkTarget);
            Assert.Equal("lib", File.ReadAllText(Path.Combine(install, "Frameworks", "lib.so")));
            Assert.Equal("v1", File.ReadAllText(Path.Combine(install, "app")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_Rollback_RestoresBackupAndKeepsIt()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var backup = install + ".bundler-backup";
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(install, "app"), "v2-broken");
            File.WriteAllText(Path.Combine(backup, "app"), "v1-good");

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    Rollback = true,
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal("v1-good", File.ReadAllText(Path.Combine(install, "app")));
            // 备份保留可重试；无 .bundler-swap 残痕。
            Assert.Equal("v1-good", File.ReadAllText(Path.Combine(backup, "app")));
            Assert.False(File.Exists(install + ".bundler-swap"));

            // 无备份 → 确定性拒绝而非静默。
            Directory.Delete(backup, recursive: true);
            Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UpdateRejectedException>(() =>
                DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                    new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                    {
                        InstallDirectory = install,
                        Rollback = true,
                    }, _ => { }));
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

        // 保留目录与安装目录同址 → 拒绝（等值路径逃逸严格子路径检查，会抹掉新装）。
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    RetainBackupDirectory = install + Path.DirectorySeparatorChar
                }, lines.Add));

        // 载荷与安装同址 → 拒绝（备份移走后 swap 以空载荷覆盖再删源，静默抹掉全部）。
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = install + Path.DirectorySeparatorChar
                }, lines.Add));

        // 卷根装位没有同级位置放默认备份槽（拼出的 install.bundler-backup 病态）→
        // 明确拒绝并提示显式 --backup-dir。
        var rootError = Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = Path.GetPathRoot(directory)!,
                    PayloadDirectory = payload
                }, lines.Add));
        Assert.Contains("volume root", rootError.Message);

        // 载荷是安装的祖先目录 → 拒绝（MoveTree 退化复制后删源父级，连备份一起抹）。
        var outer = Path.Combine(directory, "outer");
        var nestedInstall = Path.Combine(outer, "install");
        Directory.CreateDirectory(nestedInstall);
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = nestedInstall,
                    PayloadDirectory = outer
                }, lines.Add));

        // 安装目录嵌进载荷 → 拒绝（反向同形）。
        var holder = Path.Combine(directory, "holder");
        var nestedPayload = Path.Combine(holder, "payload");
        Directory.CreateDirectory(nestedPayload);
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = holder,
                    PayloadDirectory = nestedPayload
                }, lines.Add));

        // 文件级换包：载荷与安装同件 → 拒绝。
        var installFile = Path.Combine(directory, "app.bin");
        File.WriteAllText(installFile, "x");
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = installFile,
                    PayloadDirectory = installFile
                }, lines.Add));

        // 载荷经符号链接指向安装件 → 按物理位置拒绝（拼写不同但同一文件）。
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 建链需权限，POSIX 腿已实测覆盖。");
        var linkPayload = Path.Combine(directory, "linked.bin");
        File.CreateSymbolicLink(linkPayload, installFile);
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = installFile,
                    PayloadDirectory = linkPayload
                }, lines.Add));
        // 链接被搬入原位会把安装文件变成自指链接——拒绝后源件必须完好。
        Assert.Equal("x", File.ReadAllText(installFile));
        Assert.True(File.Exists(linkPayload));

        // 载荷嵌进保留目录 → 拒绝（retain 迁移 rm -rf 会清掉载荷父级全部内容）。
        var retainHolder = Path.Combine(directory, "retain-holder");
        var payloadInRetain = Path.Combine(retainHolder, "payload");
        Directory.CreateDirectory(payloadInRetain);
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payloadInRetain,
                    RetainBackupDirectory = retainHolder
                }, lines.Add));
        Assert.True(Directory.Exists(payloadInRetain));

        // 环链载荷 → 拒绝：Exists 在 lstat 口径下对环链也报存在，
        // 链接解析必须显式判环，否则换包后装位成指向自身的断链且备份被删无法恢复。
        var cyclicPayload = Path.Combine(directory, "cyclic-payload");
        File.CreateSymbolicLink(cyclicPayload, cyclicPayload);
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = cyclicPayload
                }, lines.Add));

        // 互指环（a↔b）同形。
        var cycleA = Path.Combine(directory, "cycle-a");
        var cycleB = Path.Combine(directory, "cycle-b");
        File.CreateSymbolicLink(cycleA, cycleB);
        File.CreateSymbolicLink(cycleB, cycleA);
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = cycleA
                }, lines.Add));

        // 文件级换包同样判环——install 为自环时不可被 File.Exists 恒真放行。
        var cyclicInstallFile = Path.Combine(directory, "cyclic.bin");
        File.CreateSymbolicLink(cyclicInstallFile, cyclicInstallFile);
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = cyclicInstallFile,
                    PayloadDirectory = installFile
                }, lines.Add));

        // 后代自指（a→a/child）：规范化必须快速判死——重入自身会栈溢出而非拒绝。
        var descendantLink = Path.Combine(directory, "desc-link");
        File.CreateSymbolicLink(descendantLink, Path.Combine(descendantLink, "child"));
        Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
            DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = descendantLink
                }, lines.Add));
    }

    [Fact]
    static void BootstrapPlan_RecoveryDoesNotDeleteNestedPayload()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var nestedPayload = Path.Combine(install, "payload");
            Directory.CreateDirectory(nestedPayload);
            File.WriteAllText(Path.Combine(nestedPayload, "app"), "v2");
            // 上轮崩线残痕：marker+半成品安装+完整备份——恢复会删安装目录。
            File.WriteAllText(install + ".bundler-swap", "swap in progress");
            Directory.CreateDirectory(install + ".bundler-backup");
            File.WriteAllText(Path.Combine(install + ".bundler-backup", "app"), "v1");

            // 互嵌拒绝必须先于崩溃恢复——恢复删安装目录会把本轮载荷一并抹掉。
            Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UsageException>(() =>
                DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                    new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                    {
                        InstallDirectory = install,
                        PayloadDirectory = nestedPayload
                    }, _ => { }));
            Assert.True(File.Exists(Path.Combine(nestedPayload, "app")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_BackupSlotLeafLinkToOutside_IsBenignlyReplaced()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 建链需权限，POSIX 腿已实测覆盖。");
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            File.WriteAllText(Path.Combine(payload, "app"), "v2");
            // 备份槽预置叶链指向保护区外的普通文件：mv/rm 语义下应只删链节点换包照常，
            // 绝不触目标（POSIX `rm -rf "$BACKUP_DIR"` 同义）。缺陷态下 C# 撞
            // "already exists" rc=4 且 marker 残留楔形。
            var outsideFile = Path.Combine(directory, "outside.txt");
            File.WriteAllText(outsideFile, "protected");
            File.CreateSymbolicLink(install + ".bundler-backup", outsideFile);

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
            Assert.Equal("protected", File.ReadAllText(outsideFile));
            Assert.False(File.Exists(install + ".bundler-swap"));
            // 后续 apply 不再进崩溃恢复——无楔形。
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "app"), "v3");
            rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload
                }, _ => { });
            Assert.Equal(0, rc);
            Assert.Equal("v3", File.ReadAllText(Path.Combine(install, "app")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_MarkerSlotLeafLink_DoesNotFakeRecoveryOrWriteThrough()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 建链需权限，POSIX 腿已实测覆盖。");
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            File.WriteAllText(Path.Combine(payload, "app"), "v2");
            // marker 槽预置叶链：Exists 顺链会假触发崩溃恢复（备份缺席→rc=4 楔形），
            // 写 marker 顺链会写穿污染保护区外文件——链节点必须先删。
            var outsideFile = Path.Combine(directory, "outside.txt");
            File.WriteAllText(outsideFile, "protected");
            File.CreateSymbolicLink(install + ".bundler-swap", outsideFile);

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
            Assert.Equal("protected", File.ReadAllText(outsideFile));
            Assert.False(File.Exists(install + ".bundler-swap"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_BackupMoveFailure_LeavesNoMarkerWedge()
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
            // 备份位父段是普通文件 → MoveTree(install→backup) 必失败；
            // marker 写在备份移位之前，失败不可留 marker——否则后续每次 apply
            // 都误判崩溃恢复而楔形。
            var blockerFile = Path.Combine(directory, "blocker");
            File.WriteAllText(blockerFile, "x");
            var backupUnderFile = Path.Combine(blockerFile, "b");

            Assert.ThrowsAny<Exception>(() =>
                DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                    new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                    {
                        InstallDirectory = install,
                        PayloadDirectory = payload,
                        BackupDirectory = backupUnderFile
                    }, _ => { }));

            Assert.False(File.Exists(install + ".bundler-swap"));
            Assert.Equal("v1", File.ReadAllText(Path.Combine(install, "app")));
            // 重试（修正 backup 位后）立即可用——无楔形残留。
            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload
                }, _ => { });
            Assert.Equal(0, rc);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
        }
        finally
        {
            Cleanup(directory);
        }
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
            }, PackageFormat.AppImage, "linux-x86_64");

            var sidecar = Path.Combine(payload, UpdateIdentitySidecar.FileName);
            Assert.True(File.Exists(sidecar));
            using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = document.RootElement;
            Assert.Equal("appimage", root.GetProperty("format").GetString());
            Assert.Equal("linux-x86_64", root.GetProperty("rid").GetString());
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
                directory, null, PackageFormat.Zip, "linux-x86_64");
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
                BundleTarget.TryParse("linux-x86_64", out var target) ? target! : throw new InvalidOperationException(),
                PackageFormat.Zip, input, "app.bin", directory, false);
            var keyPath = Path.Combine(directory, "key.json");
            var material = UpdateKeyMaterial.Generate();
            material.Save(keyPath);
            var context = new BundleBuildContext(
                new BundleConfiguration
                {
                    ProductName = "App",
                    Identifier = "com.example.app",
                    Version = "1.0.0",
                    OutputDirectory = directory,
                    Update = new UpdateBundleConfiguration
                    {
                        FeedUrl = "https://example.test/updates",
                        SigningKeyFile = keyPath,
                        PublicKey = material.PublicPointBase64(),
                    }
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

    [Fact]
    static void Bootstrapper_WinRid_NeverFallsBackToScript()
    {
        var directory = CreateTempDirectory();
        try
        {
            var tools = Path.Combine(directory, "tools");
            Directory.CreateDirectory(Path.Combine(tools, "posix"));
            File.WriteAllText(Path.Combine(tools, "posix", "bundler-updater.sh"), "#!/bin/sh\n");
            var update = new UpdateBundleConfiguration { BootstrapperDirectory = tools };

            // win 宿主绝不拿 POSIX 脚本——无 per-RID 二进制即不可用（确定性拒绝而非注入不可执行件）。
            Assert.False(UpdateBootstrapper.TryResolve(update, "windows-arm64", out _));
            Assert.False(UpdateBootstrapper.TryResolve(update, "windows-x86_64", out _));
            // 非 win 宿主照常降级脚本件。
            Assert.True(UpdateBootstrapper.TryResolve(update, "macos-arm64", out var resolved));
            Assert.EndsWith("bundler-updater.sh", resolved);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Bootstrapper_ResolvesEmbedded_WhenNoDirectory()
    {
        // 无显式工具目录 → Bundler.Core 内嵌资源兜底（MSBuild 包/CLI/直引/NuGet 全形态可达）。
        var update = new UpdateBundleConfiguration();
        Assert.True(UpdateBootstrapper.TryResolve(update, "linux-x86_64", out var linux));
        Assert.True(File.Exists(linux));
        Assert.True(new FileInfo(linux).Length > 1_000_000); // 真 AOT 件非桩
        Assert.True(UpdateBootstrapper.TryResolve(update, "windows-x86_64", out var win));
        Assert.EndsWith(".exe", win);
        Assert.True(File.Exists(win));
        // windows-arm64 有内嵌真件（arm64 宿主产出随包）；windows-i686 仍无件且不降级脚本 → 确定性不可用。
        Assert.True(UpdateBootstrapper.TryResolve(update, "windows-arm64", out _));
        Assert.False(UpdateBootstrapper.TryResolve(update, "windows-i686", out _));
    }

    [Fact]
    static void Emitter_AppDirectory_ProducesZipTransport()
    {
        var directory = CreateTempDirectory();
        try
        {
            var keyPath = Path.Combine(directory, "key.json");
            UpdateKeyMaterial.Generate().Save(keyPath);
            // .app 是目录件：打进 <rid>/<format>/ 分目录（planner 真实布局）。
            var appDir = Path.Combine(directory, "MyApp.app");
            Directory.CreateDirectory(Path.Combine(appDir, "Contents", "MacOS"));
            File.WriteAllText(Path.Combine(appDir, "Contents", "Info.plist"), "<plist/>");
            File.WriteAllText(Path.Combine(appDir, "Contents", "MacOS", "app"), "#!/bin/sh\necho hi\n");

            var configuration = new BundleConfiguration
            {
                ProductName = "App",
                Identifier = "com.example.app",
                Version = "2.0.0",
                OutputDirectory = directory,
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://example.test/updates",
                    Channel = "stable",
                    SigningKeyFile = keyPath,
                }
            };
            var artifacts = new[]
            {
                new BundleArtifact(PackageFormat.App, "macos-arm64", appDir)
            };

            var produced = UpdateManifestEmitter.EmitAsync(configuration, artifacts)
                .GetAwaiter().GetResult();

            var zipPath = appDir + ".zip";
            Assert.Contains(zipPath, produced);
            Assert.True(File.Exists(zipPath + ".sig"));
            Assert.True(File.Exists(zipPath + ".blockmap"));
            // 运输件解出即单顶层 MyApp.app（客户端 format=app 走解包→换包）。
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                Assert.Contains(archive.Entries,
                    e => e.FullName == "MyApp.app/Contents/MacOS/app");
            }
            var feedPath = Path.Combine(directory, "bundler-update-feed.stable.json");
            using var document = JsonDocument.Parse(File.ReadAllText(feedPath));
            var entry = document.RootElement.GetProperty("artifacts").EnumerateArray().Single();
            Assert.Equal("app", entry.GetProperty("format").GetString());
            // url 是相对清单目录的路径（制品分目录落盘），file 恒为裸名。
            Assert.Equal("MyApp.app.zip", entry.GetProperty("url").GetString());
            Assert.Equal("MyApp.app.zip", entry.GetProperty("file").GetString());
            Assert.Equal("MyApp.app.zip.blockmap",
                entry.GetProperty("blockmap").GetString());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Emitter_DuplicateAppArtifact_KeepsSingleEntryMatchingFinalFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var keyPath = Path.Combine(directory, "key.json");
            UpdateKeyMaterial.Generate().Save(keyPath);
            // .app 被构建两次（独立项 + dmg/pkg 内层暂存）覆写同一运输件——
            // feed 必须只留一条且 sha/sig 对磁盘最终文件，否则客户端首条命中 stale 必拒。
            var appDir = Path.Combine(directory, "MyApp.app");
            Directory.CreateDirectory(Path.Combine(appDir, "Contents", "MacOS"));
            File.WriteAllText(Path.Combine(appDir, "Contents", "Info.plist"), "<plist/>");
            File.WriteAllText(Path.Combine(appDir, "Contents", "MacOS", "app"), "#!/bin/sh\necho hi\n");

            var configuration = new BundleConfiguration
            {
                ProductName = "App",
                Identifier = "com.example.app",
                Version = "2.0.0",
                OutputDirectory = directory,
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://example.test/updates",
                    Channel = "stable",
                    SigningKeyFile = keyPath,
                }
            };
            var artifacts = new[]
            {
                new BundleArtifact(PackageFormat.App, "macos-arm64", appDir),
                new BundleArtifact(PackageFormat.App, "macos-arm64", appDir),
            };

            UpdateManifestEmitter.EmitAsync(configuration, artifacts)
                .GetAwaiter().GetResult();

            var feedPath = Path.Combine(directory, "bundler-update-feed.stable.json");
            using var document = JsonDocument.Parse(File.ReadAllText(feedPath));
            var entry = document.RootElement.GetProperty("artifacts").EnumerateArray().Single();
            var zipPath = appDir + ".zip";
            var expectedSha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zipPath)))
                .ToLowerInvariant();
            Assert.Equal(expectedSha, entry.GetProperty("sha256").GetString());
            var material = UpdateKeyMaterial.Load(keyPath);
            Assert.True(EcdsaSigner.VerifyFile(
                zipPath, Convert.FromBase64String(entry.GetProperty("sig").GetString()!), material));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Emitter_NoUpdateAdaptedArtifacts_StillEmitsSignedEmptyFeed()
    {
        // R2-N9：更新开启但产物全走非适配格式（deb/rpm 包管理器领地）时曾静默
        // 不发清单——已装应用按旁车 feedUrl 轮询会拉空失败。现须发签名空清单，
        // 客户端读作"无更新"。
        var directory = CreateTempDirectory();
        try
        {
            var keyPath = Path.Combine(directory, "key.json");
            UpdateKeyMaterial.Generate().Save(keyPath);
            var debPath = Path.Combine(directory, "app.deb");
            Directory.CreateDirectory(Path.GetDirectoryName(debPath)!);
            File.WriteAllText(debPath, "deb-payload");

            var configuration = new BundleConfiguration
            {
                ProductName = "App",
                Identifier = "com.example.app",
                Version = "2.0.0",
                OutputDirectory = directory,
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://example.test/updates",
                    Channel = "stable",
                    SigningKeyFile = keyPath,
                }
            };
            var artifacts = new[]
            {
                new BundleArtifact(PackageFormat.Deb, "linux-x86_64", debPath),
            };

            var produced = UpdateManifestEmitter.EmitAsync(configuration, artifacts)
                .GetAwaiter().GetResult();

            var feedPath = Path.Combine(directory, "bundler-update-feed.stable.json");
            Assert.True(File.Exists(feedPath));
            Assert.True(File.Exists(feedPath + ".sig"));
            Assert.Contains(feedPath, produced);
            using var document = JsonDocument.Parse(File.ReadAllText(feedPath));
            Assert.Empty(document.RootElement.GetProperty("artifacts").EnumerateArray());
            var material = UpdateKeyMaterial.Load(keyPath);
            Assert.True(EcdsaSigner.VerifyFile(
                feedPath, File.ReadAllBytes(feedPath + ".sig"), material));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Applier_PayloadResolution_RequiresNoRootFilesForUnwrap()
    {
        var directory = CreateTempDirectory();
        try
        {
            // "目录内容直压"形状（zip -ry <目录>/tar -C <目录> .）：顶层有文件+恰好一个目录
            // ——单目录不是 wrapper 是载荷子目录，整个暂存必须当载荷根否则同级文件静默丢。
            var flat = Path.Combine(directory, "flat");
            Directory.CreateDirectory(Path.Combine(flat, "docs"));
            File.WriteAllText(Path.Combine(flat, "readme.txt"), "x");
            File.WriteAllText(Path.Combine(flat, "docs", "a.md"), "x");
            Assert.Equal(flat, DotNet.Bundler.Updater.UpdateApplier.SingleTopDirectory(flat, "a.zip"));

            // wrapper 形状：顶层恰一个目录且零根级文件——解包到该目录。
            var wrapped = Path.Combine(directory, "wrapped");
            var inner = Path.Combine(wrapped, "myapp");
            Directory.CreateDirectory(inner);
            File.WriteAllText(Path.Combine(inner, "app"), "x");
            Assert.Equal(inner, DotNet.Bundler.Updater.UpdateApplier.SingleTopDirectory(wrapped, "b.zip"));

            // 空归档仍拒绝。
            var empty = Path.Combine(directory, "empty");
            Directory.CreateDirectory(empty);
            Assert.Throws<DotNet.Bundler.Updater.UpdateException>(
                () => DotNet.Bundler.Updater.UpdateApplier.SingleTopDirectory(empty, "c.zip"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Applier_ManagedExtract_RemovesNestedAppleDouble()
    {
        var directory = CreateTempDirectory();
        try
        {
            // wrapper 内嵌套 __MACOSX（载荷含预解压 macOS 目录树）——managed 解包必须
            // 全树清，否则会随换包落进安装目录。
            var source = Path.Combine(directory, "source");
            var wrapper = Path.Combine(source, "hello-1.0.0");
            Directory.CreateDirectory(Path.Combine(wrapper, "bin"));
            Directory.CreateDirectory(Path.Combine(wrapper, "__MACOSX", "bin"));
            File.WriteAllText(Path.Combine(wrapper, "bin", "app"), "x");
            File.WriteAllText(Path.Combine(wrapper, "__MACOSX", "bin", "._app"), "junk");
            var zipPath = Path.Combine(directory, "payload.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(source, zipPath);

            var staging = Path.Combine(directory, "staging");
            Directory.CreateDirectory(staging);
            DotNet.Bundler.Updater.ArchiveExtractor.ExtractZipToDirectory(zipPath, staging, null);

            Assert.Empty(Directory.GetDirectories(
                staging, "__MACOSX", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(staging, "hello-1.0.0", "bin", "app")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Emitter_KeepsEscapedChars_InArtifactUrl()
    {
        // 制品文件名含 '#'/'?'/空格：url 必须保留 %XX 转义——未转义字符会被 http
        // 当成 fragment/query 分隔符，产物地址直接解析错。
        var directory = CreateTempDirectory();
        try
        {
            var keyPath = Path.Combine(directory, "key.json");
            UpdateKeyMaterial.Generate().Save(keyPath);
            var artifact = WriteDummyArtifact(directory, "app 1.0#frag.zip");
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
                }
            };

            UpdateManifestEmitter.EmitAsync(configuration,
                    [new BundleArtifact(PackageFormat.Zip, "linux-x86_64", artifact)])
                .GetAwaiter().GetResult();

            var feedPath = Path.Combine(directory, "bundler-update-feed.stable.json");
            using var document = JsonDocument.Parse(File.ReadAllText(feedPath));
            var entry = document.RootElement.GetProperty("artifacts").EnumerateArray().Single();
            Assert.Equal("app%201.0%23frag.zip", entry.GetProperty("url").GetString());
            // file 字段保持原始名（仅作下载落点文件名，不进 URL）。
            Assert.Equal("app 1.0#frag.zip", entry.GetProperty("file").GetString());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void Emitter_UsesRelativeUrl_ForSubdirectoryArtifacts()
    {
        var directory = CreateTempDirectory();
        try
        {
            var keyPath = Path.Combine(directory, "key.json");
            UpdateKeyMaterial.Generate().Save(keyPath);
            var nested = directory;
            Directory.CreateDirectory(nested);
            var artifact = WriteDummyArtifact(nested, "app-1.0.0.zip");
            var configuration = new BundleConfiguration
            {
                ProductName = "App",
                Identifier = "com.example.app",
                Version = "1.0.0",
                OutputDirectory = directory,
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://example.test/updates",
                    Channel = "latest",
                    SigningKeyFile = keyPath,
                }
            };

            UpdateManifestEmitter.EmitAsync(configuration,
                    [new BundleArtifact(PackageFormat.Zip, "linux-x86_64", artifact)])
                .GetAwaiter().GetResult();

            var feedPath = Path.Combine(directory, "bundler-update-feed.latest.json");
            using var document = JsonDocument.Parse(File.ReadAllText(feedPath));
            var entry = document.RootElement.GetProperty("artifacts").EnumerateArray().Single();
            Assert.Equal("app-1.0.0.zip", entry.GetProperty("url").GetString());
            Assert.Equal("app-1.0.0.zip", entry.GetProperty("file").GetString());
            // sig 与 blockmap 随制品落在同一分目录。
            Assert.True(File.Exists(artifact + ".sig"));
            Assert.True(File.Exists(artifact + ".blockmap"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_RecoversMissingInstallDir()
    {
        // 断电发生在"备份已移走、新载荷未换入"之间：安装目录缺失 + marker + 备份完整，
        // 恢复必须先于 install 存在性检查运行，否则死在拒绝上无法自愈。
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            var backup = install + ".bundler-backup";
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(backup, "app"), "v1-good");
            File.WriteAllText(Path.Combine(payload, "app"), "v2");
            File.WriteAllText(install + ".bundler-swap", "swap in progress");

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
            // 默认不保留回滚点：恢复+v2 就位后瞬备删除。
            Assert.False(Directory.Exists(backup));
            Assert.False(File.Exists(install + ".bundler-swap"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_FileSwap_AppImageStyle()
    {
        // AppImage 单件：安装目标是文件——宿主目录里的无关文件必须原样保留。
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "MyApp.AppImage");
            var payloadDir = Path.Combine(directory, "payload");
            var payload = Path.Combine(payloadDir, "MyApp-2.0.0.AppImage");
            Directory.CreateDirectory(payloadDir);
            File.WriteAllText(install, "v1-image");
            File.WriteAllText(payload, "v2-image");
            File.WriteAllText(Path.Combine(directory, "sibling.txt"), "keep-me");

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal("v2-image", File.ReadAllText(install));
            // 默认不保留回滚点：文件级瞬备同样换完即删。
            Assert.False(File.Exists(install + ".bundler-backup"));
            Assert.False(File.Exists(install + ".bundler-swap"));
            // 目录语义会清掉 sibling——文件级换包必须留下它。
            Assert.Equal("keep-me", File.ReadAllText(Path.Combine(directory, "sibling.txt")));
            Assert.False(Directory.Exists(payloadDir) && File.Exists(payload));

            // 未保留 → 回滚确定性拒绝。
            Assert.Throws<DotNet.Bundler.Updater.Bootstrap.UpdateRejectedException>(() =>
                DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                    new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                    {
                        InstallDirectory = install,
                        Rollback = true,
                    }, _ => { }));

            // 保留语义：备份按安装文件真名落保留目录，回滚从保留位倒回。
            File.WriteAllText(install + ".bundler-backup", "v1-image");
            File.WriteAllText(payload, "v3-image");
            var retain = Path.Combine(directory, "backups", "myapp-abc");
            rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    RetainBackupDirectory = retain,
                }, _ => { });
            Assert.Equal(0, rc);
            Assert.Equal("v3-image", File.ReadAllText(install));
            Assert.Equal("v2-image", File.ReadAllText(
                Path.Combine(retain, "MyApp.AppImage")));

            rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    BackupDirectory = Path.Combine(retain, "MyApp.AppImage"),
                    Rollback = true,
                }, _ => { });
            Assert.Equal(0, rc);
            Assert.Equal("v2-image", File.ReadAllText(install));
            Assert.True(File.Exists(Path.Combine(retain, "MyApp.AppImage")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void BootstrapPlan_FileSwap_RecoversMissingInstall()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "MyApp.AppImage");
            File.WriteAllText(install + ".bundler-backup", "v1-image");
            File.WriteAllText(install + ".bundler-swap", "swap in progress");
            var payload = Path.Combine(directory, "new.AppImage");
            File.WriteAllText(payload, "v2-image");

            var rc = DotNet.Bundler.Updater.Bootstrap.BootstrapPlan.Apply(
                new DotNet.Bundler.Updater.Bootstrap.BootstrapOptions
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                }, _ => { });

            Assert.Equal(0, rc);
            Assert.Equal("v2-image", File.ReadAllText(install));
            Assert.False(File.Exists(install + ".bundler-swap"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void ArchiveBackend_DedupesSidecarEntries()
    {
        // 输入树自带 bundler-update.json 时，归档里只能有一份——本构建注入的身份为准。
        var directory = CreateTempDirectory();
        try
        {
            var input = Path.Combine(directory, "input");
            Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(input, "app.bin"), "payload");
            File.WriteAllText(Path.Combine(input, UpdateIdentitySidecar.FileName),
                "{\"format\":\"stale\"}");
            var output = Path.Combine(directory, "out");
            var keyPath = Path.Combine(directory, "key.json");
            var material = UpdateKeyMaterial.Generate();
            material.Save(keyPath);

            var artifacts = new DotNet.Bundler.Archive.ArchiveBundler(
                new DotNet.Bundler.Archive.ArchiveBundleConfiguration())
                .BuildAsync(new BundleConfiguration
                {
                    ProductName = "App",
                    Identifier = "com.example.app",
                    Version = "1.0.0",
                    OutputDirectory = output,
                    Update = new UpdateBundleConfiguration
                    {
                        FeedUrl = "https://example.test/updates",
                        SigningKeyFile = keyPath,
                        PublicKey = material.PublicPointBase64(),
                    },
                    Targets = [new BundleTargetConfiguration
                    {
                        Target = "linux-x86_64",
                        InputDirectory = input,
                        MainExecutable = "app.bin",
                        Formats = [PackageFormat.Zip]
                    }]
                }).GetAwaiter().GetResult();

            using var archive = System.IO.Compression.ZipFile.OpenRead(artifacts.Single().Path);
            var sidecars = archive.Entries.Where(
                e => e.FullName.EndsWith("/" + UpdateIdentitySidecar.FileName)).ToArray();
            Assert.Single(sidecars);
            using var reader = new StreamReader(sidecars[0].Open());
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            Assert.Equal("zip", document.RootElement.GetProperty("format").GetString());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void ExtractZip_RestoresSymlinkEntries()
    {
        // managed ExtractToDirectory 把 S_IFLNK 条目落成普通文件——提取器必须还原真链接。
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink restore is POSIX-only");
        var directory = CreateTempDirectory();
        try
        {
            var zipPath = Path.Combine(directory, "payload.zip");
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            {
                DotNet.Bundler.Archive.ZipWriter.Write(stream,
                [
                    new DotNet.Bundler.Archive.ZipEntry
                    {
                        Name = "stem/",
                        Kind = DotNet.Bundler.Archive.ZipEntryKind.Directory,
                        Mode = 493,
                    },
                    new DotNet.Bundler.Archive.ZipEntry
                    {
                        Name = "stem/real.txt",
                        Kind = DotNet.Bundler.Archive.ZipEntryKind.File,
                        Mode = 420,
                        Content = Encoding.UTF8.GetBytes("linked"),
                    },
                    new DotNet.Bundler.Archive.ZipEntry
                    {
                        Name = "stem/link.txt",
                        Kind = DotNet.Bundler.Archive.ZipEntryKind.Symlink,
                        Mode = 511,
                        LinkTarget = "real.txt",
                    },
                ]);
            }
            var staging = Path.Combine(directory, "staging");
            Directory.CreateDirectory(staging);

            DotNet.Bundler.Updater.ArchiveExtractor.ExtractZipToDirectory(
                zipPath, staging, _ => { });

            var link = new FileInfo(Path.Combine(staging, "stem", "link.txt"));
            Assert.Equal("real.txt", link.LinkTarget);
            Assert.Equal("linked", File.ReadAllText(link.FullName));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void ExtractZip_RejectsSymlinkEscapingStaging()
    {
        // 链接目标解出暂存根外（../ 或绝对）时还原必须拒——否则后续条目
        // 虽过 SafePath 字面检查，写盘会顺已物化的链接写出暂存区。
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink restore is POSIX-only");
        var directory = CreateTempDirectory();
        try
        {
            var zipPath = Path.Combine(directory, "payload.zip");
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            {
                DotNet.Bundler.Archive.ZipWriter.Write(stream,
                [
                    new DotNet.Bundler.Archive.ZipEntry
                    {
                        Name = "stem/link",
                        Kind = DotNet.Bundler.Archive.ZipEntryKind.Symlink,
                        Mode = 511,
                        LinkTarget = "../../outside",
                    },
                ]);
            }
            var staging = Path.Combine(directory, "staging");
            Directory.CreateDirectory(staging);
            var warnings = new List<string>();

            DotNet.Bundler.Updater.ArchiveExtractor.ExtractZipToDirectory(
                zipPath, staging, warnings.Add);

            var linkPath = Path.Combine(staging, "stem", "link");
            Assert.True(new FileInfo(linkPath).LinkTarget is null,
                "escaping symlink target must not be materialized");
            Assert.Contains(warnings, w => w.Contains("failed to restore symlink", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static void ExtractZip_DropsAppleDoubleTree()
    {
        // ditto 产 zip 自带 __MACOSX/ 资源叉目录——managed 提取下它是垃圾树，清掉。
        var directory = CreateTempDirectory();
        try
        {
            var zipPath = Path.Combine(directory, "ditto.zip");
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            {
                DotNet.Bundler.Archive.ZipWriter.Write(stream,
                [
                    new DotNet.Bundler.Archive.ZipEntry
                    {
                        Name = "stem/app",
                        Kind = DotNet.Bundler.Archive.ZipEntryKind.File,
                        Mode = 493,
                        Content = Encoding.UTF8.GetBytes("binary"),
                    },
                    new DotNet.Bundler.Archive.ZipEntry
                    {
                        Name = "__MACOSX/stem/._app",
                        Kind = DotNet.Bundler.Archive.ZipEntryKind.File,
                        Mode = 420,
                        Content = Encoding.UTF8.GetBytes("appledouble"),
                    },
                ]);
            }
            var staging = Path.Combine(directory, "staging");
            Directory.CreateDirectory(staging);

            DotNet.Bundler.Updater.ArchiveExtractor.ExtractZipToDirectory(
                zipPath, staging, _ => { });

            Assert.False(Directory.Exists(Path.Combine(staging, "__MACOSX")));
            Assert.Equal("binary",
                File.ReadAllText(Path.Combine(staging, "stem", "app")));
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
