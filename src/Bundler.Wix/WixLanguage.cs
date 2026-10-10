namespace DotNet.Bundler.Wix;

// A single supported MSI language entry. Cultures and code pages mirror the
// localization resources embedded in the bundled WixUIExtension.dll; product
// languages use the Windows LANGID for each culture.
public sealed record WixLanguageInfo(
    string Culture,
    int Lcid,
    int Codepage,
    string EnglishName)
{
    // File and install-location suffix; English stays unsuffixed so existing
    // en-US file names and identities keep working.
    public string Suffix => Culture.Equals("en-US", StringComparison.OrdinalIgnoreCase)
        ? ""
        : "-" + Culture.ToLowerInvariant();

    // Identity seed appended to the upgrade/product family. The zh-CN value
    // intentionally keeps the original "|zh-CN" token so shipped identities
    // remain stable.
    internal string FamilyToken => Culture.Equals("en-US", StringComparison.OrdinalIgnoreCase)
        ? ""
        : "|" + Culture;

    private static readonly WixLanguageInfo[] Entries =
    [
        new("ar-SA", 1025, 1256, "Arabic (Saudi Arabia)"),
        new("bg-BG", 1026, 1251, "Bulgarian (Bulgaria)"),
        new("ca-ES", 1027, 1252, "Catalan (Catalan)"),
        new("cs-CZ", 1029, 1250, "Czech (Czechia)"),
        new("da-DK", 1030, 1252, "Danish (Denmark)"),
        new("de-DE", 1031, 1252, "German (Germany)"),
        new("el-GR", 1032, 1253, "Greek (Greece)"),
        new("en-US", 1033, 1252, "English (United States)"),
        new("es-ES", 3082, 1252, "Spanish (Spain)"),
        new("et-EE", 1061, 1257, "Estonian (Estonia)"),
        new("fi-FI", 1035, 1252, "Finnish (Finland)"),
        new("fr-FR", 1036, 1252, "French (France)"),
        new("he-IL", 1037, 1255, "Hebrew (Israel)"),
        // hi-IN ships codepage 0 and kk-KZ translations do not fit their declared
        // code page; both fail light LGHT0311 on WiX 3.14.1, so they stay out.
        new("hr-HR", 1050, 1250, "Croatian (Croatia)"),
        new("hu-HU", 1038, 1250, "Hungarian (Hungary)"),
        new("it-IT", 1040, 1252, "Italian (Italy)"),
        new("ja-JP", 1041, 932, "Japanese (Japan)"),
        new("ko-KR", 1042, 949, "Korean (Korea)"),
        new("lt-LT", 1063, 1257, "Lithuanian (Lithuania)"),
        new("lv-LV", 1062, 1257, "Latvian (Latvia)"),
        new("nb-NO", 1044, 1252, "Norwegian Bokmål (Norway)"),
        new("nl-NL", 1043, 1252, "Dutch (Netherlands)"),
        new("pl-PL", 1045, 1250, "Polish (Poland)"),
        new("pt-BR", 1046, 1252, "Portuguese (Brazil)"),
        new("pt-PT", 2070, 1252, "Portuguese (Portugal)"),
        new("ro-RO", 1048, 1250, "Romanian (Romania)"),
        new("ru-RU", 1049, 1251, "Russian (Russia)"),
        new("sk-SK", 1051, 1250, "Slovak (Slovakia)"),
        new("sl-SI", 1060, 1250, "Slovenian (Slovenia)"),
        new("sq-SQ", 1052, 1252, "Albanian"),
        new("sr-Latn-CS", 2074, 1250, "Serbian (Latin)"),
        new("sv-SE", 1053, 1252, "Swedish (Sweden)"),
        new("th-TH", 1054, 874, "Thai (Thailand)"),
        new("tr-TR", 1055, 1254, "Turkish (Türkiye)"),
        new("uk-UA", 1058, 1251, "Ukrainian (Ukraine)"),
        new("zh-CN", 2052, 936, "Chinese (Simplified)"),
        new("zh-HK", 3076, 950, "Chinese (Traditional, Hong Kong SAR)"),
        new("zh-TW", 1028, 950, "Chinese (Traditional, Taiwan)"),
    ];

    public static IReadOnlyList<WixLanguageInfo> Supported => Entries;

    public static WixLanguageInfo Resolve(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
            throw new ArgumentException("An MSI language culture name is required.", nameof(culture));
        var match = Entries.FirstOrDefault(entry =>
            entry.Culture.Equals(culture.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ArgumentException(
                $"MSI language '{culture}' is not supported; the bundled WiX UI provides " +
                string.Join(", ", Entries.Select(entry => entry.Culture)) + ".",
                nameof(culture));
        }
        return match;
    }
}
