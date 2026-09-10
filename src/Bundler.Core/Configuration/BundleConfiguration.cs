using Bundler.Core.Models;

namespace Bundler.Core.Configuration;

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
    public NsisBundleConfiguration Nsis { get; init; } = new();
    public IReadOnlyList<BundleTargetConfiguration> Targets { get; init; } = [];
}

public sealed class BundleResourceConfiguration
{
    public string Source { get; init; } = "";
    public string TargetPath { get; init; } = "";
}

public sealed class NsisBundleConfiguration
{
    public NsisInstallMode InstallMode { get; init; } = NsisInstallMode.CurrentUser;
    public string? InstallerIcon { get; init; }
    public string? UninstallerIcon { get; init; }
    public string? HeaderImage { get; init; }
    public string? SidebarImage { get; init; }
    public string? UninstallerHeaderImage { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = ["English"];
    public IReadOnlyDictionary<string, string> CustomLanguageFiles { get; init; } =
        new Dictionary<string, string>();
    public bool DisplayLanguageSelector { get; init; }
}

public enum NsisInstallMode
{
    CurrentUser,
    PerMachine,
    Both
}

public sealed class BundleTargetConfiguration
{
    public string RuntimeIdentifier { get; init; } = "";
    public string InputDirectory { get; init; } = "";
    public string? MainExecutable { get; init; }
    public IReadOnlyList<PackageFormat> Formats { get; init; } = [];
}
