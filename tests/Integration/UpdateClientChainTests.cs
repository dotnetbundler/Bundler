// UpdateClient.Apply 真链腿：UpdateApplier 拉起真实引导件子进程做三段式换包——
// 原 UpdaterClientTests 的 Linux 真环境腿（Unit 层零子进程规则不收）。
// 覆盖：文件级安装单元（AppImage）换包+保留位备份+回滚、目录级 zip 端到端、
// 无 AOT 二进制时 /bin/sh 降级脚本链。
using System.IO.Compression;
using System.Runtime.Serialization.Json;
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
            Target = "linux-x86_64", Format = "appimage",
            Url = "MyApp-2.0.0.AppImage", File = "MyApp-2.0.0.AppImage",
            Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
        });

        // 旁车在镜像内部（挂载内可读）→ 消费侧走 FromIdentity + install=文件路径。
        var client = UpdateClient.FromIdentity(new Protocol.UpdateInstallIdentity
        {
            Format = "appimage",
            Target = "linux-x86_64",
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
            BootstrapperPath = Path.Combine(RepositoryLayout.Root,
                "src", "Bundler.Updater.Bootstrap", "tools",
                TestPlatform.LinuxTarget, "bundler-updater"),
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
            BootstrapperPath = Path.Combine(RepositoryLayout.Root,
                "src", "Bundler.Updater.Bootstrap", "tools",
                TestPlatform.LinuxTarget, "bundler-updater"),
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
        // 引导件：安装目录内注入真实 linux-<arch> AOT 二进制。
        var bootstrapper = Path.Combine(RepositoryLayout.Root,
            "src", "Bundler.Updater.Bootstrap", "tools",
            TestPlatform.LinuxTarget, "bundler-updater");
        File.Copy(bootstrapper, Path.Combine(install, "bundler-updater"));

        // v2 载荷 zip：单顶层目录 app-v2/app="v2" + 引导件也随包（真实发布形态）。
        var artifact = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2",
            extraEntry: ("bundler-updater", File.ReadAllBytes(bootstrapper)));
        var sha = Sha256Hex(artifact);
        var sig = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material));
        WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
        {
            Target = "linux-x86_64", Format = "zip",
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
            Target = "linux-x86_64", Format = "zip",
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

    static string InstallWithSidecar(
        string root, out UpdateKeyMaterial material, out string feedDir)
    {
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        feedDir = Path.Combine(root, "feed");
        Directory.CreateDirectory(feedDir);
        material = UpdateKeyMaterial.Generate();
        // FeedUrl = 清单文件位置（本地路径或 file:// 均可）；产物按同目录解析。
        Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
        {
            Format = "zip",
            Target = "linux-x86_64",
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
        (string name, byte[] bytes)? extraEntry = null)
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
            if (extraEntry is { } extra)
            {
                var e2 = archive.CreateEntry("app-v2/" + extra.name);
                using var es = e2.Open();
                es.Write(extra.bytes, 0, extra.bytes.Length);
            }
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
