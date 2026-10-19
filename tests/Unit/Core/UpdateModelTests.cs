using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;

namespace Bundler.UnitTests.Core;

public sealed class UpdateKeyMaterialTests : TempBase
{
    [Fact]
    public void Generate_ProducesP256Pair()
    {
        var key = UpdateKeyMaterial.Generate();
        Assert.Equal("ec-p256", key.Kty);
        Assert.NotNull(key.D);
        Assert.Equal(32, Convert.FromBase64String(key.X).Length);
        Assert.Equal(32, Convert.FromBase64String(key.Y).Length);
        Assert.Equal(65, Convert.FromBase64String(key.PublicPointBase64()).Length);
        Assert.Equal(0x04, Convert.FromBase64String(key.PublicPointBase64())[0]);
    }

    [Fact]
    public void SaveLoad_RoundTrip_NoOverwrite()
    {
        var dir = Temp("bundler-key");
        var path = Path.Combine(dir.FullName, "key.json");
        var key = UpdateKeyMaterial.Generate();
        key.Save(path);

        var loaded = UpdateKeyMaterial.Load(path);
        Assert.Equal(key.X, loaded.X);
        Assert.Equal(key.Y, loaded.Y);
        Assert.Equal(key.D, loaded.D);

        Assert.Throws<IOException>(() => key.Save(path)); // CreateNew 拒覆盖
    }

