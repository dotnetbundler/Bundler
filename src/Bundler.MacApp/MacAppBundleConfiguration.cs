using DotNet.Bundler;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// macOS .app specific configuration layered on the shared <see cref="BundleConfiguration"/>.
/// </summary>
public sealed class MacAppBundleConfiguration
{
    /// <summary>CFBundleName; defaults to <see cref="BundleConfiguration.ProductName"/>.</summary>
    public string? BundleName { get; init; }

    /// <summary>CFBundleDisplayName; defaults to <see cref="BundleConfiguration.ProductName"/>.</summary>
    public string? BundleDisplayName { get; init; }

    /// <summary>CFBundleShortVersionString; defaults to <see cref="BundleConfiguration.Version"/>.</summary>
    public string? ShortVersion { get; init; }

    /// <summary>CFBundleVersion; defaults to the resolved short version string.</summary>
    public string? BuildVersion { get; init; }

    /// <summary>LSMinimumSystemVersion; written only when configured.</summary>
    public string? MinimumSystemVersion { get; init; }

    /// <summary>LSApplicationCategoryType, for example "public.app-category.utilities".</summary>
    public string? Category { get; init; }

    /// <summary>
    /// Base name of the bundle icon; the produced file is <c>&lt;IconName&gt;.icns</c> under
    /// Contents/Resources and CFBundleIconFile points at it. Defaults to "AppIcon".
    /// Applies to .icns/.png inputs; .icon/.car inputs produce Assets.car + CFBundleIconName instead.
    /// </summary>
    public string? IconName { get; init; }

    /// <summary>
    /// macOS document-type entries emitted as CFBundleDocumentTypes (+ UTExportedTypeDeclarations
    /// when <see cref="MacAppDocumentTypeConfiguration.ExportedTypeIdentifier"/> is set).
    /// An entry whose extensions overlap a shared <c>bundle.FileAssociations</c> entry absorbs it:
    /// the emitted extensions are the union, and shared name/description/MIME fill unset mac fields.
    /// Entries without any shared overlap are emitted standalone (extensions may be empty for
    /// contentTypes-only declarations).
    /// </summary>
    public IReadOnlyList<MacAppDocumentTypeConfiguration> DocumentTypes { get; init; } = [];

    /// <summary>
    /// macOS URL-type entries emitted as CFBundleURLTypes; merged onto shared
    /// <c>bundle.UrlProtocols</c> by scheme, or emitted standalone.
    /// </summary>
    public IReadOnlyList<MacAppUrlTypeConfiguration> UrlTypes { get; init; } = [];

    /// <summary>
    /// ATS exception domain (for example "example.com"): writes
    /// NSAppTransportSecurity/NSExceptionDomains/&lt;domain&gt; with
    /// NSExceptionAllowsInsecureHTTPLoads + NSIncludesSubdomains. ATS stays strict unless set.
    /// </summary>
    public string? ExceptionDomain { get; init; }

    /// <summary>
    /// Path to a caller-supplied Info.plist whose top-level dict is shallow-merged over the
    /// generated keys (caller values win). Identity keys (CFBundleIdentifier, CFBundleExecutable,
    /// CFBundleShortVersionString, CFBundleVersion, CFBundlePackageType) are read back after the
    /// merge and must equal the resolved values — a conflict is a build error, not an override.
    /// Mutually exclusive with <see cref="InfoPlistXml"/>.
    /// </summary>
    public string? InfoPlistFile { get; init; }

    /// <summary>
    /// Inline plist XML (&lt;plist&gt;&lt;dict&gt;… or a bare &lt;dict&gt;) merged like
    /// <see cref="InfoPlistFile"/>. Mutually exclusive with it.
    /// </summary>
    public string? InfoPlistXml { get; init; }

    /// <summary>
    /// Explicit payload mappings into Contents/ (for example PlugIns/, SharedSupport/).
    /// Top-level reserved names (MacOS, Resources, Frameworks, Info.plist, PkgInfo) are rejected.
    /// </summary>
    public IReadOnlyList<MacAppContentConfiguration> Contents { get; init; } = [];

    /// <summary>Explicit .framework/.dylib sources copied into Contents/Frameworks/.</summary>
    public IReadOnlyList<string> Frameworks { get; init; } = [];
}

public sealed class MacAppContentConfiguration
{
    public string Source { get; init; } = "";

    /// <summary>Destination path relative to Contents/.</summary>
    public string TargetPath { get; init; } = "";
}

/// <summary>CFBundleTypeRole values.</summary>
public enum MacAppTypeRole
{
    /// <summary>The app can read and edit files of this type.</summary>
    Editor,
    /// <summary>The app can read files of this type.</summary>
    Viewer,
    /// <summary>Shell-role entry.</summary>
    Shell,
    /// <summary>QuickLook generator role.</summary>
    QLGenerator,
    /// <summary>The app does not open files of this type.</summary>
    None
}

/// <summary>LSHandlerRank values.</summary>
public enum MacAppHandlerRank
{
    /// <summary>Default opener of the type (also LaunchServices' implicit value).</summary>
    Default,
    /// <summary>Primary creator of files of this type.</summary>
    Owner,
    /// <summary>Secondary viewer of this type.</summary>
    Alternate,
    /// <summary>Never selected to open the type; still accepts drops.</summary>
    None
}

/// <summary>
/// One CFBundleDocumentTypes entry. Extensions may be empty when the entry only declares
/// content types (<see cref="ContentTypes"/>) or an exported UTI.
/// </summary>
public sealed class MacAppDocumentTypeConfiguration
{
    /// <summary>Extensions (leading '.' optional) — LSItemContentTypes/CFBundleTypeExtensions source.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>CFBundleTypeName; defaults to the first extension.</summary>
    public string? Name { get; init; }

    /// <summary>Type description; also emitted as UTTypeDescription when exporting a UTI.</summary>
    public string? Description { get; init; }

    /// <summary>CFBundleTypeRole; defaults to Editor.</summary>
    public MacAppTypeRole? Role { get; init; }

    /// <summary>LSHandlerRank; defaults to Default.</summary>
    public MacAppHandlerRank? Rank { get; init; }

    /// <summary>Explicit LSItemContentTypes UTIs (emitted before inferred ones).</summary>
    public IReadOnlyList<string> ContentTypes { get; init; } = [];

    /// <summary>MIME type — feeds UTI inference and the exported-type tag specification.</summary>
    public string? MimeType { get; init; }

    /// <summary>
    /// When set, emits a UTExportedTypeDeclarations entry and the document type references
    /// only this UTI in LSItemContentTypes.
    /// </summary>
    public string? ExportedTypeIdentifier { get; init; }

    /// <summary>UTTypeConformsTo for the exported type (for example "public.data").</summary>
    public IReadOnlyList<string> ExportedTypeConformsTo { get; init; } = [];
}

/// <summary>One CFBundleURLTypes entry.</summary>
public sealed class MacAppUrlTypeConfiguration
{
    /// <summary>URL schemes without "://", for example "my-app".</summary>
    public IReadOnlyList<string> Schemes { get; init; } = [];

    /// <summary>CFBundleURLName; defaults to "&lt;bundle identifier&gt; &lt;first scheme&gt;".</summary>
    public string? Name { get; init; }

    /// <summary>CFBundleTypeRole; defaults to Editor.</summary>
    public MacAppTypeRole? Role { get; init; }
}
