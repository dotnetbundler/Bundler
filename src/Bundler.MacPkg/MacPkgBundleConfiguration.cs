namespace DotNet.Bundler.MacPkg;

/// <summary>A file or directory mapped into the .pkg payload.</summary>
public sealed class MacPkgPayloadItem
{
    /// <summary>Host path of a file or directory to include in the payload.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Path inside the payload relative to <see cref="MacPkgBundleConfiguration.InstallLocation"/>;
    /// defaults to the source's file or directory name.
    /// </summary>
    public string? Destination { get; init; }
}

/// <summary>Settings for the macOS .pkg backend (MAC-PKG-1 minimal component-package scope).</summary>
public sealed class MacPkgBundleConfiguration
{
    /// <summary>
    /// Package identifier written to PackageInfo; defaults to the bundle's
    /// <c>Identifier</c> (the value that becomes the .app CFBundleIdentifier).
    /// </summary>
    public string? Identifier { get; init; }

    /// <summary>Package version written to PackageInfo; defaults to the bundle's <c>Version</c>.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Install prefix passed to <c>pkgbuild --install-location</c>; defaults to
    /// <c>/Applications</c>. Payload destinations are interpreted relative to it.
    /// </summary>
    public string InstallLocation { get; init; } = "/Applications";

    /// <summary>
    /// Extra files/directories added to the payload next to the default .app entry.
    /// Destinations are relative to <see cref="InstallLocation"/>.
    /// </summary>
    public IReadOnlyList<MacPkgPayloadItem>? PayloadItems { get; init; }
}
