using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// 引导件/客户端/Archive/测试面同源链接编译——命名空间随宿主切换。
#if BUNDLER_UPDATER_CLIENT
namespace DotNet.Bundler.Updater;
#elif BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Bootstrap;
#elif BUNDLER_SIGNING_LINK
namespace DotNet.Bundler.Signing.Windows;
#else
namespace DotNet.Bundler.Core;
#endif

/// <summary>
/// Unix 自有 spawn——不经 System.Diagnostics.Process。
/// macOS CI 实证 .NET 运行时 spawn/reap 全局互斥死锁（fork 子件卡 exec 前→
/// s_processStartLock 读锁不释放→reaper 与新 spawn 全饿死，进程级永久失效，
/// dotnet/runtime #135294 同族）。绕开路线演进：先在仓内做托管 fork+exec shim
/// 也被实证否决——fork 子件里跑托管 IL 继承半死运行时态（JIT/GC 锁被挂起
/// 线程持有），WSL 实测子件 SIGSEGV；BCL/Rust/Python 的共同答案是把 shim 沉
/// 到原生侧，本仓零原生工具链条件下的对应件是 <b>libc posix_spawn</b>：
/// dup2/chdir/setpgid/信号复位全部以 file_actions+attr 交给 libc 原生执行，
/// 托管代码只出现在父件。
/// 残余如实登记：posix_spawn 内部仍是 fork+shim——若命中最深层机况（内核级
/// pre-exec 卡死），该次调用会堵在 libc 回报管上。差异在性质不在有无：它
/// 无任何共享锁，单点卡死不传染进程其余 spawn；且调用放专线程 ~1min 级有界
///（60s 主界 + 落账留口与扑孤收尾），
/// Linux 超时经 /proc/self/task/&lt;tid&gt;/children 精确扑杀该线程的孤件
/// （macOS 无对应廉价枚举面，按弃件登记——调用方拿到的是失败而非挂死）。
/// </summary>
internal static class NativeSpawnUnix
{
    internal const int SpawnTimeoutMilliseconds = 60_000;

    // libc：Linux=glibc/musl（libc.so.6 或静态件）、macOS=libSystem——"libc"
    // 由运行时在两类平台各自解析，NativeAOT 静态链同符号。
    private const string LibC = "libc";

