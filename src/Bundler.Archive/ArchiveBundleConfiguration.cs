namespace DotNet.Bundler.Archive;

/// <summary>
/// Settings for the .zip/.tar.gz archive backend. The backend writes both
/// formats in managed code — no external tools, any build host OS.
/// </summary>
public sealed class ArchiveBundleConfiguration
{
    /// <summary>
    /// Package name used for the archive file name and its single top-level
    /// directory (<c>&lt;name&gt;-&lt;version&gt;-&lt;target&gt;/</c>). Defaults to
    /// <c>ProductName</c> normalized to kebab-case.
    /// </summary>
    public string? PackageName { get; init; }

    /// <summary>
    /// Version string used in the file name and top-level directory; defaults
    /// to the bundle's <c>Version</c> verbatim — archives have no EVR rules.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Overrides the archive file base name
    /// (<c>&lt;name&gt;-&lt;version&gt;-&lt;target&gt;</c> by default) and the
    /// top-level directory name. Must be a single relative path segment.
    /// </summary>
    public string? ArchiveName { get; init; }

    /// <summary>
    /// Extra files to stage inside the archive's top-level directory at
    /// archive-relative POSIX destinations (e.g. <c>docs/readme.txt</c>);
    /// absolute paths, '..'/'.'/empty segments and collisions with payload
    /// entries are rejected.
    /// </summary>
    public IReadOnlyList<ArchiveFileEntry>? Files { get; init; }
}

/// <summary>An arbitrary file mapped into the archive's top-level directory.</summary>
public sealed class ArchiveFileEntry
{
    /// <summary>Host path of the file to include.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Archive-relative POSIX destination under the top-level directory
    /// (including the file name, e.g. <c>docs/readme.txt</c>).
    /// </summary>
    public string Destination { get; init; } = "";
}
