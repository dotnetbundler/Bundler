using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using DotNet.Bundler.Update;
using DotNet.Bundler.Updater;
using Protocol = DotNet.Bundler.Updater.Protocol;

/// <summary>UPDATE-4 应用内更新库测试：版本比较、feed 选择、file:// 下载、签名验、离线端到端。</summary>

// 从旧 tests/Bundler.Tests/UpdaterClientTests.cs 收割：更新客户端（feed 解析、
// 差分下载、签名校验、ustar 提取）全托管；LoopbackFeedServer 为进程内回环服务器。
// 去 spawn：Client_UpdateAsync_AppImage_FileSwap_Linux、Client_UpdateAsync_EndToEnd_Linux、
// Client_UpdateAsync_UsesShellFallback 调 UpdateApplier.Apply 拉起引导进程——
// 去向见 Integration/Manual 层对应文件。
// 逐方法判重已跳过：Version_ComparesNumericAndPrerelease→UpdateVersionTests；
// Client_FromInstallDirectory_ReadsAppContentsSidecar/RequiresSidecar、
// Client_FromIdentity_RequiresFeedIdentity、Client_DownloadsLocalFeed_AndVerifiesSha256、
// Client_DownloadAsync_InsufficientSpace_RefusesBeforeWriting、
// Client_Check_RejectsMissingFeedSignature → UpdateClientTests 同名断言；
// Ustar_Symlink_RestoresLink_OnSuccess → UstarReaderTests.Extract_SymlinkInsideRoot_Restored；
// UpdateApplier_SweepStaleStaging_RemovesOnlyOldGuidDirs → SweepStaleStaging_RemovesOnlyOldKnownPrefixes。
namespace Bundler.UnitTests.Updater;

public sealed class UpdaterClientTests : TempBase
{
        [Fact]
    public void Version_BuildMetadataOnPrerelease_AndNumericSegments()
    {
        // +build 粘到预发布段也须剥离：1.2.3-beta+build2 == 1.2.3-beta+build1。
        Assert.True(UpdateVersion.TryParse("1.2.3-beta+build2", out var e));
        Assert.True(UpdateVersion.TryParse("1.2.3-beta+build1", out var f));
        Assert.Equal(0, e.CompareTo(f));
        Assert.True(UpdateVersion.TryParse("1.2.3+meta-beta", out var g));
        Assert.True(UpdateVersion.TryParse("1.2.3", out var plain));
        Assert.Equal(0, g.CompareTo(plain)); // 1.2.3 与 1.2.3+meta-beta 等同

        // semver 数值段比较：beta.10 > beta.2；短列低于长列；数值段低于字母段。
        Assert.True(UpdateVersion.TryParse("1.0.0-beta.10", out var b10));
        Assert.True(UpdateVersion.TryParse("1.0.0-beta.2", out var b2));
        Assert.True(b10.CompareTo(b2) > 0);
        Assert.True(UpdateVersion.TryParse("1.0.0-alpha", out var alpha));
        Assert.True(UpdateVersion.TryParse("1.0.0-alpha.1", out var alpha1));
        Assert.True(alpha.CompareTo(alpha1) < 0);
        Assert.True(UpdateVersion.TryParse("1.0.0-1", out var numeric));
        Assert.True(numeric.CompareTo(alpha) < 0);
    }

