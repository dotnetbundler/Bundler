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
    /// false——子件本就该存活）。调用方对超时腿的 <paramref name="process"/> 不得
    /// 提前 Dispose——迟到扑杀经该对象走，已释放对象的 Kill 会漏杀。
    /// </summary>
    public static bool Start(Process process, int milliseconds = SpawnTimeoutMilliseconds,
        bool killLateSpawned = true) =>
        StartCore(process, milliseconds, killLateSpawned, disposeOnLateComplete: false);

    /// <summary><see cref="ProcessStartInfo"/> 形式：超时返回 null。</summary>
    public static Process? Start(ProcessStartInfo startInfo, int milliseconds = SpawnTimeoutMilliseconds,
        bool killLateSpawned = true)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            return StartCore(process, milliseconds, killLateSpawned, disposeOnLateComplete: true)
                ? process
                : null;
        }
        catch
        {
            // 界内 fault：Start 已抛=无子件产出，内部 Process 就地释放（迟到腿归续体）。
            process.Dispose();
            throw;
        }
    }

    private static bool StartCore(Process process, int milliseconds,
        bool killLateSpawned, bool disposeOnLateComplete)
    {
        var startTask = Task.Factory.StartNew(
            process.Start,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        if (startTask.Wait(milliseconds))
        {
            return startTask.GetAwaiter().GetResult();
        }
        _ = startTask.ContinueWith(
            t =>
            {
                try
                {
                    if (t.IsFaulted)
                    {
                        _ = t.Exception;
                        return;
                    }
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
                }
                finally
                {
                    if (disposeOnLateComplete)
                    {
                        try { process.Dispose(); } catch (Exception) { }
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return false;
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
