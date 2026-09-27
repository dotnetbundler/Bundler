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

    /// <summary>
    /// Explicit dependency clauses written verbatim (comma-joined) to
    /// <c>Depends:</c>, e.g. <c>"libc6 (>= 2.35)"</c>. Pure pass-through — no
    /// runtime probing is performed.
    /// </summary>
    public IReadOnlyList<string>? Depends { get; init; }

    /// <summary><c>Recommends:</c> clauses, same pass-through contract as <see cref="Depends"/>.</summary>
    public IReadOnlyList<string>? Recommends { get; init; }

    /// <summary><c>Provides:</c> clauses, same pass-through contract as <see cref="Depends"/>.</summary>
    public IReadOnlyList<string>? Provides { get; init; }

    /// <summary><c>Conflicts:</c> clauses, same pass-through contract as <see cref="Depends"/>.</summary>
    public IReadOnlyList<string>? Conflicts { get; init; }

    /// <summary><c>Replaces:</c> clauses, same pass-through contract as <see cref="Depends"/>.</summary>
    public IReadOnlyList<string>? Replaces { get; init; }

    /// <summary>
    /// <c>Section:</c> field (e.g. "utils", "net", "devel"); a Debian archive
    /// section name or empty to omit the field.
    /// </summary>
    public string? Section { get; init; }

    /// <summary>
    /// <c>Priority:</c> field; defaults to "optional". Must be one of the Debian
    /// policy values: required, important, standard, optional, extra.
    /// </summary>
    public string? Priority { get; init; }

    /// <summary>
    /// Semicolon-separated freedesktop categories for the generated
    /// <c>.desktop</c> file (e.g. "Utility;Development").
    /// </summary>
    public string? Categories { get; init; }

    /// <summary>
    /// Expert knob: path to a caller-supplied <c>.desktop</c> file that replaces the
    /// generated one verbatim at <c>usr/share/applications/&lt;package&gt;.desktop</c>.
    /// The caller owns its validity — the backend does not lint it.
    /// </summary>
    public string? DesktopFile { get; init; }

    /// <summary>
    /// Optional AppStream metainfo XML installed at
    /// <c>usr/share/metainfo/&lt;package&gt;.metainfo.xml</c>.
    /// </summary>
    public string? MetainfoFile { get; init; }

    /// <summary>
    /// Optional upstream changelog installed gzipped at
    /// <c>usr/share/doc/&lt;package&gt;/changelog.gz</c>.
    /// </summary>
    public string? ChangelogFile { get; init; }

    /// <summary>
    /// Extra files mapped to absolute paths inside the package (e.g. a config under
    /// <c>/etc</c>); each entry's <c>Destination</c> is an absolute package path
    /// (leading '/' optional) with a file name, outside the payload root.
    /// </summary>
    public IReadOnlyList<DebFileEntry>? Files { get; init; }
}

/// <summary>A file planted at an absolute path inside the .deb payload.</summary>
public sealed class DebFileEntry
{
    /// <summary>Host path of the file to pack.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Absolute target path inside the package including the file name
    /// (e.g. <c>/etc/myapp/settings.conf</c>). No '..' or '.' segments.
    /// </summary>
    public string Destination { get; init; } = "";
}
