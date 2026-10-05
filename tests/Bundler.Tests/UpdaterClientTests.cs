using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using DotNet.Bundler.Update;
using DotNet.Bundler.Updater;
using Protocol = DotNet.Bundler.Updater.Protocol;

/// <summary>UPDATE-4 应用内更新库测试：版本比较、feed 选择、file:// 下载、签名验、离线端到端。</summary>
public static class UpdaterClientTests
{
    [Fact]
    static void Version_ComparesNumericAndPrerelease()
    {
        Assert.True(UpdateVersion.TryParse("1.2.3", out var a));
        Assert.True(UpdateVersion.TryParse("1.10.0", out var b));
        Assert.True(UpdateVersion.TryParse("1.2.3-beta", out var c));
        Assert.True(UpdateVersion.TryParse("1.2.3+build9", out var d));

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.Equal(0, a.CompareTo(d)); // +build 元数据不参与比较
        Assert.True(c.CompareTo(a) < 0); // 预发布 < 正式
        Assert.False(UpdateVersion.TryParse("x.y", out _));
        Assert.False(UpdateVersion.TryParse("", out _));
    }

    [Fact]
    static void Client_FromInstallDirectory_RequiresSidecar()
    {
        var directory = CreateTempDirectory();
        try
        {
            var ex = Assert.Throws<UpdateException>(() =>
                UpdateClient.FromInstallDirectory(directory, "1.0.0"));
            Assert.Contains("bundler-update.json", ex.Message);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static async Task Client_CheckSelectsMatchingArtifact_AndSkipsOlder()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            Directory.CreateDirectory(install);
            var feedDir = Path.Combine(directory, "feed");
            Directory.CreateDirectory(feedDir);
            var material = Protocol.UpdateKeyMaterial.Generate();

            Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
            {
                Format = "zip",
                RuntimeIdentifier = "linux-x64",
                Channel = "stable",
                FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
                PublicKey = material.PublicPointBase64(),
            });

            // feed：win-x64 不相关件 + linux-x64 zip 匹配件（v2.0.0）
            WriteFeed(feedDir, "stable", "2.0.0",
                new Protocol.UpdateFeedArtifact
                {
                    RuntimeIdentifier = "win-x64", Format = "zip",
                    Url = "other.zip", Sha256 = "0", Size = 1, Signature = "AA=="
                },
                new Protocol.UpdateFeedArtifact
                {
                    RuntimeIdentifier = "linux-x64", Format = "zip",
                    Url = "app-2.0.0-linux-x64.zip", Sha256 = "0", Size = 1, Signature = "AA=="
                });

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = await client.CheckForUpdateAsync();
            Assert.NotNull(info);
            Assert.Equal("2.0.0", info!.Feed.Version);
            Assert.Equal("app-2.0.0-linux-x64.zip", info.Artifact.Url);

            // 当前版本已最新 → null
            var current = UpdateClient.FromInstallDirectory(install, "2.0.0");
            Assert.Null(await current.CheckForUpdateAsync());

            // 更高版本但 rid 不匹配 → null（feed v3 只发 win）
            WriteFeed(feedDir, "stable", "3.0.0",
                new Protocol.UpdateFeedArtifact
                {
                    RuntimeIdentifier = "win-x64", Format = "msi",
                    Url = "x.msi", Sha256 = "0", Size = 1, Signature = "AA=="
                });
            var client2 = UpdateClient.FromInstallDirectory(install, "1.0.0");
            Assert.Null(await client2.CheckForUpdateAsync());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static async Task Client_DownloadsLocalFeed_AndVerifiesSha256()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var artifactFile = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2-content");
            var sha = Sha256Hex(artifactFile);
            var sig = Convert.ToBase64String(EcdsaSigner.SignFile(artifactFile, material));
            WriteFeed(feedDir, "stable", "2.0.0", new Protocol.UpdateFeedArtifact
            {
                RuntimeIdentifier = "linux-x64", Format = "zip",
                Url = "app-2.0.0.zip", File = "app-2.0.0.zip",
                Sha256 = sha, Size = new FileInfo(artifactFile).Length, Signature = sig,
            });

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync())!;
            var downloadDir = Path.Combine(directory, "dl");
            var downloaded = await client.DownloadAsync(info, downloadDir);
            Assert.Equal(new FileInfo(artifactFile).Length, new FileInfo(downloaded).Length);

