namespace DotNet.Bundler.Core;

/// <summary>
/// 宿主文件系统路径字符串的比较口径：Windows（'\\' 分隔符）不区分大小写，POSIX 区分。
/// 路径包含/归属/碰撞判定一律走这里——裸写 <see cref="StringComparison.Ordinal"/>
/// 在 Windows 会漏判大小写变体（如 'c:\in' 穿透 'C:\In' 前缀检查）。
/// </summary>
internal static class PathText
{
    internal static StringComparer Comparer =>
        Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static StringComparison Comparison =>
        Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
