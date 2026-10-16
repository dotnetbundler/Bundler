using System.Text;
using CoreUpdate = DotNet.Bundler.Core.Update;
using DotNet.Bundler.Update;
using DotNet.Bundler.Updater;
using Protocol = DotNet.Bundler.Updater.Protocol;

namespace Bundler.UnitTests.Updater;

/// <summary>
/// <see cref="UpdateClient"/> 进程内契约：身份旁车加载、file:// 清单验签与版本决议、
/// 产物选取（rid 精确 + portable/any 通配兜底）、空间预检缝、验签闸门。
/// 跨程序集协议互操作是本测试的本体——feed 由打包侧（Core/Update 命名空间件）
/// 序列化签名，由应用侧（Updater.Protocol 同源件）验证解析。
/// </summary>
public sealed class UpdateClientTests : TempBase
{


    public override void Dispose()
    {
        UpdateClient.FreeSpaceProbe = null;
        base.Dispose();
    }

    // ---- 身份加载 ----

    [Fact]
    public void FromInstallDirectory_MissingSidecar_Throws()
    {
        var empty = Path.Combine(Root, "install");
        Directory.CreateDirectory(empty);
        Assert.Throws<UpdateException>(() =>
            UpdateClient.FromInstallDirectory(empty, "1.0.0"));
    }

    [Fact]
    public void FromInstallDirectory_ReadsSidecar_Constructs()
    {
        var install = Path.Combine(Root, "install2");
        Directory.CreateDirectory(install);
        var identity = Identity();
        using (var stream = File.Create(Path.Combine(install, Protocol.UpdateInstallIdentity.FileName)))
        {
            Protocol.UpdateJson.WriteIdentity(stream, identity);
        }
        Assert.NotNull(UpdateClient.FromInstallDirectory(install, "1.0.0"));
    }

    [Fact]
    public void FromIdentity_MissingFeedOrKey_Throws()
    {
        var install = Path.Combine(Root, "install3");
        var noFeed = Identity();
        noFeed.FeedUrl = "";
        Assert.Throws<UpdateException>(() =>
            UpdateClient.FromIdentity(noFeed, install, "1.0.0"));
        var noKey = Identity();
        noKey.PublicKey = null;
        Assert.Throws<UpdateException>(() =>
            UpdateClient.FromIdentity(noKey, install, "1.0.0"));
    }

    // ---- 清单决议（file:// feed）----

    [Fact]
    public async Task CheckForUpdateAsync_NewerVersion_ReturnsInfo()
    {
        var (feedDir, key) = MakeFeed("2.0.0",
            [Artifact("win-x64", "nsis", "app-2.0.0.exe")]);
        var client = Client(feedDir, key, currentVersion: "1.0.0");

        var info = await client.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal("2.0.0", info.Version);
        Assert.Equal("app-2.0.0.exe", info.Artifact.File);
    }

