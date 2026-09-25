namespace DotNet.Bundler.Wix;

public enum WixInstallScope
{
    CurrentUser,
    PerMachine
}

public sealed class WixBundleConfiguration
{
    public WixInstallScope InstallScope { get; init; } = WixInstallScope.CurrentUser;
    public string? UpgradeCode { get; init; }
    public string? MsiVersion { get; init; }
    public bool AllowDowngrades { get; init; }
    // Cultures of the Windows Installer UI, e.g. "en-US" or "ja-JP". Each entry
    // produces an isolated single-language MSI with its own identity and
    // "-<culture>" output suffix; "en-US" keeps the unsuffixed name.
    public IReadOnlyList<string> Languages { get; init; } = ["en-US"];
    // Optional caller .wxl files keyed by culture. Each file must declare the
    // matching Culture attribute and encode in the language code page; its
    // String ids override the bundled WiX UI and Bundler defaults.
    public IReadOnlyDictionary<string, string> LocaleFiles { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    // Zero selects the ANSI code page declared by each language. A non-zero
    // value must match every requested language's declared code page.
    public int Codepage { get; init; }
    // Pass -fips to candle so WiX uses FIPS-compliant algorithms on the build
    // host. Does not assert that the resulting MSI is FIPS certified.
    public bool FipsCompliant { get; init; }
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

    internal IReadOnlyList<WixLanguageInfo> ResolveLanguages()
    {
        if (Languages.Count == 0)
        {
            throw new ArgumentException("At least one MSI language is required.", nameof(Languages));
        }
        var resolved = Languages.Select(WixLanguageInfo.Resolve).ToArray();
        if (resolved.Select(language => language.Culture)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != resolved.Length)
        {
            throw new ArgumentException("MSI languages must not contain duplicates.", nameof(Languages));
        }
        if (Codepage != 0)
        {
            // 覆盖码页用于产品名等非 ASCII 负载字符串（例如 en-US 界面 + 中文产品名），
            // 每个语言的自有串能否编码在生成时另行校验。
            foreach (var language in resolved)
            {
                var defaults = WixLocale.Defaults(language, "LocalAppDataFolder");
                var invalid = WixLocale.UnencodableIds(defaults, Codepage);
                if (invalid.Count > 0)
                {
                    throw new ArgumentException(
                        $"MSI strings for '{language.Culture}' cannot be encoded in code page {Codepage}: " +
                        string.Join(", ", invalid));
                }
            }
        }
        var unknownLocale = LocaleFiles.Keys
            .FirstOrDefault(key => resolved.All(language =>
                !language.Culture.Equals(key, StringComparison.OrdinalIgnoreCase)));
        if (unknownLocale is not null)
        {
            throw new ArgumentException(
                $"Locale file key '{unknownLocale}' does not match a requested MSI language.");
        }
        return resolved;
    }

    internal int EffectiveCodepage(WixLanguageInfo language) =>
        Codepage == 0 ? language.Codepage : Codepage;
}