    [DllImport(LibC, SetLastError = true)] private static extern int pipe([Out] int[] fds);
    [DllImport(LibC, SetLastError = true)] private static extern int fcntl(int fd, int cmd, int arg);
    [DllImport(LibC, SetLastError = true)] private static extern int close(int fd);
    [DllImport(LibC, SetLastError = true)] private static extern int access(byte[] path, int mode);
    [DllImport(LibC, SetLastError = true)] private static extern int kill(int pid, int sig);
    [DllImport(LibC, SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);

    // posix_spawn 面：file_actions/attr 走 opaque 缓冲（尺寸随 libc 变——分配放大
    // 冗余、init 按本机 struct 填），rc 直接是 errno（POSIX：非 0 即错误码，
    // 含子件 exec 失败——glibc/musl/libSystem 三家都经内部管同步回报）。
    [DllImport(LibC)] private static extern int posix_spawn(
        out int pid, byte[] path, IntPtr fileActions, IntPtr attrp, IntPtr argv, IntPtr envp);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_init(IntPtr actions);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_destroy(IntPtr actions);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_adddup2(
        IntPtr actions, int fd, int newfd);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_addclose(
        IntPtr actions, int fd);
    [DllImport(LibC, EntryPoint = "posix_spawn_file_actions_addchdir")]
    private static extern int AddChdirStd(IntPtr actions, byte[] path);
    [DllImport(LibC, EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    private static extern int AddChdirNp(IntPtr actions, byte[] path);
    [DllImport(LibC)] private static extern int posix_spawnattr_init(IntPtr attr);
    [DllImport(LibC)] private static extern int posix_spawnattr_destroy(IntPtr attr);
    [DllImport(LibC)] private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
    [DllImport(LibC)] private static extern int posix_spawnattr_setpgroup(IntPtr attr, int pgroup);
    [DllImport(LibC)] private static extern int posix_spawnattr_setsigmask(IntPtr attr, IntPtr sigmask);
    [DllImport(LibC)] private static extern int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr sigdefault);
    [DllImport(LibC)] private static extern int sigemptyset(IntPtr set);
    [DllImport(LibC)] private static extern int sigfillset(IntPtr set);
    [DllImport(LibC, SetLastError = true)] private static extern int chdir(byte[] path);
    [DllImport(LibC, SetLastError = true)] private static extern IntPtr readlink(byte[] path, byte[] buf, IntPtr bufsiz);

    private static readonly bool s_darwin =
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    private const int X_OK = 1;
    private const int ENOENT = 2;
    private const int EINTR = 4;
    private const int SIGKILL = 9;
    private const int ECHILD = 10;
    private const int WNOHANG = 1;
    // POSIX_SPAWN_SETPGROUP|SETSIGDEF|SETSIGMASK——glibc/musl/libSystem 低位同值。
    private const short SpawnFlags = 0x02 | 0x04 | 0x08;

    /// <summary>spawn 出的 Unix 子件句柄面：pid + 排水 fd + 退出账本。</summary>
    internal sealed class SpawnedChild
    {
        public int Pid;
        public int StdinWriteFd = -1;
        public int StdoutReadFd = -1;
        public int StderrReadFd = -1;
        // 谁先调 waitpid 谁记账——轮询线程/调用方等待/扑杀腿互斥同锁。
        public readonly object ReapLock = new();
        public bool Reaped;
        public int ExitCode;
        // reap 回调（Exited 事件面）：轮询线程在 ReapLock 外派发，防订阅者重入死锁。
        public Action? OnReaped;
    }

    // —— 单 reaper 轮询线程：自家子件的统一收尸面。
    // 不用 waitpid(-1)（会偷 reap 别的子件）；每趟 WNOHANG 扫登记表，
    // ~30ms 粒度覆盖"未等未 Dispose"子件的收尸与 Exited 派发——
    // 这正是 .NET ProcessWaitState reaper 干的事，只是锁域只圈自家 pid。
    private static readonly List<SpawnedChild> s_children = new();
    private static readonly object s_childrenLock = new();
    private static Thread? s_reaper;

    private static void Register(SpawnedChild child)
    {
        lock (s_childrenLock)
        {
            s_children.Add(child);
            s_reaper ??= StartReaper();
        }
    }

    private static Thread StartReaper()
    {
        var thread = new Thread(ReaperLoop) { IsBackground = true, Name = "bundler-nativespawn-reaper" };
        thread.Start();
        return thread;
    }

    private static void ReaperLoop()
    {
        while (true)
        {
            List<SpawnedChild> pending;
            lock (s_childrenLock)
            {
                s_children.RemoveAll(c => c.Reaped);
                if (s_children.Count == 0)
                {
                    s_reaper = null; // 表空即退场，下次 Register 再起。
                    return;
                }
                pending = new List<SpawnedChild>(s_children);
            }
            foreach (var child in pending)
            {
                Action? onReaped = null;
                lock (child.ReapLock)
                {
                    if (!child.Reaped && TryReap(child.Pid, out var code))
                    {
                        child.Reaped = true;
                        child.ExitCode = code;
                        onReaped = child.OnReaped;
                    }
                }
                try { onReaped?.Invoke(); }
                catch { /* 订阅者异常不外溢收尸链 */ }
            }
            Thread.Sleep(30);
        }
    }

    /// <summary>
    /// 启动子进程。exec/子件布置失败抛 <see cref="Win32Exception"/>（errno 直报）；
    /// posix_spawn 调用 <see cref="SpawnTimeoutMilliseconds"/> 未返回（libc shim
    /// 卡死）扑杀该线程的孤件后返回 null——"未能产出子进程"位。
    /// PATH 解析在父件完成（posix_spawn 不做 PATH 检索）。
    /// </summary>
    internal static SpawnedChild? Spawn(ToolProcessStartInfo info)
    {
        // envp：默认=父进程环境 + overrides（null 值=移除）；
        // EnvironmentIsComplete=袋子即完整环境块（父件 env 不参与，null 值按缺席跳过）。
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!info.EnvironmentIsComplete)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string k && entry.Value is string v)
                {
                    env[k] = v;
                }
            }
        }
        foreach (var pair in info.Environment)
        {
            if (pair.Value is null)
            {
                env.Remove(pair.Key);
            }
            else
            {
                env[pair.Key] = pair.Value;
            }
        }

        var executable = ResolveOnPath(info.FileName, env, info.WorkingDirectory)
            ?? throw new Win32Exception(ENOENT, $"Tool not found on PATH: '{info.FileName}'.");
        var exeBytes = Utf8(executable);
        var cwdBytes = info.WorkingDirectory is { Length: > 0 } cwd ? Utf8(cwd) : null;

        var arguments = info.ArgumentLine is not null
            ? ToolProcess.SplitCommandLine(info.ArgumentLine)
            : info.Arguments;
        var argvList = new List<string>(arguments.Count + 1) { info.FileName };
        argvList.AddRange(arguments);
        var argv = MarshalArgv(argvList);
        var envp = MarshalArgv(env.Select(p => $"{p.Key}={p.Value}"));
        var callInFlight = false;
        try
        {
            return SpawnCore(info, exeBytes, cwdBytes, argv, envp, out callInFlight);
        }
        finally
        {
            // posix_spawn 返回即可释放（execve 已消费）；超时弃件路径下调用仍
            // 可能在栈内读这两个指针——该腿按弃件口径随缓冲一并泄漏，不早释。
            if (!callInFlight)
            {
                FreeArgv(argv);
                FreeArgv(envp);
            }
        }
    }

    private static SpawnedChild? SpawnCore(ToolProcessStartInfo info, byte[] exeBytes,
        byte[]? cwdBytes, IntPtr argv, IntPtr envp, out bool callInFlight)
    {
        int[] stdinPipe = [-1, -1], stdoutPipe = [-1, -1], stderrPipe = [-1, -1];
        try
        {
            if (info.RedirectStandardInput && !CreatePipe(stdinPipe))
            {
                throw PipeFailure();
            }
            if (info.RedirectStandardOutput && !CreatePipe(stdoutPipe))
            {
                throw PipeFailure();
            }
            if (info.RedirectStandardError && !CreatePipe(stderrPipe))
            {
                throw PipeFailure();
            }

            var spawn = SpawnOnWorkerThread(exeBytes, cwdBytes, argv, envp,
                stdinPipe[0], stdoutPipe[1], stderrPipe[1],
                stdinPipe[1], stdoutPipe[0], stderrPipe[0]);
            callInFlight = spawn.TimedOut; // 仅在超腿时调用可能还在栈内

            // 父件：无论成败先关子件端。
            CloseIf(stdinPipe[0]); CloseIf(stdoutPipe[1]); CloseIf(stderrPipe[1]);
            if (spawn.Failure is not null)
            {
                CloseAll(stdinPipe); CloseAll(stdoutPipe); CloseAll(stderrPipe);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(spawn.Failure).Throw();
            }
            if (spawn.Error != 0)
            {
                CloseAll(stdinPipe); CloseAll(stdoutPipe); CloseAll(stderrPipe);
                throw new Win32Exception(spawn.Error);
            }
            if (!spawn.HasPid)
            {
                // 超时弃件：孤件已尽力扑杀（Linux 精确、macOS 登记）——返回"未产出"。
                CloseAll(stdinPipe); CloseAll(stdoutPipe); CloseAll(stderrPipe);
                return null;
            }
            var child = new SpawnedChild
            {
                Pid = spawn.Pid,
                StdinWriteFd = stdinPipe[1],
                StdoutReadFd = stdoutPipe[0],
                StderrReadFd = stderrPipe[0],
            };
            Register(child);
            return child;
        }
        catch
        {
            CloseAll(stdinPipe); CloseAll(stdoutPipe); CloseAll(stderrPipe);
            throw;
        }
    }

    /// <summary>
    /// posix_spawn 放专线程：调用本身可能堵在 libc 子件回报管（shim 卡死形态），
    /// 父件 60s 主界等待，超时腿加 2s worker 落账留口与扑孤收尾（上界 ~62s+）
    /// 后报"未产出"。无共享锁——单点卡死不传染。
    /// </summary>
    private sealed class SpawnResult
    {
        public int Pid = -1;
        public int Error; // posix_spawn 返回值=errno；0=成功
        public bool TimedOut;
        public int WorkerTid = -1; // Linux 孤件清扫用
        public Exception? Failure; // worker 内异常——父件重抛，不可落"假成功"位
        // Pid>0 硬校验：Error==0 配 Pid=-1 的漏账路径一旦出现即落本位（"未产出"），
        // 不放行成 waitpid(-1)（收任意子件）/kill(-(-1))（广播杀）灾难面。
        public bool HasPid => !TimedOut && Failure is null && Error == 0 && Pid > 0;
    }

    private static SpawnResult SpawnOnWorkerThread(byte[] exeBytes, byte[]? cwdBytes,
        IntPtr argv, IntPtr envp,
        int stdinR, int stdoutW, int stderrW,
        int stdinParentFd, int stdoutParentFd, int stderrParentFd)
    {
        var result = new SpawnResult { Pid = -1 };
        var done = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            // readlink("/proc/self/task/self") 在自身线程语境解析出内核 tid——
            // 孤件清扫按 /proc/self/task/<tid>/children 精确圈定（fork 源线程即
            // 是 posix_spawn 内部 clone 的发起者）。macOS 无 /proc——按弃件处理。
            result.WorkerTid = LinuxSelfTid();
            var actions = Marshal.AllocHGlobal(256);
            var attr = Marshal.AllocHGlobal(512);
            var sigEmpty = Marshal.AllocHGlobal(128);
            var sigFull = Marshal.AllocHGlobal(128);
            try
            {
                FillZero(actions, 256); FillZero(attr, 512);
                FillZero(sigEmpty, 128); FillZero(sigFull, 128);
                var rc = SetupSpawnState(actions, attr, sigEmpty, sigFull,
                    stdinR, stdoutW, stderrW, stdinParentFd, stdoutParentFd, stderrParentFd);
                if (rc == 0)
                {
                    if (cwdBytes is not null && !ChdirViaActions(actions, cwdBytes))
                    {
                        // addchdir 缺席（glibc<2.29/musl<1.1.24/老 libSystem/静态件
                        // 符号不可解析）时的兜底：cwd 是进程级资源，锁内临时改父件
                        // cwd 包过 spawn 是 POSIX 下唯一等价形态——窗口仅一条
                        // P/Invoke，其余线程相对路径解析竞态按已知残余登记；
                        // posix_spawn 在锁内卡死时后续带 cwd 的 spawn 排队堵锁
                        //（各自 ~1min 级有界——是退化不是传染，cwd 本就无并行安全语义）。
                        lock (s_workDirLock)
                        {
                            var old = GetCwd();
                            if (old is null)
                            {
                                // getcwd 失败时 GetLastWin32Error 是无效槽位值
                                //（新线程常为 0）——显式记 EIO，0 会落假成功。
                                result.Error = EIO;
                                return;
                            }
                            if (chdir(cwdBytes) != 0)
                            {
                                result.Error = Marshal.GetLastWin32Error();
                                return;
                            }
                            var spawned = -1;
                            try
                            {
                                result.Error = posix_spawn(out spawned, exeBytes, actions, attr, argv, envp);
                            }
                            finally
                            {
                                _ = chdir(old);
                            }
                            result.Pid = result.Error == 0 ? spawned : -1;
                        }
                    }
                    else
                    {
                        result.Error = posix_spawn(out var spawned, exeBytes, actions, attr, argv, envp);
                        result.Pid = result.Error == 0 ? spawned : -1;
                    }
                }
                else
                {
                    result.Error = rc;
                }
            }
            catch (Exception exception)
            {
                // 布置面异常（EntryPointNotFound/OOM 等）记账回传——不放任成
                // 未处理线程异常崩进程，也不允许落到"假成功"位。
                result.Failure = exception;
            }
            finally
            {
                // 弃件腿自查：父件已超时放行而本件迟到产出的子件，就地扑杀+收尸——
                // 未登记 reaper 的件不能滞留成僵尸。macOS 无 /proc 清扫靠此腿；
                // Linux 与 ScavengeStrayChildren 双向覆盖（竞态下双重 kill/waitpid
                // 对 ESRCH/ECHILD 静默无害）。
                if (Volatile.Read(ref result.TimedOut) && result.Pid > 0)
                {
                    KillTree(result.Pid);
                    ReapSync(result.Pid);
                }
                // 正常返回才释放——超时弃件路径下调用仍在栈内，释放=use-after-free，
                // 这些缓冲随弃件一并泄漏（病理态每进程至多 ~1KB，如实登记）。
                posix_spawn_file_actions_destroy(actions);
                posix_spawnattr_destroy(attr);
                Marshal.FreeHGlobal(actions); Marshal.FreeHGlobal(attr);
                Marshal.FreeHGlobal(sigEmpty); Marshal.FreeHGlobal(sigFull);
                done.Set();
            }
        })
        { IsBackground = true, Name = "bundler-posix-spawn" };
        worker.Start();
        if (!done.Wait(SpawnTimeoutMilliseconds))
        {
            // 先置位再清扫：worker finally 的弃件自查读同一旗标——次序颠倒会
            // 留出"worker 读到 false 而过置位窗"的缝，迟到产出件就没人收尸。
            Volatile.Write(ref result.TimedOut, true);
            ScavengeStrayChildren(result.WorkerTid);
            // 残余窗收口：worker 读 false 后退出、子件 reparent 后 /proc 清单与
            // worker 自查双漏——清扫顺带解开卡死的 spawn 返回，短留口让 finally
            // 落账 Pid，再按记账 pid 补杀补收一次（双杀/双收对 ESRCH/ECHILD 静默）。
            _ = done.Wait(TimeSpan.FromSeconds(2));
            if (Volatile.Read(ref result.Pid) > 0)
            {
                KillTree(result.Pid);
                ReapSync(result.Pid);
            }
        }
        else
        {
            // 置位即 worker 已越过 finally——Dispose 安全；弃件腿刻意不 Dispose
            //（worker 仍持引用，后台续跑不受影响，MRES 无内核件泄漏）。
            done.Dispose();
        }
        return result;
    }

    private static int SetupSpawnState(IntPtr actions, IntPtr attr, IntPtr sigEmpty,
        IntPtr sigFull,
        int stdinR, int stdoutW, int stderrW,
        int stdinParentFd, int stdoutParentFd, int stderrParentFd)
    {
        static bool ClosesDupTarget(int fd, int stdinR, int stdoutW, int stderrW) => fd switch
        {
            0 => stdinR >= 0,   // stdin 重定向开启→槽 0 是 dup2 目标的子件管
            1 => stdoutW >= 0,
            2 => stderrW >= 0,
            _ => false,
        };
        var rc = posix_spawn_file_actions_init(actions);
        if (rc != 0)
        {
            return rc;
        }
        // dup2 目标 0/1/2 不继承 CLOEXEC——exec 后子件三管就位；源端 addclose
        // （源端本就 CLOEXEC，显式加是语义自证）+父件端 addclose 防子件持有。
        if (stdinR >= 0 && stdinR != 0)
        {
            if ((rc = posix_spawn_file_actions_adddup2(actions, stdinR, 0)) != 0) { return rc; }
            if ((rc = posix_spawn_file_actions_addclose(actions, stdinR)) != 0) { return rc; }
        }
        if (stdoutW >= 0 && stdoutW != 1)
        {
            if ((rc = posix_spawn_file_actions_adddup2(actions, stdoutW, 1)) != 0) { return rc; }
            if ((rc = posix_spawn_file_actions_addclose(actions, stdoutW)) != 0) { return rc; }
        }
        if (stderrW >= 0 && stderrW != 2)
        {
            if ((rc = posix_spawn_file_actions_adddup2(actions, stderrW, 2)) != 0) { return rc; }
            if ((rc = posix_spawn_file_actions_addclose(actions, stderrW)) != 0) { return rc; }
        }
        // 父件端 addclose：fd 值落在 0-2 且该槽恰是前序 dup2 目标时跳过——
        // 父件 std 全关场景管道端可占入 0/1/2，此时该数字指向刚装上的排水口
        //（dup2 已把原槽位覆写——无父件端可关），误关即断该标准管。
        if (stdinParentFd >= 0 && !ClosesDupTarget(stdinParentFd, stdinR, stdoutW, stderrW)
            && (rc = posix_spawn_file_actions_addclose(actions, stdinParentFd)) != 0) { return rc; }
        if (stdoutParentFd >= 0 && !ClosesDupTarget(stdoutParentFd, stdinR, stdoutW, stderrW)
            && (rc = posix_spawn_file_actions_addclose(actions, stdoutParentFd)) != 0) { return rc; }
        if (stderrParentFd >= 0 && !ClosesDupTarget(stderrParentFd, stdinR, stdoutW, stderrW)
            && (rc = posix_spawn_file_actions_addclose(actions, stderrParentFd)) != 0) { return rc; }
        if ((rc = posix_spawnattr_init(attr)) != 0)
        {
            return rc;
        }
        // 自立进程组→kill(-pid) 树杀；信号面复位——不继承 .NET 装的处理器
        // （SIGPIPE 忽略态漏进子件会改写工具语义）与父件屏蔽掩码。
        if ((rc = posix_spawnattr_setpgroup(attr, 0)) != 0) { return rc; }
        if ((rc = sigemptyset(sigEmpty)) != 0) { return rc; }
        if ((rc = posix_spawnattr_setsigmask(attr, sigEmpty)) != 0) { return rc; }
        if ((rc = sigfillset(sigFull)) != 0) { return rc; }
        if ((rc = posix_spawnattr_setsigdefault(attr, sigFull)) != 0) { return rc; }
        return posix_spawnattr_setflags(attr, SpawnFlags);
    }

    // addchdir 双名探测：标准名（glibc 2.29+/musl 1.1.24+/新版 libSystem）
    // 或 _np 扩展名（旧 glibc/macOS）；都无则调用方走锁内父件 chdir 兜底。
    private static int AddChdir(IntPtr actions, byte[] path)
    {
        try
        {
            return AddChdirStd(actions, path);
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        try
        {
            return AddChdirNp(actions, path);
        }
        catch (EntryPointNotFoundException)
        {
            return ENOSYS;
        }
        catch (DllNotFoundException)
        {
            return ENOSYS;
        }
    }

    private static bool ChdirViaActions(IntPtr actions, byte[] path)
    {
        var rc = AddChdir(actions, path);
        if (rc == ENOSYS)
        {
            return false;
        }
        if (rc != 0)
        {
            throw new Win32Exception(rc);
        }
        return true;
    }

    private const int ENOSYS = 38;
    private const int EIO = 5;
    private static readonly object s_workDirLock = new();

    private static byte[]? GetCwd()
    {
        var buf = new byte[4096];
        return getcwd(buf, (IntPtr)buf.Length) != IntPtr.Zero ? buf : null;
    }

    [DllImport(LibC)] private static extern IntPtr getcwd(byte[] buf, IntPtr size);

    // Linux：/proc/self/task/self 经 readlink 解出本线程内核 tid；非 Linux 返回 -1。
    private static int LinuxSelfTid()
    {
        if (s_darwin || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return -1;
        }
        try
        {
            var buf = new byte[32];
            var n = (long)readlink(Utf8("/proc/self/task/self"), buf, (IntPtr)buf.Length);
            if (n > 0 && int.TryParse(Encoding.UTF8.GetString(buf, 0, (int)n), out var tid))
            {
                return tid;
            }
        }
        catch { /* /proc 缺席按弃件口径 */ }
        return -1;
    }

    /// <summary>
    /// posix_spawn 超时后的孤件扑杀：读 /proc/self/task/&lt;tid&gt;/children
    /// （该线程 fork 出的子件精确清单）逐件组杀+直系杀+回收。tid=-1（平台无
    /// /proc）时登记弃件——子件可能仍在，进程其余面不受影响。
    /// </summary>
    private static void ScavengeStrayChildren(int tid)
    {
        if (tid <= 0)
        {
            return;
        }
        try
        {
            var text = File.ReadAllText($"/proc/self/task/{tid}/children").Trim();
            foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, out var stray) || stray <= 0)
                {
                    continue;
                }
                _ = kill(-stray, SIGKILL); // 组杀（SETPGROUP 若已生效覆盖其树）
                _ = kill(stray, SIGKILL);  // 直系兜底
                ReapSync(stray);
            }
        }
        catch { /* /proc 缺读权限/格式异变——按弃件口径，不升级 */ }
    }

    private static void FillZero(IntPtr buffer, int size)
    {
        for (var i = 0; i < size; i++)
        {
            Marshal.WriteByte(buffer, i, 0);
        }
    }

    private static void ReapSync(int pid)
    {
        // 已死子件的同步回收：先 WNOHANG 快轮，未出再阻塞兜底——杀死后通常立刻可 reap。
        for (var i = 0; i < 200; i++)
        {
            var rc = waitpid(pid, out _, WNOHANG);
            if (rc == pid || (rc < 0 && Marshal.GetLastWin32Error() == ECHILD))
            {
                return;
            }
            Thread.Sleep(10);
        }
        _ = waitpid(pid, out _, 0);
    }

    /// <summary>非阻塞 reap：true=已退出（<paramref name="exitCode"/> 已记账）。</summary>
    internal static bool TryReap(int pid, out int exitCode) => Reap(pid, WNOHANG, out exitCode);

    /// <summary>阻塞 reap：Exited 事件 waiter 与 Dispose 收尾用。</summary>
    internal static bool ReapBlocking(int pid, out int exitCode) => Reap(pid, 0, out exitCode);

    private static bool Reap(int pid, int options, out int exitCode)
    {
        exitCode = 0;
        while (true)
        {
            var rc = waitpid(pid, out var status, options);
            if (rc == pid)
            {
                exitCode = DecodeStatus(status);
                return true;
            }
            if (rc == 0)
            {
                return false;
            }
            var err = Marshal.GetLastWin32Error();
            if (err == EINTR)
            {
                continue;
            }
            if (err == ECHILD)
            {
                // 已被回收（竞态腿）——按已退出记账，码不可考记 -1。
                exitCode = -1;
                return true;
            }
            // 其余错误（EINVAL 类）皆为永久性——返 false 会让登记项在 reaper 里
            // 永久滞留空转；按"已收不可考"收口，码记 -1。
            exitCode = -1;
            return true;
        }
    }

    private static int DecodeStatus(int status)
    {
        if ((status & 0x7f) == 0)
        {
            return (status >> 8) & 0xff;   // WIFEXITED→WEXITSTATUS
        }
        var sig = status & 0x7f;
        return sig != 0x7f ? 128 + sig : -1; // WIFSIGNALED→128+sig（shell 约定）；stopped 不追尸
    }

    /// <summary>树杀：先 kill 进程组（子件 SETPGROUP 自立）再补杀直系。</summary>
    internal static void KillTree(int pid)
    {
        _ = kill(-pid, SIGKILL); // 组杀：pgid==子件 pid
        _ = kill(pid, SIGKILL);  // 直系兜底：setpgroup 未及执行/组外情形
    }

    internal static void KillOnly(int pid) => _ = kill(pid, SIGKILL);

    /// <summary>
    /// PATH 解析在父件：含 / 直用（不存在也放行——execve 报准确 errno），否则按生效
    /// envp 的 PATH 逐项 access(X_OK)。相对 PATH 项（含空项="."）的解析基准是
    /// 子件的执行 cwd（exec 在 chdir 后跑），不是父件 cwd。
    /// </summary>
    private static string? ResolveOnPath(string fileName, Dictionary<string, string> env,
        string? workingDirectory)
    {
        if (fileName.Contains('/'))
        {
            return fileName;
        }
        var path = env.TryGetValue("PATH", out var p) ? p : "/usr/bin:/bin";
        // 基准归一成绝对路径：拼出的候选要同时满足父件 access 检查与子件
        // chdir 后的 exec 解析——绝对路径两语境同指一目标，无二次错解。
        // WorkingDirectory 空串与 null 同为"不 chdir"（BCL 同位语义），
        // 此时子件 cwd=父件 cwd，基准即 "."。
        var relativeBase = Path.GetFullPath(
            string.IsNullOrEmpty(workingDirectory) ? "." : workingDirectory);
        foreach (var dir in path.Split(':'))
        {
            var candidate = dir.Length > 0 && dir[0] == '/'
                ? string.Concat(dir, "/", fileName)
                : string.Concat(relativeBase, "/", dir.Length == 0 ? "." : dir, "/", fileName);
            if (access(Utf8(candidate), X_OK) == 0)
            {
                return candidate;
            }
        }
        return null;
    }

    // pipe2(O_CLOEXEC) 原子建管——消灭 pipe→fcntl 之间"并发 spawn 可能继承
    // 未收口 fd"的竞态窗（排水管写端漏进无关子件→ReadToEnd EOF 永不至的
    // 实测病理）。macOS/旧内核无 pipe2 落 pipe+fcntl，微窗按残余登记。
    private const int O_CLOEXEC = 0x80000;
    private static int s_pipe2Absent; // 0=未探 1=缺席——探测失败一次即记住，不再每 spawn 抛探测异常
    private static bool CreatePipe(int[] fds)
    {
        if (Volatile.Read(ref s_pipe2Absent) == 0)
        {
            try
            {
                if (Pipe2(fds, O_CLOEXEC) == 0)
                {
                    return true;
                }
                if (Marshal.GetLastWin32Error() != ENOSYS)
                {
                    return false; // 真实失败（EMFILE 等）——errno 透传给 PipeFailure
                }
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            Volatile.Write(ref s_pipe2Absent, 1);
        }
        // ENOSYS/无该符号 → pipe+fcntl 兜底（macOS 微窗按残余登记）
        return pipe(fds) == 0 && CloExec(fds);
    }

    [DllImport(LibC, SetLastError = true, EntryPoint = "pipe2")]
    private static extern int Pipe2([Out] int[] fds, int flags);

    private static bool CloExec(int[] fds) =>
        // 两端都 CLOEXEC：孙代一律不继承宿主 fd——并发工具排水管的写端若被无关
        // 子件持有，ReadToEnd 的 EOF 永不至（实测病理）。
        fcntl(fds[0], F_SETFD, FD_CLOEXEC) == 0 && fcntl(fds[1], F_SETFD, FD_CLOEXEC) == 0;

    private static Win32Exception PipeFailure() => new(Marshal.GetLastWin32Error(), "pipe/fcntl failed.");

    private static void CloseIf(int fd)
    {
        if (fd >= 0)
        {
            _ = close(fd);
        }
    }

    private static void CloseAll(int[] fds)
    {
        CloseIf(fds[0]);
        CloseIf(fds[1]);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + '\0');

    // argv/envp marshal：NULL 结尾 char* 数组，UTF-8。
    private static IntPtr MarshalArgv(IEnumerable<string> values)
    {
        var ptrs = new List<IntPtr>();
        var arr = IntPtr.Zero;
        try
        {
            foreach (var s in values)
            {
                var bytes = Encoding.UTF8.GetBytes((s ?? "") + '\0');
                var p = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                ptrs.Add(p);
            }
            ptrs.Add(IntPtr.Zero);
            arr = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Count);
            Marshal.Copy(ptrs.ToArray(), 0, arr, ptrs.Count);
            var result = arr;
            arr = IntPtr.Zero;
            return result;
        }
        catch
        {
            // 中途失败（OOM 等）回收已分配段——HG 不受 GC 管，放任即成堆泄漏。
            if (arr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(arr);
            }
            foreach (var p in ptrs)
            {
                if (p != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(p);
                }
            }
            throw;
        }
    }

    private static void FreeArgv(IntPtr arr)
    {
        if (arr == IntPtr.Zero)
        {
            return;
        }
        for (var i = 0; ; i++)
        {
            var p = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
            if (p == IntPtr.Zero)
            {
                break;
            }
            Marshal.FreeHGlobal(p);
        }
        Marshal.FreeHGlobal(arr);
    }
}
