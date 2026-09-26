using System.Diagnostics;

namespace DotNet.Bundler.MacApp;

internal static class MacProcessRunner
{
    internal static async Task RunAsync(
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

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(executable)}' exited with code {process.ExitCode}.{Environment.NewLine}" +
                $"{standardOutput.Result}{standardError.Result}".TrimEnd());
        }
    }

    private static string QuoteArgument(string argument) =>
        "\"" + argument.Replace("\"", "\\\"") + "\"";
}
