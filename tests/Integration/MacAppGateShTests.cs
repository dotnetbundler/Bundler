// MacAppGate 已签分支决策表（sh 腿）：真跑 tools/posix/bundler-updater.sh apply，PATH 前置
// 桩 codesign 脚本按 bundle 内标记件回放观察面（-dv TeamIdentifier / --verify 出口码 / 起不来）。
// 与 Unit MacAppGateDecisionTests（AOT 腿）共用 GateCase 表——两份实现必须同判。
// sh 门禁只在 uname=Darwin 时生效，故本腿 macOS 专属；真 Apple 签名链归 SA-P-07。
using System.Diagnostics;
using System.Security.Cryptography;

public sealed class MacAppGateShTests
{
    private static readonly string PosixScript = Path.Combine(
        RepoRoot(), "src", "Bundler.Updater.Bootstrap", "tools", "posix", "bundler-updater.sh");

    private const string TeamMarker = ".gate-team";
    private const string VerifyRcMarker = ".gate-verify-rc";

    // 桩 codesign：末参为 bundle 目录；同目录 probe-fails 存在即模拟"起不来"（127）。
    private const string CodesignShim =
        "#!/bin/sh\n" +
        "for last; do :; done\n" +
        "[ -f \"$(dirname \"$0\")/probe-fails\" ] && exit 127\n" +
        "case \"$1\" in\n" +
        "  -dv)\n" +
        "    if [ -f \"$last/Contents/" + TeamMarker + "\" ]; then\n" +
        "      printf 'Identifier=com.bundler.gate\\nTeamIdentifier=%s\\n' \"$(cat \"$last/Contents/" + TeamMarker + "\")\" >&2\n" +
        "      exit 0\n" +
        "    fi\n" +
        "    echo 'code object is not signed at all' >&2; exit 1 ;;\n" +
        "  --verify)\n" +
        "    if [ -f \"$last/Contents/" + VerifyRcMarker + "\" ]; then exit \"$(cat \"$last/Contents/" + VerifyRcMarker + "\")\"; fi\n" +
        "    exit 0 ;;\n" +
        "esac\n" +
        "exit 0\n";

    [Theory]
    [MemberData(nameof(GateCase.Data), MemberType = typeof(GateCase))]
    public void DecisionTable(GateCase gateCase)
    {
        Assert.SkipUnless(TestPlatform.IsMacOS, "sh 门禁只在 uname=Darwin 时生效。");
        Assert.SkipUnless(File.Exists(PosixScript), $"posix script missing: {PosixScript}");
        var root = Path.Combine(Path.GetTempPath(), "bundler-gate-sh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var install = MacAppStub.WriteBundle(Path.Combine(root, "install"), "App", gateCase.OldBundleId);
            var payload = MacAppStub.WriteBundle(Path.Combine(root, "payload"), "App", gateCase.NewBundleId);
            Mark(install, gateCase.Old, "v1");
            Mark(payload, gateCase.New, "v2");
            var shimDir = Path.Combine(root, "shim");
            Directory.CreateDirectory(shimDir);
            var shim = Path.Combine(shimDir, "codesign");
            File.WriteAllText(shim, CodesignShim);
            // CA1416 不认 SkipUnless——mode 面挂在非 Windows 分支。
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            if (gateCase.ProbeFails)
            {
                File.WriteAllText(Path.Combine(shimDir, "probe-fails"), "");
            }
            var before = Snapshot(install);

            var (rc, stderr) = RunSh(shimDir, "apply", "--install-dir", install, "--payload", payload);

            if (gateCase.Accepts)
            {
                Assert.True(rc == 0, $"expected accept, rc={rc}: {stderr}");
                Assert.Equal("v2", File.ReadAllText(Path.Combine(install, "Contents", "Resources", "gate-version.txt")));
            }
            else
            {
                Assert.True(rc == 4, $"expected reject rc=4, rc={rc}: {stderr}");
                Assert.Contains(gateCase.Reason!, stderr);
                // 拒绝在动备份/换包之前——安装目录零变更。
                Assert.Equal(before, Snapshot(install));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void Mark(string app, GateSide side, string version)
    {
        var contents = Path.Combine(app, "Contents");
        File.WriteAllText(Path.Combine(contents, "Resources", "gate-version.txt"), version);
        if (side.Team is { } team)
        {
            File.WriteAllText(Path.Combine(contents, TeamMarker), team);
        }
        if (side.VerifyRc != 0)
        {
            File.WriteAllText(Path.Combine(contents, VerifyRcMarker), side.VerifyRc.ToString());
        }
    }

    private static string Snapshot(string dir) =>
        string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f) + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    private static (int rc, string stderr) RunSh(string shimDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["PATH"] = shimDir + ":" + Environment.GetEnvironmentVariable("PATH");
        startInfo.ArgumentList.Add(PosixScript);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var process = TestProc.Start(startInfo)
            ?? throw new InvalidOperationException("spawn timed out");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            Assert.Fail("bundler-updater.sh did not exit within 60s");
        }
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stderr.Result);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Bundler.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}
