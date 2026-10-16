// UpdateClient.Apply 真链腿：UpdateApplier 拉起真实引导件子进程做三段式换包——
// 原 UpdaterClientTests 的 Linux 真环境腿（Unit 层零子进程规则不收）。
// 覆盖：文件级安装单元（AppImage）换包+保留位备份+回滚、目录级 zip/tar.gz 端到端、
// macOS 未签 .app 端到端（含 MacAppGate quarantine 剥离）、/bin/sh 降级脚本链。
using System.IO.Compression;
using System.Runtime.Serialization.Json;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Core;
using DotNet.Bundler.Update;
using DotNet.Bundler.Updater;
using Protocol = DotNet.Bundler.Updater.Protocol;
using UpdateKeyMaterial = DotNet.Bundler.Core.Update.UpdateKeyMaterial;

public sealed class UpdateClientChainFixture : IDisposable
{
    // 惰性创建：宿主门禁在各用例体内 Skip，之后才允许占锁建目录。
    private readonly Lazy<IntegrationWorkspace> _ws = new(() => IntegrationWorkspace.Create(
        "update-client-chain-integration", "BundlerUpdateClientChain"));

    public IntegrationWorkspace Ws => _ws.Value;

    public void Dispose()
    {
        if (_ws.IsValueCreated)
        {
            _ws.Value.Dispose();
        }
    }
}

public sealed class UpdateClientChainTests : IClassFixture<UpdateClientChainFixture>
{
    private IntegrationWorkspace Ws => _fixture.Ws;
    private readonly UpdateClientChainFixture _fixture;

    public UpdateClientChainTests(UpdateClientChainFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Client_UpdateAsync_AppImage_FileSwap_Linux()
    {
        Assert.SkipUnless(TestPlatform.IsLinux,
            "Linux-only leg (bootstrapper rides host arch).");
        var directory = Ws.Combine("appimage-fileswap");
        Directory.CreateDirectory(directory);

        // AppImage 安装单元=文件：安装目录是宿主目录（可含无关文件），install 参数是文件路径。
        var installDir = Path.Combine(directory, "host-apps");
        Directory.CreateDirectory(installDir);
        var install = Path.Combine(installDir, "MyApp.AppImage");
        File.WriteAllText(install, "v1-image");
        File.WriteAllText(Path.Combine(installDir, "sibling.txt"), "keep-me");
        var feedDir = Path.Combine(directory, "feed");
        Directory.CreateDirectory(feedDir);
        var material = UpdateKeyMaterial.Generate();

        var artifact = Path.Combine(feedDir, "MyApp-2.0.0.AppImage");
        File.WriteAllText(artifact, "v2-image");
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "appimage",
            Url = "MyApp-2.0.0.AppImage", File = "MyApp-2.0.0.AppImage",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
        });

