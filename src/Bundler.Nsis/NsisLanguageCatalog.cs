namespace DotNet.Bundler.Nsis;

internal sealed record NsisLanguageDefinition(
    string Name,
    string ConstantName,
    string? CompilerName = null)
{
    internal string NsisName => CompilerName ?? Name;
}

internal static class NsisLanguageCatalog
{
    internal static IReadOnlyList<NsisLanguageDefinition> Definitions { get; } =
    [
        new("Arabic", "ARABIC"),
        new("Bulgarian", "BULGARIAN"),
        new("Dutch", "DUTCH"),
        new("English", "ENGLISH"),
        new("French", "FRENCH"),
        new("German", "GERMAN"),
        new("Italian", "ITALIAN"),
        new("Japanese", "JAPANESE"),
        new("Korean", "KOREAN"),
        new("Norwegian", "NORWEGIAN"),
        new("Persian", "FARSI", "Farsi"),
        new("Portuguese", "PORTUGUESE"),
        new("PortugueseBR", "PORTUGUESEBR"),
        new("Russian", "RUSSIAN"),
        new("SimpChinese", "SIMPCHINESE"),
        new("Spanish", "SPANISH"),
        new("SpanishInternational", "SPANISHINTERNATIONAL"),
        new("Swedish", "SWEDISH"),
        new("TradChinese", "TRADCHINESE"),
        new("Turkish", "TURKISH"),
        new("Ukrainian", "UKRAINIAN"),
        new("Vietnamese", "VIETNAMESE")
    ];

    internal static NsisLanguageDefinition Resolve(string name) =>
        Definitions.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
        throw new InvalidOperationException(
            $"Unsupported NSIS language '{name}'. Supported languages: {string.Join(", ", Definitions.Select(item => item.Name))}.");
}
