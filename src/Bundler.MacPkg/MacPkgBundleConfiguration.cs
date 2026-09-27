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

    /// <summary>
    /// Expert knob: directory handed to <c>pkgbuild --scripts</c> verbatim. When it contains
    /// <c>preinstall</c>/<c>postinstall</c> they run as the package's top-level scripts with
    /// full installer privileges — the caller owns their contents and responsibility.
    /// </summary>
    public string? ScriptsDirectory { get; init; }

    /// <summary>
    /// Optional signature settings. There is no ad-hoc signing for .pkg: either a real
    /// "Developer ID Installer" identity (or an imported certificate) signs the package,
    /// or it is left unsigned.
    /// </summary>
    public MacPkgSigningConfiguration Signing { get; init; } = new();
}

/// <summary>Signature and notarization settings for the macOS .pkg backend.</summary>
public sealed class MacPkgSigningConfiguration
{
    /// <summary>
    /// Developer ID Installer identity already present in a keychain
    /// (e.g. "Developer ID Installer: &lt;team&gt;"). Mutually exclusive with
    /// <see cref="TemporaryCertificatePath"/>. There is no ad-hoc equivalent ("-" is rejected).
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// Path to a .p12/.pfx certificate imported into a throwaway keychain for this build
    /// (the keychain is deleted afterwards). Mutually exclusive with <see cref="Identity"/>.
    /// </summary>
    public string? TemporaryCertificatePath { get; init; }

    /// <summary>Password of the temporary certificate (may be empty).</summary>
    public string? TemporaryCertificatePassword { get; init; }

    /// <summary>
    /// Opt-in notarization of the .pkg via <c>xcrun notarytool</c> + <c>xcrun stapler</c>.
    /// Never runs by default and requires a signing identity. Credentials resolve from the
    /// properties below first, then the APPLE_* environment variables used by other bundlers.
    /// </summary>
    public bool Notarize { get; init; }

    /// <summary>Submit with --wait (default true). When false, stapling is skipped.</summary>
    public bool NotaryWait { get; init; } = true;

    /// <summary>Skip <c>xcrun stapler staple</c> after a successful (waited) submission.</summary>
    public bool SkipStapling { get; init; }

    /// <summary>notarytool --keychain-profile; falls back to the APPLE_PROFILE env var.</summary>
    public string? KeychainProfile { get; init; }

    /// <summary>notarytool --apple-id; falls back to APPLE_ID.</summary>
    public string? AppleId { get; init; }

    /// <summary>notarytool --password (app-specific password); falls back to APPLE_PASSWORD.</summary>
    public string? ApplePassword { get; init; }

    /// <summary>notarytool --team-id; falls back to APPLE_TEAM_ID.</summary>
    public string? AppleTeamId { get; init; }

    /// <summary>notarytool --key (AuthKey_*.p8 path); falls back to APPLE_API_KEY_PATH.</summary>
    public string? ApiKeyPath { get; init; }

    /// <summary>notarytool --key-id; falls back to APPLE_API_KEY.</summary>
    public string? ApiKeyId { get; init; }

    /// <summary>notarytool --issuer; falls back to APPLE_API_ISSUER.</summary>
    public string? ApiIssuer { get; init; }
}
