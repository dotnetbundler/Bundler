using DotNet.Bundler;
using DotNet.Bundler.Core.Update;
using DotNet.Bundler.Update;

namespace Bundler.UnitTests.Update;

public sealed class EcdsaSignerTests : TempBase
{

    [Fact]
    public void Sign_Verify_RoundTrip()
    {
        var key = UpdateKeyMaterial.Generate();
        var data = "payload"u8.ToArray();
        var signature = EcdsaSigner.Sign(data, key);
        Assert.Equal(64, signature.Length); // P1363 r||s
        Assert.True(EcdsaSigner.Verify(data, signature, key));
    }

    [Fact]
    public void Sign_TamperedData_FailsVerify()
    {
        var key = UpdateKeyMaterial.Generate();
        var signature = EcdsaSigner.Sign("a"u8.ToArray(), key);
        Assert.False(EcdsaSigner.Verify("b"u8.ToArray(), signature, key));
    }

    [Fact]
    public void Sign_WrongKey_FailsVerify()
    {
        var key = UpdateKeyMaterial.Generate();
        var other = UpdateKeyMaterial.Generate();
        var signature = EcdsaSigner.Sign("x"u8.ToArray(), key);
        Assert.False(EcdsaSigner.Verify("x"u8.ToArray(), signature, other));
    }

    [Fact]
    public void ToP1363_ToDer_RoundTrip()
    {
        var key = UpdateKeyMaterial.Generate();
        var p1363 = EcdsaSigner.Sign("data"u8.ToArray(), key);
        var der = EcdsaSigner.ToDer(p1363);
        var back = EcdsaSigner.ToP1363(der);
        Assert.Equal(p1363, back);
    }

    [Fact]
    public void ToP1363_DerInput_RoundTrips()
    {
        // ToP1363 须吃 DER 输入（OpenSsl 出 DER）：签->转 DER->转回仍验过
        var key = UpdateKeyMaterial.Generate();
        var p1363 = EcdsaSigner.Sign("data"u8.ToArray(), key);
        Assert.True(EcdsaSigner.Verify(
            "data"u8.ToArray(), EcdsaSigner.ToP1363(EcdsaSigner.ToDer(p1363)), key));
    }

    [Fact]
    public async Task SignFile_Verifies()
    {
        // ECDSA 非确定性（随机 k）：断可验性不比字节
        var key = UpdateKeyMaterial.Generate();
        var file = Path.Combine(Root, "f.bin");
        var data = new byte[100_000];
        new Random(7).NextBytes(data);
        await File.WriteAllBytesAsync(file, data, TestContext.Current.CancellationToken);
        Assert.True(EcdsaSigner.VerifyFile(file, EcdsaSigner.SignFile(file, key), key));
        Assert.True(EcdsaSigner.Verify(data, EcdsaSigner.SignFile(file, key), key));
    }
}

public sealed class UpdateManifestEmitterTests : TempBase
{
    private readonly string _output;
    private readonly string _keyPath;

    public UpdateManifestEmitterTests()
    {
        _output = Path.Combine(Root, "out");
        Directory.CreateDirectory(_output);
        _keyPath = Path.Combine(Root, "key.json");
        UpdateKeyMaterial.Generate().Save(_keyPath);
    }


    private BundleConfiguration Config(string channel = "") => new()
    {
        ProductName = "Sample",
        Identifier = "com.example.sample",
        Version = "1.2.3",
        OutputDirectory = _output,
        Update = new UpdateBundleConfiguration
        {
            FeedUrl = "https://updates.example.com/",
            Channel = channel,
            SigningKeyFile = _keyPath
        },
        Targets = []
    };

