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

/// <summary>Where a .pkg may be installed (distribution-package <c>&lt;domains&gt;</c> declaration).</summary>
public enum MacPkgInstallDomain
{
    /// <summary>Install into the system domain (default; requires administrator rights).</summary>
    System,

    /// <summary>
    /// Install into the user's home without elevation (payload lands under
    /// <c>~/</c>-relative equivalents, e.g. <c>~/Applications</c>).
    /// </summary>
    CurrentUserHome,
}

/// <summary>
/// Settings for the macOS .pkg backend.
/// With no distribution settings the backend emits a plain component package via
/// <c>pkgbuild</c>; configuring any distribution feature (title, welcome/license/
/// conclusion page, or a non-default install domain) upgrades the output to a
/// <c>productbuild</c> distribution package wrapping the component package.
/// </summary>
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

    /// <summary>
    /// Installer window title for a distribution package; defaults to the product name.
    /// Setting it upgrades the output to a distribution package.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Welcome page shown before installation (.html/.rtf/.rtfd/.txt);
    /// setting it upgrades the output to a distribution package.
    /// </summary>
    public string? WelcomeFile { get; init; }

    /// <summary>
    /// Conclusion page shown after installation (.html/.rtf/.rtfd/.txt);
    /// setting it upgrades the output to a distribution package.
    /// </summary>
    public string? ConclusionFile { get; init; }

    /// <summary>
    /// Install domain declaration for a distribution package; defaults to
    /// <see cref="MacPkgInstallDomain.System"/>. <c>CurrentUserHome</c> allows
    /// installing without administrator rights; setting it upgrades the output.
    /// </summary>
    public MacPkgInstallDomain Domain { get; init; } = MacPkgInstallDomain.System;
}
