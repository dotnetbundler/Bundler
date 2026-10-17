// 真实 spawn 腿：Unit 层零真实进程不变量不适用此层——子进程永不退出时
// ToolRunner 有界等待到点杀进程，按 ExitCode=-1 交给既有非零处理面。
// 直 spawn 平台既有 sleeper（ping/sleep），不经 cmd/sh 壳——杀直系即收，无孤儿孙代。
using DotNet.Bundler.Core;

public static class ToolRunnerIntegrationTests
{
    [Fact]
    public static async Task TryRunAsync_HungChild_KilledAndReported()
    {
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("ping.exe", new[] { "-n", "30", "127.0.0.1" })
            : ("sleep", new[] { "30" });
        var previous = ToolRunner.ProcessTimeout;
        ToolRunner.ProcessTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            var result = await ToolRunner.TryRunAsync(
                exe, args, ".", TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.Equal(-1, result!.ExitCode);
            Assert.Contains("did not exit", result.StandardError);
        }
        finally
        {
            ToolRunner.ProcessTimeout = previous;
        }
    }

    [Fact]
    public static async Task TryRunAsync_GrandchildHoldingPipes_KilledAndBounded()
    {
        // 壳 spawn 持管道孙代：杀树（net10）要连孙代收、排空有 5s 兜底——
        // 只杀直系的话孙代继承的管道写端会让 ReadToEnd 挂死。
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping", "-t", "127.0.0.1" })
            : ("sh", new[] { "-c", "sleep 30 & wait" });
        var previous = ToolRunner.ProcessTimeout;
        ToolRunner.ProcessTimeout = TimeSpan.FromMilliseconds(500);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await ToolRunner.TryRunAsync(
                exe, args, ".", TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.Equal(-1, result!.ExitCode);
            Assert.Contains("did not exit", result.StandardError);
            // 有界上界 = 500ms 超时 + 5s 回收 + 5s 排空；30s 余量挡一切调度抖动。
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30),
                $"hung grandchild leg took {elapsed.Elapsed}");
        }
        finally
        {
            ToolRunner.ProcessTimeout = previous;
        }
    }
}
