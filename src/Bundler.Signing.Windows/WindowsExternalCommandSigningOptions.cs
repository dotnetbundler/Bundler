namespace DotNet.Bundler.Signing.Windows;

public sealed class WindowsExternalCommandSigningOptions
{
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
