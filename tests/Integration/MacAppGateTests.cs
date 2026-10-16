// MacAppGate 门禁真件腿（macOS 宿主）：codesign/xattr 走系统工具，Unit 层零子进程规则不收。
// 断言面对应 MacAppGate.CheckAndStrip 三分支：
//   零证书面（恒可自动化）——CFBundleIdentifier 兜底拒换 + quarantine 剥离；
//   签名身份面（尽力腿）——临时 keychain 自签证书：自签件 codesign -dv 的 TeamIdentifier
//   取自证书 Subject OU，实证取到非空值才跑（降级拒/跨 TeamID 拒/坏签拒/同队放行）；
//   取不到（"not set"）即诚实 Skip——生产证书签名链仍归 special-acceptance SA-P-07。
using System.Text.RegularExpressions;
using DotNet.Bundler;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.Updater.Bootstrap;

public sealed class MacAppGateFixture : IDisposable
{
    // 惰性创建：宿主门禁在各用例体内 Skip，之后才允许占锁建目录。
    private readonly Lazy<IntegrationWorkspace> _ws = new(() => IntegrationWorkspace.Create(
        "mac-app-gate-integration", "BundlerMacAppGateIntegration"));

    public IntegrationWorkspace Ws => _ws.Value;

    public void Dispose()
    {
        if (_ws.IsValueCreated)
        {
            _ws.Value.Dispose();
        }
    }
}

public sealed class MacAppGateTests : IClassFixture<MacAppGateFixture>
{
    private IntegrationWorkspace Ws => _fixture.Ws;
    private readonly MacAppGateFixture _fixture;

    public MacAppGateTests(MacAppGateFixture fixture) => _fixture = fixture;

    [Fact]
    public void CheckAndStrip_BundleIdMismatch_RejectsWithZeroChange()
    {
        // 双方未签 → 落到 CFBundleIdentifier 兜底：不一致即拒，拒绝时安装目录零变更。
        Assert.SkipUnless(TestPlatform.IsMacOS, "codesign/xattr 门禁只在 macOS 宿主存在。");
        var directory = Ws.Combine("bundleid-mismatch");
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "App", "com.bundler.old");
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-slot"), "App", "com.bundler.new");
        var installBefore = Snapshot(install);
        var payloadBefore = Snapshot(payload);

        var exception = Assert.Throws<UpdateRejectedException>(() =>
            MacAppGate.CheckAndStrip(install, payload, _ => { }));

