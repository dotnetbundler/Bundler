// 真实 spawn 腿：Unit 层零真实进程不变量不适用此层——子进程永不退出时
// ToolRunner 有界等待到点杀进程，按 ExitCode=-1 交给既有非零处理面。
// 首条直 spawn 平台既有 sleeper（ping/sleep）不借壳；第二条刻意经 cmd/sh 壳
// 造持管道孙代，验树杀+有界排空在孙代存活时不悬停。
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
    public static void RunSync_GrandchildHoldsPipe_CompletesWithinDrainBound()
    {
        // RunSync 成功腿同样有界：子进程先退、孙代仍持 stdout 写端时 EOF 不至——
        // 排空最多等 5s，不得无界等到孙代自己寿终。孙代 20s > 排空界；
        // 断言远小于它即证明有界（旧实现会在 Task.WaitAll 悬停整 20s）。
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        if (OperatingSystem.IsWindows())
        {
            // ping 在重定向句柄下照常运行 20s 持管（timeout 会判 stdin 重定向即退，不可用）。
            ToolRunner.RunSync("powershell.exe",
                ["-NoProfile", "-Command",
                 "Start-Process ping -ArgumentList '-n','20','127.0.0.1' -NoNewWindow"]);
        }
        else
        {
            ToolRunner.RunSync("sh", ["-c", "sleep 20 &"]);
        }
        elapsed.Stop();

        // 排空界约 5s：下界断言证明确实等过孙代持管窗口（<4s 说明管道没被持住），
        // 上界断言证明有界（旧实现在 Task.WaitAll 悬停整 20s）。
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(4),
            $"drain bound did not engage ({elapsed.Elapsed}) — the pipe was not held by a grandchild.");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15),
            $"RunSync must not wait for grandchildren holding the pipes ({elapsed.Elapsed}).");
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
