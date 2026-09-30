namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// Settings for the Alpine .apk (apk v2) backend. The package is written entirely
/// in managed code (three concatenated gzip streams), so builds run on any host
/// that can run .NET. Only linux-musl targets accept this format.
/// </summary>
public sealed class AlpineApkBundleConfiguration
{
    /// <summary>
    /// Alpine package name written to <c>pkgname</c> and used in the file name.
    /// Defaults to <c>ProductName</c> normalized to kebab-case. Must match
    /// <c>[A-Za-z0-9][A-Za-z0-9._+-]*</c>, start with an alphanumeric, and end
    /// with an alphanumeric.
    /// </summary>
    public string? PackageName { get; init; }

    /// <summary>
    /// Upstream version written to <c>pkgver</c> as <c>&lt;version&gt;-r&lt;release&gt;</c>.
    /// Defaults to the bundle's SemVer <c>Version</c> with '-' mapped to '_'
    /// (apk versions cannot contain '-'). Allowed characters: letters, digits,
    /// '.', '_', '+', '~'.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Alpine architecture written to <c>arch</c> and used in the file name.
    /// Defaults to the RID mapping: linux-musl-x64 → x86_64, linux-musl-arm64 → aarch64.
    /// </summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// <c>origin</c> field; defaults to <see cref="PackageName"/>.
    /// </summary>
    public string? Origin { get; init; }

    /// <summary>
    /// <c>pkgdesc</c> field; defaults to the bundle's <c>Description</c>, then its
    /// <c>ProductName</c>.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// <c>url</c> field; defaults to the bundle's <c>Homepage</c>. The field is
    /// omitted when both are empty.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Name of the symlink created at <c>/usr/bin/&lt;name&gt;</c> pointing at the
    /// main executable under <c>/usr/lib/&lt;package-name&gt;</c>. Defaults to the
    /// package name; set to "" to disable the link.
    /// </summary>
    public string? BinLink { get; init; }
}