        Assert.Contains("bundle identifier mismatch", exception.Message);
        Assert.Equal(installBefore, Snapshot(install));
        Assert.Equal(payloadBefore, Snapshot(payload));
    }

    [Fact]
    public void CheckAndStrip_UnsignedBoth_PassesAndStripsQuarantine()
    {
        // 双方未签 + BundleID 一致 → 放行；下载件带标的 quarantine 必须剥掉。
        Assert.SkipUnless(TestPlatform.IsMacOS, "codesign/xattr 门禁只在 macOS 宿主存在。");
        var directory = Ws.Combine("quarantine-strip");
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "App", "com.bundler.gate");
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-slot"), "App", "com.bundler.gate");
        MacAppStub.SetQuarantineTree(payload);
        Assert.True(MacAppStub.HasQuarantine(payload), "fixture must mark the payload first.");

        var log = new List<string>();
        MacAppGate.CheckAndStrip(install, payload, log.Add);

        Assert.False(MacAppStub.HasQuarantine(payload));
        Assert.All(
            Directory.EnumerateFileSystemEntries(payload, "*", SearchOption.AllDirectories),
            entry => Assert.False(MacAppStub.HasQuarantine(entry)));
        Assert.Contains(log, line => line.Contains("quarantine stripped"));
    }

    [Fact]
    public void CheckAndStrip_SignedInstall_UnsignedPayload_RejectsDowngrade()
    {
        // 已签 → 未签是更新通道劫持的常见形态，降级必须拒。
        Assert.SkipUnless(TestPlatform.IsMacOS, "codesign/xattr 门禁只在 macOS 宿主存在。");
        var directory = Ws.Combine("downgrade");
        using var identity = RequireIdentity(
            Path.Combine(directory, "signing"), "BundlerGateAlpha", "GATEAAAAAA");
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        identity.Sign(install);
        var installBefore = Snapshot(install);

        var exception = Assert.Throws<UpdateRejectedException>(() =>
            MacAppGate.CheckAndStrip(install, payload, _ => { }));

        Assert.Contains("downgrade", exception.Message);
        Assert.Equal(installBefore, Snapshot(install));
    }

    [Fact]
    public void CheckAndStrip_UnsignedInstall_SignedPayload_AllowsSwap()
    {
        // 未签 → 已签是升级方向的放行分支（旧件未签不拦升级，MacAppGate.cs:46）——
        // 腿断言的不只是门禁放行，而是放行后换包真实成立（载荷落位）。
        Assert.SkipUnless(TestPlatform.IsMacOS, "codesign/xattr 门禁只在 macOS 宿主存在。");
        var directory = Ws.Combine("unsigned-install-signed-payload");
        using var identity = RequireIdentity(
            Path.Combine(directory, "signing"), "BundlerGateAlpha", "GATEAAAAAA");
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        // 区分落位内容后再签——签名后改动密封件会过不了 codesign --verify。
        File.WriteAllText(
            Path.Combine(payload, "Contents", "Resources", "data.txt"), "v2-data\n");
        identity.Sign(payload);

        var log = new List<string>();
        var rc = BootstrapPlan.Apply(new BootstrapOptions
        {
            InstallDirectory = install,
            PayloadDirectory = payload,
        }, log.Add);

        Assert.Equal(0, rc);
        Assert.Contains(log, line => line.Contains(
            "installed bundle unsigned; signed payload accepted"));
        Assert.Equal("v2-data", File.ReadAllText(
            Path.Combine(install, "Contents", "Resources", "data.txt")).TrimEnd());
    }

    [Fact]
    public void CheckAndStrip_CrossTeamIdentifier_RejectsCrossIdentity()
    {
        // 已签 → 已签但异队：跨签名者换包即通道劫持，必须拒。
        Assert.SkipUnless(TestPlatform.IsMacOS, "codesign/xattr 门禁只在 macOS 宿主存在。");
        var directory = Ws.Combine("cross-team");
        // 双 keychain 处置序承重：每个 TemporaryKeychain 在创建时快照当时的
        // list-keychains 搜索列表，Dispose 用它还原——逆序（LIFO）拆才把列表
        // 逐层还原到各创建时刻；using 声明天然逆序 Dispose，别改成统一清理。
        // 进程被强杀则搜索列表与 keychain 件残留（无析构方）——属可接受面，
        // 测试宿主重启/手工 security list-keychains -s 还原即可。
        using var alpha = RequireIdentity(
            Path.Combine(directory, "signing-a"), "BundlerGateAlpha", "GATEAAAAAA");
        using var beta = RequireIdentity(
            Path.Combine(directory, "signing-b"), "BundlerGateBeta", "GATEBBBBBB");
        Assert.NotEqual(alpha.TeamId, beta.TeamId);
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        alpha.Sign(install);
        beta.Sign(payload);
        var installBefore = Snapshot(install);

        var exception = Assert.Throws<UpdateRejectedException>(() =>
            MacAppGate.CheckAndStrip(install, payload, _ => { }));

        Assert.Contains("cross-identity", exception.Message);
        Assert.Equal(installBefore, Snapshot(install));
    }

    [Fact]
    public void CheckAndStrip_TamperedSignedPayload_Rejects()
    {
        // 自签后篡改 bundle 内密封件 → codesign --verify --deep --strict 不过即拒。
        Assert.SkipUnless(TestPlatform.IsMacOS, "codesign/xattr 门禁只在 macOS 宿主存在。");
        var directory = Ws.Combine("tampered");
        using var identity = RequireIdentity(
            Path.Combine(directory, "signing"), "BundlerGateAlpha", "GATEAAAAAA");
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "App", "com.bundler.gate");
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        identity.Sign(payload);
        // 签名后改动被 CodeResources 密封的资源件——结构验签必炸。
        File.AppendAllText(
            Path.Combine(payload, "Contents", "Resources", "data.txt"), "tampered\n");

        var exception = Assert.Throws<UpdateRejectedException>(() =>
            MacAppGate.CheckAndStrip(install, payload, _ => { }));

        Assert.Contains("codesign verification", exception.Message);
    }

    [Fact]
    public void CheckAndStrip_SameTeam_AcceptsAndStripsQuarantine()
    {
        // 正面对照：同 TeamID 已签换已签 → 通过，且 quarantine 照常剥离。
        Assert.SkipUnless(TestPlatform.IsMacOS, "codesign/xattr 门禁只在 macOS 宿主存在。");
        var directory = Ws.Combine("same-team");
        using var identity = RequireIdentity(
            Path.Combine(directory, "signing"), "BundlerGateAlpha", "GATEAAAAAA");
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        identity.Sign(install);
        identity.Sign(payload);
        MacAppStub.SetQuarantineTree(payload);

        var log = new List<string>();
        MacAppGate.CheckAndStrip(install, payload, log.Add);

        Assert.Contains(log, line => line.Contains("codesign verified"));
        Assert.False(MacAppStub.HasQuarantine(payload));
    }

    // 目录零变更断言面：相对路径 → 内容哈希（目录记 <dir>）+ unix mode + quarantine
    // xattr 随节点走——拒换路径的零变更断言连同属性面一起钉死（只盯内容哈希会漏
    // "同字节不同权限/带标"的暗变）。字典等值即全等。
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        map["."] = NodeSignature(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     root, "*", SearchOption.AllDirectories))
        {
            map[Path.GetRelativePath(root, entry)] = NodeSignature(entry);
        }
        return map;
    }

    private static string NodeSignature(string entry)
    {
        var body = Directory.Exists(entry)
            ? "<dir>"
            : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry)));
        // CA1416 不认 SkipUnless——mode/xattr 面挂在非 Windows 分支
        //（本文件各腿在 macOS 门禁后才走到这里，运行时恒走此支）。
        if (OperatingSystem.IsWindows())
        {
            return body;
        }
        return $"{body}|mode={(int)File.GetUnixFileMode(entry):x4}" +
            $"|quarantine={MacAppStub.HasQuarantine(entry)}";
    }

    // 自签身份尽力腿：不可用即诚实 Skip——断言的是真 codesign 链，不伪造环境。
    private static SigningIdentity RequireIdentity(string workDir, string commonName, string ou)
    {
        var identity = TryCreateIdentity(workDir, commonName, ou, out var reason);
        Assert.SkipWhen(identity is null,
            $"SKIP: 自签证书身份面在此宿主不可用——{reason}；该面归 special-acceptance SA-P-07。");
        return identity!;
    }

    // 临时 keychain 自签证书链：openssl 造 codeSigning EKU 自签件 → TemporaryKeychain 导入
    // （生产同款"用完还原搜索列表+删 keychain"清理语义）→ 签探针件实证 TeamIdentifier。
    // 任一步不成立返回 null（reason 出参带现场），不抛出——调用方决定 Skip。
    private static SigningIdentity? TryCreateIdentity(
        string workDir, string commonName, string ou, out string reason)
    {
        reason = "";
        if (!ExternalTools.Has("openssl"))
        {
            reason = "openssl is unavailable on this host";
            return null;
        }
        Directory.CreateDirectory(workDir);
        // 证书 Subject OU 即 codesign 填进 TeamIdentifier 的字段（签名后实证）。
        var config = Path.Combine(workDir, "cert.cnf");
        File.WriteAllText(config,
            "[ req ]\n" +
            "distinguished_name = dn\n" +
            "x509_extensions = ext\n" +
            "prompt = no\n" +
            "[ dn ]\n" +
            $"CN = {commonName}\n" +
            $"OU = {ou}\n" +
            "[ ext ]\n" +
            "keyUsage = digitalSignature\n" +
            "extendedKeyUsage = codeSigning\n" +
            "basicConstraints = critical,CA:FALSE\n");
        var keyPath = Path.Combine(workDir, commonName + ".key.pem");
        var certPath = Path.Combine(workDir, commonName + ".cert.pem");
        var p12Path = Path.Combine(workDir, commonName + ".p12");
        var request = ProcessRunner.Run("openssl",
            ["req", "-x509", "-newkey", "rsa:2048", "-nodes",
             "-keyout", keyPath, "-out", certPath, "-days", "1", "-config", config],
            new ProcessRunner.Options { WorkingDirectory = workDir });
        if (request.ExitCode != 0)
        {
            reason = $"openssl req failed rc={request.ExitCode}: {request.StdErr.Trim()}";
            return null;
        }
        // 空口令 p12：测试专用假材料，同时避免口令进 argv（仓规"秘密不入命令行"）。
        var exported = ProcessRunner.Run("openssl",
            ["pkcs12", "-export", "-out", p12Path,
             "-inkey", keyPath, "-in", certPath, "-passout", "pass:"],
            new ProcessRunner.Options { WorkingDirectory = workDir });
        if (exported.ExitCode != 0)
        {
            reason = $"openssl pkcs12 export failed rc={exported.ExitCode}: {exported.StdErr.Trim()}";
            return null;
        }

        MacAppSigning.TemporaryKeychain keychain;
        try
        {
            keychain = MacAppSigning.TemporaryKeychain.CreateAsync(
                workDir, p12Path, "", NullBundleLogger.Instance, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            reason = $"temporary keychain create failed: {exception.Message}";
            return null;
        }
        var identity = new SigningIdentity(keychain, workDir);

        // 实证探测：自签件签出后 codesign -dv 的 TeamIdentifier 必须取到非 "not set" 值，
        // 否则门禁的签名身份面在该 runner 上测不了（诚实 Skip 面）。
        try
        {
            var probe = MacAppStub.WriteBundle(
                Path.Combine(workDir, "probe"), "Probe", "com.bundler.gateprobe",
                machOExecutable: true);
            identity.Sign(probe);
        }
        catch (Exception exception)
        {
            identity.Dispose();
            reason = $"self-sign probe failed: {exception.Message}";
            return null;
        }
        // 探针 -dv 同样尽力腿：与 identity.Sign 同款语义——工具拉起失败按不可用计（Skip），不炸测试。
        string describeStdErr;
        try
        {
            var describe = ProcessRunner.Run("/usr/bin/codesign",
                ["-dv", "--verbose=4", Path.Combine(workDir, "probe", "Probe.app")],
                new ProcessRunner.Options { WorkingDirectory = workDir });
            describeStdErr = describe.StdErr;
        }
        catch (Exception exception)
        {
            identity.Dispose();
            reason = $"codesign -dv probe failed: {exception.Message}";
            return null;
        }
        var team = Regex.Match(describeStdErr, "^TeamIdentifier=(.+)$", RegexOptions.Multiline);
        var teamId = team.Success ? team.Groups[1].Value.Trim() : "";
        if (teamId.Length == 0 || teamId == "not set")
        {
            identity.Dispose();
            reason = "codesign -dv reports TeamIdentifier=" +
                (teamId.Length == 0 ? "<absent>" : "not set") +
                " for self-signed certs on this host";
            return null;
        }
        identity.TeamId = teamId;
        return identity;
    }

    // 一个可用签名身份：临时 keychain 句柄 + 证书 CN + 实证 TeamID；Dispose 还原搜索列表并删件。
    private sealed class SigningIdentity(MacAppSigning.TemporaryKeychain keychain, string workDir)
        : IDisposable
    {
        internal string Name => keychain.Identity;
        internal string TeamId = "";

        internal void Sign(string bundleDir)
        {
            // --timestamp=none：不联 Apple 时间戳服务器——CI 腿离线确定性。
            var result = ProcessRunner.Run("/usr/bin/codesign",
                ["--force", "--sign", Name, "--timestamp=none",
                 "--keychain", keychain.Path, bundleDir],
                new ProcessRunner.Options { WorkingDirectory = workDir });
            ProcessRunner.AssertSuccess(result, $"codesign --sign '{Name}'");
        }

        public void Dispose()
        {
            keychain.DisposeAsync(workDir, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
