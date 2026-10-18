using System.Diagnostics;
using System.Text;

namespace DotNet.Bundler.Core;

/// <summary>
/// Unified external-tool spawn used by every backend: captures stdout/stderr,
/// kills the child on cancellation, and throws on non-zero exit (RunAsync) or
/// returns the result (TryRunAsync, null when the process cannot be started).
/// </summary>
internal static class ToolRunner
{
    internal sealed record Request(
        string Executable,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string>? Environment = null);

    internal sealed record Result(int ExitCode, string StandardOutput, string StandardError);

    // Test seam: AsyncLocal keeps each test's async context isolated, so xUnit
    // classes running in parallel cannot observe one another's handler.
    private static readonly AsyncLocal<Func<Request, CancellationToken, Task<Result>>?> s_handler = new();

    internal static Func<Request, CancellationToken, Task<Result>>? Handler
    {
        get => s_handler.Value;
        set => s_handler.Value = value;
    }

    // 挂死兜底：外部工具永不返回时给有界等待——默认 15min，BUNDLER_TOOL_TIMEOUT_SECONDS 可配，
    // 合法长任务（notarytool --wait 类）走 RunAsync/TryRunAsync 的 timeout 形参按调用点豁免。
    // 超时后杀进程树（net10 面；旧框架杀直系）并按 ExitCode=-1 交给既有非零处理面，
    // 让"卡死"变成带工具名与耗时的失败。
    internal static TimeSpan ProcessTimeout { get; set; } = ResolveDefaultTimeout();

    private static TimeSpan ResolveDefaultTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("BUNDLER_TOOL_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(15);
    }

