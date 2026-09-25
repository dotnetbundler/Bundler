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
    // 交互安装界面允许在允许根内选择安装目录；静默 INSTALLFOLDER 始终可用并经受同一范围校验。
    public bool InstallDirectorySelection { get; init; }
    // WiX UI 横幅位图，必须为 493x58 .bmp。
    public string? BannerBitmap { get; init; }
    // WiX UI 对话框位图，必须为 503x314 .bmp。
    public string? DialogBitmap { get; init; }
    public bool AddToPath { get; init; }
    public bool UninstallShortcut { get; init; }
    // 仅在交互安装完成页提供勾选；静默、被动、升级、修复与提权上下文不启动应用。
    public bool LaunchAfterInstall { get; init; }

    internal int ProductLanguage => Language == WixPackageLanguage.ChineseSimplified ? 2052 : 1033;
    internal string Culture => Language == WixPackageLanguage.ChineseSimplified ? "zh-cn" : "en-us";
    internal int EffectiveCodepage => Codepage == 0
        ? (Language == WixPackageLanguage.ChineseSimplified ? 936 : 1252)
        : Codepage;
    internal string LanguageSuffix => Language == WixPackageLanguage.ChineseSimplified ? "-zh-cn" : "";
}