        [Fact]
    public void Client_FromInstallDirectory_ReadsLegacyContentsSidecar()
    {
        // alpha.76 及更早产出的 .app 旁车在 Contents/ 顶层——兜底探测仍须读到。
        var directory = CreateTempDirectory();
        try
        {
            var appDir = Path.Combine(directory, "MyApp.app");
            var contents = Path.Combine(appDir, "Contents");
            Directory.CreateDirectory(contents);
            Protocol.UpdateInstallIdentity.Write(contents, new Protocol.UpdateInstallIdentity
            {
                Format = "app",
                Target = "macos-aarch64",
                FeedUrl = "feed-placeholder",
                PublicKey = "cHVibGljLWtleQ==",
            });

            var client = UpdateClient.FromInstallDirectory(appDir, "1.0.0");
            Assert.NotNull(client);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_SecondDownload_OverwritesDestination()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var artifact = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", _ => "same");
            WriteFeedWithDelta(feedDir, "2.0.0", artifact, material);
            // http 通道下 .part→目标已有文件时 File.Move 曾失败——重复下载必须能落。
            using var server = new LoopbackFeedServer(feedDir);
            var identity = new Protocol.UpdateInstallIdentity
            {
                FeedUrl = server.FeedUrl, Channel = "stable",
                Target = "linux-x86_64", Format = "zip",
                PublicKey = material.PublicPointBase64(),
            };
            var sidecarSerializer = new DataContractJsonSerializer(
                typeof(Protocol.UpdateInstallIdentity),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var stream = File.Create(Path.Combine(install, "bundler-update.json")))
            {
                sidecarSerializer.WriteObject(stream, identity);
            }

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { EnableDelta = false });
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var downloadDir = Path.Combine(directory, "dl");
            var first = await client.DownloadAsync(info, downloadDir, TestContext.Current.CancellationToken);
            var second = await client.DownloadAsync(info, downloadDir, TestContext.Current.CancellationToken);
            Assert.Equal(first, second);
            Assert.True(File.ReadAllBytes(second).SequenceEqual(File.ReadAllBytes(artifact)));
        }
        finally
        {
            Cleanup(directory);
        }
    }

                [Fact]
    public async Task Client_CheckSelectsMatchingArtifact_AndSkipsOlder()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "install");
            Directory.CreateDirectory(install);
            var feedDir = Path.Combine(directory, "feed");
            Directory.CreateDirectory(feedDir);
            var material = DotNet.Bundler.Core.Update.UpdateKeyMaterial.Generate();

            Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
            {
                Format = "zip",
                Target = "linux-x86_64",
                Channel = "stable",
                FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
                PublicKey = material.PublicPointBase64(),
            });

            // feed：windows-x86_64 不相关件 + linux-x86_64 zip 匹配件（v2.0.0）
            WriteFeed(feedDir, "stable", "2.0.0", material,
                new Protocol.UpdateFeedArtifact
                {
                    Target = "windows-x86_64", Format = "zip",
                    Url = "other.zip", File = "other.zip",
                    Sha256 = "0", Size = 1, Signature = "AA=="
                },
                new Protocol.UpdateFeedArtifact
                {
                    Target = "linux-x86_64", Format = "zip",
                    Url = "app-2.0.0-linux-x86_64.zip", File = "app-2.0.0-linux-x86_64.zip",
                    Sha256 = "0", Size = 1, Signature = "AA=="
                });

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = await client.CheckForUpdateAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(info);
            Assert.Equal("2.0.0", info!.Feed.Version);
            Assert.Equal("app-2.0.0-linux-x86_64.zip", info.Artifact.Url);

            // 当前版本已最新 → null
            var current = UpdateClient.FromInstallDirectory(install, "2.0.0");
            Assert.Null(await current.CheckForUpdateAsync(TestContext.Current.CancellationToken));

            // 更高版本但 target 不匹配 → null（feed v3 只发 win）
            WriteFeed(feedDir, "stable", "3.0.0", material,
                new Protocol.UpdateFeedArtifact
                {
                    Target = "windows-x86_64", Format = "msi",
                    Url = "x.msi", File = "x.msi", Sha256 = "0", Size = 1, Signature = "AA=="
                });
            var client2 = UpdateClient.FromInstallDirectory(install, "1.0.0");
            Assert.Null(await client2.CheckForUpdateAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(directory);
        }
    }

        [Fact]
    public async Task Client_DownloadsLocalFeed_DecodesEscapedArtifactUrl()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            // 清单 url 字段按 URL 转义存储——本地解析须还原真实文件名（真实触发面=app 运输件名含空格；机制与 format 无关）。
            var artifactFile = WriteZipArtifact(feedDir, "Hello Bundler App.app.zip", "v2-content");
            var sha = Sha256Hex(artifactFile);
            var sig = Convert.ToBase64String(EcdsaSigner.SignFile(artifactFile, material));
            WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
            {
                Target = "linux-x86_64", Format = "zip",
                Url = "Hello%20Bundler%20App.app.zip", File = "Hello Bundler App.app.zip",
                Sha256 = sha, Size = new FileInfo(artifactFile).Length, Signature = sig,
            });

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var downloaded = await client.DownloadAsync(info, Path.Combine(directory, "dl"), TestContext.Current.CancellationToken);
            Assert.Equal("Hello Bundler App.app.zip", Path.GetFileName(downloaded));
            Assert.Equal(new FileInfo(artifactFile).Length, new FileInfo(downloaded).Length);
            client.Verify(info, downloaded);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void BlockMap_ComputesOrderedBlockHashes()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "blob.bin");
            var blockSize = Protocol.UpdateBlockMap.DefaultBlockSize;
            var content = new byte[blockSize + 100]; // 一整块+尾块
            new Random(42).NextBytes(content);
            File.WriteAllBytes(path, content);

            var map = Protocol.UpdateBlockMap.ComputeFile(path);
            Assert.Equal(2, map.Hashes.Count);
            Assert.Equal(blockSize, map.BlockSize);
            Assert.Equal(content.Length, map.FileSize);
            Assert.Equal(
                Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(
                        content.AsSpan(0, blockSize).ToArray())),
                map.Hashes[0]);
            // 同内容不同分块大小 → 不同表（blockSize 进契约）。
            var finer = Protocol.UpdateBlockMap.ComputeFile(path, 1024);
            Assert.True(finer.Hashes.Count > 2);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_UsesDelta_WhenCacheMatches()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var downloadDir = Path.Combine(directory, "dl");
            var log = new List<string>();

            // v1：先全量下载一次，使 .bundler-cache 有旧件。
            var v1 = WriteDeltaArtifact(feedDir, "app-1.0.0.bin", block =>
                block switch { 0 => "head-v1", 1 => "body-same", 2 => "tail-v1", _ => "?" });
            WriteFeedWithDelta(feedDir, "1.0.0", v1, material);
            var client1 = UpdateClient.FromInstallDirectory(install, "0.9.0",
                new UpdateClientOptions { Log = log.Add });
            var info1 = (await client1.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path1 = await client1.DownloadAsync(info1, downloadDir, TestContext.Current.CancellationToken);
            Assert.Contains(log, l => l.Contains("full download") || l.Contains("no delta source"));
            client1.Verify(info1, path1); // 差分缓存只收验过签的件

            // v2：块 0/1 不变（哈希命中）、块 2 变化——差分应只拉一块。
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", block =>
                block switch { 0 => "head-v1", 1 => "body-same", 2 => "tail-v2", _ => "?" });
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);
            var client2 = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { Log = log.Add });
            var info2 = (await client2.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path2 = await client2.DownloadAsync(info2, downloadDir, TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path2).SequenceEqual(File.ReadAllBytes(v2)));
            Assert.Contains(log, l => l.Contains("delta applied"));
            // 复用率成立：旧件有 2 块命中（64KiB 块表，块内容按 64KiB 填充）。
            Assert.Contains(log, l => l.Contains("B reused"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_FallsBack_WhenCacheCorrupt()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", _ => "same");
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);
            // 缓存里放一份与 feed 无关的脏件——块哈希全不匹配，差分正常执行但全走拉取，
            // 最终 sha256 兜底仍应产出正确文件。
            var cacheDir = install.TrimEnd('/', '\\') + ".bundler-cache";
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "artifact.bin"),
                new byte[Protocol.UpdateBlockMap.DefaultBlockSize]);

            var log = new List<string>();
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { Log = log.Add });
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path = await client.DownloadAsync(info, Path.Combine(directory, "dl"), TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            client.Verify(info, path);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_MissingLocalArtifact_WrappedAsUpdateException()
    {
        // 归一壳断言：本地腿 File.Copy 的 FileNotFoundException 与 http 腿
        // HttpRequestException 同归 UpdateException——"抓取/IO/HTTP 全归
        // UpdateException"契约的可区分断言（旧实现会漏裸 FileNotFoundException）。
        // 消息含件名的断言是刻意的：类型归一之外还要钉住"失败的是哪个件"——
        // 归一消息把 location 全路径带出来，不丢诊断面。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
            {
                Target = "linux-x86_64", Format = "zip",
                Url = "missing-2.0.0.zip", File = "missing-2.0.0.zip",
                Sha256 = "0", Size = 1, Signature = "AA==",
            });
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;

            var exception = await Assert.ThrowsAsync<UpdateException>(() =>
                client.DownloadAsync(info, Path.Combine(directory, "dl"),
                    TestContext.Current.CancellationToken));
            Assert.Contains("missing-2.0.0.zip", exception.Message);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_UsesDelta_OverHttp()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            // v1 全量建立缓存。
            var v1 = WriteDeltaArtifact(feedDir, "app-1.0.0.bin", block =>
                block switch { 0 => "head", 1 => "body-same", 2 => "tail-v1", _ => "?" });
            WriteFeedWithDelta(feedDir, "1.0.0", v1, material);
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", block =>
                block switch { 0 => "head", 1 => "body-same", 2 => "tail-v2", _ => "?" });
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);

            // 侧车 feedUrl 指向回环 http——feed/制品/块表全走 http 通道。
            using var server = new LoopbackFeedServer(feedDir);
            var identity = new Protocol.UpdateInstallIdentity
            {
                FeedUrl = server.FeedUrl, Channel = "stable",
                Target = "linux-x86_64", Format = "zip",
                PublicKey = material.PublicPointBase64(),
            };
            var sidecarSerializer = new DataContractJsonSerializer(
                typeof(Protocol.UpdateInstallIdentity),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var stream = File.Create(Path.Combine(install, "bundler-update.json")))
            {
                sidecarSerializer.WriteObject(stream, identity);
            }

            // 铺 v1 缓存（等价于上一轮全量下载后的 .bundler-cache）。
            var cacheDir = install.TrimEnd('/', '\\') + ".bundler-cache";
            Directory.CreateDirectory(cacheDir);
            File.Copy(v1, Path.Combine(cacheDir, "artifact.bin"));

            var log = new List<string>();
            var ranged = 0;
            server.OnRange = () => ranged++;
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { Log = log.Add });
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path = await client.DownloadAsync(info, Path.Combine(directory, "dl"), TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            Assert.Contains(log, l => l.Contains("delta applied"));
            Assert.True(ranged > 0); // 缺失块确实走了 HTTP Range
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_FallsBack_WhenServerRefusesRange()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v1 = WriteDeltaArtifact(feedDir, "app-1.0.0.bin", block =>
                block switch { 0 => "head", 1 => "body-same", 2 => "tail-v1", _ => "?" });
            WriteFeedWithDelta(feedDir, "1.0.0", v1, material);
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", block =>
                block switch { 0 => "head", 1 => "body-same", 2 => "tail-v2", _ => "?" });
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);

            // 服务端不认 Range 恒回 200 整档——差分应放弃回落全量，不得抛错留坏件。
            using var server = new LoopbackFeedServer(feedDir) { HonorRange = false };
            var identity = new Protocol.UpdateInstallIdentity
            {
                FeedUrl = server.FeedUrl, Channel = "stable",
                Target = "linux-x86_64", Format = "zip",
                PublicKey = material.PublicPointBase64(),
            };
            var sidecarSerializer = new DataContractJsonSerializer(
                typeof(Protocol.UpdateInstallIdentity),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var stream = File.Create(Path.Combine(install, "bundler-update.json")))
            {
                sidecarSerializer.WriteObject(stream, identity);
            }

            var cacheDir = install.TrimEnd('/', '\\') + ".bundler-cache";
            Directory.CreateDirectory(cacheDir);
            File.Copy(v1, Path.Combine(cacheDir, "artifact.bin"));

            var log = new List<string>();
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { Log = log.Add });
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path = await client.DownloadAsync(info, Path.Combine(directory, "dl"), TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            Assert.DoesNotContain(log, l => l.Contains("delta applied"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Client_FromIdentity_NullFeedUrl_ThrowsUpdateException()
    {
        // R2-N5：旁车 feedUrl 缺位曾沿 FeedUrl.Length 走空引用——必须裹成 UpdateException。
        var material = DotNet.Bundler.Core.Update.UpdateKeyMaterial.Generate();
        var exception = Assert.Throws<UpdateException>(() =>
            UpdateClient.FromIdentity(new Protocol.UpdateInstallIdentity
            {
                Format = "zip",
                Target = "linux-x86_64",
                Channel = "stable",
                FeedUrl = null!,
                PublicKey = material.PublicPointBase64(),
            }, Temp("idle-install").FullName, "1.0.0"));
        Assert.Contains("feed url", exception.Message);
    }

    [Fact]
    public async Task Client_Check_BadPublicKey_ThrowsUpdateException()
    {
        // R2-N5：旁车公钥非 base64 曾让 FromPublicPoint 的 FormatException 逃逸——
        // feed 拉取腿须裹成 UpdateException。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            WriteFeed(feedDir, "stable", "2.0.0", material);
            Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
            {
                Format = "zip",
                Target = "linux-x86_64",
                Channel = "stable",
                FeedUrl = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable")),
                PublicKey = "%%%not-a-key",
            });

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            await Assert.ThrowsAsync<UpdateException>(() => client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Client_Verify_NonBase64Signature_ThrowsUpdateException()
    {
        // R2-N5：工件签名非 base64 曾让 FormatException 逃逸契约——Verify 须裹成 UpdateException。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out _, out _);
            var artifactPath = Path.Combine(directory, "app.bin");
            File.WriteAllText(artifactPath, "payload");
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var exception = Assert.Throws<UpdateException>(() =>
                client.Verify(new UpdateInfo(
                    new Protocol.UpdateFeed { Version = "2.0.0", Channel = "stable" },
                    new Protocol.UpdateFeedArtifact
                    {
                        Target = "linux-x86_64",
                        Format = "zip",
                        Url = "app.bin",
                        File = "app.bin",
                        Signature = "!!!not-base64!!!",
                    }, "app.bin"), artifactPath));
            Assert.Contains("signature", exception.Message);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_ShaMismatch_DeletesPartResidue()
    {
        // R2-N2：sha 校验拒件时 `.part` 残留会让下次下载从脏起点续传——拒件清理须连同抹掉。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v2 = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2");
            WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
            {
                Target = "linux-x86_64",
                Format = "zip",
                Url = "app-2.0.0.zip",
                File = "app-2.0.0.zip",
                Sha256 = new string('0', 64),
                Size = new FileInfo(v2).Length,
                Signature = Convert.ToBase64String(EcdsaSigner.SignFile(v2, material)),
            });

            var server = new LoopbackFeedServer(feedDir);
            Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
            {
                Format = "zip",
                Target = "linux-x86_64",
                Channel = "stable",
                FeedUrl = server.FeedUrl,
                PublicKey = material.PublicPointBase64(),
            });
            var destDir = Path.Combine(directory, "dl");
            Directory.CreateDirectory(destDir);
            var partPath = Path.Combine(destDir, "app-2.0.0.zip.part");
            File.WriteAllBytes(partPath, File.ReadAllBytes(v2).AsSpan(0, 8).ToArray());

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(info, destDir, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(destDir, "app-2.0.0.zip")), "destination must be deleted");
            Assert.False(File.Exists(partPath), "stale .part must be deleted");
            server.Dispose();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_RangeStartMismatch_RestartsFullDownload()
    {
        // R2-N2：206 的 Content-Range 起点若不等于请求偏移，`.part` 前缀错位——
        // 客户端须弃 `.part` 并以无 Range 全档重取，结果须是全件字节。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v2 = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2");
            WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
            {
                Target = "linux-x86_64",
                Format = "zip",
                Url = "app-2.0.0.zip",
                File = "app-2.0.0.zip",
                Sha256 = Sha256Hex(v2),
                Size = new FileInfo(v2).Length,
                Signature = Convert.ToBase64String(EcdsaSigner.SignFile(v2, material)),
            });

            var server = new LoopbackFeedServer(feedDir) { WrongRangeStart = true };
            var fullGets = 0;
            server.OnFullGet = () => fullGets++;
            Protocol.UpdateInstallIdentity.Write(install, new Protocol.UpdateInstallIdentity
            {
                Format = "zip",
                Target = "linux-x86_64",
                Channel = "stable",
                FeedUrl = server.FeedUrl,
                PublicKey = material.PublicPointBase64(),
            });
            var destDir = Path.Combine(directory, "dl");
            Directory.CreateDirectory(destDir);
            var head = File.ReadAllBytes(v2).AsSpan(0, 32).ToArray();
            File.WriteAllBytes(Path.Combine(destDir, "app-2.0.0.zip.part"), head);

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path = await client.DownloadAsync(info, destDir, TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            Assert.True(fullGets > 0, "mismatched Content-Range must fall back to a full GET");
            client.Verify(info, path);
            server.Dispose();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_DeltaSuccess_ClearsStalePart()
    {
        // R2-N2 另半边：增量腿成功时 `.part` 残留曾留着——与拒件清理对称抹除。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v1 = WriteDeltaArtifact(feedDir, "app-1.0.0.bin", _ => "v1");
            WriteFeedWithDelta(feedDir, "1.0.0", v1, material);
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", _ => "v2");
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);

            var cacheDir = install.TrimEnd('/', '\\') + ".bundler-cache";
            Directory.CreateDirectory(cacheDir);
            File.Copy(v1, Path.Combine(cacheDir, "artifact.bin"));
            var destDir = Path.Combine(directory, "dl");
            Directory.CreateDirectory(destDir);
            var partPath = Path.Combine(destDir, "app-2.0.0.bin.part");
            File.WriteAllText(partPath, "stale-resume");

            var log = new List<string>();
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { Log = log.Add });
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path = await client.DownloadAsync(info, destDir, TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            Assert.False(File.Exists(partPath), "stale .part must be swept on delta success");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_CorruptBlockMap_FallsBackToFull()
    {
        // R2-N4：blockmap 拉取/解析失败曾是逃逸差分回落的硬 UpdateException——
        // 现须与 Range 失败同型回落全量，不得坏整体下载。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v1 = WriteDeltaArtifact(feedDir, "app-1.0.0.bin", _ => "v1");
            WriteFeedWithDelta(feedDir, "1.0.0", v1, material);
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", _ => "v2");
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);
            // 把 blockmap 写成合法的 JSON null——解析落空即无法建块表。
            File.WriteAllText(v2 + ".blockmap", "null");

            var cacheDir = install.TrimEnd('/', '\\') + ".bundler-cache";
            Directory.CreateDirectory(cacheDir);
            File.Copy(v1, Path.Combine(cacheDir, "artifact.bin"));

            var log = new List<string>();
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { Log = log.Add });
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path = await client.DownloadAsync(info, Path.Combine(directory, "dl"), TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            Assert.DoesNotContain(log, l => l.Contains("delta applied"));
            client.Verify(info, path);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SignatureVerifier_ToP1363_MalformedDer_ThrowsInvalidOperation()
    {
        // R2-N3：畸形 DER 远端输入曾抛 IndexOutOfRange/ArgumentException 逃逸契约——
        // 现须统一 InvalidOperationException，不得越界。
        Assert.Throws<InvalidOperationException>(() =>
            Protocol.UpdateSignatureVerifier.ToP1363(new byte[] { 0x30, 0x06, 0x02, 0xFF, 0x01 }));
        Assert.Throws<InvalidOperationException>(() =>
            Protocol.UpdateSignatureVerifier.ToP1363(
                new byte[] { 0x30, 0x07, 0x02, 0x02, 0xAA, 0xBB, 0x02 }));
        Assert.Throws<InvalidOperationException>(() =>
            Protocol.UpdateSignatureVerifier.ToP1363(new byte[] { 0x30, 0x81, 0x40, 0x02, 0x21 }));
    }

    [Fact]
    public void SignatureVerifier_MalformedDer_VerifyReturnsFalse()
    {
        // R2-N3 兜底：畸形签名喂给 Verify 只能判拒（false），不得把协议外异常漏给调用方。
        var material = DotNet.Bundler.Core.Update.UpdateKeyMaterial.Generate();
        var key = Protocol.UpdateKeyMaterial.FromPublicPoint(material.PublicPointBase64());
        Assert.False(Protocol.UpdateSignatureVerifier.Verify(
            Encoding.ASCII.GetBytes("payload"), new byte[] { 0x30, 0x06, 0x02, 0xFF, 0x01 }, key));
    }

        [Fact]
    public void Ustar_Symlink_Warns_WhenLinkCannotBeCreated()
    {
        // #18：链接还原失败曾静默跳过——现须与 zip 腿同型 WARN。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // 只读目录驱动 EACCES 失败腿为 POSIX 语义。
        }
        var directory = CreateTempDirectory();
        try
        {
            var staging = Path.Combine(directory, "staging");
            // 链接父目录只读——symlink() 必败，驱动 WARN 腿。
            var appDir = Path.Combine(staging, "app");
            Directory.CreateDirectory(appDir);
            File.SetUnixFileMode(appDir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            try
            {
                var log = new List<string>();
                UstarReader.Extract(new MemoryStream(BuildTarWithSymlink("app/link", "target")),
                    staging, log.Add);
                Assert.Contains(log, l => l.Contains("failed to restore symlink"));
            }
            finally
            {
                File.SetUnixFileMode(appDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // 手工构造最小 ustar 档案：一个 '2' 符号链接项 + 零块收尾。
    static byte[] BuildTarWithSymlink(string name, string target)
    {
        var header = new byte[512];
        WriteField(header, 0, name, 100);
        WriteField(header, 100, "0000755", 8);
        WriteField(header, 124, "00000000000", 12);
        WriteField(header, 136, "00000000000", 12);
        header[156] = (byte)'2';
        WriteField(header, 157, target, 100);
        WriteField(header, 257, "ustar", 6);
        return header.Concat(new byte[1024]).ToArray();
    }

    static void WriteField(byte[] block, int offset, string text, int width)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Buffer.BlockCopy(bytes, 0, block, offset, Math.Min(bytes.Length, width));
    }

    [Fact]
    public void UpdateApplier_RetentionRoot_RequiresSegmentBoundary()
    {
        // R2-N6：最长前缀匹配曾把 "/optimal" 吃进 "/opt" 段——必须按整段边界判。
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return; // mac/win 腿根名集合不同——断言口径按 linux 根表。
        }
        var retention = UpdateApplier.ResolveRetentionDirectory("/optimal/app", new ApplyOptions());
        Assert.False(retention.StartsWith("/var/lib", StringComparison.Ordinal),
            "non-segment prefix '/optimal' must not count as '/opt'");
        Assert.StartsWith("/var/lib",
            UpdateApplier.ResolveRetentionDirectory("/opt/app", new ApplyOptions()));
    }

        [Fact]
    public void UpdateBootstrapper_ExtractionRoot_SweepsStaleDirectories()
    {
        // R2-N7 抽取侧：bundler-updater 抽取根下 `.` 前缀残留目录按 mtime 清扫。
        // 走公开入口：两次 TryResolve——第一次建根，铺陈旧目录后第二次触发清扫。
        // 解析结果是 <root>/.<guid>/updater/<target>/<file>——root 取四级上级。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // 抽取根清扫只发生在 POSIX 路径腿。
        }
        var directory = CreateTempDirectory();
        try
        {
            var update = new DotNet.Bundler.UpdateBundleConfiguration();
            Assert.True(DotNet.Bundler.Core.Update.UpdateBootstrapper.TryResolve(
                update, "linux-x86_64", out var first));
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
                Path.GetDirectoryName(first)!)!)!)!;
            var stale = Path.Combine(root, ".stale-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stale);
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-25));
            var fresh = Path.Combine(root, ".fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fresh);

            Assert.True(DotNet.Bundler.Core.Update.UpdateBootstrapper.TryResolve(
                update, "linux-x86_64", out _));
            Assert.False(Directory.Exists(stale), "stale extraction dir must be swept");
            Assert.True(Directory.Exists(fresh), "fresh extraction dir must be kept");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // 回环微型静态服务器：GET /<name> 返文件、Range 返 206 分片——专测 http 差分腿。
    sealed class LoopbackFeedServer : IDisposable
    {
        readonly TcpListener _listener;
        readonly string _dir;
        readonly CancellationTokenSource _cts = new();
        public string FeedUrl { get; }
        public Action? OnRange;
        public Action? OnFullGet;
        public bool HonorRange = true;
        public bool CorruptRange;
        public bool WrongRangeStart;

        public LoopbackFeedServer(string dir)
        {
            _dir = dir;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            FeedUrl = $"http://127.0.0.1:{port}/{Protocol.UpdateFeed.FeedFileName("stable")}";
            _ = Task.Run(ServeLoop);
        }

        async Task ServeLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => Handle(client));
            }
        }

        async Task Handle(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new List<byte>();
                var buf = new byte[4096];
                string request;
                while (true)
                {
                    var read = await stream.ReadAsync(buf, 0, buf.Length, TestContext.Current.CancellationToken);
                    if (read == 0) { return; }
                    header.AddRange(buf.AsSpan(0, read).ToArray());
                    var text = Encoding.ASCII.GetString(header.ToArray());
                    var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0) { continue; }
                    request = text.Substring(0, end);
                    break;
                }
                var lines = request.Split("\r\n");
                var parts = lines[0].Split(' ');
                var name = parts[1].TrimStart('/');
                var file = Path.Combine(_dir, name);
                long? rangeStart = null, rangeEnd = null;
                foreach (var line in lines)
                {
                    if (line.StartsWith("Range: bytes=", StringComparison.Ordinal))
                    {
                        var bounds = line.Substring("Range: bytes=".Length).Split('-');
                        rangeStart = long.Parse(bounds[0]);
                        rangeEnd = bounds[1].Length > 0 ? long.Parse(bounds[1]) : (long?)null;
                        OnRange?.Invoke();
                    }
                }
                if (!File.Exists(file))
                {
                    await Write(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n", null);
                    return;
                }
                var body = File.ReadAllBytes(file);
                if (HonorRange && rangeStart is { } start)
                {
                    var end = Math.Min(rangeEnd ?? body.Length - 1, body.Length - 1);
                    var slice = body.AsSpan((int)start, (int)(end - start + 1)).ToArray();
                    if (CorruptRange)
                    {
                        // Content-Length 声明全长却只发一半即断流——模拟 206 响应中途损坏。
                        var truncated = slice[..Math.Max(1, slice.Length / 2)];
                        await Write(stream,
                            $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{body.Length}\r\nContent-Length: {slice.Length}\r\n\r\n",
                            truncated);
                        return;
                    }
                    if (WrongRangeStart)
                    {
                        // 206 但 Content-Range 起点与请求偏移不符——客户端须弃 .part 重下全档。
                        await Write(stream,
                            $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start + 1}-{end}/{body.Length}\r\nContent-Length: {slice.Length}\r\n\r\n",
                            slice);
                        return;
                    }
                    await Write(stream,
                        $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{body.Length}\r\nContent-Length: {slice.Length}\r\n\r\n",
                        slice);
                    return;
                }
                OnFullGet?.Invoke();
                await Write(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n", body);
            }
        }

        static Task Write(Stream stream, string head, byte[]? body)
        {
            return stream.WriteAsync(Encoding.ASCII.GetBytes(head).Concat(body ?? Array.Empty<byte>()).ToArray(), TestContext.Current.CancellationToken).AsTask();
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); }
    }

            // ---- 复审修复轮回归 ----

        [Fact]
    public async Task Client_Check_RejectsTamperedFeed()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var artifact = WriteZipArtifact(feedDir, "app-1.0.0.zip", "v1");
            WriteFeed(feedDir, "stable", "1.0.0", material, new Protocol.UpdateFeedArtifact
            {
                Target = "linux-x86_64", Format = "zip",
                Url = "app-1.0.0.zip", File = "app-1.0.0.zip",
                Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
                Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
            });
            // 换源攻击：清单文件被改（版本号抬高指回旧签名件）→ 验签必拒。
            var feedPath = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable"));
            File.WriteAllText(feedPath,
                File.ReadAllText(feedPath).Replace("\"1.0.0\"", "\"9.9.9\""));

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var exception = await Assert.ThrowsAsync<UpdateException>(
                () => client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
            Assert.Contains("failed signature verification", exception.Message);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_Apply_RefusesUnverifiedArtifact()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var artifact = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2");
            WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
            {
                Target = "linux-x86_64", Format = "zip",
                Url = "app-2.0.0.zip", File = "app-2.0.0.zip",
                Sha256 = Sha256Hex(artifact), Size = new FileInfo(artifact).Length,
                Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifact, material)),
            });
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var downloaded = await client.DownloadAsync(
                info, Path.Combine(directory, "dl"), TestContext.Current.CancellationToken);
            // 未走 Verify 直接 Apply——未认证产物永不进入应用面。
            var exception = Assert.Throws<UpdateException>(
                () => client.Apply(info, downloaded));
            Assert.Contains("has not passed Verify", exception.Message);
            // 下载件也不应已沉进差分缓存（缓存只收验过签的字节）。
            Assert.False(File.Exists(Path.Combine(
                install.TrimEnd('/', '\\') + ".bundler-cache", "artifact.bin")));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SignatureUrl_KeepsQueryAndFragmentAfterSig()
    {
        // feed 带 query/fragment：.sig 追加在路径段——末位拼接会被 http
        // 当成查询串一部分，签名请求打到错误地址。
        Assert.Equal("/feed/latest.json.sig",
            UpdateDownloader.SignatureUrl("/feed/latest.json"));
        Assert.Equal("https://cdn.test/feed.json.sig?v=3",
            UpdateDownloader.SignatureUrl("https://cdn.test/feed.json?v=3"));
        Assert.Equal("https://cdn.test/feed.json.sig#top",
            UpdateDownloader.SignatureUrl("https://cdn.test/feed.json#top"));
        Assert.Equal("https://cdn.test/feed.json.sig?a=1#x",
            UpdateDownloader.SignatureUrl("https://cdn.test/feed.json?a=1#x"));
        // 裸本地路径的 '#''?' 是文件名字符不是分隔符——恒定末位追加。
        Assert.Equal("/tmp/feed#dir/latest.json.sig",
            UpdateDownloader.SignatureUrl("/tmp/feed#dir/latest.json"));
        // file URI 形式里 '#''?' 仍是分隔符（文件名中的早已转义）。
        Assert.Equal("file:///tmp/feed.json.sig",
            UpdateDownloader.SignatureUrl("file:///tmp/feed.json"));
        Assert.Equal("file:///tmp/feed.json.sig?v=1",
            UpdateDownloader.SignatureUrl("file:///tmp/feed.json?v=1"));
        Assert.Equal("file:///tmp/a%23b/feed.json.sig",
            UpdateDownloader.SignatureUrl("file:///tmp/a%23b/feed.json"));
    }

    [Fact]
    public void Identity_FileInstall_SidecarRoundtrip()
    {
        var directory = CreateTempDirectory();
        try
        {
            var install = Path.Combine(directory, "MyApp.AppImage");
            File.WriteAllText(install, "v1-image");
            // 无旁车 → null（不猜不补）
            Assert.Null(Protocol.UpdateInstallIdentity.TryRead(install));

            var identity = new Protocol.UpdateInstallIdentity
            {
                Format = "appimage", Target = "linux-x86_64",
                Channel = "stable", FeedUrl = "/feed/latest.json",
                PublicKey = "cHVia2V5",
            };
            Protocol.UpdateInstallIdentity.WriteSidecar(install, identity);
            var back = Protocol.UpdateInstallIdentity.TryRead(install);
            Assert.NotNull(back);
            Assert.Equal("appimage", back!.Format);
            Assert.Equal("/feed/latest.json", back.FeedUrl);
            Assert.Equal("cHVia2V5", back.PublicKey);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // ---- helpers ----

    // 按块标签填充内容写 3×64KiB 差分测试件——同标签块哈希相等。
    static string WriteDeltaArtifact(string dir, string name, Func<int, string> blockLabel)
    {
        var blockSize = Protocol.UpdateBlockMap.DefaultBlockSize;
        var path = Path.Combine(dir, name);
        using var stream = File.Create(path);
        for (var i = 0; i < 3; i++)
        {
            var label = Encoding.ASCII.GetBytes(blockLabel(i));
            var block = new byte[blockSize];
            for (var j = 0; j < blockSize; j += label.Length)
            {
                Buffer.BlockCopy(label, 0, block, j, Math.Min(label.Length, blockSize - j));
            }
            stream.Write(block, 0, blockSize);
        }
        return path;
    }

    [Fact]
    public async Task Client_Check_MalformedFeed_ThrowsUpdateException()
    {
        // 畸形 JSON 清单（签名有效）：协议拒绝必须统一为 UpdateException——
        // 不得把 JsonException/SerializationException 漏给调用方。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var feedPath = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable"));
            File.WriteAllText(feedPath, "not-a-manifest-at-all");
            File.WriteAllBytes(feedPath + ".sig", EcdsaSigner.SignFile(feedPath, material));

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var exception = await Assert.ThrowsAsync<UpdateException>(
                () => client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
            Assert.Contains("manifest", exception.Message);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_Check_FeedMissingFields_ThrowsUpdateException()
    {
        // 合法 JSON 但缺版本字段——同样 UpdateException 确定性拒绝，不 NRE 不泄漏。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var feedPath = Path.Combine(feedDir, Protocol.UpdateFeed.FeedFileName("stable"));
            File.WriteAllText(feedPath, "{}");
            File.WriteAllBytes(feedPath + ".sig", EcdsaSigner.SignFile(feedPath, material));

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            await Assert.ThrowsAsync<UpdateException>(() => client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_FallsBack_WhenRangeResponseTruncatedMidStream()
    {
        // 206 响应声明全长却只发一半即断流——差分须放弃回落全量，不得留坏件。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v1 = WriteDeltaArtifact(feedDir, "app-1.0.0.bin", block =>
                block switch { 0 => "head", 1 => "body-same", 2 => "tail-v1", _ => "?" });
            WriteFeedWithDelta(feedDir, "1.0.0", v1, material);
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", block =>
                block switch { 0 => "head", 1 => "body-same", 2 => "tail-v2", _ => "?" });
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);

            using var server = new LoopbackFeedServer(feedDir) { CorruptRange = true };
            var identity = new Protocol.UpdateInstallIdentity
            {
                FeedUrl = server.FeedUrl, Channel = "stable",
                Target = "linux-x86_64", Format = "zip",
                PublicKey = material.PublicPointBase64(),
            };
            Protocol.UpdateInstallIdentity.Write(install, identity);

            var cacheDir = install.TrimEnd('/', '\\') + ".bundler-cache";
            Directory.CreateDirectory(cacheDir);
            File.Copy(v1, Path.Combine(cacheDir, "artifact.bin"));

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path = await client.DownloadAsync(info, Path.Combine(directory, "dl"), TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Client_DownloadAsync_FallsBack_WhenBlockMapVersionUnsupported()
    {
        // blockmap 版本不符 → 差分不进场直接回落全量（电子链版本协商负例）。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v1 = WriteDeltaArtifact(feedDir, "app-1.0.0.bin", block =>
                block switch { 0 => "head", 1 => "body-same", 2 => "tail-v1", _ => "?" });
            WriteFeedWithDelta(feedDir, "1.0.0", v1, material);
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", block =>
                block switch { 0 => "head", 1 => "body-same", 2 => "tail-v2", _ => "?" });
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);
            // 重写 v2 的 blockmap：Version=2 即本端不识别的未来版本。
            var map = Protocol.UpdateBlockMap.ComputeFile(v2);
            map.Version = 2;
            var mapSerializer = new DataContractJsonSerializer(typeof(Protocol.UpdateBlockMap));
            using (var mapStream = File.Create(v2 + Protocol.UpdateBlockMap.FileSuffix))
            {
                mapSerializer.WriteObject(mapStream, map);
            }

            var cacheDir = install.TrimEnd('/', '\\') + ".bundler-cache";
            Directory.CreateDirectory(cacheDir);
            File.Copy(v1, Path.Combine(cacheDir, "artifact.bin"));

            var log = new List<string>();
            var client = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { Log = log.Add });
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            var path = await client.DownloadAsync(info, Path.Combine(directory, "dl"), TestContext.Current.CancellationToken);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            Assert.Contains(log, l => l.Contains("block-map"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

        [Fact]
    public async Task Client_DownloadAsync_ResumablePart_OnlyNeedsRemainingSpace()
    {
        // 断点续传空间量：`.part` 已持有大部分工件时预检只须补齐剩余量——
        // 探针报的剩余够尾巴不够全件，旧按全件判会误拒合法续传。
        var directory = CreateTempDirectory();
        try
        {
            var install = InstallWithSidecar(directory, out var material, out var feedDir);
            var v2 = WriteZipArtifact(feedDir, "app-2.0.0.zip", "v2");
            WriteFeed(feedDir, "stable", "2.0.0", material, new Protocol.UpdateFeedArtifact
            {
                Target = "linux-x86_64", Format = "zip",
                Url = "app-2.0.0.zip", File = "app-2.0.0.zip",
                Sha256 = Sha256Hex(v2), Size = new FileInfo(v2).Length,
                Signature = Convert.ToBase64String(EcdsaSigner.SignFile(v2, material)),
            });

            using var server = new LoopbackFeedServer(feedDir);
            var identity = new Protocol.UpdateInstallIdentity
            {
                FeedUrl = server.FeedUrl, Channel = "stable",
                Target = "linux-x86_64", Format = "zip",
                PublicKey = material.PublicPointBase64(),
            };
            var sidecarSerializer = new DataContractJsonSerializer(
                typeof(Protocol.UpdateInstallIdentity),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var stream = File.Create(Path.Combine(install, "bundler-update.json")))
            {
                sidecarSerializer.WriteObject(stream, identity);
            }

            var size = new FileInfo(v2).Length;
            var destDir = Path.Combine(directory, "dl");
            Directory.CreateDirectory(destDir);
            // `.part` 已持有前大半——剩余只需 16 字节，全件量判会误拒。
            var head = File.ReadAllBytes(v2).AsSpan(0, (int)size - 16).ToArray();
            File.WriteAllBytes(Path.Combine(destDir, "app-2.0.0.zip.part"), head);

            var client = UpdateClient.FromInstallDirectory(install, "1.0.0");
            var info = (await client.CheckForUpdateAsync(TestContext.Current.CancellationToken))!;
            UpdateClient.FreeSpaceProbe = _ => 16;
            try
            {
                var path = await client.DownloadAsync(info, destDir, TestContext.Current.CancellationToken);
                Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            }
            finally
            {
                UpdateClient.FreeSpaceProbe = null;
            }
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // 清单+`.sig`+`.blockmap` 三件齐写（delta 腿需要 blockmap 在场）。
    static void WriteFeedWithDelta(
        string dir, string version, string artifactPath,
        DotNet.Bundler.Core.Update.UpdateKeyMaterial material)
    {
        var fileName = Path.GetFileName(artifactPath);
        var map = Protocol.UpdateBlockMap.ComputeFile(artifactPath);
        var mapPath = artifactPath + Protocol.UpdateBlockMap.FileSuffix;
        var mapSerializer = new DataContractJsonSerializer(typeof(Protocol.UpdateBlockMap));
        using (var mapStream = File.Create(mapPath))
        {
            mapSerializer.WriteObject(mapStream, map);
        }
        WriteFeed(dir, "stable", version, material, new Protocol.UpdateFeedArtifact
        {
            Target = "linux-x86_64", Format = "zip",
            Url = fileName, File = fileName,
            Sha256 = Sha256Hex(artifactPath), Size = new FileInfo(artifactPath).Length,
            Signature = Convert.ToBase64String(EcdsaSigner.SignFile(artifactPath, material)),
            BlockMap = fileName + Protocol.UpdateBlockMap.FileSuffix,
        });
    }


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
        DotNet.Bundler.Core.Update.UpdateKeyMaterial material,
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

    string CreateTempDirectory()
    {
        var path = Temp("bundler-updater-").FullName;
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
