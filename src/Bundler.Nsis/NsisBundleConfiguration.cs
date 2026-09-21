namespace DotNet.Bundler.Nsis;

public sealed class NsisBundleConfiguration
{
    public NsisInstallMode InstallMode { get; init; } = NsisInstallMode.CurrentUser;
    public NsisCompression Compression { get; init; } = NsisCompression.Lzma;
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
    public bool AllowDowngrades { get; init; }
    public NsisShortcutConfiguration Shortcuts { get; init; } = new();
    public IReadOnlyList<string> LegacyMsiProductCodes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LegacyMsiUpgradeCodes { get; init; } = Array.Empty<string>();
}

public sealed class NsisShortcutConfiguration
{
    public bool Desktop { get; init; } = true;
    public bool StartMenu { get; init; } = true;
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Icon { get; init; }
    public string? AppUserModelId { get; init; }
    public string? StartMenuFolder { get; init; }
    public IReadOnlyList<string> LegacyProductNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LegacyMainExecutables { get; init; } = Array.Empty<string>();
}

public enum NsisInstallMode
{
    CurrentUser,
    PerMachine,
    Both
}

public enum NsisCompression
{
    Lzma,
    Zlib,
    Bzip2,
    None
}
