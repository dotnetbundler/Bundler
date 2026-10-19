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
/// 有界进程退出等待，替代 <see cref="Process.WaitForExit(int)"/>。WaitForExit 的兑现
/// 依赖运行时 wait-state 事件链——macOS CI 实证过子进程早已 defunct（Z 态 3 小时，
/// 未被 reap）而该调用永不返回、超时界形同虚设的失效面。本实现改用
/// <see cref="Process.HasExited"/> 逐趟轮询：每趟直接命中内核退出态（POSIX waitpid
/// WNOHANG / Windows WaitForSingleObject 探针），事件链丢唤醒与本等待完全解耦；
/// 探到僵尸即回收，退出码随后可由 <see cref="Process.ExitCode"/> 正常读取。
/// </summary>
internal static class ProcessWait
{
    /// <summary>
    /// 有界等待进程退出。<paramref name="milliseconds"/> 为负表示不限时（与
    /// WaitForExit(-1) 同义，仅用于已登记"无现实挂死入口"的调用面）。返回值语义
    /// 与 <see cref="Process.WaitForExit(int)"/> 一致：true=已退出。
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
