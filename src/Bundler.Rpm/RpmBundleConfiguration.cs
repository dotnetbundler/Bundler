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

    /// <summary>
    /// Explicit dependency clauses written to the <c>REQUIRE*</c> tags, e.g.
    /// <c>"libc.so.6"</c> or <c>"libfoo &gt;= 1.2"</c>. Clause syntax is
    /// <c>name</c> or <c>name &lt;op&gt; evr</c> with op one of
    /// <c>&lt;</c>, <c>&lt;=</c>, <c>=</c>, <c>&gt;=</c>, <c>&gt;</c>. Pure
    /// pass-through — no runtime probing is performed; the writer already
    /// registers the <c>rpmlib(...)</c> self-dependencies.
    /// </summary>
    public IReadOnlyList<string>? Requires { get; init; }

    /// <summary><c>PROVIDE*</c> clauses, same syntax as <see cref="Requires"/>; the self-provides <c>name = evr</c> and <c>name(arch) = evr</c> are always emitted.</summary>
    public IReadOnlyList<string>? Provides { get; init; }

    /// <summary><c>CONFLICT*</c> clauses, same syntax as <see cref="Requires"/>.</summary>
    public IReadOnlyList<string>? Conflicts { get; init; }

    /// <summary><c>OBSOLETE*</c> clauses, same syntax as <see cref="Requires"/>.</summary>
    public IReadOnlyList<string>? Obsoletes { get; init; }

    /// <summary>Weak dependency <c>RECOMMEND*</c> clauses, same syntax as <see cref="Requires"/>.</summary>
    public IReadOnlyList<string>? Recommends { get; init; }

    /// <summary>Weak dependency <c>SUGGEST*</c> clauses, same syntax as <see cref="Requires"/>.</summary>
    public IReadOnlyList<string>? Suggests { get; init; }

    /// <summary>
    /// <c>LICENSE</c> tag, conventionally an SPDX expression (e.g. "MIT");
    /// defaults to <c>Unspecified</c>.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// <c>GROUP</c> tag (legacy rpm group string); defaults to
    /// <c>Unspecified</c>. Modern packages omit a group — pass "" to omit.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// <c>URL</c> tag; defaults to the bundle's <c>Homepage</c>. Pass "" to omit
    /// the tag entirely.
    /// </summary>
    public string? Url { get; init; }

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
    /// <c>usr/share/doc/&lt;package&gt;/changelog.gz</c> and flagged
    /// <c>%doc</c>.
    /// </summary>
    public string? ChangelogFile { get; init; }

    /// <summary>
    /// Extra files mapped to absolute paths inside the package (e.g. a config
    /// under <c>/etc</c>); each entry's <c>Destination</c> is an absolute
    /// package path (leading '/' optional) with a file name, outside the
    /// payload root. Destinations under <c>/etc</c> are marked
    /// <c>%config(noreplace)</c> automatically — <c>rpm -e</c> leaves a modified
    /// file behind as <c>.rpmsave</c>.
    /// </summary>
    public IReadOnlyList<RpmFileEntry>? Files { get; init; }

    /// <summary>
    /// Additional package paths (absolute, must exist in the payload) marked
    /// <c>%config(noreplace)</c>. Destinations of <see cref="Files"/> under
    /// <c>/etc</c> are marked automatically.
    /// </summary>
    public IReadOnlyList<string>? ConfigFiles { get; init; }

    /// <summary>
    /// Managed systemd unit: path to a <c>.service</c> file installed at
    /// <c>usr/lib/systemd/system/&lt;package&gt;.service</c> (0644). A
    /// <c>systemctl daemon-reload</c> epilogue is synthesized into
    /// <c>%post</c> and <c>%postun</c> (appended to caller-supplied scriptlets
    /// when present). The unit is installed but not enabled or started —
    /// enabling policy stays with the caller's scriptlets.
    /// </summary>
    public string? SystemdServiceFile { get; init; }

    /// <summary>
    /// Expert knob: path to a caller-supplied <c>%pre</c> scriptlet file
    /// (<c>PREIN</c>/<c>PREINPROG</c> tags). LF line endings only; a
    /// <c>#!interpreter</c> first line is stripped and becomes the interpreter
    /// tag (default <c>/bin/sh</c>). rpm passes the install count in $1.
    /// </summary>
    public string? PreInstallFile { get; init; }

    /// <summary>Expert knob: caller-supplied <c>%post</c> scriptlet file; see <see cref="PreInstallFile"/>.</summary>
    public string? PostInstallFile { get; init; }

    /// <summary>Expert knob: caller-supplied <c>%preun</c> scriptlet file; see <see cref="PreInstallFile"/>.</summary>
    public string? PreUninstallFile { get; init; }

    /// <summary>Expert knob: caller-supplied <c>%postun</c> scriptlet file; see <see cref="PreInstallFile"/>.</summary>
    public string? PostUninstallFile { get; init; }

    /// <summary>Explicit interpreter for <c>%pre</c> (<c>PREINPROG</c>), overriding any shebang-derived one.</summary>
    public string? PreInstallProgram { get; init; }

    /// <summary>Explicit interpreter for <c>%post</c> (<c>POSTINPROG</c>).</summary>
    public string? PostInstallProgram { get; init; }

    /// <summary>Explicit interpreter for <c>%preun</c> (<c>PREUNPROG</c>).</summary>
    public string? PreUninstallProgram { get; init; }

    /// <summary>Explicit interpreter for <c>%postun</c> (<c>POSTUNPROG</c>).</summary>
    public string? PostUninstallProgram { get; init; }

    /// <summary>
    /// Payload compression: only <c>"gzip"</c> is supported — the writer is
    /// pure managed code and netstandard2.0 ships no xz/zstd encoder without
    /// a third-party dependency. Any other value is rejected.
    /// </summary>
    public string? Compression { get; init; }
}

/// <summary>A file planted at an absolute path inside the .rpm payload.</summary>
public sealed class RpmFileEntry
{
    /// <summary>Host path of the file to pack.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Absolute target path inside the package including the file name
    /// (e.g. <c>/etc/myapp/settings.conf</c>). No '..' or '.' segments.
    /// </summary>
    public string Destination { get; init; } = "";
}
