// APK-4 集成腿的 C# 移植：对应原 tests/Alpine.Apk.Integration/Verify.sh。
// 覆盖点：nupkg 结构 → publish → gzip 多成员结构（python3 zlib 拆分）
// → .PKGINFO/datahash/size/pax 校验 → override/scripts/upgrade 变体
// → 签名验签 → 三次逐字节一致 → API fixture → 非法 Release 失败
// → docker 实装/升级/未签名拒绝/签名信任链/aarch64 binfmt。
using System.Text.RegularExpressions;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AlpineApkFixture : IDisposable
{
    public IntegrationWorkspace Ws { get; private set; } = null!;
    public string CacheDir { get; private set; } = null!;
    public string ExtractRoot { get; private set; } = null!;
    public string KeysDir { get; private set; } = null!;
    public string FixtureProject { get; private set; } = null!;

    public string DefaultApk { get; private set; } = null!;
    public string OverrideApk { get; private set; } = null!;
    public string ScriptsApk { get; private set; } = null!;
    public string UpgradeV1Apk { get; private set; } = null!;
    public string UpgradeV2Apk { get; private set; } = null!;
    public string SignedApk { get; private set; } = null!;
    public string Det1Apk { get; private set; } = null!;
    public string Det2Apk { get; private set; } = null!;
    public string Det3Apk { get; private set; } = null!;
    public string Arm64Apk { get; private set; } = null!;

    private readonly Lazy<bool> _init;

    public AlpineApkFixture() => _init = new Lazy<bool>(() => { Initialize(); return true; });

    public bool Ensure() => _init.Value;

    private void Initialize()
    {
        Assert.SkipWhen(!TestPlatform.IsLinux, "SKIP: apk integration test requires a Linux host.");
        foreach (var tool in new[] { "tar", "sha256sum", "openssl", "python3", "unzip" })
        {
            ExternalTools.Require(tool);
        }
        Ws = IntegrationWorkspace.Create("alpine-apk-integration", "BundlerAlpineApkIntegration");
        CacheDir = Ws.Combine("nuget-cache");
        ExtractRoot = Ws.Combine("extract");
        KeysDir = Ws.Combine("keys");
        FixtureProject = Path.Combine(RepositoryLayout.FixturesDirectory,
            "AlpineApk", "BundlerAlpineApkIntegrationFixture.csproj");
        _ = RepositoryPackages.DirectoryPath;

        var fixtureDir = Path.GetDirectoryName(FixtureProject)!;
        Publish("bundle");
        DefaultApk = Ws.Combine("bundle", "linux-musl-x64", "apk", "bundler-apk-fixture-1.0.0-r0.apk");
        Assert.True(File.Exists(DefaultApk), $"The .apk artifact is missing: {DefaultApk}");

        Publish("override",
            "-p:BundlerTestAlpineApkPackageName=custom-apk",
            "-p:BundlerTestAlpineApkRelease=7",
            "-p:BundlerTestAlpineApkLicense=MIT",
            "-p:BundlerTestAlpineApkDepends=musl%3Bso:libc.musl-x86_64.so.1>=1.2",
            "-p:BundlerTestAlpineApkProvides=virtual-apk-fixture",
            "-p:BundlerTestAlpineApkTriggers=/usr/lib/bundler-apk-fixture");
        OverrideApk = Ws.Combine("override", "linux-musl-x64", "apk", "custom-apk-1.0.0-r7.apk");
        Assert.True(File.Exists(OverrideApk), "Override variant produced no .apk.");

        Publish("scripts",
            $"-p:BundlerTestAlpineApkPostInstallScript={fixtureDir}/Assets/post-install.sh",
            $"-p:BundlerTestAlpineApkPreDeinstallScript={fixtureDir}/Assets/pre-deinstall.sh");
        ScriptsApk = Ws.Combine("scripts", "linux-musl-x64", "apk", "bundler-apk-fixture-1.0.0-r0.apk");
        Assert.True(File.Exists(ScriptsApk), "Scripts variant produced no .apk.");

        Publish("upgrade-v1",
            $"-p:BundlerTestAlpineApkPostInstallScript={fixtureDir}/Assets/post-install.sh");
        UpgradeV1Apk = Ws.Combine("upgrade-v1", "linux-musl-x64", "apk", "bundler-apk-fixture-1.0.0-r0.apk");
        Assert.True(File.Exists(UpgradeV1Apk), "Upgrade v1 variant produced no .apk.");

        Publish("upgrade-v2",
            "-p:BundlerTestAlpineApkRelease=1",
            $"-p:BundlerTestAlpineApkPreUpgradeScript={fixtureDir}/Assets/pre-upgrade.sh",
            $"-p:BundlerTestAlpineApkPostUpgradeScript={fixtureDir}/Assets/post-upgrade.sh",
            $"-p:BundlerTestAlpineApkPostInstallScript={fixtureDir}/Assets/post-install-v2.sh");
        UpgradeV2Apk = Ws.Combine("upgrade-v2", "linux-musl-x64", "apk", "bundler-apk-fixture-1.0.0-r1.apk");
        Assert.True(File.Exists(UpgradeV2Apk), "Upgrade v2 variant produced no .apk.");

        Directory.CreateDirectory(KeysDir);
        var privateKey = Path.Combine(KeysDir, "bundler-test.rsa");
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("openssl", ["genrsa", "-out", privateKey, "2048"]),
            "openssl genrsa failed.");
        ProcessRunner.AssertSuccess(
            ProcessRunner.Run("openssl", ["rsa", "-in", privateKey,
                "-pubout", "-out", privateKey + ".rsa.pub"]),
            "openssl pubkey export failed.");
        Publish("signed", $"-p:BundlerTestAlpineApkSigningKeyFile={privateKey}");
        SignedApk = Ws.Combine("signed", "linux-musl-x64", "apk", "bundler-apk-fixture-1.0.0-r0.apk");
        Assert.True(File.Exists(SignedApk), "Signed variant produced no .apk.");

        foreach (var name in new[] { "det1", "det2", "det3" })
        {
            Publish(name);
        }
        Det1Apk = Ws.Combine("det1", "linux-musl-x64", "apk", "bundler-apk-fixture-1.0.0-r0.apk");
        Det2Apk = Ws.Combine("det2", "linux-musl-x64", "apk", "bundler-apk-fixture-1.0.0-r0.apk");
        Det3Apk = Ws.Combine("det3", "linux-musl-x64", "apk", "bundler-apk-fixture-1.0.0-r0.apk");

        Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine("arm64")}",
             "--packages", CacheDir, "-r", "linux-musl-arm64"],
            "linux-musl-arm64 publish failed", noRestore: false);
        Arm64Apk = Ws.Combine("arm64", "linux-musl-arm64", "apk", "bundler-apk-fixture-1.0.0-r0.apk");
        Assert.True(File.Exists(Arm64Apk), "No .apk produced for linux-musl-arm64.");
    }

    public string PrivateKey => Path.Combine(KeysDir, "bundler-test.rsa");
    public string PublicKey => PrivateKey + ".rsa.pub";

    public void Publish(string name, params string[] extraProperties)
        => Dotnet.Publish(FixtureProject, "Release",
            [$"-p:BundlerIntegrationOutput={Ws.Combine(name)}", "--packages", CacheDir,
             .. extraProperties],
            $"apk fixture publish '{name}' failed", noRestore: false);

    public string SplitTo(string apkPath, string name)
        => ExtractRoot + "/" + name;

    public void Dispose() => Ws?.Dispose();
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AlpineApkIntegrationTests : IClassFixture<AlpineApkFixture>
{
    private readonly AlpineApkFixture _f;

    public AlpineApkIntegrationTests(AlpineApkFixture fixture)
    {
        _f = fixture;
        _f.Ensure();
    }

    [Fact]
    public void RepositoryPackagesCarryAlpineApkBackend()
    {
        var dir = RepositoryPackages.DirectoryPath;
        var version = RepositoryLayout.PackageVersion;
        foreach (var id in new[] { "DotNet.Bundler", "DotNet.Bundler.MSBuild", "DotNet.Bundler.AlpineApk" })
        {
            Dotnet.AssertPackageExists(dir, id, version);
        }
        var entries = Dotnet.NupkgEntries(
            Path.Combine(dir, $"DotNet.Bundler.MSBuild.{version}.nupkg"));
        Assert.Contains(entries, e => e.EndsWith("DotNet.Bundler.AlpineApk.dll"));
    }

    [Fact]
    public void DefaultApkHasSha256Sidecar()
    {
        Assert.True(File.Exists(_f.DefaultApk + ".sha256"), "The .sha256 sidecar is missing.");
        var result = ProcessRunner.Run("sha256sum", ["-c", Path.GetFileName(_f.DefaultApk + ".sha256")],
            new ProcessRunner.Options { WorkingDirectory = Path.GetDirectoryName(_f.DefaultApk)! });
        ProcessRunner.AssertSuccess(result, "The .sha256 sidecar does not match the .apk.");
    }

    [Fact]
    public void UnsignedApkHasTwoGzipMembersWithCorrectTails()
    {
        var dest = _f.SplitTo(_f.DefaultApk, "default");
        var members = ApkTools.SplitMembers(_f.DefaultApk, dest);
        Assert.Equal(2, members);
        Assert.True(ApkTools.HasNonzeroTail(Path.Combine(dest, "member0.tar")),
            "The control tar must not end with zero blocks.");
        Assert.False(ApkTools.HasNonzeroTail(Path.Combine(dest, "member1.tar")),
            "The data tar must end with zero blocks.");
    }

    [Fact]
    public void PkgInfoFieldsDataHashSizeAndNoSignMembers()
    {
        var dest = _f.SplitTo(_f.DefaultApk, "pkginfo");
        ApkTools.SplitMembers(_f.DefaultApk, dest);
        var member0 = Path.Combine(dest, "member0.tar");
        var member1Tar = Path.Combine(dest, "member1.tar");
        var member1Gz = Path.Combine(dest, "member1.gz");
        var pkgInfo = ApkTools.TarMemberText(member0, ".PKGINFO");
        ApkTools.AssertPkgInfoFields(pkgInfo,
            "pkgname = bundler-apk-fixture", "pkgver = 1.0.0-r0",
            "pkgdesc = Disposable .apk integration-test fixture.",
            "url = https://example.com/apk-fixture", "arch = x86_64",
            "origin = bundler-apk-fixture", "builddate = 0",
            "depend = libstdc++", "depend = libgcc");
        Assert.Equal(ApkTools.Sha256Hex(member1Gz), ApkTools.PkgInfoValue(pkgInfo, "datahash"));

        var listing = ApkTools.TarListVerbose(member1Tar).StdOut;
        var size = listing.Split('\n')
            .Where(l => Regex.IsMatch(l, @"^-"))
            .Sum(l => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]));
        Assert.Equal(size.ToString(), ApkTools.PkgInfoValue(pkgInfo, "size"));

        var names = ApkTools.TarListNames(member0).StdOut;
        Assert.DoesNotMatch(new Regex(@"^\.SIGN\.", RegexOptions.Multiline), names);
    }

    [Fact]
    public void PayloadContentsAndPaxChecksumRecords()
    {
        var dest = _f.SplitTo(_f.DefaultApk, "payload");
        ApkTools.SplitMembers(_f.DefaultApk, dest);
        var member1 = Path.Combine(dest, "member1.tar");
        var listing = ApkTools.TarListVerbose(member1).StdOut;
        Assert.Contains("usr/lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture", listing);
        Assert.Contains("usr/lib/bundler-apk-fixture/docs/readme.txt", listing);
        Assert.Contains("etc/bundler-apk-fixture/defaults.conf", listing);
        Assert.Contains(
            "usr/bin/bundler-apk-fixture -> ../lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture",
            listing);
        Assert.Matches(
            new Regex(@"^-rwxr-xr-x .*usr/lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture$",
                RegexOptions.Multiline),
            listing);

        var fileCount = listing.Split('\n').Count(l => l.StartsWith('-') || l.StartsWith('l'));
        var raw = File.ReadAllBytes(member1);
        var paxCount = CountOccurrences(raw, "APK-TOOLS.checksum.SHA1"u8.ToArray());
        Assert.Equal(fileCount, paxCount);
        var paxMatch = Regex.Match(
            System.Text.Encoding.Latin1.GetString(raw),
            @"APK-TOOLS\.checksum\.SHA1=[0-9a-f]{40}");
        Assert.True(paxMatch.Success, "The checksum pax record must be a hex sha1 like abuild emits.");
    }

    [Fact]
    public void OverrideVariantFields()
    {
        var dest = _f.SplitTo(_f.OverrideApk, "override");
        ApkTools.SplitMembers(_f.OverrideApk, dest);
        var pkgInfo = ApkTools.TarMemberText(Path.Combine(dest, "member0.tar"), ".PKGINFO");
        ApkTools.AssertPkgInfoFields(pkgInfo,
            "pkgname = custom-apk", "pkgver = 1.0.0-r7", "license = MIT",
            "depend = musl", "depend = so:libc.musl-x86_64.so.1>=1.2",
            "provides = virtual-apk-fixture", "triggers = /usr/lib/bundler-apk-fixture");
    }

    [Fact]
    public void ScriptsVariantControlMembers()
    {
        var dest = _f.SplitTo(_f.ScriptsApk, "scripts");
        ApkTools.SplitMembers(_f.ScriptsApk, dest);
        var listing = ApkTools.TarListVerbose(Path.Combine(dest, "member0.tar")).StdOut;
        foreach (var member in new[] { ".post-install", ".pre-deinstall" })
        {
            Assert.Matches(
                new Regex($"^-rwxr-xr-x .* {Regex.Escape(member)}$", RegexOptions.Multiline),
                listing);
        }
        var names = ApkTools.TarListNames(Path.Combine(dest, "member0.tar")).StdOut;
        Assert.DoesNotMatch(new Regex(@"^\.pre-install$", RegexOptions.Multiline), names);
    }

    [Fact]
    public void UpgradeV2ControlHasUpgradeScripts()
    {
        var dest = _f.SplitTo(_f.UpgradeV2Apk, "upgrade-v2");
        ApkTools.SplitMembers(_f.UpgradeV2Apk, dest);
        var names = ApkTools.TarListNames(Path.Combine(dest, "member0.tar")).StdOut;
        foreach (var member in new[] { ".pre-upgrade", ".post-upgrade", ".post-install" })
        {
            Assert.Matches(
                new Regex($"^{Regex.Escape(member)}$", RegexOptions.Multiline), names);
        }
    }

    [Fact]
    public void SignedApkHasThreeMembersAndValidSignature()
    {
        var dest = _f.SplitTo(_f.SignedApk, "signed");
        var members = ApkTools.SplitMembers(_f.SignedApk, dest);
        Assert.Equal(3, members);
        var member0 = Path.Combine(dest, "member0.tar");
        Assert.True(ApkTools.HasNonzeroTail(member0),
            "The signature tar must not end with zero blocks.");
        var names = ApkTools.TarListNames(member0).StdOut.Trim();
        Assert.Equal(".SIGN.RSA.bundler-test.rsa.rsa.pub", names);

        var sigBin = Path.Combine(dest, "sig.bin");
        ApkTools.TarMemberBytesToFile(member0, ".SIGN.RSA.bundler-test.rsa.rsa.pub", sigBin);
        var verify = ProcessRunner.Run("openssl",
            ["dgst", "-sha1", "-verify", _f.PublicKey, "-signature", sigBin,
             Path.Combine(dest, "member1.gz")]);
        ProcessRunner.AssertSuccess(verify, "openssl dgst failed.");
        Assert.Contains("Verified OK", verify.StdOut);
    }

    [Fact]
    public void ThreeConsecutiveProducesAreByteIdentical()
    {
        var sha1 = ApkTools.Sha256Hex(_f.Det1Apk);
        var sha2 = ApkTools.Sha256Hex(_f.Det2Apk);
        var sha3 = ApkTools.Sha256Hex(_f.Det3Apk);
        Assert.True(sha1 == sha2 && sha2 == sha3,
            $"Three consecutive produces are not byte-identical: {sha1} {sha2} {sha3}");
    }

    [Fact]
    public void ApiFixtureProducesApk()
    {
        var output = _f.Ws.Combine("api");
        var result = ProcessRunner.Run("dotnet",
            ["test",
             Path.Combine(RepositoryLayout.TestsDirectory, "Bundler.ApiTests", "Bundler.ApiTests.csproj"),
             "-c", "Release", "--", "--filter-class", "AlpineApkApiTests"],
            new ProcessRunner.Options
            {
                Environment = new Dictionary<string, string?> { ["APK_API_FIXTURE_OUTPUT"] = output },
                Timeout = TimeSpan.FromMinutes(10),
            });
        ProcessRunner.AssertSuccess(result, "AlpineApkApiTests failed");
        var apiApk = Path.Combine(output, "artifacts", "linux-musl-x64", "apk", "api-fixture-1.0.0-r0.apk");
        Assert.True(File.Exists(apiApk), "The direct-API fixture produced no .apk.");
        var head = new byte[3];
        using (var stream = File.OpenRead(apiApk))
        {
            Assert.Equal(3, stream.Read(head));
        }
        Assert.Equal(new byte[] { 0x1f, 0x8b, 0x08 }, head);
    }

    [Fact]
    public void NonNumericReleaseFailsPublish()
    {
        var result = Dotnet.Run(
            ["publish", _f.FixtureProject, "-c", "Release",
             $"-p:BundlerIntegrationOutput={_f.Ws.Combine("fail-release")}",
             "-p:BundlerTestAlpineApkRelease=abc",
             "--packages", _f.CacheDir]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Matches(new Regex("release", RegexOptions.IgnoreCase), result.Output);
    }

    [Fact]
    public void Arm64ApkDeclaresAarch64()
    {
        var dest = _f.SplitTo(_f.Arm64Apk, "arm64");
        ApkTools.SplitMembers(_f.Arm64Apk, dest);
        var pkgInfo = ApkTools.TarMemberText(Path.Combine(dest, "member0.tar"), ".PKGINFO");
        ApkTools.AssertPkgInfoFields(pkgInfo, "arch = aarch64");
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerAlpineInstallRunRemove()
    {
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: 该腿工件为 x64 二进制/amd64 包，非 x64 宿主无法执行或安装。");
        DockerRunner.RequireImage("alpine:latest");
        var result = DockerRunner.RunScript("alpine:latest",
            """
            set -e
            apk add --allow-untrusted /tmp/pkg.apk >/dev/null
            test -f /tmp/bundler-apk-fixture-post-install.ran
            test -f /etc/bundler-apk-fixture/defaults.conf
            bundler-apk-fixture smoke | grep -q "BundlerAlpineApkIntegrationFixture:smoke"
            test "$(readlink /usr/bin/bundler-apk-fixture)" = "../lib/bundler-apk-fixture/BundlerAlpineApkIntegrationFixture"
            apk info -e bundler-apk-fixture
            apk del bundler-apk-fixture >/dev/null
            test -f /tmp/bundler-apk-fixture-pre-deinstall.ran
            ! test -e /usr/lib/bundler-apk-fixture
            ! test -e /usr/bin/bundler-apk-fixture
            """,
            [new DockerRunner.Mount(_f.ScriptsApk, "/tmp/pkg.apk")]);
        ProcessRunner.AssertSuccess(result, "alpine install/run/remove leg failed.");
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerV1ToV2UpgradeRunsUpgradeScripts()
    {
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: 该腿工件为 x64 二进制/amd64 包，非 x64 宿主无法执行或安装。");
        DockerRunner.RequireImage("alpine:latest");
        var result = DockerRunner.RunScript("alpine:latest",
            """
            set -e
            apk add --allow-untrusted /tmp/v1.apk >/dev/null
            test -f /tmp/bundler-apk-fixture-post-install.ran
            apk add --allow-untrusted /tmp/v2.apk >/dev/null
            test -f /tmp/bundler-apk-fixture-pre-upgrade.ran
            test -f /tmp/bundler-apk-fixture-post-upgrade.ran
            ! test -e /tmp/bundler-apk-fixture-post-install-v2.ran
            apk list --installed | grep -q "bundler-apk-fixture-1.0.0-r1"
            """,
            [new DockerRunner.Mount(_f.UpgradeV1Apk, "/tmp/v1.apk"),
             new DockerRunner.Mount(_f.UpgradeV2Apk, "/tmp/v2.apk")]);
        ProcessRunner.AssertSuccess(result, "alpine v1->v2 upgrade leg failed.");
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerRejectsUnsignedPackageWithoutAllowUntrusted()
    {
        DockerRunner.RequireImage("alpine:latest");
        var result = DockerRunner.RunScript("alpine:latest",
            "apk add /tmp/pkg.apk >/dev/null 2>&1",
            [new DockerRunner.Mount(_f.DefaultApk, "/tmp/pkg.apk")]);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerSignedPackageInstallsViaTrustedKey()
    {
        Assert.SkipWhen(!TestPlatform.IsX64,
            "SKIP: 该腿工件为 x64 二进制/amd64 包，非 x64 宿主无法执行或安装。");
        DockerRunner.RequireImage("alpine:latest");
        var result = DockerRunner.RunScript("alpine:latest",
            """
            set -e
            cp /tmp/key.rsa.pub /etc/apk/keys/bundler-test.rsa.rsa.pub
            apk add /tmp/signed.apk >/dev/null
            bundler-apk-fixture smoke | grep -q "BundlerAlpineApkIntegrationFixture:smoke"
            apk del bundler-apk-fixture >/dev/null
            """,
            [new DockerRunner.Mount(_f.SignedApk, "/tmp/signed.apk"),
             new DockerRunner.Mount(_f.PublicKey, "/tmp/key.rsa.pub")]);
        ProcessRunner.AssertSuccess(result, "The signed .apk did not install as a trusted package.");
    }

    [Fact]
    [Trait("Requires", "docker")]
    public void DockerArm64InstallRunRemoveUnderBinfmt()
    {
        DockerRunner.RequireImage("alpine:latest");
        Assert.SkipWhen(!DockerRunner.CanRunArm64Containers,
            "SKIP: aarch64 emulation unavailable; arm64 leg is structure-only.");
        var result = DockerRunner.RunScript("alpine:latest",
            """
            set -e
            apk add --allow-untrusted /tmp/pkg.apk >/dev/null
            bundler-apk-fixture smoke | grep -q "BundlerAlpineApkIntegrationFixture:smoke"
            apk del bundler-apk-fixture >/dev/null
            """,
            [new DockerRunner.Mount(_f.Arm64Apk, "/tmp/pkg.apk")],
            platform: "linux/arm64");
        ProcessRunner.AssertSuccess(result, "aarch64 container install/run/remove failed.");
    }

    private static int CountOccurrences(byte[] haystack, byte[] needle)
    {
        var count = 0;
        var span = haystack.AsSpan();
        while (true)
        {
            var index = span.IndexOf(needle);
            if (index < 0)
            {
                return count;
            }
            count++;
            span = span[(index + needle.Length)..];
        }
    }
}
