using System.Text;

#if BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Protocol;
#else
namespace DotNet.Bundler.Core.Update;
#endif

/// <summary>
/// UPDATE 链 argv 拼写单源：ProcessStartInfo.Arguments 是整条字符串（netstandard2.0 无
/// ArgumentList），逐元双引号包裹 + CommandLineToArgvW 规则转义（.NET 的 POSIX
/// Arguments→argv 解析同规则）；另供 POSIX 单引号引用（su -c/问密码管线腿）。
/// 客户端 SyspkgReplay/UpdateApplier 与引导件 PkgApplyPlan 同源链接编译——
/// 不转义时路径里的 " 或串尾 \ 会撕裂参数边界（含把 --install-dir/--backup-dir
/// 解析到别的目录的数据损类后果）。
/// </summary>
internal static class UpdateArgv
{
    /// <summary>逐元双引号包裹、空格连接成 ProcessStartInfo.Arguments 字符串。</summary>
    public static string Join(IEnumerable<string> argv)
    {
        var builder = new StringBuilder();
        foreach (var arg in argv)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            AppendQuoted(builder, arg);
        }
        return builder.ToString();
    }

    // 引号规则：引号区内 " → 前导 \ 翻倍后再附 \"；串尾连续 \（紧邻收尾引号）
    // 翻倍防吞引号；其余 \ 原样。
    private static void AppendQuoted(StringBuilder builder, string arg)
    {
        builder.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
    }

    /// <summary>POSIX 单引号引用（' 折为 '\''）——su -c/问密码管线腿的逐 token 转义。</summary>
    public static string ShQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>逐元单引号引用、空格连接成 sh 命令串。</summary>
    public static string ShJoin(IEnumerable<string> argv) =>
        string.Join(" ", argv.Select(ShQuote));
}
