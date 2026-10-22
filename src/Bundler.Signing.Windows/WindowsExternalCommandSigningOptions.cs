namespace DotNet.Bundler.Signing.Windows;

public sealed class WindowsExternalCommandSigningOptions
{
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];

    // 单次签名命令的有界等待上限；未设时回退 BUNDLER_TOOL_TIMEOUT_SECONDS 环境变量，
    // 再回退默认 15min（与 Bundler.Core ToolRunner 同口径）。
    public TimeSpan? Timeout { get; init; }
}
