using System.Diagnostics;
using System.Text;

namespace DotNet.Bundler.Nsis;

internal static class ProcessRunner
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
            try { if (!process.HasExited) process.Kill(); } catch { }
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

    // MSVCRT 规则：引号内 '\\' 与收尾反斜杠需成对，否则吞并收尾引号。
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
