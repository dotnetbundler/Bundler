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
        // 打标=宿主能力探测：e2e 换包链不依赖标存在而照跑——标落不了位的宿主上
        // 剥标面是真空通过，只对依赖"标真实在过"的断言按 marked 条件化，不整腿 Skip。
        var marked = MacAppStub.TrySetQuarantineTree(payload);
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
        // 门禁生效面（marked 条件化）：标真实在过才谈得上剥——不持久化打标的宿主
        // 上这组断言无对象，换包全链与其余断言不受影响（剥标面归 special-acceptance）。
        if (marked)
        {
            Assert.False(MacAppStub.HasQuarantine(install));
            Assert.All(
                Directory.EnumerateFileSystemEntries(install, "*", SearchOption.AllDirectories),
                entry => Assert.False(MacAppStub.HasQuarantine(entry)));
            // 日志 "quarantine stripped" 证明标确实在过（"not present" 是另一支）。
            Assert.Contains("quarantine stripped", File.ReadAllText(logPath));
        }
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

    // ---- syspkg replay（deb/rpm/alpineapk）垫片端到端腿 ----
    // 全链 POSIX-only：假 bundler-updater/pkexec/sudo/zenity/dpkg/apk 皆为 sh stub，
    // 客户端命令面经 BUNDLER_UPDATER_CMD_<TOOL> env 缝供给 + CommandProbe=_=>null
    // 权威缺席化真实 PATH——零提权真实发生；stub 记 argv 与 --env-file 内容进 rec 件。
    // env 缝是进程全局——恢复与探针复位全收 finally（集成层无并行，见 AssemblyInfo）。

    // bundler-updater 垫片：记 argv；扫到 --env-file 就把件内容与权限位一并落 rec——
    // env 件 0600/会话变量面在它被引导件消费前的形态是可断言的。
    private const string UpdaterStubBody = """
        line=updater
        for a do line="$line $a"; done
        printf '%s\n' "$line" >> "$BUNDLER_STUB_REC"
        prev=
        for a do
          if [ "$prev" = "--env-file" ]; then
            {
              printf 'env{'
              cat "$a"
              printf '}\n'
              stat -c 'env-mode %a' "$a" 2>/dev/null || stat -f 'env-mode %Lp' "$a" 2>/dev/null || true
            } >> "$BUNDLER_STUB_REC"
          fi
          prev=$a
        done
        exit "${BUNDLER_STUB_RC:-0}"
        """;

    // 账本垫片体把工具名烙进脚本（同名 env 槽多垫片会互踩）——rc 经 BUNDLER_STUB_LEDGER_RC 控。
    private static string LedgerStubBody(string tool) => $$"""
        line='{{tool}}'
        for a do line="$line $a"; done
        printf '%s\n' "$line" >> "$BUNDLER_STUB_REC"
        exit "${BUNDLER_STUB_LEDGER_RC:-0}"
        """;

    private const string PkexecStubBody = """
        line=pkexec
        for a do line="$line $a"; done
        printf '%s\n' "$line" >> "$BUNDLER_STUB_REC"
        exec "$@"
        """;

    // sudo 垫片：剥掉 -S/-p <提示>/\-\- 前缀后 exec 余下命令行（密码行留在管道里）。
    private const string SudoStubBody = """
        line=sudo
        for a do line="$line $a"; done
        printf '%s\n' "$line" >> "$BUNDLER_STUB_REC"
        while [ $# -gt 0 ]; do
          case "$1" in
            -S) shift ;;
            -p) if [ $# -ge 2 ]; then shift 2; else shift; fi ;;
            --) shift; break ;;
            *) break ;;
          esac
        done
        exec "$@"
        """;

    // 问密码垫片：记 argv，向 stdout 吐一行假密码喂 sudo -S。
    private static string AskpassStubBody(string tool) => $$"""
        line='{{tool}}'
        for a do line="$line $a"; done
        printf '%s\n' "$line" >> "$BUNDLER_STUB_REC"
        printf 'stubpass\n'
        """;

    private static string WritePosixStub(string dir, string name, string body)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    // syspkg 装位：身份旁车（pkg 包名入旁车）+ 垫片 bundler-updater；假 pm/提权器
    // 各按 name 落 stub，路径经 BUNDLER_UPDATER_CMD_<TOOL> 缝供给客户端（env 全走
    // EnvScope 记账——进程全局缝的恢复不能漏）。
    private static (string Install, string FeedDir, string Bin, UpdateKeyMaterial Material)
        SetupSyspkgInstall(EnvScope env, string directory, string format, string pkg,
            string[] ledgerTools, params string[] elevationTools)
    {
        var install = Path.Combine(directory, "install");
        Directory.CreateDirectory(install);
        var feedDir = Path.Combine(directory, "feed");
        Directory.CreateDirectory(feedDir);
        var bin = Path.Combine(directory, "bin");
        Directory.CreateDirectory(bin);
        var material = UpdateKeyMaterial.Generate();
        Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
        {
            Format = format,
            Target = TestPlatform.LinuxTarget,
            Channel = "stable",
            FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
            PublicKey = material.PublicPointBase64(),
            Pkg = pkg,
        });
        WritePosixStub(install, "bundler-updater", UpdaterStubBody);
        foreach (var tool in ledgerTools)
        {
            env.Set("BUNDLER_UPDATER_CMD_" + CommandEnvName(tool),
                WritePosixStub(bin, tool, LedgerStubBody(tool)));
        }
        foreach (var tool in elevationTools)
        {
            var body = tool switch
            {
                "pkexec" => PkexecStubBody,
                "sudo" => SudoStubBody,
                _ => AskpassStubBody(tool),
            };
            env.Set("BUNDLER_UPDATER_CMD_" + CommandEnvName(tool),
                WritePosixStub(bin, tool, body));
        }
        return (install, feedDir, bin, material);
    }

    private static string CommandEnvName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            var upper = char.ToUpperInvariant(c);
            builder.Append(char.IsAsciiLetterOrDigit(upper) ? upper : '_');
        }
        return builder.ToString();
    }

    // rec 件按行断言：垫片顺序执行时确定性；管线腿内 zenity/sudo 行序可能并发翻转，
    // 调用方对那两行用 Contains 而非按下标。
    private static string[] WaitRecord(string path, int minLines)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length >= minLines)
                {
                    return lines;
                }
            }
            Thread.Sleep(100);
        }
        Assert.Fail($"record file '{path}' did not reach {minLines} lines");
        return [];
    }

    private static void WaitForGone(string path)
    {
        for (var i = 0; i < 100 && File.Exists(path); i++)
        {
            Thread.Sleep(100);
        }
        Assert.False(File.Exists(path), $"session env file '{path}' must be deleted after launcher exit");
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];

        internal void Set(string name, string? value)
        {
            if (!_previous.ContainsKey(name))
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
            }
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            SyspkgReplay.CommandProbe = null;
        }
    }

    [Fact]
    public async Task Client_SyspkgApply_Deb_PkexecLeg_EndToEnd()
    {
        // deb 全链 pkexec 腿：预检（dpkg -s 账本）→ 新包留存 retention + applied 标记 →
        // pkexec 原地直跑装位 bundler-updater pkg-apply → 客户端拉起即退。
        Assert.SkipUnless(TestPlatform.IsLinux, "syspkg replay shim legs are POSIX-only");
        var directory = Ws.Combine("syspkg-deb-pkexec");
        Directory.CreateDirectory(directory);
        using var env = new EnvScope();
        // rec 件落原生 POSIX 临时区：管线腿里问密码器/sudo 并发 `>>` 追加，
        // DrvFs（/mnt/*）上 O_APPEND 语义不可靠会撕裂行——rec 属测试划痕与工作区无关。
        var rec = Path.Combine(
            Path.GetTempPath(), "bundler-syspkg-rec-" + Guid.NewGuid().ToString("N") + ".txt");
        env.Set("BUNDLER_STUB_REC", rec);
        env.Set("DISPLAY", ":99");
        env.Set("XAUTHORITY", "/tmp/xa-file");
        var (install, feedDir, _, material) = SetupSyspkgInstall(
            env, directory, "deb", "myapp", ["apt-get", "dpkg"], "pkexec", "sudo", "zenity");
        // CommandProbe 权威化全缺席——真实 PATH 上的 apt-get/pkexec 不得参与解析。
        SyspkgReplay.CommandProbe = _ => null;

        var artifact = Path.Combine(feedDir, "myapp_2.0-1_amd64.deb");
        File.WriteAllText(artifact, "pkg-v2-bytes");
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "deb",
            Url = "myapp_2.0-1_amd64.deb", File = "myapp_2.0-1_amd64.deb",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
        });

        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        // env 件 0600 收权只在原生 POSIX fs 上有语义——DrvFs（/mnt/*）chmod 静默无操作，
        // 故 StagingRoot 指原生临时区；retention/app 仍在工作区内。
        var staging = Path.Combine(
            Path.GetTempPath(), "bundler-syspkg-staging-" + Guid.NewGuid().ToString("N"));
        var retention = Path.Combine(directory, "retention");
        var app = WritePosixStub(directory, "app", "printf 'app-restarted\\n'\n");
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = staging,
            AppPath = app,
            RollbackBackupDirectory = retention,
        });
        Assert.True(process.WaitForExit(60_000), "pkg-apply launcher did not exit in 60s");
        Assert.Equal(0, process.ExitCode);

        var retained = Path.Combine(retention, "myapp_2.0-1_amd64.deb");
        var lines = WaitRecord(rec, 3);
        // rec[0]=账本查询（提权链之前）；rec[1]=pkexec 垫片见到的逐字 argv。
        Assert.Equal("dpkg -s myapp", lines[0]);
        Assert.StartsWith(
            $"pkexec {install}/bundler-updater pkg-apply --pkg {retained} " +
            $"--format deb --app {Path.GetFullPath(app)} --env-file {staging}/pkg-",
            lines[1]);
        Assert.EndsWith("/session.env", lines[1]);
        Assert.StartsWith(
            $"updater pkg-apply --pkg {retained} --format deb --app {Path.GetFullPath(app)} --env-file ",
            lines[2]);
        // env 件内容随 rec 回收：HOME 随行 + 显式注入的会话键。
        var text = File.ReadAllText(rec);
        Assert.Contains("DISPLAY=:99", text);
        Assert.Contains("XAUTHORITY=/tmp/xa-file", text);
        Assert.Contains("env-mode 600", text);
        // 留存件 + applied 标记。
        Assert.Equal("pkg-v2-bytes", File.ReadAllText(retained));
        Assert.Equal("myapp_2.0-1_amd64.deb",
            File.ReadAllText(Path.Combine(retention, "applied")).Trim());
        // env 件用完即删（Exited 钩）——拉起即退语义下的清理面。
        var envFile = lines[2][(lines[2].IndexOf("--env-file ") + "--env-file ".Length)..];
        WaitForGone(envFile);
    }

    [Fact]
    public async Task Client_SyspkgApply_Deb_AskpassPipeline_EndToEnd()
    {
        // pkexec 缺席 → zenity --password | sudo -S -p '' -- <verb> 管线腿：
        // 密码行不过本进程，sh -c 包整条管线，末级 rc 即拉起 rc。
        // 管线腿有图形会话门槛——显式注 DISPLAY，headless CI 宿主也选得到。
        Assert.SkipUnless(TestPlatform.IsLinux, "syspkg replay shim legs are POSIX-only");
        var directory = Ws.Combine("syspkg-deb-askpass");
        Directory.CreateDirectory(directory);
        using var env = new EnvScope();
        // rec 件落原生 POSIX 临时区：管线腿里问密码器/sudo 并发 `>>` 追加，
        // DrvFs（/mnt/*）上 O_APPEND 语义不可靠会撕裂行——rec 属测试划痕与工作区无关。
        var rec = Path.Combine(
            Path.GetTempPath(), "bundler-syspkg-rec-" + Guid.NewGuid().ToString("N") + ".txt");
        env.Set("BUNDLER_STUB_REC", rec);
        env.Set("DISPLAY", ":99");
        var (install, feedDir, _, material) = SetupSyspkgInstall(
            env, directory, "deb", "myapp", ["dpkg"], "zenity", "sudo");
        SyspkgReplay.CommandProbe = _ => null;

        var artifact = Path.Combine(feedDir, "myapp_2.0-1_amd64.deb");
        File.WriteAllText(artifact, "pkg-v2-bytes");
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "deb",
            Url = "myapp_2.0-1_amd64.deb", File = "myapp_2.0-1_amd64.deb",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
        });

        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var retention = Path.Combine(directory, "retention");
        var app = WritePosixStub(directory, "app", "true\n");
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            AppPath = app,
            RollbackBackupDirectory = retention,
        });
        Assert.True(process.WaitForExit(60_000));
        Assert.Equal(0, process.ExitCode);

        var retained = Path.Combine(retention, "myapp_2.0-1_amd64.deb");
        var text = string.Join('\n', WaitRecord(rec, 4));
        Assert.Contains("dpkg -s myapp\n", text);
        Assert.Contains("zenity --password\n", text);
        // sudo 垫片记的是它自己看到的 argv：-S -p '' -- 前缀 + verb argv 逐字。
        Assert.Contains(
            $"sudo -S -p  -- {install}/bundler-updater pkg-apply --pkg {retained} " +
            $"--format deb --app {Path.GetFullPath(app)}", text);
        Assert.Contains($"updater pkg-apply --pkg {retained}", text);
    }

    [Fact]
    public async Task Client_SyspkgApply_Deb_BareSudo_EndToEnd()
    {
        // pkexec/问密码器全缺席只余 sudo → 末级腿（tty 密码提示场景）。
        Assert.SkipUnless(TestPlatform.IsLinux, "syspkg replay shim legs are POSIX-only");
        var directory = Ws.Combine("syspkg-deb-baresudo");
        Directory.CreateDirectory(directory);
        using var env = new EnvScope();
        // rec 件落原生 POSIX 临时区：管线腿里问密码器/sudo 并发 `>>` 追加，
        // DrvFs（/mnt/*）上 O_APPEND 语义不可靠会撕裂行——rec 属测试划痕与工作区无关。
        var rec = Path.Combine(
            Path.GetTempPath(), "bundler-syspkg-rec-" + Guid.NewGuid().ToString("N") + ".txt");
        env.Set("BUNDLER_STUB_REC", rec);
        var (install, feedDir, _, material) = SetupSyspkgInstall(
            env, directory, "deb", "myapp", ["dpkg"], "sudo");
        SyspkgReplay.CommandProbe = _ => null;

        var artifact = Path.Combine(feedDir, "myapp_2.0-1_amd64.deb");
        File.WriteAllText(artifact, "pkg-v2-bytes");
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "deb",
            Url = "myapp_2.0-1_amd64.deb", File = "myapp_2.0-1_amd64.deb",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
        });

        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var retention = Path.Combine(directory, "retention");
        var app = WritePosixStub(directory, "app", "true\n");
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            AppPath = app,
            RollbackBackupDirectory = retention,
        });
        Assert.True(process.WaitForExit(60_000));
        Assert.Equal(0, process.ExitCode);
        var text = string.Join('\n', WaitRecord(rec, 3));
        var retained = Path.Combine(retention, "myapp_2.0-1_amd64.deb");
        Assert.Contains(
            $"sudo -- {install}/bundler-updater pkg-apply --pkg {retained} " +
            $"--format deb --app {Path.GetFullPath(app)}", text);
        Assert.Contains($"updater pkg-apply --pkg {retained}", text);
    }

    [Fact]
    public async Task Client_SyspkgApply_AlpineApk_MapsToApkVerb()
    {
        // 身份 alpineapk → 动词 --format apk + 账本查询 apk info -e。
        Assert.SkipUnless(TestPlatform.IsLinux, "syspkg replay shim legs are POSIX-only");
        var directory = Ws.Combine("syspkg-apk");
        Directory.CreateDirectory(directory);
        using var env = new EnvScope();
        // rec 件落原生 POSIX 临时区：管线腿里问密码器/sudo 并发 `>>` 追加，
        // DrvFs（/mnt/*）上 O_APPEND 语义不可靠会撕裂行——rec 属测试划痕与工作区无关。
        var rec = Path.Combine(
            Path.GetTempPath(), "bundler-syspkg-rec-" + Guid.NewGuid().ToString("N") + ".txt");
        env.Set("BUNDLER_STUB_REC", rec);
        var (install, feedDir, _, material) = SetupSyspkgInstall(
            env, directory, "alpineapk", "myapp", ["apk"], "pkexec");
        SyspkgReplay.CommandProbe = _ => null;

        var artifact = Path.Combine(feedDir, "myapp-2.0.apk");
        File.WriteAllText(artifact, "apk-v2-bytes");
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "alpineapk",
            Url = "myapp-2.0.apk", File = "myapp-2.0.apk",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
        });

        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var retention = Path.Combine(directory, "retention");
        var app = WritePosixStub(directory, "app", "true\n");
        var process = client.Apply(info, downloaded, new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            AppPath = app,
            RollbackBackupDirectory = retention,
        });
        Assert.True(process.WaitForExit(60_000));
        Assert.Equal(0, process.ExitCode);
        var text = string.Join('\n', WaitRecord(rec, 3));
        Assert.Contains("apk info -e myapp", text);
        Assert.Contains("--format apk", text);
        Assert.Contains($"--pkg {Path.Combine(retention, "myapp-2.0.apk")}", text);
    }

    [Fact]
    public async Task Client_SyspkgApply_PrecheckRefusals_AreDeterministic()
    {
        // 预检三件套逐项缺席面（垫片供给可控）：
        //   无包管理器前端 → 无账本工具 → 账本查询失败 → 无提权腿——全部 UpdateException。
        Assert.SkipUnless(TestPlatform.IsLinux, "syspkg replay shim legs are POSIX-only");
        var directory = Ws.Combine("syspkg-preflight");
        Directory.CreateDirectory(directory);
        using var env = new EnvScope();
        // rec 件落原生 POSIX 临时区：管线腿里问密码器/sudo 并发 `>>` 追加，
        // DrvFs（/mnt/*）上 O_APPEND 语义不可靠会撕裂行——rec 属测试划痕与工作区无关。
        var rec = Path.Combine(
            Path.GetTempPath(), "bundler-syspkg-rec-" + Guid.NewGuid().ToString("N") + ".txt");
        env.Set("BUNDLER_STUB_REC", rec);
        var (install, feedDir, _, material) = SetupSyspkgInstall(env, directory, "deb", "myapp", []);
        SyspkgReplay.CommandProbe = _ => null;
        var artifact = Path.Combine(feedDir, "myapp_2.0-1_amd64.deb");
        File.WriteAllText(artifact, "pkg-v2-bytes");
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "deb",
            Url = "myapp_2.0-1_amd64.deb", File = "myapp_2.0-1_amd64.deb",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
        });
        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var options = new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            AppPath = Path.Combine(directory, "app"),
            RollbackBackupDirectory = Path.Combine(directory, "retention"),
        };

        // ①+②缺席面之一：env 缝不给任何包管理器 → "不参与更新"。
        var noPm = Assert.Throws<UpdateException>(() => client.Apply(info, downloaded, options));
        Assert.Contains("does not participate in updates", noPm.Message);

        // ② 另一支：apt-get 在场但账本工具 dpkg 缺席。
        env.Set("BUNDLER_UPDATER_CMD_APT_GET",
            WritePosixStub(Path.Combine(directory, "bin"), "apt-get", LedgerStubBody("apt-get")));
        var noLedger = Assert.Throws<UpdateException>(() => client.Apply(info, downloaded, options));
        Assert.Contains("ledger tool 'dpkg' not found", noLedger.Message);

        // ③ 账本查询返非零 → "未登记"拒绝（不空转）。
        var bin = Path.Combine(directory, "bin");
        env.Set("BUNDLER_UPDATER_CMD_DPKG",
            WritePosixStub(bin, "dpkg", LedgerStubBody("dpkg")));
        env.Set("BUNDLER_STUB_LEDGER_RC", "1");
        var notOwned = Assert.Throws<UpdateException>(() => client.Apply(info, downloaded, options));
        Assert.Contains("not registered in the dpkg ledger", notOwned.Message);
        env.Set("BUNDLER_STUB_LEDGER_RC", null);

        // 提权腿全缺席（pkexec/zenity/kdialog/sudo 皆无）→ 确定性拒绝。
        var noElevation = Assert.Throws<UpdateException>(() => client.Apply(info, downloaded, options));
        Assert.Contains("privilege escalation", noElevation.Message);
        // 留存件不得污染——拒绝发生于留存动作之前。
        Assert.False(Directory.Exists(Path.Combine(directory, "retention")));
    }

    [Fact]
    public async Task Client_SyspkgRollback_ReplaysRetainedOldPackage()
    {
        // apply 留存新包（applied=新）→ rollback 拣 applied 之外最新件（旧包）重放
        // 并带 --rollback；applied 标记翻回旧包名（flip-flop 语义）。
        Assert.SkipUnless(TestPlatform.IsLinux, "syspkg replay shim legs are POSIX-only");
        var directory = Ws.Combine("syspkg-rollback");
        Directory.CreateDirectory(directory);
        using var env = new EnvScope();
        // rec 件落原生 POSIX 临时区：管线腿里问密码器/sudo 并发 `>>` 追加，
        // DrvFs（/mnt/*）上 O_APPEND 语义不可靠会撕裂行——rec 属测试划痕与工作区无关。
        var rec = Path.Combine(
            Path.GetTempPath(), "bundler-syspkg-rec-" + Guid.NewGuid().ToString("N") + ".txt");
        env.Set("BUNDLER_STUB_REC", rec);
        var (install, feedDir, _, material) = SetupSyspkgInstall(
            env, directory, "deb", "myapp", ["dpkg"], "pkexec");
        SyspkgReplay.CommandProbe = _ => null;

        var artifact = Path.Combine(feedDir, "myapp_2.0-1_amd64.deb");
        File.WriteAllText(artifact, "pkg-v2-bytes");
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = TestPlatform.LinuxTarget, Format = "deb",
            Url = "myapp_2.0-1_amd64.deb", File = "myapp_2.0-1_amd64.deb",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
        });
        var retention = Path.Combine(directory, "retention");
        var app = WritePosixStub(directory, "app", "true\n");
        var options = new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            AppPath = app,
            RollbackBackupDirectory = retention,
        };

        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var info = (await client.CheckForUpdateAsync())!;
        var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
        client.Verify(info, downloaded);
        var apply = client.Apply(info, downloaded, options);
        Assert.True(apply.WaitForExit(60_000));
        Assert.Equal(0, apply.ExitCode);
        // 模拟"上一次 Bundler 链更新留存过旧包"——retention 里种旧件；
        // applied 仍指新件（刚 apply 写入）。
        var oldPkg = Path.Combine(retention, "myapp_1.0-1_amd64.deb");
        File.WriteAllText(oldPkg, "pkg-v1-bytes");
        File.SetLastWriteTimeUtc(oldPkg, DateTime.UtcNow.AddMinutes(-10));

        File.Delete(rec);
        var rollback = client.Rollback(options);
        Assert.True(rollback.WaitForExit(60_000));
        Assert.Equal(0, rollback.ExitCode);
        var lines = WaitRecord(rec, 3);
        // pkexec/updater 行：旧包 + --rollback 尾位；--env-file 夹在 --app 与
        // --rollback 之间，故按行断言首/尾而不钉中间段。
        Assert.StartsWith(
            $"pkexec {install}/bundler-updater pkg-apply --pkg {oldPkg} " +
            $"--format deb --app {Path.GetFullPath(app)}", lines[1]);
        Assert.EndsWith("--rollback", lines[1]);
        Assert.StartsWith($"updater pkg-apply --pkg {oldPkg}", lines[2]);
        Assert.EndsWith("--rollback", lines[2]);
        Assert.Equal("myapp_1.0-1_amd64.deb",
            File.ReadAllText(Path.Combine(retention, "applied")).Trim());
    }

    [Fact]
    public async Task Client_SyspkgRollback_NoRetainedPackage_Refuses()
    {
        // 无留存件 → 确定性拒绝不空转（pre-ledger 断言顺带覆盖回滚也走预检）。
        Assert.SkipUnless(TestPlatform.IsLinux, "syspkg replay shim legs are POSIX-only");
        var directory = Ws.Combine("syspkg-rollback-empty");
        Directory.CreateDirectory(directory);
        using var env = new EnvScope();
        env.Set("BUNDLER_STUB_REC",
            Path.Combine(Path.GetTempPath(), "bundler-syspkg-rec-" + Guid.NewGuid().ToString("N") + ".txt"));
        var (install, _, _, _) = SetupSyspkgInstall(env, directory, "deb", "myapp", ["dpkg"], "pkexec");
        SyspkgReplay.CommandProbe = _ => null;
        var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
        var exception = Assert.Throws<UpdateException>(() => client.Rollback(new ApplyOptions
        {
            StagingRoot = Path.Combine(directory, "staging"),
            AppPath = Path.Combine(directory, "app"),
            RollbackBackupDirectory = Path.Combine(directory, "retention"),
        }));
        Assert.Contains("no retained package", exception.Message);
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
