using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Core;

/// <summary>
/// 入口层共享的缺省推导单源：宿主 target 词、目标格式缺省集、可执行文件候选、
/// identifier 段规范化。CLI 与 MSBuild 推断链同一来源——规则只在这里写一遍；
/// MSBuild eval 期 XML 镜像（props/targets 里的 RID 表与格式缺省）以本表为准。
/// </summary>
public static class BundleDefaults
{
    /// <summary>未提供版本时的占位值——纯数字三段式，Apple 元数据（[0-9.]+）可接受。</summary>
    public const string PlaceholderVersion = "0.1.0";

    /// <summary>未提供 identifier 时的合成前缀。</summary>
    public const string IdentifierPrefix = "dev.bundler.";

    /// <summary>当前宿主的 canonical target 词（{os}-{arch}）。</summary>
    public static string HostTarget()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => "i686",
            Architecture.Arm => "armv7hf",
            _ => "x86_64"
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows-" + arch;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macos-" + arch;
        }
        return "linux-" + arch;
    }

    /// <summary>
    /// 目标 OS 的格式缺省集（显式 formats 恒胜）：windows→nsis、macos→dmg、
    /// linux-musl→alpineapk、linux→deb、未知/不可解析→zip（纯托管、全宿主可产）。
    /// universal 视同 macos。
    /// </summary>
    public static IReadOnlyList<PackageFormat> DefaultFormatsForTarget(string targetText)
    {
        if (BundleTarget.TryParse(targetText, out var target) && target is not null)
        {
            return target.OperatingSystem switch
            {
                TargetOperatingSystem.Windows => [PackageFormat.Nsis],
                TargetOperatingSystem.MacOS => [PackageFormat.Dmg],
                TargetOperatingSystem.Linux => target.IsMusl
                    ? [PackageFormat.AlpineApk]
                    : [PackageFormat.Deb],
                _ => [PackageFormat.Zip]
            };
        }
        return [PackageFormat.Zip];
    }

    /// <summary>
    /// 输入目录顶层的可执行文件候选名（不递归）：windows 目标按 .exe 判，
    /// 其余按 POSIX 执行位判；Windows 宿主看不透 POSIX 执行位，返回空。
    /// </summary>
    public static IReadOnlyList<string> ExecutableCandidates(string inputDirectory, bool forWindows)
    {
        return Directory.EnumerateFiles(inputDirectory)
            .Select(path => new { Name = Path.GetFileName(path), Path = path })
            .Where(entry => IsExecutableCandidate(entry.Path, entry.Name!, forWindows))
            .Select(entry => entry.Name!)
            .ToArray();
    }

    private static bool IsExecutableCandidate(string path, string name, bool forWindows)
    {
        if (forWindows)
        {
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // POSIX 执行位在 Windows 宿主无从判——不猜，回落 productName 惯例。
            return false;
        }
#if NET10_0_OR_GREATER
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
#else
        // netstandard2.0 无 UnixFileMode——Windows 侧工具链程序集（MSBuild 任务）
        // 不服务 POSIX 宿主，返回空让调用方回落惯例名。
        return false;
#endif
    }

    /// <summary>
    /// identifier 段规范化：小写 [a-z0-9]，非法字符折叠成单个 '-'，首尾不留 '-'，
    /// 全非法回落 "app"——与 MSBuild props 里 Regex 镜像同一产出。
    /// </summary>
    public static string NormalizeIdentifierSegment(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastDash = true; // 首字符不允许 '-'——当作前导连续态跳过
        foreach (var ch in name.ToLowerInvariant())
        {
            var valid = ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9';
            if (valid)
            {
                builder.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                builder.Append('-');
                lastDash = true;
            }
        }
        var segment = builder.ToString().TrimEnd('-');
        return segment.Length == 0 ? "app" : segment;
    }
}
