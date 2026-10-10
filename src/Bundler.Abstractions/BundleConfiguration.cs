namespace DotNet.Bundler;

public sealed class BundleConfiguration
{
    public string ProductName { get; init; } = "";
    public string Identifier { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? Homepage { get; init; }
    public string? Copyright { get; init; }
    public string? LicenseFile { get; init; }
    public string OutputDirectory { get; init; } = "artifacts";
    /// <summary>输出布局：flat（默认，产物全落根目录）| byFormat（按格式分一层子目录）。</summary>
    public OutputLayout OutputLayout { get; init; } = OutputLayout.Flat;
    public IReadOnlyList<string> Icons { get; init; } = [];
    public IReadOnlyList<BundleResourceConfiguration> Resources { get; init; } = [];
    public IReadOnlyList<BundleFileAssociationConfiguration> FileAssociations { get; init; } = [];
    public IReadOnlyList<BundleUrlProtocolConfiguration> UrlProtocols { get; init; } = [];
    public IReadOnlyList<BundleTargetConfiguration> Targets { get; init; } = [];
    public UpdateBundleConfiguration? Update { get; init; }
}

public sealed class UpdateBundleConfiguration
{
    public string FeedUrl { get; init; } = "";
    public string Channel { get; init; } = "latest";
    public string? SigningKeyFile { get; init; }
    public string? PublicKey { get; init; }
    public string? Notes { get; init; }
    /// <summary>引导件工具目录（&lt;target&gt;/ 与 posix/ 子目录结构）；空时入口按程序集旁 conventions 解析。</summary>
    public string? BootstrapperDirectory { get; init; }
}

public sealed class BundleResourceConfiguration
{
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
}

/// <summary>
/// Secret-key-file signing shared by backends that sign with a single key file
/// (rpm/AppImage/apk). Apple backends carry their own signing shapes.
/// </summary>
public sealed class KeyFileSigningConfiguration
{
    /// <summary>
    /// Path to the secret key file read at build time; its bytes are never logged.
    /// Unset produces an unsigned artifact identical to a build with no signing configured.
    /// </summary>
    public string? KeyFile { get; init; }

    /// <summary>
    /// Passphrase for <see cref="KeyFile"/>. Supplying one without a key file is a
    /// configuration error. Prefer feeding the value from a secret store — it is a
    /// secret, do not commit it.
    /// </summary>
    public string? Passphrase { get; init; }
}

public sealed class BundleFileAssociationConfiguration
{
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? MimeType { get; init; }
}

public sealed class BundleUrlProtocolConfiguration
{
    public IReadOnlyList<string> Schemes { get; init; } = [];
    public string? Name { get; init; }
}

/// <summary>输出布局模式。</summary>
public enum OutputLayout
{
    /// <summary>扁平——全部产物直落输出根目录，靠文件名里的平台/架构段区分。</summary>
    Flat,
    /// <summary>按格式分子目录 out/&lt;format&gt;/。</summary>
    ByFormat
}

public sealed class BundleTargetConfiguration
{
    /// <summary>canonical 目标 <c>{os}[-{libc}]-{arch}</c>，如 linux-musl-aarch64。</summary>
    public string Target { get; init; } = "";
    public string InputDirectory { get; init; } = "";
    public string? MainExecutable { get; init; }
    public IReadOnlyList<string> SigningFiles { get; init; } = [];
    public IReadOnlyList<PackageFormat> Formats { get; init; } = [];
}
