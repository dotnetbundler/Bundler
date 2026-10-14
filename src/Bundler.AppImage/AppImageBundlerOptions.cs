namespace DotNet.Bundler.AppImage;

/// <summary>
/// Options for <see cref="AppImageBundler"/>.
/// </summary>
public sealed class AppImageBundlerOptions
{
    /// <summary>Optional logger for pipeline and tool output.</summary>
    public IBundleLogger? Logger { get; init; }

    /// <summary>
    /// Directory the embedded appimagetool binaries are materialized to.
    /// Defaults to the shared tool cache
    /// (<c>%LocalAppData%/DotNetBundler/tools</c> or the platform equivalent).
    /// </summary>
    public string? ToolsetCacheDirectory { get; init; }
}
