namespace DotNet.Bundler.Wix;

public enum WixInstallScope
{
    CurrentUser,
    PerMachine
}

public enum WixPackageLanguage
{
    English,
    ChineseSimplified
}

public sealed class WixBundleConfiguration
{
    public WixInstallScope InstallScope { get; init; } = WixInstallScope.CurrentUser;
    public string? UpgradeCode { get; init; }
    public string? MsiVersion { get; init; }
    public bool AllowDowngrades { get; init; }
    public WixPackageLanguage Language { get; init; } = WixPackageLanguage.English;
    // Zero selects the ANSI code page associated with Language.
    public int Codepage { get; init; }
    public bool StartMenuShortcut { get; init; }
    public bool DesktopShortcut { get; init; }

    internal int ProductLanguage => Language == WixPackageLanguage.ChineseSimplified ? 2052 : 1033;
    internal string Culture => Language == WixPackageLanguage.ChineseSimplified ? "zh-cn" : "en-us";
    internal int EffectiveCodepage => Codepage == 0
        ? (Language == WixPackageLanguage.ChineseSimplified ? 936 : 1252)
        : Codepage;
    internal string LanguageSuffix => Language == WixPackageLanguage.ChineseSimplified ? "-zh-cn" : "";
}