        // 旁车在镜像内部（挂载内可读）→ 消费侧走 FromIdentity + install=文件路径。
        var client = UpdateClient.FromIdentity(new Protocol.UpdateInstallIdentity
        {
            Format = "appimage",
            Target = TestPlatform.LinuxTarget,
            Channel = "stable",
            FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
            PublicKey = material.PublicPointBase64(),
        }, install, "1.0.0");

        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            BootstrapperPath = LinuxBootstrapper(),
            KeepRollbackBackup = true,
            RollbackBackupDirectory = Path.Combine(directory, "backups", "myapp"),
        });
        Assert.True(process.WaitForExit(90_000), "bootstrapper did not exit in 90s");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("v2-image", File.ReadAllText(install));
        // 保留语义：文件级备份按安装文件真名落保留目录；兄弟位瞬备清走。
        Assert.False(File.Exists(install + ".bundler-backup"));
        Assert.Equal("v1-image", File.ReadAllText(
            Path.Combine(directory, "backups", "myapp", "MyApp.AppImage")));
        // 宿主目录里的无关文件原样保留——文件级换包的防误清面。
        Assert.Equal("keep-me", File.ReadAllText(Path.Combine(installDir, "sibling.txt")));
        Assert.False(File.Exists(install + ".bundler-swap"));
        Assert.False(File.Exists(install + ".bundler-lock"));
        // 文件级安装单元的身份落在 <file>.bundler-update.json 旁车——
        // 下轮 FromInstallDirectory(文件路径) 直读，更新身份不随镜像内嵌而失联。
        var persisted = Protocol.UpdateInstallIdentity.TryRead(install);
        Assert.NotNull(persisted);
        Assert.Equal("appimage", persisted!.Format);
        Assert.Equal(Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
            persisted.FeedUrl);

        var rollback = client.Rollback(new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging-rb"),
            BootstrapperPath = LinuxBootstrapper(),
            KeepRollbackBackup = true,
            RollbackBackupDirectory = Path.Combine(directory, "backups", "myapp"),
        });
        Assert.True(rollback.WaitForExit(60_000));
        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal("v1-image", File.ReadAllText(install));
    }

    [Fact]
    public async Task Client_UpdateAsync_EndToEnd_Linux()
    {
        Assert.SkipUnless(TestPlatform.IsLinux,
            "Linux-only leg (bootstrapper rides host arch).");
        var directory = Ws.Combine("endtoend");
        Directory.CreateDirectory(directory);

        var install = InstallWithSidecar(directory, out var material, out var feedDir);
        File.WriteAllText(Path.Combine(install, "app"), "v1");
        // 引导件：安装目录内注入真实 musl 静态 AOT 二进制（glibc 宿主同跑）。
        var bootstrapper = LinuxBootstrapper();
        File.Copy(bootstrapper, Path.Combine(install, "bundler-updater"));

        // v2 载荷 zip：单顶层目录 app-v2/app="v2" + 引导件与身份侧车也随包
        //（真实发布形态——换包后安装位仍带 bundler-update.json，更新链可持续）。
        var artifact = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2",
            ("bundler-updater", File.ReadAllBytes(bootstrapper)),
            ("bundler-update.json", SidecarBytes("zip", TestPlatform.LinuxTarget,
                Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
                material.PublicPointBase64())));
        var sha = Sha256Hex(artifact);
        var sig = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material));
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "zip",
            Url = "app-2.0.0.zip", File = "app-2.0.0.zip",
            Sha256 = sha, Size = new FileInfo(artifact).Length, Signature = sig,
        });

        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            KeepRollbackBackup = true,
            RollbackBackupDirectory = Path.Combine(directory, "backups", "install-x"),
        });
        Assert.True(process.WaitForExit(90_000), "bootstrapper did not exit in 90s");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
        // 保留语义：备份迁到保留目录当回滚点；兄弟位瞬备清走。
        Assert.False(Directory.Exists(install + ".bundler-backup"));
        Assert.Equal("v1", File.ReadAllText(
            Path.Combine(directory, "backups", "install-x", "app")));
        Assert.False(File.Exists(install + ".bundler-swap"));
        Assert.False(File.Exists(install + ".bundler-lock"));
        // 载荷侧车换进安装位——更新链可持续（下轮 FromInstallDirectory 读得出身份）。
        var persisted = Protocol.UpdateInstallIdentity.TryRead(install);
        Assert.NotNull(persisted);
        Assert.Equal("zip", persisted!.Format);
        Assert.Equal(material.PublicPointBase64(), persisted.PublicKey);

        // 回滚钩子：保留位备份倒回 → 安装目录回到 v1。
        var rollback = client.Rollback(new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging-rb"),
            KeepRollbackBackup = true,
            RollbackBackupDirectory = Path.Combine(directory, "backups", "install-x"),
        });
        Assert.True(rollback.WaitForExit(60_000));
        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(install, "app")));
    }

    [Fact]
    public async Task Client_UpdateAsync_UsesShellFallback()
    {
        Assert.SkipUnless(TestPlatform.IsLinux, "Linux-only leg.");
        var directory = Ws.Combine("shell-fallback");
        Directory.CreateDirectory(directory);

        var install = InstallWithSidecar(directory, out var material, out var feedDir);
        File.WriteAllText(Path.Combine(install, "app"), "v1");
        // 只有 .sh 降级件，没有 AOT 二进制。
        var script = Path.Combine(RepositoryLayout.Root,
            "src", "Bundler.Updater.Bootstrap", "tools", "posix", "bundler-updater.sh");
        File.Copy(script, Path.Combine(install, "bundler-updater.sh"));

        var artifact = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2");
        var sig = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material));
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "zip",
            Url = "app-2.0.0.zip", File = "app-2.0.0.zip",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = sig,
        });

        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
        });
        Assert.True(process.WaitForExit(90_000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
    }

    // SupportedOSPlatform 供 CA1416 编译期判定（运行期仍靠 SkipUnless 门禁）。
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [Fact]
    public async Task Client_UpdateAsync_TarGz_EndToEnd_Linux()
    {
        // tar.gz 运输件端到端：与 zip 腿同一换包通道（ExtractTarGz → 三段式），
        // 独有断言面是执行位经 ustar mode 还原——managed zip 腿无此保真。
        Assert.SkipUnless(TestPlatform.IsLinux,
            "Linux-only leg (bootstrapper rides host arch).");
        var directory = Ws.Combine("targz-endtoend");
        Directory.CreateDirectory(directory);

        var install = InstallWithSidecar(directory, out var material, out var feedDir,
            format: "targz", target: TestPlatform.LinuxTarget);
        File.WriteAllText(Path.Combine(install, "app"), "v1");
        var bootstrapper = LinuxBootstrapper();
        File.Copy(bootstrapper, Path.Combine(install, "bundler-updater"));

        var artifact = WriteTarGzArtifact(feedDir, "app-2.0.0.tar.gz", "v2",
            ("bundler-updater", File.ReadAllBytes(bootstrapper)),
            ("bundler-update.json", SidecarBytes("targz", TestPlatform.LinuxTarget,
                Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
                material.PublicPointBase64())));
        var sha = Sha256Hex(artifact);
        var sig = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material));
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "targz",
            Url = "app-2.0.0.tar.gz", File = "app-2.0.0.tar.gz",
            Sha256 = sha, Size = new FileInfo(artifact).Length, Signature = sig,
        });

        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            KeepRollbackBackup = true,
            RollbackBackupDirectory = Path.Combine(directory, "backups", "install-x"),
        });
        Assert.True(process.WaitForExit(90_000), "bootstrapper did not exit in 90s");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
        // tar 保真面：随包引导件的 0755 经 ustar mode 落回文件系统。
        Assert.True(
            (File.GetUnixFileMode(Path.Combine(install, "bundler-updater")) &
                UnixFileMode.UserExecute) != 0,
            "payload executable bit must survive the tar.gz transport.");
        Assert.False(Directory.Exists(install + ".bundler-backup"));
        Assert.Equal("v1", File.ReadAllText(
            Path.Combine(directory, "backups", "install-x", "app")));
        Assert.False(File.Exists(install + ".bundler-swap"));
        Assert.False(File.Exists(install + ".bundler-lock"));
        // 载荷侧车换进安装位——更新链可持续。
        var persisted = Protocol.UpdateInstallIdentity.TryRead(install);
        Assert.NotNull(persisted);
        Assert.Equal("targz", persisted!.Format);

        var rollback = client.Rollback(new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging-rb"),
            KeepRollbackBackup = true,
            RollbackBackupDirectory = Path.Combine(directory, "backups", "install-x"),
        });
        Assert.True(rollback.WaitForExit(60_000));
        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(install, "app")));
    }

    [Fact]
    public async Task Client_UpdateAsync_AppBundle_EndToEnd_MacOS()
    {
        // 未签 .app 双方换包全链（CI 无 Apple 凭证——MacAppGate 走"双未签+BundleID"面）：
        // 载荷带 quarantine 经 ditto 运输件往返，换包前门剥离、落位件不得再带。
        Assert.SkipUnless(TestPlatform.IsMacOS,
            "macOS-only leg (.app gate + ditto transport zip).");
        var directory = Ws.Combine("app-bundle-e2e");
        Directory.CreateDirectory(directory);
        var material = UpdateKeyMaterial.Generate();
        var feedDir = Path.Combine(directory, "feed");
        Directory.CreateDirectory(feedDir);

        // v1 安装位：.app 目录件 + 引导件按真实装位注入 Contents/MacOS/。
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "MyApp", "com.bundler.chain");
        File.WriteAllText(Path.Combine(install, "Contents", "MacOS", "MyApp"), "v1");
        var bootstrapper = MacAppStub.BootstrapperTool(TestPlatform.OsxTarget);
        File.Copy(bootstrapper, Path.Combine(install, "Contents", "MacOS", "bundler-updater"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(install, "Contents", "MacOS", "bundler-updater"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // v2 载荷：同 BundleID .app + quarantine 全树打标——经 ditto 运输件保真到提取侧。
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-src"), "MyApp", "com.bundler.chain");
        File.WriteAllText(Path.Combine(payload, "Contents", "MacOS", "MyApp"), "v2");
        File.Copy(bootstrapper,
            Path.Combine(payload, "Contents", "MacOS", "bundler-updater"));
        if (!OperatingSystem.IsWindows())
        {
            // 真实发布形态：载荷内引导件同样要可执行（第二次更新跑它）。
            File.SetUnixFileMode(Path.Combine(payload, "Contents", "MacOS", "bundler-updater"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        // 身份侧车落 Contents/Resources（资源密封位——TryRead 的 .app 标准读位），
        // 换包后 .app 安装位仍带身份，更新链可持续。
        Protocol.UpdateInstallIdentity.Write(
            Path.Combine(payload, "Contents", "Resources"),
            new Protocol.UpdateInstallIdentity
            {
                Format = "app", Target = TestPlatform.OsxTarget, Channel = "stable",
                FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
                PublicKey = material.PublicPointBase64(),
            });
        MacAppStub.SetQuarantineTree(payload);
        var transport = Path.Combine(feedDir, "MyApp-2.0.0.app.zip");
        Assert.True(MacArchiveTools.TryWriteAppZipViaDitto(
            payload, transport, NullBundleLogger.Instance),
            "ditto transport zip must be writable on macOS (xattr fidelity path).");
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.OsxTarget, Format = "app",
            Url = "MyApp-2.0.0.app.zip", File = "MyApp-2.0.0.app.zip",
            Sha256 = Sha256Hex(transport), Size = new FileInfo(transport).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(transport, material)),
        });

        var client = UpdateClient.FromIdentity(new Protocol.UpdateInstallIdentity
        {
            Format = "app", Target = TestPlatform.OsxTarget, Channel = "stable",
            FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
            PublicKey = material.PublicPointBase64(),
        }, install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var logPath = Path.Combine(directory, "update.log");
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            KeepRollbackBackup = true,
            RollbackBackupDirectory = Path.Combine(directory, "backups", "myapp"),
            LogFile = logPath,
        });
        Assert.True(process.WaitForExit(90_000), "bootstrapper did not exit in 90s");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("v2", File.ReadAllText(
            Path.Combine(install, "Contents", "MacOS", "MyApp")));
        // 门禁生效面：载荷 quarantine 在换包前剥掉，落位安装件全树零残留；
        // 日志 "quarantine stripped" 证明标确实在过（"not present" 是另一支）。
        Assert.False(MacAppStub.HasQuarantine(install));
        Assert.All(
            Directory.EnumerateFileSystemEntries(install, "*", SearchOption.AllDirectories),
            entry => Assert.False(MacAppStub.HasQuarantine(entry)));
        Assert.Contains("quarantine stripped", File.ReadAllText(logPath));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(
            directory, "backups", "myapp", "Contents", "MacOS", "MyApp")));
        // 兄弟位残件清零（瞬备迁走/marker 回收/锁释放）——与 Linux 目录腿平价。
        Assert.False(Directory.Exists(install + ".bundler-backup"));
        Assert.False(File.Exists(install + ".bundler-swap"));
        Assert.False(File.Exists(install + ".bundler-lock"));
        // 载荷侧车换进 .app 安装位（Contents/Resources 密封位）——更新链可持续。
        var persisted = Protocol.UpdateInstallIdentity.TryRead(install);
        Assert.NotNull(persisted);
        Assert.Equal("app", persisted!.Format);
        Assert.Equal(material.PublicPointBase64(), persisted.PublicKey);

        // 回滚对照：保留位备份倒回 → .app 回到 v1（回滚不验门禁——备份是自家产物）。
        var rollback = client.Rollback(new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging-rb"),
            KeepRollbackBackup = true,
            RollbackBackupDirectory = Path.Combine(directory, "backups", "myapp"),
        });
        Assert.True(rollback.WaitForExit(60_000));
        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal("v1", File.ReadAllText(
            Path.Combine(install, "Contents", "MacOS", "MyApp")));
    }

    // linux 引导件收敛 musl 静态单件（#100 起 tools/ 无字面 linux-<arch> 件，
    // static-pie 件 glibc/musl 双宿主通吃）——与打包侧 MuslSibling 同一选件语义。
    static string LinuxBootstrapper() => Path.Combine(
        RepositoryLayout.Root, "src", "Bundler.Updater.Bootstrap", "tools",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64
            ? "linux-musl-aarch64" : "linux-musl-x86_64",
        "bundler-updater");

    static string InstallWithSidecar(
        string root, out UpdateKeyMaterial material, out string feedDir,
        string format = "zip", string? target = null)
    {
        target ??= TestPlatform.LinuxTarget;
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        feedDir = Path.Combine(root, "feed");
        Directory.CreateDirectory(feedDir);
        material = UpdateKeyMaterial.Generate();
        // FeedUrl = 清单文件位置（本地路径或 file:// 均可）；产物按同目录解析。
        Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
        {
            Format = format,
            Target = target,
            Channel = "stable",
            FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
            PublicKey = material.PublicPointBase64(),
        });
        return install;
    }

    // 清单按发布侧口径签名——客户端验不过就拒读，测试 fixture 必须同形态。
    static void WriteFeed(
        string dir, string channel, string version,
        UpdateKeyMaterial material,
        params Protocol.UpdateFeedArtifact[] artifacts)
    {
        var feed = new Protocol.UpdateFeed
        {
            Version = version,
            Channel = channel,
            PublishedAt = "2026-10-02T00:00:00+00:00",
        };
        feed.Artifacts.AddRange(artifacts);
        var serializer = new DataContractJsonSerializer(typeof(Protocol.UpdateFeed));
        var path = Path.Combine(dir, Protocol.UpdateFeed.FeedFileName(channel));
        using (var stream = File.Create(path))
        {
            serializer.WriteObject(stream, feed);
        }
        File.WriteAllBytes(path + ".sig", EcdsaSigner.SignFile(path, material));
    }

    static string WriteZipArtifact(
        string dir, string name, string content,
        params (string name, byte[] bytes)[] extraEntries)
    {
        var path = Path.Combine(dir, name);
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("app-v2/app");
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(content);
            }
            foreach (var extra in extraEntries)
            {
                var e2 = archive.CreateEntry("app-v2/" + extra.name);
                using var es = e2.Open();
                es.Write(extra.bytes, 0, extra.bytes.Length);
            }
        }
        return path;
    }

    // 载荷内身份侧车：与打包期 UpdateIdentitySidecar 同形态的最小 JSON——
    // 换包后安装位仍带 bundler-update.json，下轮更新链才能续上。
    static byte[] SidecarBytes(string format, string target, string feedJsonPath, string publicKey) =>
        Encoding.UTF8.GetBytes(
            "{\"format\":\"" + format + "\",\"rid\":\"" + target + "\",\"channel\":\"stable\"," +
            "\"feedUrl\":\"" + feedJsonPath + "\",\"publicKey\":\"" + publicKey + "\"}");

    // tar.gz 运输件：仓内 TarWriter + GZipStream 产确定性 ustar——
    // 单顶层目录是 UpdateApplier.SingleTopDirectory 的载荷根判据。
    static string WriteTarGzArtifact(
        string dir, string name, string content,
        params (string name, byte[] bytes)[] extraEntries)
    {
        var path = Path.Combine(dir, name);
        var entries = new List<TarEntry>
        {
            new() { Name = "app-v2/", Kind = TarEntryKind.Directory, Mode = 493 /* 0755 */ },
            new()
            {
                Name = "app-v2/app", Kind = TarEntryKind.File, Mode = 420 /* 0644 */,
                Content = Encoding.UTF8.GetBytes(content),
            },
        };
        foreach (var extra in extraEntries)
        {
            entries.Add(new TarEntry
            {
                Name = "app-v2/" + extra.name, Kind = TarEntryKind.File,
                Mode = 493 /* 0755 */, Content = extra.bytes,
            });
        }
        using (var stream = File.Create(path))
        using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
        {
            TarWriter.Write(gzip, entries);
        }
        return path;
    }

    static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
