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
        // env，Clear() 后所余即全集）——整块语义原样交 EnvironmentIsComplete
        // 完整块模式（增量差集会把"袋中缺席"的父件变量透传：CI 实证 sh 腿
        // Clear() 后 DEBIAN_FRONTEND 仍漏进 stub 环境）。
        mapped.EnvironmentIsComplete = true;
        foreach (var pair in info.Environment)
        {
            mapped.Environment[pair.Key] = pair.Value;
        }
        return mapped;
    }

    public static ToolProcess? Start(ProcessStartInfo info)
        => ToolProcess.Start(Map(info));
}
