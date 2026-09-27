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
    /// Defaults to a hash-stamped folder under the user cache
    /// (<c>$XDG_CACHE_HOME</c> or <c>~/.cache</c> on Linux).
    /// </summary>
    public string? ToolsetCacheDirectory { get; init; }
}