    [Fact]
    public void Load_Garbage_Throws()
    {
        var path = TempPath($"key-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"kty\":\"nope\"}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => UpdateKeyMaterial.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromPublicPoint_RoundTrips_PointPrefixEnforced()
    {
        var key = UpdateKeyMaterial.Generate();
        var point = key.PublicPointBase64();
        var restored = UpdateKeyMaterial.FromPublicPoint(point);
        Assert.Equal(key.X, restored.X);
        Assert.Equal(key.Y, restored.Y);
        Assert.Null(restored.D);

        Assert.Throws<InvalidOperationException>(() =>
            UpdateKeyMaterial.FromPublicPoint(Convert.ToBase64String(new byte[64])));
    }

    [Fact]
    public void ToPrivateParameters_RequiresPrivateKey()
    {
        var publicOnly = UpdateKeyMaterial.FromPublicPoint(UpdateKeyMaterial.Generate().PublicPointBase64());
        Assert.Throws<InvalidOperationException>(() => publicOnly.ToPrivateParameters());
    }

    [Fact]
    public void Load_NullCoordinates_Throws_NotNre()
    {
        // JSON 显式 null 字段曾 NRE——必须按"非法密钥文件"分类。
        var path = TempPath($"key-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"kty\":\"ec-p256\",\"x\":null,\"y\":null}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => UpdateKeyMaterial.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_WrongLengthCoordinates_Throws()
    {
        // base64 合法但坐标非 32 字节——不是 P-256 公钥。
        var path = TempPath($"key-{Guid.NewGuid():N}.json");
        var short64 = Convert.ToBase64String(new byte[16]);
        File.WriteAllText(path, $"{{\"kty\":\"ec-p256\",\"x\":\"{short64}\",\"y\":\"{short64}\"}}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => UpdateKeyMaterial.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadBase64_Throws()
    {
        var path = TempPath($"key-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"kty\":\"ec-p256\",\"x\":\"!!!\",\"y\":\"!!!\"}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => UpdateKeyMaterial.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadPrivateScalar_Throws()
    {
        // d 在场即校验：坏 base64/非 32 字节标量不能漏到签名时炸 FormatException；
        // 缺失 d 仍是合法"只含公钥"件形态。
        var coord = Convert.ToBase64String(new byte[32]);
        var path = TempPath($"key-{Guid.NewGuid():N}.json");
        File.WriteAllText(path,
            $"{{\"kty\":\"ec-p256\",\"x\":\"{coord}\",\"y\":\"{coord}\",\"d\":\"!!!\"}}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => UpdateKeyMaterial.Load(path));
        }
        finally
        {
            File.Delete(path);
        }

        var shortPath = TempPath($"key-{Guid.NewGuid():N}.json");
        File.WriteAllText(shortPath,
            $"{{\"kty\":\"ec-p256\",\"x\":\"{coord}\",\"y\":\"{coord}\"," +
            $"\"d\":\"{Convert.ToBase64String(new byte[16])}\"}}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => UpdateKeyMaterial.Load(shortPath));
        }
        finally
        {
            File.Delete(shortPath);
        }

        var publicPath = TempPath($"key-{Guid.NewGuid():N}.json");
        File.WriteAllText(publicPath,
            $"{{\"kty\":\"ec-p256\",\"x\":\"{coord}\",\"y\":\"{coord}\"}}");
        try
        {
            var loaded = UpdateKeyMaterial.Load(publicPath);
            Assert.Null(loaded.D);
        }
        finally
        {
            File.Delete(publicPath);
        }
    }
}

public sealed class UpdateFeedModelTests
{
    [Theory]
    [InlineData("latest", "bundler-update-feed.latest.json")]
    [InlineData("stable", "bundler-update-feed.stable.json")]
    [InlineData("beta_1", "bundler-update-feed.beta_1.json")]
    public void FeedFileName_ChannelMapped(string channel, string expected)
    {
        Assert.Equal(expected, UpdateFeed.FeedFileName(channel));
    }

    [Fact]
    public void Feed_JsonRoundTrip()
    {
        var feed = new UpdateFeed
        {
            Version = "1.2.3",
            Channel = "latest",
            PublishedAt = "2025-01-01T00:00:00Z",
            Artifacts =
            [
                new UpdateFeedArtifact
                {
                    Target = "linux-x86_64", Format = "appimage",
                    Url = "a.AppImage", File = "a.AppImage",
                    Size = 10, Sha256 = "deadbeef", Signature = "c2ln",
                    BlockMap = "a.blockmap"
                }
            ]
        };

        using var stream = new MemoryStream();
        UpdateJson.WriteFeed(stream, feed);
        stream.Position = 0;
        var loaded = UpdateJson.ReadFeed(stream);

        Assert.NotNull(loaded);
        Assert.Equal("1.2.3", loaded!.Version);
        Assert.Single(loaded.Artifacts);
        Assert.Equal("linux-x86_64", loaded.Artifacts[0].Target);
        Assert.Equal("a.blockmap", loaded.Artifacts[0].BlockMap);
    }
}

public sealed class UpdateInstallIdentityTests : TempBase
{
    [Fact]
    public void Write_TryRead_RoundTrip()
    {
        var dir = Temp("bundler-id");
        UpdateInstallIdentity.Write(dir.FullName, new UpdateInstallIdentity
        {
            Format = "deb", Target = "linux-x86_64", Channel = "stable",
            FeedUrl = "https://example.com/feed", PublicKey = "pk"
        });

        var path = Path.Combine(dir.FullName, UpdateInstallIdentity.FileName);
        Assert.True(File.Exists(path));

        var identity = UpdateInstallIdentity.TryRead(dir.FullName);
        Assert.NotNull(identity);
        Assert.Equal("deb", identity!.Format);
        Assert.Equal("stable", identity.Channel);
        Assert.Equal("https://example.com/feed", identity.FeedUrl);
    }

    [Fact]
    public void TryRead_Missing_ReturnsNull()
    {
        var dir = Temp("bundler-id");
        Assert.Null(UpdateInstallIdentity.TryRead(dir.FullName));
    }

    [Fact]
    public void TryRead_IoFailure_ReturnsNull()
    {
        // 旁车存在但读不到（排他锁/权限/竞争删除）与"解析失败"同义返回 null——
        // 探测语义不该把原始 IO 异常漏给调用方。
        var dir = Temp("bundler-id");
        var path = Path.Combine(dir.FullName, UpdateInstallIdentity.FileName);
        File.WriteAllText(path, "{}");
        using var exclusive = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Null(UpdateInstallIdentity.TryRead(dir.FullName));
    }

    // v1 兼容断言：含 `pkg` 的新旁车喂给 v1 形读端（无 pkg 成员的 v1 模型）不炸——
    // netstandard2.0 走 DataContractJsonSerializer、net10 走 System.Text.Json，
    // 两条 v1 读径对未知 JSON 成员均为忽略语义；已知字段照常解析。
    [Fact]
    public void V1Reader_UnknownPkgField_Ignored()
    {
        var dir = Temp("bundler-id-v1");
        UpdateInstallIdentity.Write(dir.FullName, new UpdateInstallIdentity
        {
            Format = "deb", Target = "linux-x86_64", Channel = "stable",
            FeedUrl = "https://example.com/feed", PublicKey = "pk", Pkg = "example-app",
        });
        var json = File.ReadAllText(Path.Combine(dir.FullName, UpdateInstallIdentity.FileName));
        Assert.Contains("\"pkg\"", json, StringComparison.Ordinal); // 确带 v1 未知字段，否则断言落空转

        // v1 netstandard2.0 读径：DataContractJsonSerializer。
        var viaDcs = (V1IdentityShape)new DataContractJsonSerializer(typeof(V1IdentityShape))
            .ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(json)))!;
        Assert.Equal("deb", viaDcs.Format);
        Assert.Equal("linux-x86_64", viaDcs.Target);
        Assert.Equal("stable", viaDcs.Channel);
        Assert.Equal("https://example.com/feed", viaDcs.FeedUrl);
        Assert.Equal("pk", viaDcs.PublicKey);

        // v1 net10 读径：System.Text.Json（与源生成上下文同一命名策略——rid 自定义映射在此生效）。
        var stjOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = UpdateJsonNamingPolicy.Instance,
            IncludeFields = true,
        };
        var viaStj = JsonSerializer.Deserialize<V1IdentityShape>(json, stjOptions)!;
        Assert.Equal("deb", viaStj.Format);
        Assert.Equal("linux-x86_64", viaStj.Target);
        Assert.Equal("stable", viaStj.Channel);
        Assert.Equal("https://example.com/feed", viaStj.FeedUrl);
        Assert.Equal("pk", viaStj.PublicKey);

        // 新读端同件照常读出 pkg——新增字段双向兼容。
        var current = UpdateInstallIdentity.TryRead(dir.FullName);
        Assert.NotNull(current);
        Assert.Equal("example-app", current!.Pkg);
    }

    // v1 时代身份模型形——UpdateInstallIdentity 去掉 B1 起新增的 `pkg` 成员。
    [DataContract]
    private sealed class V1IdentityShape
    {
        [DataMember(Name = "format")] public string Format = "";
        [DataMember(Name = "rid")] public string Target = "";
        [DataMember(Name = "channel")] public string Channel = "";
        [DataMember(Name = "feedUrl")] public string FeedUrl = "";
#pragma warning disable CS0649 // 字段只由反序列化赋值——v1 读端模型复刻，保留 nullable 原状
        [DataMember(Name = "publicKey")] public string? PublicKey;
#pragma warning restore CS0649
    }

    [Fact]
    public void SidecarPath_AppendsSuffix()
    {
        Assert.Equal("install.AppImage.bundler-update.json",
            UpdateInstallIdentity.SidecarPath("install.AppImage"));
    }
}

public sealed class UpdateIdentitySidecarTests : TempBase
{
    [Fact]
    public void WriteIfEnabled_NullUpdate_NoOp()
    {
        var dir = Temp("bundler-sc");
        UpdateIdentitySidecar.WriteIfEnabled(
            dir.FullName, null, PackageFormat.Deb, "linux-x86_64");
        Assert.Empty(Directory.GetFiles(dir.FullName));
    }

    [Fact]
    public void WriteIfEnabled_WritesSidecar_WithDefaults()
    {
        var dir = Temp("bundler-sc");
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(dir.FullName, "key.json");
        key.Save(keyPath);

        UpdateIdentitySidecar.WriteIfEnabled(
            dir.FullName,
            new UpdateBundleConfiguration
            {
                FeedUrl = "https://example.com/feed",
                SigningKeyFile = keyPath
            },
            PackageFormat.AppImage, "linux-x86_64");

        var identity = UpdateInstallIdentity.TryRead(dir.FullName);
        Assert.NotNull(identity);
        Assert.Equal("appimage", identity!.Format);
        Assert.Equal("linux-x86_64", identity.Target);
        Assert.Equal("latest", identity.Channel); // 空 channel 按 latest 落地
        Assert.Equal("https://example.com/feed", identity.FeedUrl);
        Assert.Equal(key.PublicPointBase64(), identity.PublicKey); // 公钥从私钥派生
    }
}

public sealed class UpdateSignatureVerifierTests
{
    [Fact]
    public void Verify_P1363AndDer_BothAccepted()
    {
        var key = UpdateKeyMaterial.Generate();
        var data = System.Text.Encoding.UTF8.GetBytes("payload");

        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportParameters(key.ToPrivateParameters());
        var p1363 = ecdsa.SignData(data,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var der = ecdsa.SignData(data,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(UpdateSignatureVerifier.Verify(data, p1363, key));
        Assert.True(UpdateSignatureVerifier.Verify(data, der, key));
        Assert.False(UpdateSignatureVerifier.Verify(data, p1363, UpdateKeyMaterial.Generate()));
        Assert.False(UpdateSignatureVerifier.Verify(
            System.Text.Encoding.UTF8.GetBytes("other"), p1363, key));
    }

    [Fact]
    public void P1363DerConversion_RoundTrips()
    {
        var key = UpdateKeyMaterial.Generate();
        var data = "payload"u8.ToArray();
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportParameters(key.ToPrivateParameters());
        var p1363 = ecdsa.SignData(data,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var der = UpdateSignatureVerifier.ToDer(p1363);
        var back = UpdateSignatureVerifier.ToP1363(der);
        Assert.Equal(p1363, back);
    }
}

public sealed class UpdateBootstrapperTests : TempBase
{
    [Theory]
    [InlineData("windows-x86_64", "bundler-updater.exe")]
    [InlineData("linux-x86_64", "bundler-updater")]
    [InlineData("macos-aarch64", "bundler-updater")]
    public void FileNameFor_PlatformSuffix(string target, string expected)
    {
        Assert.Equal(expected, UpdateBootstrapper.FileNameFor(target));
    }

    [Fact]
    public void TryResolve_PerTargetWins_OverPosix()
    {
        var dir = Temp("bundler-boot");
        var perTarget = Path.Combine(dir.FullName, "linux-x86_64");
        Directory.CreateDirectory(perTarget);
        File.WriteAllText(Path.Combine(perTarget, "bundler-updater"), "aot");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "posix"));
        File.WriteAllText(Path.Combine(dir.FullName, "posix", "bundler-updater.sh"), "sh");

        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        Assert.True(UpdateBootstrapper.TryResolve(update, "linux-x86_64", out var source));
        Assert.Contains("linux-x86_64", source);
    }

    [Fact]
    public void TryResolve_MuslSibling_UsedWhenExactMissing()
    {
        var dir = Temp("bundler-boot");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "linux-musl-aarch64"));
        File.WriteAllText(Path.Combine(dir.FullName, "linux-musl-aarch64", "bundler-updater"), "musl");

        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        // linux glibc 目标无字面件 → 收敛 musl 姊妹件（static-pie 双宿主通吃）。
        Assert.True(UpdateBootstrapper.TryResolve(update, "linux-aarch64", out var source));
        Assert.Contains(Path.Combine("linux-musl-aarch64", "bundler-updater"), source);
        // musl 目标自身走字面命中，不再二次映射。
        Assert.True(UpdateBootstrapper.TryResolve(update, "linux-musl-aarch64", out source));
        Assert.Contains(Path.Combine("linux-musl-aarch64", "bundler-updater"), source);
    }

    [Fact]
    public void TryResolve_ExactWins_OverMuslSibling()
    {
        var dir = Temp("bundler-boot");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "linux-x86_64"));
        Directory.CreateDirectory(Path.Combine(dir.FullName, "linux-musl-x86_64"));
        File.WriteAllText(Path.Combine(dir.FullName, "linux-x86_64", "bundler-updater"), "glibc");
        File.WriteAllText(Path.Combine(dir.FullName, "linux-musl-x86_64", "bundler-updater"), "musl");

        // 显式目录的字面 per-target 件优先于 musl 收敛（开发覆盖自有 glibc 件）。
        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        Assert.True(UpdateBootstrapper.TryResolve(update, "linux-x86_64", out var source));
        Assert.Contains(Path.Combine("linux-x86_64", "bundler-updater"), source);
    }

    [Fact]
    public void TryResolve_PosixFallback_NonWindows()
    {
        var dir = Temp("bundler-boot");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "posix"));
        File.WriteAllText(Path.Combine(dir.FullName, "posix", "bundler-updater.sh"), "sh");

        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        Assert.True(UpdateBootstrapper.TryResolve(update, "linux-x86_64", out var source));
        Assert.EndsWith("bundler-updater.sh", source);
    }

