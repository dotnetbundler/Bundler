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
}
