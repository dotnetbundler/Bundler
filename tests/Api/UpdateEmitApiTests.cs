using System.Text.Json;
using DotNet.Bundler;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;
using DotNet.Bundler.Update;

namespace Bundler.ApiTests;

/// <summary>
/// UPDATE 发射公开契约：BuildAsync 产物 → EmitAsync 在输出根产 feed+签名+块表，
/// 全宿主可跑（纯托管）。自更新面只覆盖 nsis/msi/app/appimage/zip/targz——
/// 系统包管理器格式（deb/rpm/apk/pkg）不进更新链，自带生态更新通道。
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
    public async Task Emit_NonCoveredFormat_EmptyFeedButSigned()
    {
        // deb 不进更新链——feed 仍发射（零产物但带签名），客户端读"无适用更新"而非配置坏。
        var bundle = UpdateBundle(PackageFormat.Deb);
        var artifacts = await new DotNet.Bundler.Deb.DebBundler().BuildAsync(
            bundle, TestContext.Current.CancellationToken);
        var produced = await UpdateManifestEmitter.EmitAsync(
            bundle, artifacts, TestContext.Current.CancellationToken);

        var feedPath = Path.Combine(bundle.OutputDirectory, "bundler-update-feed.beta.json");
        // produced 只含 feed+feed.sig——非覆盖产物完全不进更新面。
        Assert.Equal(2, produced.Count);
        Assert.True(File.Exists(feedPath));
        Assert.True(File.Exists(feedPath + ".sig"));
        using var feed = JsonDocument.Parse(File.ReadAllText(feedPath));
        Assert.Equal(0, feed.RootElement.GetProperty("artifacts").GetArrayLength());
        // 非覆盖产物无 .sig/.blockmap
        Assert.False(File.Exists(artifacts[0].Path + ".sig"));
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
