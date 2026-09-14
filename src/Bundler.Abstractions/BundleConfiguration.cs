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
    public IReadOnlyList<string> Icons { get; init; } = [];
    public IReadOnlyList<BundleResourceConfiguration> Resources { get; init; } = [];
    public IReadOnlyList<BundleFileAssociationConfiguration> FileAssociations { get; init; } = [];
    public IReadOnlyList<BundleUrlProtocolConfiguration> UrlProtocols { get; init; } = [];
    public IReadOnlyList<BundleTargetConfiguration> Targets { get; init; } = [];
}

public sealed class BundleResourceConfiguration
{
    public string Source { get; init; } = "";
    public string TargetPath { get; init; } = "";
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

public sealed class BundleTargetConfiguration
{
    public string RuntimeIdentifier { get; init; } = "";
    public string InputDirectory { get; init; } = "";
    public string? MainExecutable { get; init; }
    public IReadOnlyList<PackageFormat> Formats { get; init; } = [];
}