    [Fact]
    public void TryResolve_PosixNeverForWindows()
    {
        var dir = Temp("bundler-boot");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "posix"));
        File.WriteAllText(Path.Combine(dir.FullName, "posix", "bundler-updater.sh"), "sh");

        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        Assert.False(UpdateBootstrapper.TryResolve(update, "windows-x86_64", out _));
    }

    [Fact]
    public void TryResolve_Missing_ReturnsFalse()
    {
        var dir = Temp("bundler-boot");
        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        Assert.False(UpdateBootstrapper.TryResolve(update, "linux-x86_64", out _));
    }

    [Fact]
    public void Inject_CopiesWithFileName()
    {
        var dir = Temp("bundler-boot");
        var targetDir = Temp("bundler-payload");
        var perTarget = Path.Combine(dir.FullName, "linux-x86_64");
        Directory.CreateDirectory(perTarget);
        File.WriteAllText(Path.Combine(perTarget, "bundler-updater"), "binary");

        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        var name = UpdateBootstrapper.InjectOrThrow(targetDir.FullName, update, "linux-x86_64");

        Assert.Equal("bundler-updater", name);
        Assert.True(File.Exists(Path.Combine(targetDir.FullName, "bundler-updater")));
    }

    [Fact]
    public void InjectOrThrow_ThrowsWhenUnresolvable()
    {
        var dir = Temp("bundler-boot");
        var targetDir = Temp("bundler-payload");
        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };

        // 缺件不再容忍 null——死更新链必须带诊断抛出。
        var error = Assert.Throws<InvalidOperationException>(
            () => UpdateBootstrapper.InjectOrThrow(targetDir.FullName, update, "linux-x86_64"));
        Assert.Contains("bundler-updater", error.Message);
    }

    [Fact]
    public void TryResolve_RejectsNonCanonicalTarget()
    {
        var dir = Temp("bundler-boot");
        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };

        // target 直拼目录段——分隔符/上跳会被当成工具目录路径的一部分。
        Assert.Throws<ArgumentException>(
            () => UpdateBootstrapper.TryResolve(update, "../escape", out _));
        Assert.Throws<ArgumentException>(
            () => UpdateBootstrapper.TryResolve(update, "linux/../x", out _));
        Assert.Throws<ArgumentException>(
            () => UpdateBootstrapper.TryResolve(update, "", out _));
    }
}

