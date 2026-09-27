namespace DotNet.Bundler.Archive;

/// <summary>Optional knobs for <see cref="ArchiveBundler"/>.</summary>
public sealed class ArchiveBundlerOptions
{
    /// <summary>Receives progress log lines; defaults to a null logger.</summary>
    public IBundleLogger? Logger { get; init; }
}
