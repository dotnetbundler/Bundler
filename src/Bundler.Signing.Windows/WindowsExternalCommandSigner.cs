using DotNet.Bundler;
using System.Diagnostics;

namespace DotNet.Bundler.Signing.Windows;

public sealed class WindowsExternalCommandSigner : IBundleSigner
{
    private readonly WindowsExternalCommandSigningOptions options;

    public WindowsExternalCommandSigner(WindowsExternalCommandSigningOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Command))
        {
            throw new ArgumentException("The external signing command is required.", nameof(options));
        }
        if (!options.Arguments.Any(ContainsPathPlaceholder))
        {
            throw new ArgumentException(
                "At least one external signing argument must contain the {path} or %1 placeholder.",
                nameof(options));
        }
    }

    public async Task SignAsync(BundleSigningRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var path = Path.GetFullPath(request.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The file to sign was not found.", path);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.Command,
                Arguments = string.Join(" ", options.Arguments.Select(argument => Quote(Expand(argument, request, path)))),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => completion.TrySetResult(process.ExitCode);
        if (!process.Start())
        {
            throw new InvalidOperationException("The external signing command could not be started.");
        }
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            completion.TrySetCanceled(cancellationToken);
        });
        var exitCode = await completion.Task.ConfigureAwait(false);
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"The external signing command failed for {request.ArtifactKind} with exit code {exitCode}. Provider output and arguments were suppressed because they may contain secrets.");
        }
    }

    private static bool ContainsPathPlaceholder(string value) =>
        value.IndexOf("{path}", StringComparison.Ordinal) >= 0 || value.IndexOf("%1", StringComparison.Ordinal) >= 0;

    private static string Expand(string value, BundleSigningRequest request, string path) => value
        .Replace("{path}", path)
        .Replace("%1", path)
        .Replace("{artifactKind}", request.ArtifactKind.ToString())
        .Replace("{target}", request.TargetRuntimeIdentifier)
        .Replace("{productName}", request.ProductName);

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
