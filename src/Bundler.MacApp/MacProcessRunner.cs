using System.Diagnostics;

namespace DotNet.Bundler.MacApp;

internal static class MacProcessRunner
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
                    $"{Environment.NewLine}{intercepted.StandardOutput}{intercepted.StandardError}".TrimEnd());
            }
            return;
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

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{executable}'.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        await Task.WhenAll(standardOutput, standardError);
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(executable)}' exited with code {process.ExitCode}.{Environment.NewLine}" +
                $"{standardOutput.Result}{standardError.Result}".TrimEnd());
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

    private static string QuoteArgument(string argument) =>
        "\"" + argument.Replace("\"", "\\\"") + "\"";
}