    private string Artifact(string name, string content = "x")
    {
        var path = Path.Combine(_output, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void IsUpdateAdapted_CoversExpectedFormats()
    {
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.Nsis));
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.Msi));
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.App));
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.AppImage));
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.Zip));
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.TarGz));
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.Deb));
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.Rpm));
        Assert.True(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.AlpineApk));
        Assert.False(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.Dmg));
        Assert.False(UpdateManifestEmitter.IsUpdateAdapted(PackageFormat.Pkg));
    }

    [Fact]
    public void FeedFileName_ChannelMapped()
    {
        Assert.Equal("bundler-update-feed.latest.json",
            UpdateManifestEmitter.FeedFileName("latest"));
        Assert.Equal("bundler-update-feed.beta.json",
            UpdateManifestEmitter.FeedFileName("beta"));
    }

    [Fact]
    public async Task EmitAsync_ProducesSigBlockmapFeed()
    {
        var artifact = Artifact("sample.zip");
        var produced = await UpdateManifestEmitter.EmitAsync(Config(),
            [new BundleArtifact(PackageFormat.Zip, "windows-x86_64", artifact)],
            TestContext.Current.CancellationToken);

        var sigPath = artifact + ".sig";
        var mapPath = artifact + ".blockmap";
        var feedPath = Path.Combine(_output, "bundler-update-feed.latest.json");
        Assert.True(File.Exists(sigPath));
        Assert.True(File.Exists(mapPath));
        Assert.True(File.Exists(feedPath));
        Assert.True(File.Exists(feedPath + ".sig"));
        Assert.Equal(64, File.ReadAllBytes(sigPath).Length);
        Assert.Contains(sigPath, produced);
        Assert.Contains(mapPath, produced);
        Assert.Contains(feedPath, produced);
        Assert.Contains(feedPath + ".sig", produced);
    }

    [Fact]
    public async Task EmitAsync_FeedArtifactFields()
    {
        var artifact = Artifact("a.zip", "content");
        await UpdateManifestEmitter.EmitAsync(Config("beta"),
            [new BundleArtifact(PackageFormat.Zip, "linux-x86_64", artifact)],
            TestContext.Current.CancellationToken);

        var feed = ReadFeed("beta");
        Assert.Equal("1.2.3", feed.GetProperty("version").GetString());
        Assert.Equal("beta", feed.GetProperty("channel").GetString());
        var artifacts = feed.GetProperty("artifacts");
        Assert.Equal(1, artifacts.GetArrayLength());
        var entry = artifacts[0];
        Assert.Equal("linux-x86_64", entry.GetProperty("rid").GetString());
        Assert.Equal("zip", entry.GetProperty("format").GetString());
        Assert.Equal("a.zip", entry.GetProperty("file").GetString());
        Assert.Equal("a.zip", entry.GetProperty("url").GetString());
        Assert.Equal(new FileInfo(artifact).Length, entry.GetProperty("size").GetInt64());
        Assert.Equal(DotNet.Bundler.Core.Hashes.Sha256HexFile(artifact),
            entry.GetProperty("sha256").GetString());
        Assert.Equal("a.zip.blockmap", entry.GetProperty("blockmap").GetString());
        // 签名旁车与 feed 内联签名一致且可验
        var inlineSig = Convert.FromBase64String(entry.GetProperty("sig").GetString()!);
        Assert.Equal(File.ReadAllBytes(artifact + ".sig"), inlineSig);
        Assert.True(EcdsaSigner.VerifyFile(artifact, inlineSig,
            UpdateKeyMaterial.Load(_keyPath)));
    }

    [Fact]
    public async Task EmitAsync_NoAdaptedArtifacts_StillEmitsSignedFeed()
    {
        // dmg 是仅剩的非覆盖产物形态之一——零适配产物仍须发射签名空 feed。
        var artifact = Artifact("x.dmg");
        var produced = await UpdateManifestEmitter.EmitAsync(Config(),
            [new BundleArtifact(PackageFormat.Dmg, "macos-aarch64", artifact)],
            TestContext.Current.CancellationToken);

        var feed = ReadFeed("latest");
        Assert.Equal(0, feed.GetProperty("artifacts").GetArrayLength());
        Assert.False(File.Exists(artifact + ".sig"));
        Assert.True(File.Exists(Path.Combine(_output, "bundler-update-feed.latest.json.sig")));
    }

    [Fact]
    public async Task EmitAsync_MissingArtifactFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            UpdateManifestEmitter.EmitAsync(Config(),
                [new BundleArtifact(PackageFormat.Zip, "linux-x86_64",
                    Path.Combine(_output, "ghost.zip"))],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmitAsync_NoSigningKey_Throws()
    {
        var config = Config();
        config = new BundleConfiguration
        {
            ProductName = config.ProductName,
            Identifier = config.Identifier,
            Version = config.Version,
            OutputDirectory = config.OutputDirectory,
            Update = new UpdateBundleConfiguration { FeedUrl = "https://x/" },
            Targets = []
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateManifestEmitter.EmitAsync(config,
                [new BundleArtifact(PackageFormat.Zip, "linux-x86_64", Artifact("a.zip"))],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmitAsync_NoUpdateSection_Throws()
    {
        var config = new BundleConfiguration
        {
            ProductName = "S", Identifier = "com.x", Version = "1.0",
            OutputDirectory = _output, Targets = []
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            UpdateManifestEmitter.EmitAsync(config, [],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmitAsync_DuplicateArtifact_PicksFinalBytes()
    {
        // 同 target/format 同路径重复条目 → feed 收最终件而非首件
        var artifact = Artifact("dup.zip", "old");
        var artifacts = new List<BundleArtifact>
        {
            new(PackageFormat.Zip, "linux-x86_64", artifact)
        };
        // 首个 EmitAsync 看老字节
        await UpdateManifestEmitter.EmitAsync(Config(), artifacts,
            TestContext.Current.CancellationToken);
        // 复写同路径后重发射：feed 哈希必须跟最终字节
        File.WriteAllText(artifact, "new-bytes");
        await UpdateManifestEmitter.EmitAsync(Config(), artifacts,
            TestContext.Current.CancellationToken);
        var feed = ReadFeed("latest");
        var feedArtifacts = feed.GetProperty("artifacts");
        Assert.Equal(1, feedArtifacts.GetArrayLength());
        Assert.Equal(DotNet.Bundler.Core.Hashes.Sha256HexFile(artifact),
            feedArtifacts[0].GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task EmitAsync_FeedSignature_Verifies()
    {
        Artifact("z.zip");
        await UpdateManifestEmitter.EmitAsync(Config(),
            [new BundleArtifact(PackageFormat.Zip, "linux-x86_64",
                Path.Combine(_output, "z.zip"))],
            TestContext.Current.CancellationToken);
        var feedPath = Path.Combine(_output, "bundler-update-feed.latest.json");
        Assert.True(EcdsaSigner.VerifyFile(feedPath,
            File.ReadAllBytes(feedPath + ".sig"), UpdateKeyMaterial.Load(_keyPath)));
    }

    private System.Text.Json.JsonElement ReadFeed(string channel)
    {
        var path = Path.Combine(_output, $"bundler-update-feed.{channel}.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
