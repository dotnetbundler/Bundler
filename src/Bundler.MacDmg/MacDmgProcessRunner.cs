using System.Text;
using System.Diagnostics;

namespace DotNet.Bundler.MacDmg;

internal static class MacDmgProcessRunner
{
    internal sealed record Request(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);

    /// <summary>
    /// Test seam: when set, both RunAsync and TryRunAsync delegate to it instead of spawning a
    /// process. Tests use it to assert argument assembly and to simulate tool failures.
    /// </summary>
    internal static Func<Request, CancellationToken, Task<Result>>? Handler;

    internal static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (Handler is not null)
        {
            var intercepted = await Handler(
                new Request(executable, arguments.ToArray(), workingDirectory), cancellationToken);
            if (intercepted.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(executable)}' exited with code {intercepted.ExitCode}." +
                    $"{intercepted.StandardOutput}{intercepted.StandardError}".TrimEnd());
            }
            return;
        }
        var result = await TryRunAsync(executable, arguments, workingDirectory, cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(executable)}' could not be started on this host.");
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(executable)}' exited with code {result.ExitCode}." +
                $"{result.StandardOutput}{result.StandardError}".TrimEnd());
        }
    }

    internal sealed record Result(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Non-throwing variant for tool detection; null when the tool cannot be started.</summary>
    internal static async Task<Result?> TryRunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (Handler is not null)
        {
            return await Handler(
                new Request(executable, arguments.ToArray(), workingDirectory), cancellationToken);
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Arguments = string.Join(" ", arguments.Select(QuoteArgument));

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }
        }
        catch (Exception) // tool absent (ENOENT) or not launchable on this host
        {
            return null;
        }
        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            await Task.WhenAll(standardOutput, standardError);
            return new Result(process.ExitCode, standardOutput.Result, standardError.Result);
        }
    }

    // MSVCRT 规则：引号内 '"' 与收尾反斜杠需成对，否则吞并收尾引号。
    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(character);
            }
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }
}
