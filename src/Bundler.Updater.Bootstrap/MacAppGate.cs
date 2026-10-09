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
        var oldIdentity = TryReadSigningIdentity(installDir);
        var newIdentity = TryReadSigningIdentity(payloadDir);

        if (newIdentity is { } signed)
        {
            // 签了名就必须完好——坏签/残签视同篡改件拒绝。
            var verify = RunTool("/usr/bin/codesign", "--verify", "--deep", "--strict", payloadDir);
            if (verify.ExitCode != 0)
            {
                throw new UpdateRejectedException(
                    $"payload bundle failed codesign verification: {verify.Error.Trim()}");
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

    // codesign -dv 读签名身份；未签/工具缺失 → null。Identifier+TeamIdentifier 都在 stderr。
    private static SigningIdentity? TryReadSigningIdentity(string bundleDir)
    {
        if (!Directory.Exists(bundleDir))
        {
            return null;
        }
        var result = RunTool("/usr/bin/codesign", "-dv", "--verbose=4", bundleDir);
        if (result.ExitCode != 0)
        {
            return null;
        }
        var identifier = MatchField(result.Error, "Identifier");
        var team = MatchField(result.Error, "TeamIdentifier");
        // ad-hoc/未设置团队签名视同未签（无身份可比）。
        return team is { Length: > 0 } && team != "not set"
            ? new SigningIdentity(identifier ?? "", team)
            : null;
    }

    // Info.plist 可能是 XML 或二进制：XML 直接正则取值，其余形态返回 null（该兜底项跳过）。
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
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? MatchField(string output, string field)
    {
        var match = Regex.Match(output, $"^{Regex.Escape(field)}=(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static (int ExitCode, string Error) RunTool(string path, params string[] arguments)
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
            using var process = Process.Start(startInfo)!;
            // 并行消费：stderr 写满管道会让子进程卡住，等满 30s 造成假超时。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                process.WaitForExit();
                return (-1, "timeout");
            }
            Task.WaitAll(stdout, stderr);
            return (process.ExitCode, stderr.Result);
        }
        catch (Exception)
        {
            // 工具不可用的宿主等价"未签"——让调用方按未签路径继续判断，不伪造拒绝。
            return (-1, "");
        }
    }

    private sealed record SigningIdentity(string Identifier, string TeamIdentifier);
}

internal sealed class UpdateRejectedException(string message) : Exception(message);
