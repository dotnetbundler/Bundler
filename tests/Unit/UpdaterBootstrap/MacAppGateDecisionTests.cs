using DotNet.Bundler.Updater.Bootstrap;
using Xunit;

namespace Bundler.UnitTests.UpdaterBootstrap;

/// <summary>
/// <see cref="MacAppGate"/> 已签分支决策表（AOT 腿）：经 GateTool 缝注入桩 codesign 输出，
/// 无 Apple 证书即覆盖同队/跨队/篡改/降级/无 Team/探测失败/BundleID 兜底——
/// 自签证书 TeamIdentifier 恒 "not set"，真签名腿取不到已签分支，此表是该分支唯一的自动化证据。
/// sh 腿同表见 Integration MacAppGateShTests。
/// </summary>
public sealed class MacAppGateDecisionTests : TempBase
{
    [Theory]
    [MemberData(nameof(GateCase.Data), MemberType = typeof(GateCase))]
    public void DecisionTable(GateCase gateCase)
    {
        var install = WriteBundle("install-" + gateCase.Name, gateCase.OldBundleId);
        var payload = WriteBundle("payload-" + gateCase.Name, gateCase.NewBundleId);
        var calls = new List<string>();
        MacAppGate.GateTool tool = (path, args) =>
        {
            calls.Add(Path.GetFileName(path) + " " + string.Join(" ", args));
            if (path == "/usr/bin/codesign" && gateCase.ProbeFails)
            {
                return (-1, "", "spawn failed");
            }
            if (path == "/usr/bin/codesign" && args[0] == "-dv")
            {
                var side = args[^1] == install ? gateCase.Old : gateCase.New;
                var (rc, stderr) = GateCase.DescribeOutput(side, gateCase.NewBundleId);
                return (rc, "", stderr);
            }
            if (path == "/usr/bin/codesign" && args[0] == "--verify")
            {
                Assert.Equal(payload, args[^1]);
                return (gateCase.New.VerifyRc, "", gateCase.New.VerifyRc == 0 ? "" : "invalid signature");
            }
            if (path == "/usr/bin/xattr")
            {
                return (0, "", "");
            }
            throw new InvalidOperationException($"unexpected tool call: {path} {string.Join(" ", args)}");
        };

        if (gateCase.Accepts)
        {
            MacAppGate.CheckAndStrip(install, payload, _ => { }, tool);
            // 放行必剥 quarantine；载荷有 Team 才跑 --verify（无 Team 视同未签不验）。
            Assert.Contains(calls, c => c.StartsWith("xattr -dr com.apple.quarantine", StringComparison.Ordinal));
            var hasTeam = gateCase.New.Team is { } team && team != "not set";
            Assert.Equal(hasTeam, calls.Any(c => c.StartsWith("codesign --verify", StringComparison.Ordinal)));
        }
        else
        {
            var exception = Assert.Throws<UpdateRejectedException>(() =>
                MacAppGate.CheckAndStrip(install, payload, _ => { }, tool));
            Assert.Contains(gateCase.Reason!, exception.Message);
            // 拒绝发生在剥标之前——安装目录零变更由调用方在换包前拒绝保证。
            Assert.DoesNotContain(calls, c => c.StartsWith("xattr", StringComparison.Ordinal));
        }
    }

    private string WriteBundle(string name, string bundleId)
    {
        var app = Path.Combine(Root, name, "App.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents"));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict>\n" +
            $"<key>CFBundleIdentifier</key><string>{bundleId}</string>\n</dict></plist>\n");
        return app;
    }
}
