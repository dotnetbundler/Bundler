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
    /// </summary>
    public string? IconName { get; init; }

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