public sealed class UpdatePayloadStagingTests : TempBase
{
    private static BundleBuildContext Context(string workDir, UpdateBundleConfiguration? update)
    {
        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb, "unused", "app", "out", false);
        return new BundleBuildContext(
            new BundleConfiguration { Update = update }, item, workDir,
            NullBundleLogger.Instance);
    }

    [Fact]
    public void EnsureStaged_NoUpdate_ReturnsSameItem()
    {
        var work = Temp("bundler-work");
        var input = Temp("bundler-input");
        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb,
            input.FullName, "app", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration(), item, work.FullName, NullBundleLogger.Instance);

        var staged = UpdatePayloadStaging.EnsureStaged(context, item);
        Assert.Equal(input.FullName, staged.InputDirectory);
        Assert.False(Directory.Exists(Path.Combine(work.FullName, "update-payload")));
    }

    [Fact]
    public void EnsureStaged_CopiesInputToWorkDir_WritesSidecar()
    {
        var work = Temp("bundler-work");
        var input = Temp("bundler-input");
        File.WriteAllText(Path.Combine(input.FullName, "app"), "bin");
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(work.FullName, "key.json");
        key.Save(keyPath);
        // 引导件目录：posix 脚本件让 Inject 有件可注
        var boot = Directory.CreateDirectory(Path.Combine(work.FullName, "boot", "posix"));
        File.WriteAllText(Path.Combine(boot.FullName, "bundler-updater.sh"), "sh");

        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb,
            input.FullName, "app", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration
            {
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://x", SigningKeyFile = keyPath,
                    BootstrapperDirectory = Path.Combine(work.FullName, "boot")
                }
            },
            item, work.FullName, NullBundleLogger.Instance);

        var staged = UpdatePayloadStaging.EnsureStaged(context, item);

        Assert.Contains("update-payload", staged.InputDirectory);
        Assert.True(File.Exists(Path.Combine(staged.InputDirectory, "app")));
        Assert.NotNull(UpdateInstallIdentity.TryRead(staged.InputDirectory));
        Assert.True(File.Exists(Path.Combine(staged.InputDirectory, "bundler-updater")));
        // 用户输入目录未被改
        Assert.False(File.Exists(Path.Combine(input.FullName, "bundler-update.json")));
    }

    [Fact]
    public void EnsureStaged_MissingBootstrapper_Throws()
    {
        var work = Temp("bundler-work");
        var input = Temp("bundler-input");
        File.WriteAllText(Path.Combine(input.FullName, "app"), "bin");
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(work.FullName, "key.json");
        key.Save(keyPath);
        // 显式引导件目录下既无 per-target 件也无 posix 脚本件：
        // 配置了 BootstrapperDirectory 后不再查内嵌资源兜底——必走缺件抛错路径。
        var boot = Directory.CreateDirectory(Path.Combine(work.FullName, "boot"));

        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb,
            input.FullName, "app", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration
            {
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://x", SigningKeyFile = keyPath,
                    BootstrapperDirectory = boot.FullName
                }
            },
            item, work.FullName, NullBundleLogger.Instance);

        var exception = Assert.Throws<InvalidOperationException>(
            () => UpdatePayloadStaging.EnsureStaged(context, item));
        Assert.Contains("no bootstrapper", exception.Message);
    }

    [Fact]
    public void EnsureStaged_NoEmbeddedBootstrapperForTarget_Throws()
    {
        var work = Temp("bundler-work");
        var input = Temp("bundler-input");
        File.WriteAllText(Path.Combine(input.FullName, "app"), "bin");
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(work.FullName, "key.json");
        key.Save(keyPath);

        // windows-riscv64 无内嵌 per-target 件；windows 目标又不接受 posix 脚本件兜底。
        var item = new BundlePlanItem(
            BundleTarget.Parse("windows-riscv64"), PackageFormat.Zip,
            input.FullName, "Sample.exe", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration
            {
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://x", SigningKeyFile = keyPath
                }
            },
            item, work.FullName, NullBundleLogger.Instance);

        Assert.Throws<InvalidOperationException>(
            () => UpdatePayloadStaging.EnsureStaged(context, item));
    }

    [Fact]
    public void EnsureStaged_InstallerReplayFormat_SkipsBootstrapper()
    {
        // nsis/msi 更新=重跑安装器：载荷不带换包执行体——无引导件可注也不抛。
        var work = Temp("bundler-work");
        var input = Temp("bundler-input");
        File.WriteAllText(Path.Combine(input.FullName, "Sample.exe"), "bin");
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(work.FullName, "key.json");
        key.Save(keyPath);

        var item = new BundlePlanItem(
            BundleTarget.Parse("windows-x86_64"), PackageFormat.Nsis,
            input.FullName, "Sample.exe", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration
            {
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://x", SigningKeyFile = keyPath
                }
            },
            item, work.FullName, NullBundleLogger.Instance);

        var staged = UpdatePayloadStaging.EnsureStaged(context, item);

        Assert.Contains("update-payload", staged.InputDirectory);
        Assert.NotNull(UpdateInstallIdentity.TryRead(staged.InputDirectory));
        Assert.False(File.Exists(Path.Combine(staged.InputDirectory, "bundler-updater.exe")));
    }

    [Fact]
    public void EnsureStaged_AlreadyInsideWork_WritesInPlace()
    {
        var work = Temp("bundler-work");
        var staged = Directory.CreateDirectory(Path.Combine(work.FullName, "signed-payload"));
        File.WriteAllText(Path.Combine(staged.FullName, "app"), "bin");
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(work.FullName, "key.json");
        key.Save(keyPath);
        Directory.CreateDirectory(Path.Combine(work.FullName, "boot", "posix"));
        File.WriteAllText(Path.Combine(work.FullName, "boot", "posix", "bundler-updater.sh"), "sh");

        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb,
            staged.FullName, "app", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration
            {
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://x", SigningKeyFile = keyPath,
                    BootstrapperDirectory = Path.Combine(work.FullName, "boot")
                }
            },
            item, work.FullName, NullBundleLogger.Instance);

        var result = UpdatePayloadStaging.EnsureStaged(context, item);

        Assert.Equal(staged.FullName, result.InputDirectory);
        Assert.NotNull(UpdateInstallIdentity.TryRead(staged.FullName));
    }

    [Fact]
    public void EnsureStaged_ReservedSidecarName_Rejected()
    {
        var work = Temp("bundler-work");
        var input = Temp("bundler-input");
        // 用户载荷自带保留名：显式拒绝，不静默覆盖身份旁车。
        File.WriteAllText(Path.Combine(input.FullName, UpdateIdentitySidecar.FileName), "{}");
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(work.FullName, "key.json");
        key.Save(keyPath);

        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb,
            input.FullName, "app", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration
            {
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://x", SigningKeyFile = keyPath,
                }
            },
            item, work.FullName, NullBundleLogger.Instance);

        var error = Assert.Throws<InvalidOperationException>(
            () => UpdatePayloadStaging.EnsureStaged(context, item));
        Assert.Contains(UpdateIdentitySidecar.FileName, error.Message);
    }

    [Fact]
    public void EnsureStaged_ReservedBootstrapperName_Rejected()
    {
        var work = Temp("bundler-work");
        var input = Temp("bundler-input");
        File.WriteAllText(Path.Combine(input.FullName, "bundler-updater"), "squat");
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(work.FullName, "key.json");
        key.Save(keyPath);

        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb,
            input.FullName, "app", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration
            {
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://x", SigningKeyFile = keyPath,
                }
            },
            item, work.FullName, NullBundleLogger.Instance);

        var error = Assert.Throws<InvalidOperationException>(
            () => UpdatePayloadStaging.EnsureStaged(context, item));
        Assert.Contains("bundler-updater", error.Message);
    }

    [Fact]
    public void EnsureStaged_StagingInsideInput_Rejected()
    {
        // 工作目录落在输入树内时 staged 会把自己再拷进自己——前置拒掉。
        var input = Temp("bundler-input");
        var work = Path.Combine(input.FullName, "work");
        Directory.CreateDirectory(work);
        var key = UpdateKeyMaterial.Generate();
        var keyPath = Path.Combine(work, "key.json");
        key.Save(keyPath);

        var item = new BundlePlanItem(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.Deb,
            input.FullName, "app", "out", false);
        var context = new BundleBuildContext(
            new BundleConfiguration
            {
                Update = new UpdateBundleConfiguration
                {
                    FeedUrl = "https://x", SigningKeyFile = keyPath,
                }
            },
            item, work, NullBundleLogger.Instance);

        Assert.Throws<InvalidOperationException>(
            () => UpdatePayloadStaging.EnsureStaged(context, item));
    }
}
