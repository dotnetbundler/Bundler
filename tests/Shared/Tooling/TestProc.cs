// 测试夹具沿用 ProcessStartInfo 数据袋形状的转换层：
// 数据袋字段映射到 ToolProcessStartInfo 后交产品 ToolProcess——
// 无平行进程实现，spawn/wait/kill 语义全在产品件（杜绝测试镜像漂移）。
using System.Diagnostics;
using DotNet.Bundler.Core;

internal static class TestProc
{
    public static ToolProcessStartInfo Map(ProcessStartInfo info)
    {
        var mapped = new ToolProcessStartInfo
        {
            FileName = info.FileName,
            // ProcessStartInfo.Arguments 默认 ""（非 null）——空串不得覆盖 ArgumentList，
            // 否则 spawn 出去的进程拿不到任何参数。已知口径差：BCL 双设时
            // ArgumentList 优先 Arguments 串，本映射后 ToolProcessStartInfo 是
            // ArgumentLine 优先——调用面无双设者，登记不处理。
            ArgumentLine = string.IsNullOrEmpty(info.Arguments) ? null : info.Arguments,
            WorkingDirectory = info.WorkingDirectory,
            UseShellExecute = info.UseShellExecute,
            RedirectStandardInput = info.RedirectStandardInput,
            RedirectStandardOutput = info.RedirectStandardOutput,
            RedirectStandardError = info.RedirectStandardError,
        };
        foreach (var arg in info.ArgumentList)
        {
            mapped.Arguments.Add(arg);
        }
        // BCL：ProcessStartInfo.Environment 是"最终整块环境"（构造时预填父件
        // env，Clear() 后所余即全集）；ToolProcessStartInfo.Environment 是增量
        // 覆写。逐对拷贝会把"父件有而袋中缺席"的变量默默透传——Clear() 语义
        // 丢失（CI 实证：sh 腿 Clear() 后 DEBIAN_FRONTEND 仍漏进 stub 环境）。
        // 缺席项显式补 null 移除，等效整块替换。
        foreach (System.Collections.DictionaryEntry entry in
            System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && !info.Environment.ContainsKey(key))
            {
                mapped.Environment[key] = null;
            }
        }
        foreach (var pair in info.Environment)
        {
            mapped.Environment[pair.Key] = pair.Value;
        }
        return mapped;
    }

    // 夹具把启动参数拼回整串（道具 spawn 需要 argv 文本）。
    public static string Describe(ToolProcessStartInfo info)
        => info.Arguments.Count > 0 ? string.Join(' ', info.Arguments) : info.ArgumentLine ?? "";

    public static ToolProcess? Start(ProcessStartInfo info)
        => ToolProcess.Start(Map(info));

    public static bool WaitForExit(ToolProcess process, int timeoutMilliseconds)
        => process.WaitForExit(timeoutMilliseconds);
}
