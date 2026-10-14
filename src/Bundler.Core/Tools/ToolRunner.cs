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

    internal static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await TryRunAsync(executable, arguments, workingDirectory, cancellationToken, environment)
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
        IReadOnlyDictionary<string, string>? environment = null)
    {
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

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using (cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
        }))
        {
            process.WaitForExit();
        }
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new Result(process.ExitCode, standardOutput.Result, standardError.Result);
    }

    /// <summary>
    /// Synchronous fixed-argument helper calls (chmod/ln on POSIX hosts):
    /// captures output, throws on non-zero exit. Not for user-facing tools.
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
        // stdout 异步排空、stderr 同步读：两个都同步顺序读在管道缓冲满时会死锁。
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        output.Wait();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{executable}' exited with code {process.ExitCode}: {(output.Result + error).Trim()}");
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
