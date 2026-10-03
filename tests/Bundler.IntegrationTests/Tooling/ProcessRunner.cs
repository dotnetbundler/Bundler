// 子进程运行辅助：集成腿统一经这里 spawn 外部工具/安装器/dotnet，
// 捕获 stdout/stderr/退出码，失败时把输出带进断言消息。
using System.Diagnostics;
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
        var startInfo = new ProcessStartInfo
        {
            FileName = filePath,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        if (argumentLine is not null)
        {
            startInfo.Arguments = argumentLine;
        }
        else if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        if (options.Environment is { } env)
        {
            foreach (var pair in env)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
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
        process.WaitForExit();
        return new Result(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // 启动后不等待：供 "进程仍在运行 / 杀进程树模拟崩溃" 等腿使用。
    public static Process StartDetached(string filePath, string argumentLine = "",
        string? workingDirectory = null, Dictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = filePath,
            Arguments = argumentLine,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
        };
        if (environment is { } env)
        {
            foreach (var pair in env)
            {
                if (pair.Value is not null)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {filePath} {argumentLine}");
    }

    public static void TryKillTree(Process process)
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
