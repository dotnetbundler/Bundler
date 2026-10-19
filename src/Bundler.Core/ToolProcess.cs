using System.ComponentModel;
using System.Diagnostics;
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
/// <see cref="ToolProcess"/> 的启动参数。argv 以列表承载——不经 shell 转义层；
/// <see cref="ArgumentLine"/> 供确有整串命令行的调用面（Windows 直通原串，
/// Unix 按 .NET PasteArguments 语义拆列表）。
/// </summary>
public sealed class ToolProcessStartInfo
{
    public string FileName { get; set; } = "";
    /// <summary>argv 列表（不经 shell 转义）；<see cref="ArgumentLine"/> 设置时优先。</summary>
    public List<string> Arguments { get; } = new();
    /// <summary>整串命令行：Windows 直通原串；Unix 按 .NET PasteArguments 语义拆列表。</summary>
    public string? ArgumentLine { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool RedirectStandardInput { get; set; }
    public bool RedirectStandardOutput { get; set; }
    public bool RedirectStandardError { get; set; }
    /// <summary>
    /// Windows ShellExecute 语义（文件关联拉起——重启/打开面）；Unix 无关联概念，
    /// 与 .NET 同规按 PATH 解析直接 exec，等价寻常 spawn。
    /// </summary>
    public bool UseShellExecute { get; set; }
    /// <summary>环境覆盖：value=null 表示从继承环境里移除该变量。</summary>
    public Dictionary<string, string?> Environment { get; } = new();
}

/// <summary>
/// 跨平台外部进程句柄：Unix 走 <see cref="NativeSpawnUnix"/>（libc posix_spawn——
/// 绕开 System.Diagnostics.Process 的 spawn/reap 全局锁死锁面，macOS CI 实证
/// dotnet/runtime #135294 同族失效是进程级永久的）；Windows 包装
/// <see cref="Process"/>（CreateProcess 无该失效类）。成员面与 Process 对齐：
/// Id/ExitCode/HasExited/Standard*/Kill/Dispose/Exited。
/// </summary>
public sealed class ToolProcess : IDisposable
{
    private readonly Process? _win;
    private readonly NativeSpawnUnix.SpawnedChild? _unix;
    private readonly StreamReader? _stdout;
    private readonly StreamReader? _stderr;
    private readonly StreamWriter? _stdin;
    private EventHandler? _exited;
    private bool _disposed;

    private ToolProcess(Process win)
    {
        _win = win;
        _stdout = win.StartInfo.RedirectStandardOutput ? win.StandardOutput : null;
        _stderr = win.StartInfo.RedirectStandardError ? win.StandardError : null;
        _stdin = win.StartInfo.RedirectStandardInput ? win.StandardInput : null;
    }

    private ToolProcess(NativeSpawnUnix.SpawnedChild unix)
    {
        _unix = unix;
        _stdout = unix.StdoutReadFd >= 0 ? Reader(unix.StdoutReadFd) : null;
        _stderr = unix.StderrReadFd >= 0 ? Reader(unix.StderrReadFd) : null;
        _stdin = unix.StdinWriteFd >= 0 ? Writer(unix.StdinWriteFd) : null;
    }

