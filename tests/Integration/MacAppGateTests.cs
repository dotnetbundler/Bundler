// MacAppGate 门禁真件腿（macOS 宿主）：codesign/xattr 走系统工具，Unit 层零子进程规则不收。
// 断言面对应 MacAppGate.CheckAndStrip 三分支：
//   零证书面（恒可自动化）——CFBundleIdentifier 兜底拒换 + quarantine 剥离；
//   签名身份面（真证书腿）——自签证书签出的 TeamIdentifier 恒为 "not set"（门禁视同未签），
//   测不到已签分支；故改由环境变量提供带 Team ID 的真 .p12（BUNDLER_TEST_MAC_SIGNING_P12[_B]
//   及同名 _PASSWORD[_B]），未提供即诚实 Skip——SA-P-07 取得 Developer ID 证书时直接跑本类。
//   已签分支决策表的常驻自动化证据见 Unit MacAppGateDecisionTests（AOT）与 MacAppGateShTests（sh）。
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
    // 真证书腿的输入：A=主身份；B=异队身份（只跨队腿需要）。口令经 TemporaryKeychain
    // 落 0600 口令文件再以 openssl -passin file: 解包，全程不进 argv。
    private const string P12EnvVar = "BUNDLER_TEST_MAC_SIGNING_P12";
    private const string P12PasswordEnvVar = "BUNDLER_TEST_MAC_SIGNING_P12_PASSWORD";

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
        // 能力门禁：探测即打标——宿主读不回标时剥标断言是真空通过，诚实 Skip。
        Assert.SkipWhen(!MacAppStub.TrySetQuarantineTree(payload),
            "宿主不持久化 com.apple.quarantine（macOS 26+ 受保护属性），剥标断言不可用——归 special-acceptance。");

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
        using var identity = RequireIdentity(Path.Combine(directory, "signing"));
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
        using var identity = RequireIdentity(Path.Combine(directory, "signing"));
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
        // 临时 keychain 不碰用户搜索列表：codesign 经 --keychain 直给身份，
        // Dispose 只删 keychain 件——销毁序不再承重，using 逆序仅作惯例。
        using var alpha = RequireIdentity(Path.Combine(directory, "signing-a"));
        using var beta = RequireIdentity(Path.Combine(directory, "signing-b"), secondary: true);
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
        using var identity = RequireIdentity(Path.Combine(directory, "signing"));
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
        using var identity = RequireIdentity(Path.Combine(directory, "signing"));
        var install = MacAppStub.WriteBundle(
            Path.Combine(directory, "install-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        var payload = MacAppStub.WriteBundle(
            Path.Combine(directory, "payload-slot"), "App", "com.bundler.gate",
            machOExecutable: true);
        identity.Sign(install);
        identity.Sign(payload);
        // 同 unsigned-both 腿：宿主不持久化打标时剥标断言是真空通过，诚实 Skip。
        Assert.SkipWhen(!MacAppStub.TrySetQuarantineTree(payload),
            "宿主不持久化 com.apple.quarantine（macOS 26+ 受保护属性），剥标断言不可用——归 special-acceptance。");

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

    // 真证书腿：未提供 .p12 即诚实 Skip——断言的是真 codesign 链，不伪造环境。
    private static SigningIdentity RequireIdentity(string workDir, bool secondary = false)
    {
        var suffix = secondary ? "_B" : "";
        var p12 = Environment.GetEnvironmentVariable(P12EnvVar + suffix);
        var password = Environment.GetEnvironmentVariable(P12PasswordEnvVar + suffix);
        Assert.SkipWhen(string.IsNullOrEmpty(p12) || password is null,
            $"SKIP: 未提供 {P12EnvVar}{suffix}/{P12PasswordEnvVar}{suffix}（带 Team ID 的真 .p12）——" +
            "自签证书 TeamIdentifier 恒 not set 测不到已签分支；决策表由 MacAppGateDecisionTests/" +
            "MacAppGateShTests 桩覆盖，真签名链归 special-acceptance SA-P-07。");
        var identity = TryCreateIdentity(workDir, p12!, password!, out var reason);
        // 已提供证书却用不起来是配置错误，不是宿主能力缺口——失败而非 Skip。
        Assert.True(identity is not null, $"{P12EnvVar}{suffix} 提供的证书不可用：{reason}");
        return identity!;
    }

    // 临时 keychain 导入调用方提供的 .p12（生产同款"--keychain 直给 + 用完删件"语义，
    // 不触碰用户搜索列表）→ 签探针件实证 TeamIdentifier。任一步不成立返回 null（reason 带现场）。
    private static SigningIdentity? TryCreateIdentity(
        string workDir, string p12Path, string password, out string reason)
    {
        reason = "";
        Directory.CreateDirectory(workDir);
        MacSigningSupport.TemporaryKeychain keychain;
        try
        {
            keychain = MacSigningSupport.TemporaryKeychain.CreateAsync(
                workDir, p12Path, password, NullBundleLogger.Instance, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            reason = $"temporary keychain create failed: {exception.Message}";
            return null;
        }
        var identity = new SigningIdentity(keychain, workDir);

        // 实证探测：签出后 codesign -dv 的 TeamIdentifier 必须取到非 "not set" 值——
        // 门禁只认 Team ID，无 Team 的证书测不到已签分支。
        try
        {
            var probe = MacAppStub.WriteBundle(
                Path.Combine(workDir, "probe"), "Probe", "com.bundler.gateprobe",
                machOExecutable: true);
            identity.Sign(probe);
            var describe = ProcessRunner.Run("/usr/bin/codesign",
                ["-dv", "--verbose=4", probe],
                new ProcessRunner.Options { WorkingDirectory = workDir });
            var team = Regex.Match(describe.StdErr, "^TeamIdentifier=(.+)$", RegexOptions.Multiline);
            var teamId = team.Success ? team.Groups[1].Value.Trim() : "";
            if (teamId.Length == 0 || teamId == "not set")
            {
                identity.Dispose();
                reason = "codesign -dv reports TeamIdentifier=" +
                    (teamId.Length == 0 ? "<absent>" : "not set") + " for this certificate";
                return null;
            }
            identity.TeamId = teamId;
            return identity;
        }
        catch (Exception exception)
        {
            identity.Dispose();
            reason = $"sign probe failed: {exception.Message}";
            return null;
        }
    }

    // 一个可用签名身份：临时 keychain 句柄 + 证书 CN + 实证 TeamID；Dispose 删件即可（不涉搜索列表）。
    private sealed class SigningIdentity(MacSigningSupport.TemporaryKeychain keychain, string workDir)
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
