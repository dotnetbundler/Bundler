using Bundler.Core.Models;

namespace Bundler.Core.Configuration;

public sealed class BundleConfiguration
{
    public string ProductName { get; init; } = "";
    public string Identifier { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string OutputDirectory { get; init; } = "artifacts";
    public IReadOnlyList<string> Icons { get; init; } = [];
    public IReadOnlyList<string> Resources { get; init; } = [];
    public NsisBundleConfiguration Nsis { get; init; } = new();
    public IReadOnlyList<BundleTargetConfiguration> Targets { get; init; } = [];
}

public sealed class NsisBundleConfiguration
{
    public IReadOnlyList<string> Languages { get; init; } = ["English"];
    public IReadOnlyDictionary<string, string> CustomLanguageFiles { get; init; } =
        new Dictionary<string, string>();
    public bool DisplayLanguageSelector { get; init; }
}

public sealed class BundleTargetConfiguration
{
    public string RuntimeIdentifier { get; init; } = "";
    public string InputDirectory { get; init; } = "";
    public string? MainExecutable { get; init; }
    public IReadOnlyList<PackageFormat> Formats { get; init; } = [];
}
