using System.Runtime.InteropServices;

namespace DotNet.Bundler.Core;

/// <summary>
/// 宿主文件系统路径字符串的比较口径：Windows/macOS 默认文件系统不区分大小写，
/// Linux 区分；其他宿主（FreeBSD 等）保守按不敏感处理——过检不敏感路径只是
/// 多拒，漏判敏感路径是语义错误。路径包含/归属/碰撞判定一律走这里——裸写
/// <see cref="StringComparison.Ordinal"/> 在 Windows/macOS 会漏判大小写变体
/// （如 'c:\in' 穿透 'C:\In' 前缀检查，或大小写差一字的产物撞名静默互覆）。
/// 与 <see cref="BundleConfigurationValidator"/> 的宿主 FS 判定同一约定。
/// </summary>
internal static class PathText
{
    internal static StringComparer Comparer =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    internal static StringComparison Comparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