    [Fact]
    public async Task CheckForUpdateAsync_SameOrOlderVersion_Null()
    {
        var (feedDir, key) = MakeFeed("1.0.0",
            [Artifact("win-x64", "nsis", "app-1.0.0.exe")]);
        var client = Client(feedDir, key, currentVersion: "1.0.0");

        Assert.Null(await client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoMatchingRid_Null()
    {
        var (feedDir, key) = MakeFeed("2.0.0",
            [Artifact("linux-x64", "nsis", "app-linux.exe")]);
        var client = Client(feedDir, key, currentVersion: "1.0.0");

        Assert.Null(await client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckForUpdateAsync_PortableWildcard_Fallback()
    {
        var (feedDir, key) = MakeFeed("2.0.0",
            [Artifact("linux-x64", "nsis", "other.exe"),
             Artifact("portable", "nsis", "app-portable.exe")]);
        var client = Client(feedDir, key, currentVersion: "1.0.0");

        var info = await client.CheckForUpdateAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("app-portable.exe", info.Artifact.File);
    }

    [Fact]
    public async Task CheckForUpdateAsync_FormatMismatch_Null()
    {
        var (feedDir, key) = MakeFeed("2.0.0",
            [Artifact("win-x64", "msi", "app-2.0.0.msi")]);
        var client = Client(feedDir, key, currentVersion: "1.0.0");

        Assert.Null(await client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckForUpdateAsync_UnsignedFeed_Rejected()
    {
        var feedDir = Path.Combine(Root, "unsigned");
        Directory.CreateDirectory(feedDir);
        var feedPath = Path.Combine(feedDir, "bundler-update-feed.json");
        var feed = new CoreUpdate.UpdateFeed { Version = "2.0.0", Channel = "latest" };
        using (var stream = File.Create(feedPath))
        {
            CoreUpdate.UpdateJson.WriteFeed(stream, feed);
        }
        // 无 .sig 旁车：缺签名清单一律拒。
        var key = CoreUpdate.UpdateKeyMaterial.Generate();
        var client = Client(feedDir, key, currentVersion: "1.0.0");

        await Assert.ThrowsAsync<UpdateException>(() =>
            client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckForUpdateAsync_FeedSignedByOtherKey_Rejected()
    {
        var (feedDir, _) = MakeFeed("2.0.0",
            [Artifact("win-x64", "nsis", "app-2.0.0.exe")]);
        var attacker = CoreUpdate.UpdateKeyMaterial.Generate();
        var client = Client(feedDir, attacker, currentVersion: "1.0.0");

        await Assert.ThrowsAsync<UpdateException>(() =>
            client.CheckForUpdateAsync(TestContext.Current.CancellationToken));
    }

    // ---- 下载/验签/应用闸 ----

    [Fact]
    public async Task DownloadAndVerify_LocalArtifact_RoundTrip()
    {
        var payload = Encoding.UTF8.GetBytes("new-version-bytes");
        var (feedDir, key) = MakeFeed("2.0.0",
            [Artifact("win-x64", "nsis", "app-2.0.0.exe", payload)]);
        var client = Client(feedDir, key, currentVersion: "1.0.0");
        var info = await client.CheckForUpdateAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(info);

        var downloads = Path.Combine(Root, "dl");
        var path = await client.DownloadAsync(info!, downloads, TestContext.Current.CancellationToken);

        Assert.Equal(payload, File.ReadAllBytes(path));
        client.Verify(info!, path); // 验签通过不抛
    }

    [Fact]
    public async Task Verify_TamperedArtifact_Rejected()
    {
        var payload = Encoding.UTF8.GetBytes("original");
        var (feedDir, key) = MakeFeed("2.0.0",
            [Artifact("win-x64", "nsis", "app-2.0.0.exe", payload)]);
        var client = Client(feedDir, key, currentVersion: "1.0.0");
        var info = await client.CheckForUpdateAsync(TestContext.Current.CancellationToken);
        var path = await client.DownloadAsync(
            info!, Path.Combine(Root, "dl2"), TestContext.Current.CancellationToken);

        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("tampered"));

        Assert.Throws<UpdateException>(() => client.Verify(info!, path));
    }

    [Fact]
    public async Task DownloadAsync_InsufficientSpace_RejectedBeforeCopy()
    {
        var payload = new byte[4096];
        var (feedDir, key) = MakeFeed("2.0.0",
            [Artifact("win-x64", "nsis", "app-2.0.0.exe", payload)]);
        var client = Client(feedDir, key, currentVersion: "1.0.0");
        var info = await client.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        UpdateClient.FreeSpaceProbe = _ => 1; // 缝：只剩 1 字节
        var downloads = Path.Combine(Root, "dl3");
        await Assert.ThrowsAsync<UpdateException>(() =>
            client.DownloadAsync(info!, downloads, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(downloads, "app-2.0.0.exe")));
    }

    [Fact]
    public async Task Apply_UnverifiedArtifact_Refused()
    {
        var (feedDir, key) = MakeFeed("2.0.0",
            [Artifact("win-x64", "nsis", "app-2.0.0.exe", "x"u8.ToArray())]);
        var install = Path.Combine(Root, "install4");
        Directory.CreateDirectory(install);
        var client = Client(feedDir, key, currentVersion: "1.0.0", install);
        var info = await client.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Throws<UpdateException>(() =>
            client.Apply(info!, Path.Combine(Root, "never-verified.bin")));
    }

    // ---- 装置 ----

    private Protocol.UpdateInstallIdentity Identity(
        CoreUpdate.UpdateKeyMaterial? key = null, string? feedUrl = null)
    {
        var material = key ?? CoreUpdate.UpdateKeyMaterial.Generate();
        return new Protocol.UpdateInstallIdentity
        {
            Format = "nsis",
            Target = "win-x64",
            Channel = "latest",
            FeedUrl = feedUrl ?? "file:///" + Root.Replace('\\', '/') + "/feed/bundler-update-feed.json",
            PublicKey = material.PublicPointBase64(),
        };
    }

    private UpdateClient Client(string feedDir, CoreUpdate.UpdateKeyMaterial key,
        string currentVersion, string? installDirectory = null)
    {
        var identity = Identity(key,
            new Uri(Path.Combine(feedDir, "bundler-update-feed.json")).AbsoluteUri);
        return UpdateClient.FromIdentity(
            identity, installDirectory ?? Path.Combine(Root, "install-app"), currentVersion);
    }

    private readonly Dictionary<string, byte[]> _artifactContent = new();

    private CoreUpdate.UpdateFeedArtifact Artifact(
        string rid, string format, string file, byte[]? content = null)
    {
        if (content is not null)
        {
            _artifactContent[file] = content;
        }
        return new() { Target = rid, Format = format, Url = file, File = file };
    }

    /// <summary>产一份打包侧签名清单 + 产物文件，返回 (feed 目录, 签名钥)。</summary>
    private (string FeedDir, CoreUpdate.UpdateKeyMaterial Key) MakeFeed(
        string version, CoreUpdate.UpdateFeedArtifact[] artifacts)
    {
        var key = CoreUpdate.UpdateKeyMaterial.Generate();
        var feedDir = Path.Combine(Root, "feed-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(feedDir);
        foreach (var artifact in artifacts)
        {
            // 测试声明的内容优先生效；未带的用确定性字节。
            var bytes = _artifactContent.TryGetValue(artifact.File, out var declared)
                ? declared
                : Encoding.UTF8.GetBytes("artifact:" + artifact.File);
            File.WriteAllBytes(Path.Combine(feedDir, artifact.File), bytes);
            artifact.Size = bytes.Length;
            artifact.Sha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            artifact.Signature = Convert.ToBase64String(EcdsaSigner.Sign(bytes, key));
        }
        var feed = new CoreUpdate.UpdateFeed
        {
            Version = version,
            Channel = "latest",
            PublishedAt = "2024-01-01T00:00:00Z",
        };
        feed.Artifacts.AddRange(artifacts);
        var feedPath = Path.Combine(feedDir, "bundler-update-feed.json");
        byte[] feedBytes;
        using (var stream = new MemoryStream())
        {
            CoreUpdate.UpdateJson.WriteFeed(stream, feed);
            feedBytes = stream.ToArray();
        }
        File.WriteAllBytes(feedPath, feedBytes);
        // 签名地址约定：<feed>.sig
        File.WriteAllBytes(feedPath + ".sig", EcdsaSigner.Sign(feedBytes, key));
        return (feedDir, key);
    }
}

/// <summary><see cref="UpdateApplier"/> 纯文件缝：顶层目录决议、过期暂存清扫、回滚保留目录。</summary>
public sealed class UpdateApplierTests : TempBase
{



    [Fact]
    public void SingleTopDirectory_WrapperShape_PicksChild()
    {
        var staging = Path.Combine(Root, "s1");
        var inner = Path.Combine(staging, "App.app");
        Directory.CreateDirectory(inner);
        Assert.Equal(inner, UpdateApplier.SingleTopDirectory(staging, "x.zip"));
    }

    [Fact]
    public void SingleTopDirectory_FlatShape_KeepsRoot()
    {
        var staging = Path.Combine(Root, "s2");
        Directory.CreateDirectory(Path.Combine(staging, "bin"));
        File.WriteAllText(Path.Combine(staging, "root.txt"), "r");
        Assert.Equal(staging, UpdateApplier.SingleTopDirectory(staging, "x.zip"));
    }

    [Fact]
    public void SingleTopDirectory_Empty_Throws()
    {
        var staging = Path.Combine(Root, "s3");
        Directory.CreateDirectory(staging);
        Assert.Throws<UpdateException>(() =>
            UpdateApplier.SingleTopDirectory(staging, "x.zip"));
    }

    [Fact]
    public void SweepStaleStaging_RemovesOnlyOldKnownPrefixes()
    {
        var stagingRoot = Path.Combine(Root, "stagingRoot");
        Directory.CreateDirectory(stagingRoot);
        var oldPayload = Path.Combine(stagingRoot, "payload-aaaa");
        var freshBoot = Path.Combine(stagingRoot, "bootstrap-bbbb");
        var unrelated = Path.Combine(stagingRoot, "keepme");
        Directory.CreateDirectory(oldPayload);
        Directory.CreateDirectory(freshBoot);
        Directory.CreateDirectory(unrelated);
        Directory.SetLastWriteTimeUtc(oldPayload, DateTime.UtcNow.AddDays(-2));

        UpdateApplier.SweepStaleStaging(stagingRoot);

        Assert.False(Directory.Exists(oldPayload));
        Assert.True(Directory.Exists(freshBoot));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void ResolveRetentionDirectory_Explicit_Wins()
    {
        var explicitDir = Path.Combine(Root, "retained");
        var resolved = UpdateApplier.ResolveRetentionDirectory(
            Path.Combine(Root, "install"), new ApplyOptions { RollbackBackupDirectory = explicitDir });
        Assert.Equal(Path.GetFullPath(explicitDir), resolved);
    }

    [Fact]
    public void ResolveRetentionDirectory_Default_DeterministicHashSuffix()
    {
        var install = Path.Combine(Root, "install");
        var options = new ApplyOptions();
        var first = UpdateApplier.ResolveRetentionDirectory(install, options);
        var second = UpdateApplier.ResolveRetentionDirectory(install, options);
        Assert.Equal(first, second);
        Assert.StartsWith("install-", Path.GetFileName(first));
    }
}
