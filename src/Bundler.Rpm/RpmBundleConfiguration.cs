namespace DotNet.Bundler.Rpm;

/// <summary>
/// Settings for the Linux .rpm backend. The package is written entirely in managed
/// code (lead + headers + cpio + gzip), so builds run on any host that can run .NET.
/// </summary>
public sealed class RpmBundleConfiguration
{
    /// <summary>
    /// RPM package name written to the <c>NAME</c> tag and used in the file name.
    /// Defaults to <c>ProductName</c> normalized to kebab-case. RPM names may contain
    /// letters, digits, '+', '-', '.', '_' and must not contain whitespace.
    /// </summary>
    public string? PackageName { get; init; }

    /// <summary>
    /// Explicit rpm <c>Version</c> override. When unset, the bundle's SemVer
    /// <c>Version</c> is mapped: the prerelease suffix moves into
    /// <see cref="Release"/> (Fedora convention, e.g. <c>1.0.0-alpha.1</c> →
    /// <c>Version=1.0.0</c>, <c>Release=0.1.alpha.1</c>), so prereleases sort
    /// before their release. The rpm Version must not contain '-'.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// rpm <c>Release</c> override; defaults to "1" for releases and
    /// <c>0.&lt;n&gt;.&lt;prerelease&gt;</c> for prereleases. Must not contain '-'.
    /// Ignored when <see cref="Version"/> is set together with an explicit release
    /// is not applicable — set Version for a full EVR override.
    /// </summary>
    public string? Release { get; init; }

    /// <summary>
    /// Optional numeric epoch written to the <c>EPOCH</c> tag. The epoch is part of
    /// dependency strings and <c>rpm -q</c> output but not the file name.
    /// </summary>
    public string? Epoch { get; init; }

    /// <summary>
    /// RPM architecture written to the <c>ARCH</c> tag and used in the file name.
    /// Defaults to the RID mapping: linux-x64 → x86_64, linux-arm64 → aarch64.
    /// </summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// <c>VENDOR</c> tag; defaults to the bundle's <c>Publisher</c>, then its
    /// <c>Identifier</c> when no publisher is set.
    /// </summary>
    public string? Vendor { get; init; }

    /// <summary>
    /// Absolute payload root inside the package; defaults to
    /// <c>/usr/lib/&lt;package-name&gt;</c>. Must start with '/' and contain no
    /// '..' segments.
    /// </summary>
    public string? InstallRoot { get; init; }

    /// <summary>
    /// Name of the symlink created at <c>/usr/bin/&lt;name&gt;</c> pointing at the
    /// main executable under <see cref="InstallRoot"/>. Defaults to the package
    /// name; set to "" or "none" to omit the link.
    /// </summary>
    public string? BinLink { get; init; }
}
