using System.Diagnostics;

// Api/Integration 双侧链接。Api 只走公开面、不引 Bundler.Core internals——
// 与产品侧 DotNet.Bundler.Core.ProcessWait 同实现的测试件拷贝。
/// <summary>
/// 有界进程启动/退出等待——CI 实证过 macOS 上 Process.Start 内部全局 spawn 锁
/// 死锁（fork 子件卡死持读锁→reaper 写锁饿死）与子进程 zombie 面；启动 Task 化
/// 限时放弃、退出逐趟 HasExited 读旗标（reaper 死时旗标不落也按期 false）。
/// </summary>
internal static class TestProcessExit
{
    /// <summary>spawn 界：fork+exec 正常亚秒级，60s 未返回即按锁死面失败。</summary>
    internal const int SpawnTimeoutMilliseconds = 60_000;

    /// <summary>
    /// 有界 <see cref="Process.Start()"/>：超时返回 false；迟到完成的 spawn 在
    /// <paramref name="killLateSpawned"/> 为 true 时扑杀其迟到子件（分离 spawn 传
    /// false——子件本就该存活）。
    /// </summary>
    public static bool Start(Process process, int milliseconds = SpawnTimeoutMilliseconds,
        bool killLateSpawned = true)
    {
        var startTask = Task.Run(process.Start);
        if (!startTask.Wait(milliseconds))
        {
            _ = startTask.ContinueWith(
                t =>
                {
                    if (killLateSpawned && t.Result)
                    {
                        try
                        {
#if NET10_0_OR_GREATER
                            process.Kill(entireProcessTree: true);
#else
                            process.Kill();
#endif
                        }
                        catch (Exception) { }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
            return false;
        }
        return startTask.GetAwaiter().GetResult();
    }

    /// <summary><see cref="ProcessStartInfo"/> 形式：超时返回 null。</summary>
    public static Process? Start(ProcessStartInfo startInfo, int milliseconds = SpawnTimeoutMilliseconds,
        bool killLateSpawned = true)
    {
        var process = new Process { StartInfo = startInfo };
        return Start(process, milliseconds, killLateSpawned) ? process : null;
    }

    public static bool WaitForExit(Process process, int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (!process.HasExited)
        {
            if (milliseconds >= 0 && elapsed.ElapsedMilliseconds >= milliseconds)
            {
                return false;
            }
            var poll = elapsed.ElapsedMilliseconds < 2_000 ? 5 : 100;
            Thread.Sleep(milliseconds < 0
                ? poll
                : (int)Math.Max(1, Math.Min(poll, milliseconds - elapsed.ElapsedMilliseconds)));
        }
        return true;
    }
}
