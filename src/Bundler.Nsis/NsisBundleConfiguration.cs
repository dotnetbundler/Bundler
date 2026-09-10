namespace DotNet.Bundler.Nsis;

public sealed class NsisBundleConfiguration
{
    public NsisInstallMode InstallMode { get; init; } = NsisInstallMode.CurrentUser;
    public string? InstallerIcon { get; init; }
    public string? UninstallerIcon { get; init; }
    public string? HeaderImage { get; init; }
    public string? SidebarImage { get; init; }
    public string? UninstallerHeaderImage { get; init; }
    public string? InstallerHooks { get; init; }
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
