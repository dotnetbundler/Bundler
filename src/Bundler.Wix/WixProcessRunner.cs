using DotNet.Bundler.Core;

namespace DotNet.Bundler.Wix;

internal static class WixProcessRunner
{
    // WiX 3 otherwise exits successfully while ICE/compiler warnings disappear with captured output.
    internal static Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken) =>
        ToolRunner.RunAsync(
            executable,
            new[] { "-wx" }.Concat(arguments).ToArray(),
            workingDirectory,
            cancellationToken);
}
