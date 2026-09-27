namespace DotNet.Bundler.MacDmg;

/// <summary>Read-only DMG compression format passed to <c>hdiutil convert -format</c>.</summary>
public enum MacDmgCompression
{
    /// <summary>zlib; mountable on OS X 10.1+ — pick this for prehistoric mount hosts.</summary>
    Udzo,

    /// <summary>LZFSE (default); smaller and faster, mountable on macOS 10.12+.</summary>
    Ulmo,

    /// <summary>bzip2; mountable on OS X 10.4+.</summary>
    Udbz
}

/// <summary>Settings for the macOS .dmg backend (MAC-DMG-1 minimal image scope).</summary>
public sealed class MacDmgBundleConfiguration
{
    /// <summary>Compression applied by <c>hdiutil convert</c>; defaults to <see cref="MacDmgCompression.Ulmo"/>.</summary>
    public MacDmgCompression Compression { get; init; } = MacDmgCompression.Ulmo;

    /// <summary>Volume name shown by the mounted image; defaults to the sanitized product name.</summary>
    public string? VolumeName { get; init; }

    /// <summary>Skips the Finder window-layout pass entirely (still produces a mountable image).</summary>
    public bool SkipWindowLayout { get; init; }

    /// <summary>Finder window origin X; defaults to the upstream value 200.</summary>
    public int WindowX { get; init; } = 200;

    /// <summary>Finder window origin Y; defaults to the upstream value 120.</summary>
    public int WindowY { get; init; } = 120;

    /// <summary>Finder window width; defaults to the upstream value 660.</summary>
    public int WindowWidth { get; init; } = 660;

    /// <summary>Finder window height; defaults to the upstream value 400.</summary>
    public int WindowHeight { get; init; } = 400;

    /// <summary>Horizontal position of the .app icon inside the window; upstream default 180.</summary>
    public int AppIconX { get; init; } = 180;

    /// <summary>Vertical position of the .app icon inside the window; upstream default 170.</summary>
    public int AppIconY { get; init; } = 170;

    /// <summary>Horizontal position of the /Applications drop icon; upstream default 480.</summary>
    public int ApplicationsIconX { get; init; } = 480;

    /// <summary>Vertical position of the /Applications drop icon; upstream default 170.</summary>
    public int ApplicationsIconY { get; init; } = 170;

    /// <summary>Icon size shown in the Finder window; upstream default 128.</summary>
    public int IconSize { get; init; } = 128;

    /// <summary>Optional window background image (png/jpg/gif) copied into the hidden .background folder.</summary>
    public string? BackgroundFile { get; init; }

    /// <summary>Optional volume icon file (.icns) written to .VolumeIcon.icns with the custom-icon flag.</summary>
    public string? VolumeIconFile { get; init; }

    /// <summary>Optional .dmg body signing; null leaves the image unsigned.</summary>
    public MacDmgSigningConfiguration? Signing { get; init; }
}

/// <summary>
/// .dmg body codesign settings. Mirrors the applicable subset of the .app signing surface:
/// identity ("-" = ad-hoc) or a temporary keychain certificate, mutually exclusive.
/// Hardened runtime / entitlements / notarization do not apply to the image container.
/// </summary>
public sealed class MacDmgSigningConfiguration
{
    /// <summary>codesign identity; "-" produces an ad-hoc signature (self-verify only).</summary>
    public string? Identity { get; init; }

    /// <summary>PKCS#12 certificate imported into a throwaway keychain for the build.</summary>
    public string? TemporaryCertificatePath { get; init; }

    /// <summary>Password for <see cref="TemporaryCertificatePath"/>.</summary>
    public string? TemporaryCertificatePassword { get; init; }
}
