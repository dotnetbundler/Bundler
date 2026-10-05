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
    static void BlockMap_ComputesOrderedBlockHashes()
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
    static async Task Client_DownloadAsync_UsesDelta_WhenCacheMatches()
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
            var info1 = (await client1.CheckForUpdateAsync())!;
            var path1 = await client1.DownloadAsync(info1, downloadDir);
            Assert.Contains(log, l => l.Contains("full download") || l.Contains("no delta source"));

            // v2：块 0/1 不变（哈希命中）、块 2 变化——差分应只拉一块。
            var v2 = WriteDeltaArtifact(feedDir, "app-2.0.0.bin", block =>
                block switch { 0 => "head-v1", 1 => "body-same", 2 => "tail-v2", _ => "?" });
            WriteFeedWithDelta(feedDir, "2.0.0", v2, material);
            var client2 = UpdateClient.FromInstallDirectory(install, "1.0.0",
                new UpdateClientOptions { Log = log.Add });
            var info2 = (await client2.CheckForUpdateAsync())!;
            var path2 = await client2.DownloadAsync(info2, downloadDir);
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
    static async Task Client_DownloadAsync_FallsBack_WhenCacheCorrupt()
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
            var info = (await client.CheckForUpdateAsync())!;
            var path = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            client.Verify(info, path);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    static async Task Client_DownloadAsync_UsesDelta_OverHttp()
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
            var server = new LoopbackFeedServer(feedDir);
            var identity = new Protocol.UpdateInstallIdentity
            {
                FeedUrl = server.FeedUrl, Channel = "stable",
                RuntimeIdentifier = "linux-x64", Format = "zip",
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
            var info = (await client.CheckForUpdateAsync())!;
            var path = await client.DownloadAsync(info, Path.Combine(directory, "dl"));
            Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2)));
            Assert.Contains(log, l => l.Contains("delta applied"));
            Assert.True(ranged > 0); // 缺失块确实走了 HTTP Range
            server.Dispose();
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
                    var read = await stream.ReadAsync(buf, 0, buf.Length);
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
                if (rangeStart is { } start)
                {
                    var end = Math.Min(rangeEnd ?? body.Length - 1, body.Length - 1);
                    var slice = body.AsSpan((int)start, (int)(end - start + 1)).ToArray();
                    await Write(stream,
                        $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{body.Length}\r\nContent-Length: {slice.Length}\r\n\r\n",
                        slice);
                    return;
                }
                await Write(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n", body);
            }
        }

        static Task Write(Stream stream, string head, byte[]? body)
        {
            return stream.WriteAsync(Encoding.ASCII.GetBytes(head).Concat(body ?? Array.Empty<byte>()).ToArray()).AsTask();
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); }
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
        WriteFeed(dir, "stable", version, new Protocol.UpdateFeedArtifact
        {
            RuntimeIdentifier = "linux-x64", Format = "zip",
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