    // 挂死插桩：BUNDLER_TOOL_TRACE 指向文件时，每次真实 spawn 追加一行（打开即写即关，
    // 进程被硬杀也不丢已落行）——CI 侧落盘经 ci-logs-* 工件上传，挂死现场=最后一条在途 spawn。
    // argv 过密旗标遮罩：security -p / notarytool --password 等凭证位不落明文（秘密不入可回显日志）。
    private static void TraceSpawn(Process process, Request request)
    {
        var path = Environment.GetEnvironmentVariable("BUNDLER_TOOL_TRACE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            File.AppendAllText(path,
                $"{DateTime.UtcNow:O} pid={process.Id} exe={request.Executable} " +
                $"cwd={request.WorkingDirectory} args={SafeArgs(request.Executable, request.Arguments)}" + Environment.NewLine);
        }
        catch
        {
        }
    }

    // 值随旗标/等号承载秘密的旗标集合——遮罩其后一参或 = 右值。
    // -k 只在 security 可执行名下视为密码位（ditto -c -k --keepParent 的 -k 不携秘密）。
    // 判据是可执行名而非子命令：security 域内 -k 对 import 钥匙串路径等非密操作数仍遮，
    // 属可接受过遮罩——方向安全，仅 trace 可读性微降。
    private static readonly HashSet<string> s_secretFlags = new(StringComparer.Ordinal)
    {
        "-p", "-P", "--password", "--pass", "--secret",
        "--apple-id", "--team-id", "--key", "--key-id", "--api-key", "--apiKey",
    };

    private static bool IsSecretFlag(string arg, bool isSecurityTool) =>
        s_secretFlags.Contains(arg) || (isSecurityTool && arg == "-k");

    internal static string SafeArgs(string executable, IReadOnlyList<string> arguments)
    {
        var isSecurityTool = string.Equals(
            Path.GetFileNameWithoutExtension(executable), "security", StringComparison.OrdinalIgnoreCase);
        var parts = new List<string>(arguments.Count);
        var maskNext = false;
        foreach (var arg in arguments)
        {
            if (maskNext)
            {
                // 被遮位若本身还是密位旗标（flag-flag-value 形），它对后参仍有遮罩义务。
                parts.Add("\"***\"");
                maskNext = IsSecretFlag(arg, isSecurityTool);
                continue;
            }
            var eq = arg.IndexOf('=');
            if (eq > 0 && IsSecretFlag(arg.Substring(0, eq), isSecurityTool))
            {
                parts.Add(arg.Substring(0, eq) + "=\"***\"");
                continue;
            }
            parts.Add(QuoteArgument(arg));
            if (IsSecretFlag(arg, isSecurityTool))
            {
                maskNext = true;
            }
        }
        return string.Join(" ", parts);
    }

    private static int WaitMilliseconds(TimeSpan timeout) =>
        (int)Math.Max(1, Math.Min(timeout.TotalMilliseconds, int.MaxValue - 1));

    // netstandard2.0 无 Kill(true)：新目标杀整棵树，旧面只杀直系子——
    // 孙代持管道时由有界排空收尾兜底，不无限等 ReadToEnd。
    private static void KillProcessTree(Process process)
    {
        try
        {
#if NET10_0_OR_GREATER
            process.Kill(entireProcessTree: true);
#else
            process.Kill();
#endif
        }
        catch
        {
        }
    }

    // 子进程死亡但孙代持管道时 ReadToEnd 不完——排空最多再等 5s，拿到的算数、拿不到的弃。
    // Drained：faulted/canceled 读任务不在此重抛，超时/取消语义不丢。
    private static async Task<(string Out, string Err)> DrainPipesAsync(
        Task<string> standardOutput, Task<string> standardError)
    {
        await Task.WhenAny(
            Task.WhenAll(standardOutput, standardError),
            Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        return (Drained(standardOutput), Drained(standardError));
    }

    private static string Drained(Task<string> task) =>
        task is { IsCompleted: true, IsFaulted: false, IsCanceled: false } ? task.Result : "";

    internal static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var result = await TryRunAsync(executable, arguments, workingDirectory, cancellationToken, environment, timeout)
            .ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException($"Failed to start '{executable}'.");
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(executable)}' exited with code {result.ExitCode}." +
                $"{Environment.NewLine}{result.StandardOutput}{result.StandardError}".TrimEnd());
        }
    }

    /// <summary>Non-throwing variant for tool detection; null when the tool cannot be started.</summary>
    internal static async Task<Result?> TryRunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var bound = timeout ?? ProcessTimeout;
        var request = new Request(executable, arguments.ToArray(), workingDirectory, environment);
        var handler = Handler;
        if (handler is not null)
        {
            return await handler(request, cancellationToken).ConfigureAwait(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(" ", request.Arguments.Select(QuoteArgument)),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }

        TraceSpawn(process, request);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using (cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) KillProcessTree(process); } catch { }
        }))
        {
            if (!process.WaitForExit(WaitMilliseconds(bound)))
            {
                KillProcessTree(process);
                process.WaitForExit(5000);
                var (outText, errText) = await DrainPipesAsync(standardOutput, standardError)
                    .ConfigureAwait(false);
                return new Result(-1, outText,
                    errText + $"{Environment.NewLine}bundler: " +
                    $"'{Path.GetFileName(executable)}' did not exit within {bound} and was killed.");
            }
        }
        // 正常/取消腿同走有界排空：旧 TFM 杀直系后孙代持管道时 EOF 不至，
        // ReadToEnd 的 WhenAll 会在取消后悬停——5s 宽限内读完的算数、读不完的整路弃。
        var (stdout, stderr) = await DrainPipesAsync(standardOutput, standardError)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new Result(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Synchronous fixed-argument helper calls (chmod/ln on POSIX hosts):
    /// captures output, throws on non-zero exit. Not for user-facing tools.
    /// 不经 <see cref="Handler"/> 测试缝——此路径是宿主机真实文件操作，调用不可桩。
    /// </summary>
    internal static void RunSync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{executable}'.");
        TraceSpawn(process, new Request(executable, arguments, startInfo.WorkingDirectory, environment));
        // 两路都异步排空（同步顺序读在管道缓冲满时会死锁）；等待再有界——子进程挂死不再是无底洞。
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(WaitMilliseconds(ProcessTimeout)))
        {
            KillProcessTree(process);
            process.WaitForExit(5000);
            var drained = DrainPipesAsync(output, error);
            drained.Wait();
            throw new InvalidOperationException(
                $"'{executable}' did not exit within {ProcessTimeout} and was killed." +
                $"{Environment.NewLine}{drained.Result.Out}{drained.Result.Err}".TrimEnd());
        }
        process.WaitForExit();
        Task.WaitAll(output, error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{executable}' exited with code {process.ExitCode}: {(output.Result + error.Result).Trim()}");
        }
    }

    /// <summary>Windows command-line quoting: backslashes before a quote (and trailing) double.</summary>
    internal static string QuoteArgument(string argument)
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
}
