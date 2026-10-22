// 子进程运行辅助：集成腿统一经这里 spawn 外部工具/安装器/dotnet，
// 捕获 stdout/stderr/退出码，失败时把输出带进断言消息。
// 进程面统一挂 ToolProcess——Unix 经 libc posix_spawn 不碰运行时 spawn/reap
// 全局锁（macOS CI 实证死锁面）；Windows 包装 Process 同语义。
using System.Text;

internal static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public string Output => StdOut + StdErr;
    }

    public sealed class Options
    {
        public string? WorkingDirectory { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
        public string? StandardInput { get; set; }
        public Dictionary<string, string?>? Environment;
        // 进程 Start 成功后的 pid 观测钩子（诊断用）：回调异常不外溢破坏被测流程。
        public Action<int>? ProcessStarted;
    }

    // ArgumentList 形式：参数逐个传递，由运行时负责转义。
    public static Result Run(string filePath, IEnumerable<string> arguments, Options? options = null)
        => Run(filePath, arguments, null, options);

    // argumentLine 形式：需要精确命令行（如 NSIS /S /D= 或 msiexec 组合参数）时直接给整串。
    public static Result Run(string filePath, string argumentLine, Options? options = null)
        => Run(filePath, null, argumentLine, options);

    private static Result Run(string filePath, IEnumerable<string>? arguments,
        string? argumentLine, Options? options)
    {
        // dotnet 子命令进程内串行：多个夹具/腿并发的 publish/build/restore 会共享
        // 上游 src/*/obj 还原目标，NuGet 对 project.assets.json 有并发写防护。
        // 宿主工具与 docker 调用不经此闸，保持并行。
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            lock (DotnetGate)
            {
                return RunCore(filePath, arguments, argumentLine, options);
            }
        }
        return RunCore(filePath, arguments, argumentLine, options);
    }

    private static readonly object DotnetGate = new();

    private static Result RunCore(string filePath, IEnumerable<string>? arguments,
        string? argumentLine, Options? options)
    {
        options ??= new Options();
        var startInfo = new DotNet.Bundler.Core.ToolProcessStartInfo
        {
            FileName = filePath,
            ArgumentLine = argumentLine,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        if (argumentLine is null && arguments is not null)
        {
            startInfo.Arguments.AddRange(arguments);
        }
        if (options.Environment is { } env)
        {
            foreach (var pair in env)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var process = DotNet.Bundler.Core.ToolProcess.Start(startInfo)
            ?? throw new TimeoutException(
                $"Process spawn did not complete within {DotNet.Bundler.Core.NativeSpawnUnix.SpawnTimeoutMilliseconds}ms: " +
                $"{filePath} {argumentLine ?? string.Join(' ', arguments ?? [])}");
        using (process)
        {
            try { options.ProcessStarted?.Invoke(process.Id); }
            catch { /* 诊断回调故障不得改变被测进程流程 */ }
            // 行级异步排空：孙代持管写端时 EOF 永不至——排水任务收尾走下方限窗，
            // 等不到按丢弃尾量处理（与原 BeginOutputReadLine+Cancel* 同价）。
            var outTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    stdout.AppendLine(line);
                }
            });
            var errTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    stderr.AppendLine(line);
                }
            });
            if (options.StandardInput is not null)
            {
                process.StandardInput.Write(options.StandardInput);
            }
            process.StandardInput.Close();
            if (!process.WaitForExit((int)options.Timeout.TotalMilliseconds))
            {
                TryKillTree(process);
                throw new TimeoutException(
                    $"Process timed out after {options.Timeout}: {filePath} {argumentLine ?? string.Join(' ', arguments ?? [])}\n" +
                    $"stdout so far:\n{stdout}\nstderr so far:\n{stderr}");
            }
            // EOF 排水限窗：孙代继承管道写端时退出不等于 EOF——10s 内读不完的
            // 丢尾量收尾（读任务被钉在管道上不可取消，弃任务不阻塞断言面）。
            if (!Task.WaitAll([outTask, errTask], TimeSpan.FromSeconds(10)))
            {
                try { process.StandardOutput.Dispose(); } catch { /* 关管逼读退出 */ }
                try { process.StandardError.Dispose(); } catch { }
            }
            return new Result(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }

    // 启动后不等待：供 "进程仍在运行 / 杀进程树模拟崩溃" 等腿使用。
    public static DotNet.Bundler.Core.ToolProcess StartDetached(string filePath, string argumentLine = "",
        string? workingDirectory = null, Dictionary<string, string?>? environment = null)
    {
        var startInfo = new DotNet.Bundler.Core.ToolProcessStartInfo
        {
            FileName = filePath,
            ArgumentLine = argumentLine,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        if (environment is { } env)
        {
            foreach (var pair in env)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        // 分离 spawn：句柄只作观察/扑杀用，reaper 线程兜底收尸——不 Dispose 也无僵尸。
        return DotNet.Bundler.Core.ToolProcess.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {filePath} {argumentLine}");
    }

    public static void TryKillTree(DotNet.Bundler.Core.ToolProcess process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // taskkill /T /F 覆盖整棵进程树（NSIS launcher 会再起子进程）。
                Run("taskkill.exe", $"/PID {process.Id} /T /F",
                    new Options { Timeout = TimeSpan.FromSeconds(30) });
            }
            else
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* 进程已退出 */ }
        }
    }

    public static void AssertSuccess(Result result, string what)
    {
        Assert.True(result.ExitCode == 0,
            $"{what} failed with exit code {result.ExitCode}.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    }

    public static void AssertExitCode(Result result, int expected, string what)
    {
        Assert.True(result.ExitCode == expected,
            $"{what} returned {result.ExitCode}, expected {expected}.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    }
}