    private static StreamReader Reader(int fd) =>
        new(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(fd), ownsHandle: true),
            FileAccess.Read, bufferSize: 4096), Encoding.UTF8);

    private static StreamWriter Writer(int fd) =>
        new(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(fd), ownsHandle: true),
            FileAccess.Write, bufferSize: 4096), new UTF8Encoding(false)) { AutoFlush = true };

    /// <summary>
    /// 启动子进程。exec 失败抛 <see cref="Win32Exception"/>（与 Process.Start 同语义）；
    /// spawn 超时（Unix shim 卡死 ~1min 级上界仍无 exec 回报）返回 null——"未能产出子进程"位。
    /// </summary>
    public static ToolProcess? Start(ToolProcessStartInfo info)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var win = StartWindows(info);
            return win is null ? null : new ToolProcess(win);
        }
        var child = NativeSpawnUnix.Spawn(info);
        return child is null ? null : new ToolProcess(child);
    }

    private static Process? StartWindows(ToolProcessStartInfo info)
    {
        var psi = new ProcessStartInfo
        {
            FileName = info.FileName,
            WorkingDirectory = info.WorkingDirectory ?? "",
            UseShellExecute = info.UseShellExecute,
            RedirectStandardInput = info.RedirectStandardInput,
            RedirectStandardOutput = info.RedirectStandardOutput,
            RedirectStandardError = info.RedirectStandardError,
            CreateNoWindow = true,
        };
        if (info.ArgumentLine is not null)
        {
            psi.Arguments = info.ArgumentLine;
        }
        else
        {
            psi.Arguments = string.Join(" ", info.Arguments.Select(QuoteWin));
        }
        // ShellExecute 不接受环境块——BCL Process.Start 同位抛
        // InvalidOperationException。本面按 ShellExecute 天然继承父件环境的
        // 语义跳过覆盖（适配层把 ProcessStartInfo.Environment 全集拷来是常态，
        // 显式覆写在 ShellExecute 下本就不可表达）。
        if (!info.UseShellExecute)
        {
            foreach (var pair in info.Environment)
            {
                if (pair.Value is null)
                {
                    psi.EnvironmentVariables.Remove(pair.Key);
                }
                else
                {
                    psi.EnvironmentVariables[pair.Key] = pair.Value;
                }
            }
        }
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        return process.Start() ? process : null;
    }

    /// <summary>Windows 参数引号规则：整词加引，引号内反斜杠与引号前反斜杠翻倍。</summary>
    internal static string QuoteWin(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
            }
            else if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
            }
            else
            {
                result.Append('\\', slashes).Append(character);
                slashes = 0;
            }
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// Unix 命令行拆分（POSIX shell-lite：空白分隔；'…' 与 "…" 成对取字面量，
    /// 引号外 \ 转义下一字符；不展开 $、不跑通配——直喂 execve argv）。
    /// 仅供 NativeSpawnUnix 的 ArgumentLine 腿使用——Windows 面原串直通不拆；
    /// UpdateArgv.Join 产出的 Windows 双引号串在此规则下同往返（"…" 成对）。
    /// </summary>
    internal static List<string> SplitCommandLine(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quote = '\0'; // '\'' 或 '"'——0=引号外
        var hasToken = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length &&
                (quote == '\0' || (quote == '"' && (line[i + 1] == '"' || line[i + 1] == '\\'))))
            {
                // 引号外 \ 转义任意字符；双引号内仅 \" 与 \\（POSIX 与
                // CommandLineToArgvW 引号内反斜杠语义恰好同规——两生产端通吃）。
                current.Append(line[++i]);
                hasToken = true;
                continue;
            }
            if (c == quote || (quote == '\0' && (c == '\'' || c == '"')))
            {
                quote = quote == '\0' ? c : '\0';
                hasToken = true;
                continue;
            }
            if (quote == '\0' && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken)
        {
            args.Add(current.ToString());
        }
        return args;
    }

    public int Id => _win?.Id ?? _unix!.Pid;

    public bool HasExited
    {
        get
        {
            if (_win is not null)
            {
                return _win.HasExited;
            }
            Action? onReaped = null;
            bool exited;
            lock (_unix!.ReapLock)
            {
                if (!_unix.Reaped && ReapUnix(blocking: false))
                {
                    onReaped = _unix.OnReaped;
                }
                exited = _unix.Reaped;
            }
            FireReaped(onReaped);
            return exited;
        }
    }

    public int ExitCode
    {
        get
        {
            if (_win is not null)
            {
                return _win.ExitCode;
            }
            if (!HasExited)
            {
                throw new InvalidOperationException("Process has not exited.");
            }
            return _unix!.ExitCode;
        }
    }

    public StreamReader StandardOutput =>
        _stdout ?? throw new InvalidOperationException("StandardOutput not redirected.");
    public StreamReader StandardError =>
        _stderr ?? throw new InvalidOperationException("StandardError not redirected.");
    public StreamWriter StandardInput =>
        _stdin ?? throw new InvalidOperationException("StandardInput not redirected.");

    /// <summary>
    /// 有界等待退出。Unix 直查 waitpid（内核是事实源——不依赖任何运行时事件链）；
    /// Windows 委托 Process.WaitForExit(ms)。
    /// </summary>
    public bool WaitForExit(int milliseconds)
    {
        if (_win is not null)
        {
            return _win.WaitForExit(milliseconds);
        }
        var sw = Stopwatch.StartNew();
        while (!HasExited)
        {
            if (milliseconds >= 0 && sw.ElapsedMilliseconds >= milliseconds)
            {
                return false;
            }
            var poll = sw.ElapsedMilliseconds < 2_000 ? 5 : 100;
            var slice = milliseconds < 0
                ? poll
                : (int)Math.Max(1, Math.Min(poll, milliseconds - sw.ElapsedMilliseconds));
            Thread.Sleep(slice);
        }
        return true;
    }

    /// <summary>阻塞等退出（排水 EOF 窗、Dispose 收尾用）。</summary>
    public void WaitForExit()
    {
        if (_win is not null)
        {
            _win.WaitForExit();
            return;
        }
        Action? onReaped = null;
        // 阻塞 waitpid 持 ReapLock 是刻意的：同一 pid 本就只允许一个收尸者，
        // 其余等待方在锁上排队与"自己去 waitpid 阻塞"语义等价——子件退出
        // 即放行，不构成死锁（锁内无回调、无子件依赖回环）。
        lock (_unix!.ReapLock)
        {
            if (!_unix.Reaped && ReapUnix(blocking: true))
            {
                onReaped = _unix.OnReaped;
            }
        }
        FireReaped(onReaped);
    }

    private static void FireReaped(Action? onReaped)
    {
        // 收尸回调一律锁外派发：订阅者重入（再调 Kill/Wait）不卡 ReapLock。
        try { onReaped?.Invoke(); }
        catch { /* 订阅者异常不外溢 */ }
    }

    /// <summary>退出一经确认即发——Unix 由统一 reaper 轮询线程派发（≤30ms 粒度），Windows 透传 Exited。</summary>
    public event EventHandler? Exited
    {
        add
        {
            if (_win is not null)
            {
                _win.Exited += value;
                return;
            }
            _exited += value;
            var fireNow = false;
            lock (_unix!.ReapLock)
            {
                if (_unix.Reaped)
                {
                    fireNow = true; // 已收尸——BCL Exited 同规补派发一次。
                }
                else
                {
                    _unix.OnReaped = () => _exited?.Invoke(this, EventArgs.Empty);
                }
            }
            if (fireNow)
            {
                try { value?.Invoke(this, EventArgs.Empty); }
                catch { /* 订阅者异常不外溢 */ }
            }
        }
        remove
        {
            if (_win is not null)
            {
                _win.Exited -= value;
                return;
            }
            _exited -= value;
            _unix!.OnReaped = _exited is null ? null : () => _exited?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool ReapUnix(bool blocking)
    {
        var done = blocking
            ? NativeSpawnUnix.ReapBlocking(_unix!.Pid, out var code)
            : NativeSpawnUnix.TryReap(_unix!.Pid, out code);
        if (done)
        {
            _unix!.Reaped = true;
            _unix.ExitCode = code;
        }
        return done;
    }

    /// <summary>杀进程树（Unix=kill 进程组+直系；Windows 新框架杀树/旧框架杀直系）。</summary>
    public void Kill(bool entireProcessTree = true)
    {
        if (_win is not null)
        {
#if NET10_0_OR_GREATER
            _win.Kill(entireProcessTree);
#else
            _win.Kill();
#endif
            return;
        }
        // 已退出件拒杀对齐 BCL Process.Kill：子件收尸后其 pid/pgid 可被系统
        // 复用，kill(-pid) 会把 SIGKILL 打进无关进程组——先 waitpid 刷退出态再判。
        if (HasExited)
        {
            throw new InvalidOperationException(
                "Cannot kill a process that has already exited.");
        }
        if (entireProcessTree)
        {
            NativeSpawnUnix.KillTree(_unix!.Pid);
        }
        else
        {
            NativeSpawnUnix.KillOnly(_unix!.Pid);
        }
        // 杀后顺势记退出——不等调用方再来轮询。
        WaitForExit(5_000);
    }

    /// <summary>
    /// 释放句柄面（排水流与 Windows 包装件）。Unix 子件的收尸不随 Dispose：
    /// 统一 reaper 轮询线程始终在册，未等未放的子件退出也必然被收——
    /// 分离 spawn（拉起即退的 nohup/open/重启腿）同样无僵尸化入口。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _stdout?.Dispose(); } catch { }
        try { _stderr?.Dispose(); } catch { }
        try { _stdin?.Dispose(); } catch { }
        _win?.Dispose();
    }
}
