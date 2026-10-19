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
        var bound = ProcessTimeout();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            completion.TrySetCanceled(cancellationToken);
        });
        // 有界等待（与 Bundler.Core ToolRunner 同口径：BUNDLER_TOOL_TIMEOUT_SECONDS、
        // 默认 15min）——签名工具挂死/等控制台输入时不能让 MSBuild 任务无限悬停。
        var exited = await Task.WhenAny(
            completion.Task, Task.Delay(bound, CancellationToken.None)).ConfigureAwait(false);
        if (exited != completion.Task)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            await Task.WhenAny(
                completion.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"The external signing command did not exit within {bound} and was killed. " +
                "Provider output and arguments were suppressed because they may contain secrets.");
        }
        var exitCode = await completion.Task.ConfigureAwait(false);
        // 杀直系后孙代持管道时 ReadToEnd 悬停——5s 有界排空，读不到的弃。
        await Task.WhenAny(
            Task.WhenAll(standardOutput, standardError),
            Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"The external signing command failed for {request.ArtifactKind} with exit code {exitCode}. Provider output and arguments were suppressed because they may contain secrets.");
        }
    }

    private static TimeSpan ProcessTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("BUNDLER_TOOL_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(15);
    }

    // %1 只在 token 边界匹配：%10/%100 等更长数字占位不被误伤。
    private static readonly System.Text.RegularExpressions.Regex LegacyPathPlaceholder =
        new("%1(?![0-9])");

    internal static bool ContainsPathPlaceholder(string value) =>
        value.IndexOf("{path}", StringComparison.Ordinal) >= 0 || LegacyPathPlaceholder.IsMatch(value);

    internal static string Expand(string value, BundleSigningRequest request, string path) =>
        LegacyPathPlaceholder.Replace(value
            .Replace("{path}", path), path)
            .Replace("{artifactKind}", request.ArtifactKind.ToString())
            .Replace("{target}", request.TargetTarget)
            .Replace("{productName}", request.ProductName);

    internal static string Quote(string value)
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
