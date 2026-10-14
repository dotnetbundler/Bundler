using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;

namespace Bundler.UnitTests.Core;

public sealed class UpdateKeyMaterialTests
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
        var dir = Directory.CreateTempSubdirectory("bundler-key");
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
        var path = Path.Combine(Path.GetTempPath(), $"key-{Guid.NewGuid():N}.json");
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

public sealed class UpdateInstallIdentityTests
{
    [Fact]
    public void Write_TryRead_RoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("bundler-id");
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
        var dir = Directory.CreateTempSubdirectory("bundler-id");
        Assert.Null(UpdateInstallIdentity.TryRead(dir.FullName));
    }

    [Fact]
    public void SidecarPath_AppendsSuffix()
    {
        Assert.Equal("install.AppImage.bundler-update.json",
            UpdateInstallIdentity.SidecarPath("install.AppImage"));
    }
}

public sealed class UpdateIdentitySidecarTests
{
    [Fact]
    public void WriteIfEnabled_NullUpdate_NoOp()
    {
        var dir = Directory.CreateTempSubdirectory("bundler-sc");
        UpdateIdentitySidecar.WriteIfEnabled(
            dir.FullName, null, PackageFormat.Deb, "linux-x86_64");
        Assert.Empty(Directory.GetFiles(dir.FullName));
    }

    [Fact]
    public void WriteIfEnabled_WritesSidecar_WithDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("bundler-sc");
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

public sealed class UpdateBootstrapperTests
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
        var dir = Directory.CreateTempSubdirectory("bundler-boot");
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
    public void TryResolve_PosixFallback_NonWindows()
    {
        var dir = Directory.CreateTempSubdirectory("bundler-boot");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "posix"));
        File.WriteAllText(Path.Combine(dir.FullName, "posix", "bundler-updater.sh"), "sh");

        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        Assert.True(UpdateBootstrapper.TryResolve(update, "linux-x86_64", out var source));
        Assert.EndsWith("bundler-updater.sh", source);
    }

    [Fact]
    public void TryResolve_PosixNeverForWindows()
    {
        var dir = Directory.CreateTempSubdirectory("bundler-boot");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "posix"));
        File.WriteAllText(Path.Combine(dir.FullName, "posix", "bundler-updater.sh"), "sh");

        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        Assert.False(UpdateBootstrapper.TryResolve(update, "windows-x86_64", out _));
    }

    [Fact]
    public void TryResolve_Missing_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory("bundler-boot");
        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        Assert.False(UpdateBootstrapper.TryResolve(update, "linux-x86_64", out _));
    }

    [Fact]
    public void Inject_CopiesWithFileName()
    {
        var dir = Directory.CreateTempSubdirectory("bundler-boot");
        var targetDir = Directory.CreateTempSubdirectory("bundler-payload");
        var perTarget = Path.Combine(dir.FullName, "linux-x86_64");
        Directory.CreateDirectory(perTarget);
        File.WriteAllText(Path.Combine(perTarget, "bundler-updater"), "binary");

        var update = new UpdateBundleConfiguration { BootstrapperDirectory = dir.FullName };
        var name = UpdateBootstrapper.Inject(targetDir.FullName, update, "linux-x86_64");

        Assert.Equal("bundler-updater", name);
        Assert.True(File.Exists(Path.Combine(targetDir.FullName, "bundler-updater")));
    }
}

public sealed class UpdatePayloadStagingTests
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
        var work = Directory.CreateTempSubdirectory("bundler-work");
        var input = Directory.CreateTempSubdirectory("bundler-input");
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
        var work = Directory.CreateTempSubdirectory("bundler-work");
        var input = Directory.CreateTempSubdirectory("bundler-input");
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
    public void EnsureStaged_AlreadyInsideWork_WritesInPlace()
    {
        var work = Directory.CreateTempSubdirectory("bundler-work");
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
}
