using System.Diagnostics;

namespace DotNet.Bundler.AppImage;

internal static class AppImageProcessRunner
{
    public static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
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
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{executable}'.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using (cancellationToken.Register(() =>
               {
                   try { process.Kill(); } catch (InvalidOperationException) { }
               }))
        {
            process.WaitForExit();
        }
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
