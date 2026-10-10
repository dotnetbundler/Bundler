namespace DotNet.Bundler.AppImage;

/// <summary>
/// Settings for the Linux .AppImage backend. The backend assembles an AppDir
/// and drives the embedded appimagetool binary; it requires a Linux build host.
/// </summary>
public sealed class AppImageBundleConfiguration
{
    /// <summary>
    /// Package name used for the AppImage file name, the root
    /// <c>&lt;name&gt;.desktop</c> entry and the icon name. Defaults to
    /// <c>ProductName</c> normalized to kebab-case.
    /// </summary>
    public string? PackageName { get; init; }

    /// <summary>
    /// Version string used in the file name
    /// (<c>&lt;name&gt;_&lt;version&gt;_&lt;arch&gt;.AppImage</c>); defaults to
    /// the bundle's <c>Version</c> verbatim — AppImage has no EVR rules.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Target architecture token (<c>x86_64</c>/<c>aarch64</c>)
    /// passed to appimagetool via <c>ARCH</c>. Defaults to the RID mapping
    /// (linux-x86_64 → x86_64, linux-aarch64 → aarch64). Cross-arch output uses the
    /// bundled runtime file when one is embedded for the target arch.
    /// </summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// Payload root inside the AppDir; defaults to <c>usr/lib/&lt;package&gt;</c>.
    /// Relative to the AppDir root (no leading '/'), no '..' segments.
    /// </summary>
    public string? InstallRoot { get; init; }

    /// <summary>
    /// Name of the symlink created at <c>usr/bin/&lt;name&gt;</c> inside the
    /// AppDir pointing at the main executable under <see cref="InstallRoot"/>.
    /// Defaults to the package name; <c>none</c> disables the link.
    /// </summary>
    public string? BinLink { get; init; }

    /// <summary>
    /// Root icon override: a PNG copied to the AppDir root as
    /// <c>&lt;name&gt;.png</c> and <c>.DirIcon</c>. When unset, the largest
    /// square icon from the bundle's <c>Icons</c> hicolor set is linked;
    /// with no icons at all a bundled default PNG is used.
    /// </summary>
    public string? IconFile { get; init; }

    /// <summary>
    /// Caller-supplied <c>.desktop</c> file replacing the generated entry
    /// verbatim (written to <c>usr/share/applications/&lt;name&gt;.desktop</c>
    /// and linked from the AppDir root).
    /// </summary>
    public string? DesktopFile { get; init; }

    /// <summary>
    /// Freedesktop categories for the generated <c>.desktop</c> entry
    /// (for example ["Utility", "Development"]).
    /// </summary>
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>
    /// Optional AppStream metainfo XML file staged under
    /// <c>usr/share/metainfo/&lt;name&gt;.metainfo.xml</c>.
    /// </summary>
    public string? MetainfoFile { get; init; }

    /// <summary>
    /// Arbitrary files planted at AppDir-relative destinations
    /// (e.g. <c>usr/lib/&lt;pkg&gt;/tools/helper.sh</c> or <c>opt/x</c>).
    /// Destinations must be relative POSIX paths with no
    /// <c>..</c>/<c>.</c>/empty segments and may not collide with entries the
    /// builder generates (<c>AppRun</c>, <c>.DirIcon</c>, root
    /// <c>&lt;name&gt;.desktop</c>/<c>&lt;name&gt;.png</c>).
    /// </summary>
    public IReadOnlyList<AppImageFileEntry>? Files { get; init; }

    /// <summary>
    /// Optional OpenPGP signing via <c>appimagetool --sign</c>: the key is
    /// imported into an isolated throwaway GNUPGHOME for the duration of the
    /// build and the signature is embedded in the produced AppImage. An empty
    /// <see cref="Signing"/> leaves the AppImage unsigned. Requires <c>gpg</c>
    /// on the host.
    /// </summary>
    public KeyFileSigningConfiguration Signing { get; init; } = new();
}

/// <summary>A file planted at an AppDir-relative path.</summary>
public sealed class AppImageFileEntry
{
    /// <summary>Host path of the file to pack.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// AppDir-relative target path including the file name
    /// (e.g. <c>opt/myapp/extra.conf</c>). No leading '/', no
    /// '..' / '.' / empty segments.
    /// </summary>
    public string Destination { get; init; } = "";
}
