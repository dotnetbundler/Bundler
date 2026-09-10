using System.Diagnostics;

namespace Bundler.Core.Tools;

internal static class ProcessRunner
{
    public static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
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
        var output = standardOutput.Result;
        var error = standardError.Result;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(executable)}' exited with code {process.ExitCode}.{Environment.NewLine}{output}{error}".TrimEnd());
        }
    }

    private static string QuoteArgument(string argument) =>
        "\"" + argument.Replace("\"", "\\\"") + "\"";
}
