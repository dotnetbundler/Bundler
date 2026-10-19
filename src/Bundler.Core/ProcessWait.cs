using System.Diagnostics;

// 引导件/客户端/Archive 同源链接编译——命名空间随宿主切换，与 UnixFileTypes 同规。
#if BUNDLER_UPDATER_CLIENT
namespace DotNet.Bundler.Updater;
#elif BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Bootstrap;
#else
namespace DotNet.Bundler.Core;
#endif

/// <summary>
/// 有界进程启动/退出等待。macOS CI 实证过 System.Diagnostics.Process 的 Unix
/// spawn/reap 全局互斥死锁：<c>ForkAndExecProcess</c> 持 s_processStartLock 读锁
/// 同步等 fork 子进程回报 exec 结果，子进程卡在 exec 前空转时读锁永不释放——
/// SIGCHLD reaper（CheckChildren）排写锁被饿死、僵尸不 reap，写者排队后新读者
/// 全线阻塞，整进程 Process 调用面死锁（dotnet/runtime #135294 同族失效）。
/// 等待面以 <see cref="Process.HasExited"/> 轮询保底（探的是 reaper 落地旗标，
/// 旗标不置位时界照样生效），启动面以 Task+超时兜底（锁死时 Start 不返回，
/// 界内失败并按调用面扑杀或放生迟到子件）。
/// </summary>
internal static class ProcessWait
{
    /// <summary>spawn 本身的界：fork+exec 正常亚秒级，60s 未返回即按锁死面失败。</summary>
    private const int SpawnTimeoutMilliseconds = 60_000;

    /// <summary>
    /// 有界 <see cref="Process.Start()"/>。Unix 上 Start 内部持全局 spawn 锁并同步
    /// 等子进程 exec 回报——该路径死锁时调用线程永远卡在 Start 内（超时语义与
    /// "未能产出子进程"等价，返回 false）。界内未返回的 Start 仍在后台跑：
    /// <paramref name="killLateSpawned"/> 为 true（同步工具调用——迟到子件是废物）
    /// 时迟到完成的 Start 产出子进程立即扑杀；分离 spawn（子件本就该存活——
    /// nohup/open 重启腿）传 false，迟到子件按正常落地处理。
    /// 注意：调用方对 <paramref name="process"/> 的超时腿不得提前 Dispose——迟到
    /// 扑杀经该对象走，已释放对象的 Kill 会抛 ObjectDisposedException 而漏杀；
    /// 需要事件订阅等成员的场景若必须早释放，改用 <see cref="ProcessStartInfo"/>
    /// 重载（内部自建 Process 不归调用方处置）。
    /// </summary>
    public static bool Start(Process process, int milliseconds = SpawnTimeoutMilliseconds,
        bool killLateSpawned = true) =>
        StartCore(process, milliseconds, killLateSpawned, disposeOnLateComplete: false);

    /// <summary>
    /// <see cref="Start(Process, int, bool)"/> 的 <see cref="ProcessStartInfo"/> 形式，
    /// 替代静态 <see cref="Process.Start(ProcessStartInfo)"/>：超时返回 null（与原
    /// API "未能产出子进程"的 null 语义同位）。内部持有的 Process 不交给调用方，
    /// 迟到完成处置干净（杀/放后释放对象）。调用方对返回值的使用与处置照旧。
    /// </summary>
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
            // 界内 fault：Start 已抛=无子件产出，内部 Process 就地释放（迟到腿才归续体）。
            process.Dispose();
            throw;
        }
    }

    // 内嵌 Start 走独立线程而非线程池：锁死面下每次卡死 spawn 永久占用工作线程，
    // LongRunning 专线把病损隔离出池，不饿死同宿主其余任务。
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
                        // 触摸 Exception 记为已观察——spawn 迟到失败的异常不外溢成
                        // UnobservedTaskException 噪声。
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

    /// <summary>
    /// 有界等待进程退出。<paramref name="milliseconds"/> 为负表示不限时（与
    /// WaitForExit(-1) 同义，仅用于已登记"无现实挂死入口"的调用面）。返回值语义
    /// 与 <see cref="Process.WaitForExit(int)"/> 一致：true=已退出。
    /// 注意：HasExited 对子进程只读 reaper 写的退出旗标（源码核实——
    /// CheckForNonChildExit 对 _isChild 是空操作）；本等待的兜底是轮询界本身，
    /// reaper 卡死导致旗标永不置位时按期 false，调用方走杀+排水失败路。
    /// </summary>
    public static bool WaitForExit(Process process, int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (!process.HasExited)
        {
            if (milliseconds >= 0 && elapsed.ElapsedMilliseconds >= milliseconds)
            {
                return false;
            }
            // 快出子进程首趟即中、零额外延迟；长跑后放宽到 100ms 一档降 syscall 频度。
            var poll = elapsed.ElapsedMilliseconds < 2_000 ? 5 : 100;
            var slice = milliseconds < 0
                ? poll
                : (int)Math.Max(1, Math.Min(poll, milliseconds - elapsed.ElapsedMilliseconds));
            Thread.Sleep(slice);
        }
        return true;
    }
}
