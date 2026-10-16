using System.Text.Json;
using DotNet.Bundler;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;
using DotNet.Bundler.Update;

namespace Bundler.ApiTests;

/// <summary>
/// UPDATE 发射公开契约：BuildAsync 产物 → EmitAsync 在输出根产 feed+签名+块表，
/// 全宿主可跑（纯托管）。自更新面覆盖 nsis/msi/app/appimage/zip/targz 与
/// 系统包管理器格式 deb/rpm/alpineapk（身份旁车+引导件已随包注入载荷）；
/// dmg/pkg 非更新载体不进链。
/// </summary>
public sealed class UpdateEmitApiTests : TempBase
{
    private BundleConfiguration UpdateBundle(PackageFormat format = PackageFormat.Zip)
    {
        var keyPath = Path.Combine(Root, "update-key.json");
        UpdateKeyMaterial.Generate().Save(keyPath);
        return BundleFixture.Minimal(Root, "linux-x86_64", format,
            update: new UpdateBundleConfiguration
            {
                FeedUrl = "https://updates.example.com/feed/",
                Channel = "beta",
                SigningKeyFile = keyPath,
            });
    }

    [Fact]
    public async Task Emit_ProducesFeedSignatureBlockmap()
    {
        var bundle = UpdateBundle();
        var artifacts = await new ArchiveBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);
        var produced = await UpdateManifestEmitter.EmitAsync(
            bundle, artifacts, TestContext.Current.CancellationToken);

        var output = bundle.OutputDirectory;
        var feedPath = Path.Combine(output, "bundler-update-feed.beta.json");
        Assert.True(File.Exists(feedPath));
        Assert.True(File.Exists(feedPath + ".sig"));
        Assert.Contains(feedPath, produced);

        // zip 是自更新覆盖格式 → .sig + .blockmap 旁车落盘
        var artifact = artifacts[0].Path;
        Assert.True(File.Exists(artifact + ".sig"));
        Assert.True(File.Exists(artifact + UpdateBlockMap.FileSuffix));

        using var feed = JsonDocument.Parse(File.ReadAllText(feedPath));
        Assert.Equal("1.2.3", feed.RootElement.GetProperty("version").GetString());
        Assert.Equal("beta", feed.RootElement.GetProperty("channel").GetString());
        var items = feed.RootElement.GetProperty("artifacts");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("zip", items[0].GetProperty("format").GetString());
        Assert.Equal("linux-x86_64", items[0].GetProperty("rid").GetString());
    }

    [Fact]
    public async Task Emit_DebArtifact_EntersFeedWithSigBlockmap()
    {
        // syspkg 格式（deb/rpm/alpineapk）进更新链——deb 产物同样产 .sig/.blockmap
        // 旁车并进 feed（feed.format 口径=枚举名小写 "deb"）。
        var bundle = UpdateBundle(PackageFormat.Deb);
        var artifacts = await new DotNet.Bundler.Deb.DebBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);
        var produced = await UpdateManifestEmitter.EmitAsync(
            bundle, artifacts, TestContext.Current.CancellationToken);

        var feedPath = Path.Combine(bundle.OutputDirectory, "bundler-update-feed.beta.json");
        Assert.True(File.Exists(feedPath));
        Assert.True(File.Exists(feedPath + ".sig"));
        Assert.Contains(feedPath, produced);

        var artifact = artifacts[0].Path;
        Assert.True(File.Exists(artifact + ".sig"));
        Assert.True(File.Exists(artifact + UpdateBlockMap.FileSuffix));

        using var feed = JsonDocument.Parse(File.ReadAllText(feedPath));
        var items = feed.RootElement.GetProperty("artifacts");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("deb", items[0].GetProperty("format").GetString());
        Assert.Equal("linux-x86_64", items[0].GetProperty("rid").GetString());
    }

    [Fact]
    public async Task Emit_MissingKey_Rejected()
    {
        // 发射层门禁：Update 配置无签名钥直接拒（构建期校验器另有一道拦）。
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.Zip,
            update: new UpdateBundleConfiguration
            {
                FeedUrl = "https://updates.example.com/feed/",
            });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateManifestEmitter.EmitAsync(bundle, [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Build_UpdateWithoutKey_ValidationRejects()
    {
        // 构建期门禁：开启更新面必须配签名钥——校验在打包前拦。
        var bundle = BundleFixture.Minimal(Root, "linux-x86_64", PackageFormat.Zip,
            update: new UpdateBundleConfiguration
            {
                FeedUrl = "https://updates.example.com/feed/",
            });
        await Assert.ThrowsAsync<BundleValidationException>(() =>
            new ArchiveBundler().BuildAsync(bundle, TestContext.Current.CancellationToken));
    }
}
