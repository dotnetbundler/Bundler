using DotNet.Bundler;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;

namespace Bundler.UnitTests.MacDmgPkg;

/// <summary>
/// macOS 签名/打包的配置期校验面——全部在任何 codesign/pkgbuild 子进程之前抛，
/// 是宿主无关的纯判定。真签名/真挂载归集成层与特殊验收。
/// </summary>
public sealed class MacSigningTests : TempBase
{


    public override void Dispose()
    {
        base.Dispose();
    }

    private static BundleConfiguration Bundle() => new()
    {
        ProductName = "Sample App",
        Identifier = "com.example.sample",
        Version = "1.0.0",
    };

    // ---- MacAppSigning.Validate ----

    [Fact]
    public void Signing_IdentityAndCertificate_MutuallyExclusive()
    {
        var cert = Path.Combine(Root, "c.p12");
        File.WriteAllText(cert, "stub");
        Assert.Throws<ArgumentException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration
            {
                Identity = "Developer ID Application: X",
                TemporaryCertificateFile = cert,
            }));
    }

    [Fact]
    public void Signing_EmptyIdentity_Rejected()
    {
        Assert.Throws<ArgumentException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration { Identity = "" }));
    }

    [Fact]
    public void Signing_MissingCertificateFile_Rejected()
    {
        Assert.Throws<FileNotFoundException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration
            {
                TemporaryCertificateFile = Path.Combine(Root, "ghost.p12"),
            }));
    }

    [Fact]
    public void Signing_MissingEntitlementsFile_Rejected()
    {
        var cert = Path.Combine(Root, "c2.p12");
        File.WriteAllText(cert, "stub");
        Assert.Throws<FileNotFoundException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration
            {
                TemporaryCertificateFile = cert,
                EntitlementsFile = Path.Combine(Root, "ghost.entitlements"),
            }));
    }

    [Fact]
    public void Signing_NotarizeAdHoc_Rejected()
    {
        // ad-hoc（"-"）签名不可公证——必须真身份。
        Assert.Throws<ArgumentException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration { Identity = "-", Notarize = true }));
    }

    [Fact]
    public void Signing_NotarizeWithoutIdentity_Rejected()
    {
        Assert.Throws<ArgumentException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration { Notarize = true }));
    }

    [Fact]
    public void Signing_SkipStaplingRequiresNotarize()
    {
        Assert.Throws<ArgumentException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration { SkipStapling = true }));
        Assert.Throws<ArgumentException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration { NotaryWait = false }));
    }

    [Fact]
    public void Signing_SignedConfig_NonMacHost_Rejected()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "PNSE 支路只在非 macOS 触发");
        Assert.Throws<PlatformNotSupportedException>(() => MacAppSigning.Validate(
            new MacAppSigningConfiguration { Identity = "Developer ID Application: X" }));
    }

    // ---- 公证凭证解析 ----

    [Fact]
    public void Credentials_KeychainProfile_Wins()
    {
        var args = MacAppSigning.ResolveCredentials(
            new MacAppSigningConfiguration { KeychainProfile = "ci-profile" });
        Assert.Equal(["--keychain-profile", "ci-profile"], args);
    }

    [Fact]
    public void Credentials_ApiKey_NeedsAllThree()
    {
        var key = Path.Combine(Root, "key.p8");
        File.WriteAllText(key, "k");
        var args = MacAppSigning.ResolveCredentials(
            new MacAppSigningConfiguration
            {
                ApiKeyFile = key, ApiKeyId = "KEYID1234", ApiKeyIssuer = "iss",
            });
        Assert.Equal(["--key", key, "--key-id", "KEYID1234", "--issuer", "iss"], args);

        Assert.Throws<ArgumentException>(() => MacAppSigning.ResolveCredentials(
            new MacAppSigningConfiguration { ApiKeyFile = key })); // 缺 id/issuer
    }

    [Fact]
    public void Credentials_AppleId_NeedsAllThree()
    {
        var args = MacAppSigning.ResolveCredentials(
            new MacAppSigningConfiguration
            {
                AppleId = "a@b.c", ApplePassword = "app-pw", AppleTeamId = "TEAM",
            });
        Assert.Equal(["--apple-id", "a@b.c", "--password", "app-pw", "--team-id", "TEAM"], args);

        Assert.Throws<ArgumentException>(() => MacAppSigning.ResolveCredentials(
            new MacAppSigningConfiguration { AppleId = "a@b.c" }));
    }

    // ---- codesign 参数构造（纯构造，不执行） ----

    [Fact]
    public void CodesignArguments_AdHoc_NoTimestamp()
    {
        var args = MacAppSigning.CodesignArguments(
            "-", new MacAppSigningConfiguration(), entitlements: false, "/tmp/x.app");
        Assert.Contains("--sign", args);
        Assert.Contains("--timestamp=none", args); // ad-hoc 无时间戳服务
        Assert.Equal("/tmp/x.app", args[^1]);
    }

    [Fact]
    public void CodesignArguments_HardenedAndEntitlements()
    {
        var ent = Path.Combine(Root, "e.plist");
        File.WriteAllText(ent, "<plist/>");
        var args = MacAppSigning.CodesignArguments(
            "Dev ID", new MacAppSigningConfiguration
            {
                HardenedRuntime = true, EntitlementsFile = ent,
            }, entitlements: true, "/tmp/x.app");
        Assert.Contains("--options", args);
        Assert.Contains("runtime", args);
        Assert.Contains("--entitlements", args);
        Assert.Contains("--timestamp", args);
    }

    // ---- NotarySubmissionId ----

    [Theory]
    [InlineData("""{"id":"ABC-123-Def","status":"Accepted"}""", "ABC-123-Def")]
    [InlineData("""{"status":"Accepted"}""", null)]
    [InlineData("not json", null)]
    public void NotarySubmission_ParsesId(string json, string? expected)
    {
        Assert.Equal(expected, MacAppSigning.NotarySubmissionId(json));
    }

    // ---- MacPkgSigning.Validate ----

    [Fact]
    public void PkgSigning_NoAdHocEquivalent()
    {
        // .pkg 无 ad-hoc 签名等价物——"-" 身份被拒。
        Assert.Throws<ArgumentException>(() => MacPkgSigning.Validate(
            new MacPkgSigningConfiguration { Identity = "-" }));
    }

    [Fact]
    public void PkgSigning_MutexAndStaplingRules()
    {
        var cert = Path.Combine(Root, "pkg.p12");
        File.WriteAllText(cert, "stub");
        Assert.Throws<ArgumentException>(() => MacPkgSigning.Validate(
            new MacPkgSigningConfiguration { Identity = "X", TemporaryCertificateFile = cert }));
        Assert.Throws<ArgumentException>(() => MacPkgSigning.Validate(
            new MacPkgSigningConfiguration { SkipStapling = true }));
        Assert.Throws<ArgumentException>(() => MacPkgSigning.Validate(
            new MacPkgSigningConfiguration { Notarize = true })); // 无身份即公证
    }

    [Fact]
    public void PkgSigning_PkgbuildArgs()
    {
        var args = MacPkgSigning.PkgbuildSignArguments("Dev ID Installer", null);
        Assert.Equal(["--sign", "Dev ID Installer", "--timestamp"], args);
    }

    // ---- MacPkgBundleBackend.ValidateConfiguration ----

    [Fact]
    public void PkgConfig_RequiresAbsoluteInstallRoot()
    {
        var (identifier, version) = MacPkgBundleBackend.ValidateConfiguration(
            new MacPkgBundleConfiguration { InstallRoot = "/Applications" }, Bundle());
        Assert.Equal("com.example.sample", identifier);
        Assert.Equal("1.0.0", version);

        Assert.Throws<ArgumentException>(() => MacPkgBundleBackend.ValidateConfiguration(
            new MacPkgBundleConfiguration { InstallRoot = "relative/dir" }, Bundle()));
    }

    [Fact]
    public void PkgConfig_MissingScriptsDir_Rejected()
    {
        Assert.Throws<DirectoryNotFoundException>(() => MacPkgBundleBackend.ValidateConfiguration(
            new MacPkgBundleConfiguration
            {
                InstallRoot = "/Applications",
                ScriptsDirectory = Path.Combine(Root, "ghost-scripts"),
            }, Bundle()));
    }

    [Fact]
    public void PkgConfig_EmptyIdentityOrVersion_Rejected()
    {
        var blankId = new BundleConfiguration
        {
            ProductName = "X", Identifier = " ", Version = "1.0.0",
        };
        Assert.Throws<ArgumentException>(() => MacPkgBundleBackend.ValidateConfiguration(
            new MacPkgBundleConfiguration { InstallRoot = "/Applications", PackageName = " " },
            blankId));
        var blankVersion = new BundleConfiguration
        {
            ProductName = "X", Identifier = "com.x", Version = " ",
        };
        Assert.Throws<ArgumentException>(() => MacPkgBundleBackend.ValidateConfiguration(
            new MacPkgBundleConfiguration { InstallRoot = "/Applications" }, blankVersion));
    }

    // ---- MacDmgBundleBackend.ValidateConfiguration ----

    [Fact]
    public void DmgConfig_VolumeNameDefaultsAndKnob()
    {
        Assert.Equal("Sample App",
            MacDmgBundleBackend.ValidateConfiguration(new MacDmgBundleConfiguration(), Bundle()));
        Assert.Equal("Install Disc",
            MacDmgBundleBackend.ValidateConfiguration(
                new MacDmgBundleConfiguration { VolumeName = "Install Disc" }, Bundle()));
        Assert.Throws<ArgumentException>(() => MacDmgBundleBackend.ValidateConfiguration(
            new MacDmgBundleConfiguration { VolumeName = " " }, Bundle()));
    }

    [Fact]
    public void DmgConfig_SigningMutexAndEmptyIdentity()
    {
        var cert = Path.Combine(Root, "dmg.p12");
        File.WriteAllText(cert, "stub");
        Assert.Throws<ArgumentException>(() => MacDmgBundleBackend.ValidateConfiguration(
            new MacDmgBundleConfiguration
            {
                Signing = new MacDmgSigningConfiguration
                {
                    Identity = "X", TemporaryCertificateFile = cert,
                },
            }, Bundle()));
        Assert.Throws<ArgumentException>(() => MacDmgBundleBackend.ValidateConfiguration(
            new MacDmgBundleConfiguration
            {
                Signing = new MacDmgSigningConfiguration { Identity = "" },
            }, Bundle()));
        Assert.Throws<FileNotFoundException>(() => MacDmgBundleBackend.ValidateConfiguration(
            new MacDmgBundleConfiguration
            {
                Signing = new MacDmgSigningConfiguration
                {
                    TemporaryCertificateFile = Path.Combine(Root, "ghost.p12"),
                },
            }, Bundle()));
        // "-" ad-hoc 合法：dmg 可以 ad-hoc 签。
        MacDmgBundleBackend.ValidateConfiguration(
            new MacDmgBundleConfiguration
            {
                Signing = new MacDmgSigningConfiguration { Identity = "-" },
            }, Bundle());
    }

    // ---- MacAppBundleBackend.SanitizeFileName ----

    [Theory]
    [InlineData("My App", "My App")]
    [InlineData("a<b>:c", "a-b--c")]
    [InlineData("a/b\\c", "a-b-c")]
    public void SanitizeFileName_WindowsRules(string input, string expected)
    {
        Assert.Equal(expected, MacAppBundleBackend.SanitizeFileName(input));
    }
}
