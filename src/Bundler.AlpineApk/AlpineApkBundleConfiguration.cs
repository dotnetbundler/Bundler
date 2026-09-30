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

    /// <summary>
    /// Package release written to <c>pkgver</c> as <c>-r&lt;release&gt;</c>.
    /// Defaults to "0"; must be a non-negative integer (digits only).
    /// </summary>
    public string? Release { get; init; }

    /// <summary>
    /// <c>license</c> field (e.g. "MIT" or "BSD-3-Clause"). Omitted when unset.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// <c>builddate</c> field as seconds since the Unix epoch. Defaults to "0"
    /// for deterministic output; must be a non-negative integer when set.
    /// </summary>
    public string? BuildDate { get; init; }

    /// <summary>
    /// Runtime dependencies; each entry is written as a repeated
    /// <c>depend = ...</c> line (e.g. "so:libc.musl-x86_64.so.1", "busybox&gt;=1.35").
    /// Entries must be non-empty and free of whitespace.
    /// </summary>
    public IReadOnlyList<string>? Depends { get; init; }

    /// <summary>
    /// Virtual provisions; each entry is written as a repeated
    /// <c>provides = ...</c> line.
    /// </summary>
    public IReadOnlyList<string>? Provides { get; init; }

    /// <summary>
    /// Directories watched by apk triggers; written as a single space-separated
    /// <c>triggers = ...</c> line. Each entry must be an absolute path.
    /// </summary>
    public IReadOnlyList<string>? Triggers { get; init; }

    /// <summary>
    /// Arbitrary payload files mapped to absolute destinations inside the
    /// package (e.g. "/etc/myapp/settings.conf"); installed with mode 0644.
    /// </summary>
    public IReadOnlyList<AlpineApkFileEntry>? Files { get; init; }

    /// <summary>Caller-supplied <c>.pre-install</c> script file.</summary>
    public string? PreInstallScript { get; init; }

    /// <summary>Caller-supplied <c>.post-install</c> script file.</summary>
    public string? PostInstallScript { get; init; }

    /// <summary>Caller-supplied <c>.pre-deinstall</c> script file.</summary>
    public string? PreDeinstallScript { get; init; }

    /// <summary>Caller-supplied <c>.post-deinstall</c> script file.</summary>
    public string? PostDeinstallScript { get; init; }

    /// <summary>Caller-supplied <c>.pre-upgrade</c> script file.</summary>
    public string? PreUpgradeScript { get; init; }

    /// <summary>Caller-supplied <c>.post-upgrade</c> script file.</summary>
    public string? PostUpgradeScript { get; init; }

    /// <summary>
    /// Escape hatch: extra <c>.PKGINFO</c> key/value pairs appended after the
    /// built-in fields (e.g. "replaces", "install_if"). Keys must match
    /// <c>[A-Za-z0-9._-]+</c> and must not collide with fields the writer emits.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraPkgInfo { get; init; }
}

/// <summary>
/// A host file packed at an absolute destination inside the apk data segment.
/// </summary>
public sealed class AlpineApkFileEntry
{
    /// <summary>Host path of the file to pack.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Absolute target path inside the package including the file name
    /// (e.g. <c>/etc/myapp/settings.conf</c>). No '..' or '.' segments.
    /// </summary>
    public string Destination { get; init; } = "";
}
