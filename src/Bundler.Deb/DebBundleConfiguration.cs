namespace DotNet.Bundler.Deb;

/// <summary>
/// Settings for the Linux .deb backend. The package is written entirely in managed
/// code (ar + tar + gzip), so builds run on any host that can run .NET.
/// </summary>
public sealed class DebBundleConfiguration
{
    /// <summary>
    /// Debian package name written to <c>Package:</c> and used in the file name.
    /// Defaults to <c>ProductName</c> normalized to kebab-case. Must match the Debian
    /// source-name character set: lowercase letters, digits, '+', '-', '.', starting
    /// with an alphanumeric and at least two characters long.
    /// </summary>
    public string? PackageName { get; init; }

    /// <summary>
    /// Complete Debian version override (optionally with <c>epoch:</c> prefix and
    /// <c>-revision</c> suffix). When unset, the bundle's SemVer <c>Version</c> is
    /// mapped: a prerelease suffix becomes <c>~</c>-separated so prereleases sort
    /// before their release, and <see cref="Revision"/> supplies the debian revision.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Debian revision appended to the mapped upstream version; defaults to "1".
    /// Ignored when <see cref="Version"/> is set. Set to "" to emit a native-style
    /// version without a revision component.
    /// </summary>
    public string? Revision { get; init; }

    /// <summary>
    /// Optional numeric epoch prefixed to the mapped version (e.g. "2" produces
    /// <c>2:1.0.0-1</c>). Ignored when <see cref="Version"/> is set. The epoch is
    /// not part of the output file name.
    /// </summary>
    public string? Epoch { get; init; }

    /// <summary>
    /// Debian architecture written to <c>Architecture:</c> and used in the file name.
    /// Defaults to the RID mapping: linux-x64 → amd64, linux-arm64 → arm64.
    /// </summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// <c>Maintainer:</c> field; defaults to the bundle's <c>Publisher</c>, then its
    /// <c>Identifier</c> when no publisher is set.
    /// </summary>
    public string? Maintainer { get; init; }

    /// <summary>
    /// Absolute payload root inside the package; defaults to
    /// <c>/usr/lib/&lt;package-name&gt;</c>. The publish output lands at this root and the
    /// <c>usr/bin</c> link targets it. Must start with '/' and contain no '..' segments.
    /// </summary>
    public string? InstallRoot { get; init; }

    /// <summary>
    /// Name of the symlink created at <c>/usr/bin/&lt;name&gt;</c> pointing at the main
    /// executable under <see cref="InstallRoot"/>. Defaults to the package name;
    /// set to "" to disable the link.
    /// </summary>
    public string? BinLink { get; init; }
}
