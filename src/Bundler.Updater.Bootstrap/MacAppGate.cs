using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotNet.Bundler.Updater.Bootstrap;

/// <summary>
/// macOS .app 换包前置门禁（Sparkle 同款三项，在备份/换包之前执行——拒绝时安装目录零变更）：
///   1. 载荷签名完好（codesign --verify --deep --strict）；
///   2. 签名身份连续（旧件已签则新件必须同 Team ID——防跨签名者劫持更新通道；旧件未签不拦升级）；
///   3. 剥 quarantine 属性，免 Gatekeeper 首跑拦截；
/// 另加 CFBundleIdentifier 一致兜底，防“拿别的应用来换”。
/// </summary>
internal static class MacAppGate
{
    internal static bool LooksLikeAppBundle(string payloadDir) =>
        payloadDir.EndsWith(".app", StringComparison.Ordinal) ||
        File.Exists(Path.Combine(payloadDir, "Contents", "Info.plist"));

    internal static void CheckAndStrip(string installDir, string payloadDir, Action<string> log)
    {
        var oldIdentity = TryReadSigningIdentity(installDir, out var oldProbeFailed);
        var newIdentity = TryReadSigningIdentity(payloadDir, out var newProbeFailed);
        // codesign 起不来/超时 ≠ 未签——签名门禁判定器缺席时若按"未签"放行，
        // 等于把"已签→未签降级/跨身份劫持"判定整扇拆掉（无法执行的探测把
        // 已签安装静默降级成可换）。stock macOS 恒有 codesign，缺席即异常宿主
        // fail closed（与 sh 腿"探测失败即拒"同口径）。
        if (oldProbeFailed || newProbeFailed)
        {
            throw new UpdateRejectedException(
                "codesign could not be executed on this host — refusing to evaluate " +
                "the bundle signing gate.");
        }

        if (newIdentity is { } signed)
        {
            // 签了名就必须完好——坏签/残签视同篡改件拒绝。
            var verify = RunTool("/usr/bin/codesign", "--verify", "--deep", "--strict", payloadDir);
            if (verify.ExitCode != 0)
            {
                throw new UpdateRejectedException(
                    $"payload bundle failed codesign verification: {verify.StdErr.Trim()}");
            }
            if (oldIdentity is { } expected)
            {
                if (!string.Equals(signed.TeamIdentifier, expected.TeamIdentifier,
                        StringComparison.Ordinal))
                {
                    throw new UpdateRejectedException(
                        $"payload is signed by '{signed.TeamIdentifier}', installed bundle by " +
                        $"'{expected.TeamIdentifier}' — refusing cross-identity update.");
                }
                log($"bundler-updater: codesign verified, team '{signed.TeamIdentifier}'");
            }
            else
            {
                log("bundler-updater: installed bundle unsigned; signed payload accepted");
            }
        }
        else if (oldIdentity is { } expected)
        {
            // 已签 → 未签是降级，更新通道劫持的常见形态。
            throw new UpdateRejectedException(
                $"installed bundle is signed by '{expected.TeamIdentifier}' but payload is unsigned — " +
                "refusing downgrade.");
        }
        else
        {
            log("bundler-updater: both bundles unsigned; codesign identity check skipped");
        }

        // CFBundleIdentifier 一致兜底：未签场景下防“拿别的应用来换”。
        var oldBundleId = TryReadBundleId(Path.Combine(installDir, "Contents", "Info.plist"));
        var newBundleId = TryReadBundleId(Path.Combine(payloadDir, "Contents", "Info.plist"));
        if (oldBundleId is { Length: > 0 } && newBundleId is { Length: > 0 } &&
            !string.Equals(oldBundleId, newBundleId, StringComparison.Ordinal))
        {
            throw new UpdateRejectedException(
                $"bundle identifier mismatch: installed '{oldBundleId}' vs payload '{newBundleId}'.");
        }

        // 更新件经下载通道而来会带 quarantine；签名/身份核验过后剥掉，免首跑拦截。
        var strip = RunTool("/usr/bin/xattr", "-dr", "com.apple.quarantine", payloadDir);
        log(strip.ExitCode == 0
            ? "bundler-updater: quarantine stripped from payload"
            : "bundler-updater: quarantine not present (ok)");
    }

    // codesign -dv 读签名身份；未签 → null。Identifier+TeamIdentifier 都在 stderr。
    // probeFailed 出参区分两种"未签"：codesign 实跑判未签（正常放行路径）vs
    // 工具不可执行/超时（-1 出口，fail-closed 场景必须可区分）。
    private static SigningIdentity? TryReadSigningIdentity(string bundleDir, out bool probeFailed)
    {
        probeFailed = false;
        if (!Directory.Exists(bundleDir))
        {
            return null;
        }
        var result = RunTool("/usr/bin/codesign", "-dv", "--verbose=4", bundleDir);
        if (result.ExitCode == -1)
        {
            probeFailed = true;
            return null;
        }
        if (result.ExitCode != 0)
        {
            return null;
        }
        var identifier = MatchField(result.StdErr, "Identifier");
        var team = MatchField(result.StdErr, "TeamIdentifier");
        // ad-hoc/未设置团队签名视同未签（无身份可比）。
        return team is { Length: > 0 } && team != "not set"
            ? new SigningIdentity(identifier ?? "", team)
            : null;
    }

    // Info.plist 可能是 XML 或二进制：XML 直接正则取值；二进制（bplist00 魔数）
    // 退化 /usr/libexec/PlistBuddy 取值——与 sh 腿同件同键。再取不到按缺省
    // （该兜底项跳过，签名门禁仍是主判据）。
    private static string? TryReadBundleId(string infoPlist)
    {
        if (!File.Exists(infoPlist))
        {
            return null;
        }
        string text;
        try
        {
            text = File.ReadAllText(infoPlist);
        }
        catch (IOException)
        {
            return null;
        }
        var match = Regex.Match(text,
            "<key>CFBundleIdentifier</key>\\s*<string>([^<]+)</string>");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }
        if (!text.StartsWith("bplist", StringComparison.Ordinal))
        {
            return null;
        }
        var buddy = RunTool("/usr/libexec/PlistBuddy", "-c", "Print :CFBundleIdentifier", infoPlist);
        return buddy.ExitCode == 0 && buddy.StdOut.Trim() is { Length: > 0 } value
            ? value
            : null;
    }

    private static string? MatchField(string output, string field)
    {
        var match = Regex.Match(output, $"^{Regex.Escape(field)}=(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // 出口码 -1 专属"工具没跑起来"（spawn 失败/超时 kill）——真实退出码不会为负，
    // 调用方据此把"判定器缺席"与"判定器判未签"分流（fail-closed 前提）。
    private static (int ExitCode, string StdOut, string StdErr) RunTool(string path, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(path) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            using var process = ProcessWait.Start(startInfo)!;
            // 并行消费：stderr 写满管道会让子进程卡住，等满 30s 造成假超时。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!ProcessWait.WaitForExit(process, 30_000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                // 杀后回收也有界：macOS CI 实证过 reaper 饿死后僵尸子件不回收——
                // 轮询旗标不落位也按期放弃，不在此悬停。
                ProcessWait.WaitForExit(process, 30_000);
                return (-1, "", "timeout");
            }
            Task.WaitAll(stdout, stderr);
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception exception)
        {
            return (-1, "", exception.Message);
        }
    }

    private sealed record SigningIdentity(string Identifier, string TeamIdentifier);
}

internal sealed class UpdateRejectedException(string message) : Exception(message);
