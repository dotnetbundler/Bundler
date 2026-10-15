// 字面样品链腿：用户照 docs 走的真实路径——samples/HelloBundlerApp 经
// `dotnet publish` → buildTransitive 产包 → 本宿主默认产物集 + UPDATE 侧车端到端。
// 各格式集成腿的 fixture 是合成被测件；这条覆盖样品工程本体（轮测"样品测试"字面链收编）。
using System.Runtime.InteropServices;
using UpdateKeyMaterial = DotNet.Bundler.Core.Update.UpdateKeyMaterial;

public sealed class SampleChainIntegrationFixture : IDisposable
{
    // 惰性创建：宿主门禁在各用例体内 Skip，之后才允许占锁建目录。
    private readonly Lazy<IntegrationWorkspace> _ws = new(() => IntegrationWorkspace.Create(
        "sample-chain", "HelloBundlerSample"));

    public IntegrationWorkspace Ws => _ws.Value;

    public string CacheDir => Ws.Combine("nuget-cache");

    public void Dispose()
    {
        if (_ws.IsValueCreated)
        {
            _ws.Value.Dispose();
        }
    }
}

public sealed class SampleChainIntegrationTests : IClassFixture<SampleChainIntegrationFixture>
{
    private readonly SampleChainIntegrationFixture _fixture;

    public SampleChainIntegrationTests(SampleChainIntegrationFixture fixture) => _fixture = fixture;

    private IntegrationWorkspace Ws => _fixture.Ws;

    private static string SampleProject => Path.Combine(RepositoryLayout.Root,
        "samples", "HelloBundlerApp", "HelloBundlerApp.csproj");

    // 宿主 publish RID：Windows 按架构分 win-x64/win-arm64，其余走 TestPlatform 的 target→RID 映射。
    private static string HostPublishRid =>
        TestPlatform.IsWindows
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64")
            : TestPlatform.IsMacOS ? TestPlatform.OsxPublishRid : TestPlatform.LinuxPublishRid;

    // 样品默认产物集（csproj _HelloBundlerFormatsDefault 按 BundlerTarget 前缀+宿主工具裁剪）：
    // win 宿主=nsis;msi;zip;targz；mac 宿主=dmg;pkg;zip;targz（独立 app 因许可旋钮显式缺席）；
    // glibc linux=deb;rpm;appimage;zip;targz；musl=alpineapk;zip;targz。
    private static string[] HostDefaultGlobs =>
        TestPlatform.IsWindows ? ["*-setup.exe", "*.msi", "*.zip", "*.tar.gz"]
        : TestPlatform.IsMacOS ? ["*.dmg", "*.pkg", "*.zip", "*.tar.gz"]
        : TestPlatform.IsMusl ? ["*.apk", "*.zip", "*.tar.gz"]
        : ["*.deb", "*.rpm", "*.AppImage", "*.zip", "*.tar.gz"];

    private string OutputFiles(string outDir) =>
        string.Join(",", Directory
            .EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName));

    [Fact]
    public void PublishProducesHostDefaultArtifacts()
    {
        ExternalTools.Require("dotnet");
        var outDir = Ws.Combine("sample-default");
        Dotnet.Publish(SampleProject, "Release",
            ["-r", HostPublishRid, "--packages", _fixture.CacheDir,
             $"-p:HelloBundlerOutputPath={outDir}"],
            "HelloBundlerApp publish failed", noRestore: false);
        foreach (var glob in HostDefaultGlobs)
        {
            Assert.True(Directory.EnumerateFiles(outDir, glob, SearchOption.AllDirectories).Any(),
                $"expected artifact '{glob}' under {outDir}: {OutputFiles(outDir)}");
        }
    }

    [Fact]
    public void PublishWithUpdateEmitsFeedAndSidecars()
    {
        ExternalTools.Require("dotnet");
        var dir = Ws.Combine("sample-update");
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "update.key");
        var material = UpdateKeyMaterial.Generate();
        material.Save(keyPath);
        var outDir = Path.Combine(dir, "out");
        Dotnet.Publish(SampleProject, "Release",
            ["-r", HostPublishRid, "--packages", _fixture.CacheDir,
             $"-p:HelloBundlerOutputPath={outDir}",
             "-p:HelloBundlerUpdateEnabled=true",
             "-p:HelloBundlerUpdateFeedUrl=https://updates.example.com/feed",
             "-p:HelloBundlerUpdateChannel=stable",
             $"-p:HelloBundlerUpdateSigningKeyFile={keyPath}",
             $"-p:HelloBundlerUpdatePublicKey={material.PublicPointBase64()}"],
            "HelloBundlerApp update-enabled publish failed", noRestore: false);

        // 通道清单+清单签名（零适配产物也发空清单——样品各宿主均有适配格式，必非空）
        Assert.True(File.Exists(Path.Combine(outDir, "bundler-update-feed.stable.json")),
            "channel feed missing: " + OutputFiles(outDir));
        Assert.True(File.Exists(Path.Combine(outDir, "bundler-update-feed.stable.json.sig")),
            "feed signature missing: " + OutputFiles(outDir));

        // 身份侧车随载荷进包：zip 件内必带 bundler-update.json；
        // 产物级 .sig/.blockmap 断言须钉在已定位产物上——裸 *.sig 会被清单签名遮蔽成恒真。
        var zip = Directory.EnumerateFiles(outDir, "*.zip", SearchOption.AllDirectories).FirstOrDefault();
        Assert.NotNull(zip);
        Assert.True(File.Exists(zip + ".sig"), "artifact .sig sidecar missing: " + zip);
        Assert.True(File.Exists(zip + ".blockmap"), "artifact .blockmap sidecar missing: " + zip);
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e =>
            e.FullName.EndsWith("bundler-update.json", StringComparison.Ordinal));
    }
}