            client.Verify(info, downloaded); // 不抛即过

            // 篡改下载件 → 验签拒绝
            var tampered = Path.Combine(directory, "tampered.zip");
            File.Copy(downloaded, tampered);
            var bytes = File.ReadAllBytes(tampered);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(tampered, bytes);
            Assert.Throws<UpdateException>(() => client.Verify(info, tampered));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static async Task Client_UpdateAsync_EndToEnd_Linux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return; // 本腿只在 linux 宿主跑（引导二进制为 linux-x64 件）
        }

        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            // 引导件：安装目录内注入真实 linux-x64 AOT 二进制。
            var bootstrapper = RepoPath("src/Bundler.Updater.Bootstrap/tools/linux-x64/bundler-updater");
            File.Copy(bootstrapper, Path.Combine(install, "bundler-updater"));

            // v2 载荷 zip：单顶层目录 app-v2/app="v2" + 引导件也随包（真实发布形态）。
            var artifact = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2",
                extraEntry: ("bundler-updater", File.ReadAllBytes(bootstrapper)));
            var sha = Sha256Hex(artifact);
            var sig = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material));
            WriteFeed(feedDir, "stable", "2.0.0", new Protocol.UpdateFeedArtifact
            {
                RuntimeIdentifier = "linux-x64", Format = "zip",
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
            });
            Assert.True(process.WaitForExit(90_000), "bootstrapper did not exit in 90s");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "app")));
            Assert.Equal("v1", File.ReadAllText(
                Path.Combine(install + ".bundler-backup", "app")));
            Assert.False(File.Exists(install + ".bundler-swap"));

            // 回滚钩子：备份倒回 → 安装目录回到 v1。
            var rollback = client.Rollback(new ApplyOptions
            {
                StagingRoot = Path.Combine(directory, "staging-rb"),
            });
            Assert.True(rollback.WaitForExit(60_000));
            Assert.Equal(0, rollback.ExitCode);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(install, "app")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static async Task Client_UpdateAsync_UsesShellFallback()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            File.WriteAllText(Path.Combine(install, "app"), "v1");
            // 只有 .sh 降级件，没有 AOT 二进制。
            var script = RepoPath("src/Bundler.Updater.Bootstrap/tools/posix/bundler-updater.sh");
            File.Copy(script, Path.Combine(install, "bundler-updater.sh"));

            var artifact = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2");
            var sig = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material));
            WriteFeed(feedDir, "stable", "2.0.0", new Protocol.UpdateFeedArtifact
            {
                RuntimeIdentifier = "linux-x64", Format = "zip",
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
        finally
        {
            Cleanup(directory);
        }
    }

    // ---- helpers ----

    static string InstallWithSidecar(
        string root, out DotNet.Bundler.Core.Update.UpdateKeyMaterial material, out string feedDir)
    {
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        feedDir = Path.Combine(root, "feed");
        Directory.CreateDirectory(feedDir);
        material = DotNet.Bundler.Core.Update.UpdateKeyMaterial.Generate();
        // FeedUrl = 清单文件位置（本地路径或 file:// 均可）；产物按同目录解析。
        Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
        {
            Format = "zip",
            RuntimeIdentifier = "linux-x64",
            Channel = "stable",
            FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
            PublicKey = material.PublicPointBase64(),
        });
        return install;
    }

    static void WriteFeed(
        string dir, string channel, string version, params Protocol.UpdateFeedArtifact[] artifacts)
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

    static string RepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Bundler.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bundler-updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
