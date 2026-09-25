using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace DotNet.Bundler.Wix;

// Bundler-owned installer strings. The .wxs documents reference them through
// !(loc.BundlerXxx) ids; this class emits a merged per-language .wxl with the
// caller's overrides winning over the defaults below. zh-CN keeps Chinese
// defaults; every other culture defaults to English.
internal static class WixLocale
{
    private static readonly XNamespace Wxl = "http://schemas.microsoft.com/wix/2006/localization";

    private static readonly (string Id, string English, string Chinese)[] Entries =
    [
        ("BundlerDowngradeErrorMessage",
            "A newer version of [ProductName] is already installed.",
            "已安装较新版本的 [ProductName]。"),
        ("BundlerVersionConflictMessage",
            "A different MSI package already uses this product version. Use a new product version.",
            "此产品版本已由另一个 MSI 包使用。请使用新的产品版本。"),
        ("BundlerInstallDirScopeError",
            "The installation folder must be a subfolder inside [{0}].",
            "安装文件夹必须是 [{0}] 内的子文件夹。"),
        ("BundlerLaunchCheckboxText",
            "Launch [ProductName]",
            "启动 [ProductName]"),
        ("BundlerShortcutsFeature", "Shortcuts", "快捷方式"),
        ("BundlerPathFeature", "Add to PATH", "加入 PATH"),
        ("BundlerUninstallShortcutFeature", "Uninstall shortcut", "卸载快捷方式"),
        ("BundlerUninstallShortcutPrefix", "Uninstall ", "卸载 "),
    ];

    internal static IReadOnlyList<string> Ids => Entries.Select(entry => entry.Id).ToArray();

    internal static Dictionary<string, string> Defaults(WixLanguageInfo language, string scopeRoot)
    {
        var chinese = language.Culture.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
        return Entries.ToDictionary(
            entry => entry.Id,
            entry => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                chinese ? entry.Chinese : entry.English, scopeRoot),
            StringComparer.OrdinalIgnoreCase);
    }

    // Uses the Windows ANSI code page directly so the backend package stays
    // self-contained (no System.Text.Encoding.CodePages dependency).
    internal static IReadOnlyList<string> UnencodableIds(
        IReadOnlyDictionary<string, string> strings, int codepage)
    {
        if (codepage == 0)
        {
            return Array.Empty<string>();
        }
        if (!IsSupportedCodePage(codepage))
        {
            throw new ArgumentException($"Code page {codepage} is not a valid Windows code page.");
        }
        return strings.Where(pair => !CanEncode(pair.Value, codepage))
            .Select(pair => pair.Key).ToArray();
    }

    private static bool CanEncode(string value, int codepage)
    {
        // WC_ERR_INVALID_CHARS is not honored by every ANSI code page, so rely
        // on lpUsedDefaultChar instead: the system sets it when any character
        // had to be replaced by the page's default character.
        var written = WideCharToMultiByte((uint)codepage, 0,
            value, value.Length, IntPtr.Zero, 0, IntPtr.Zero, out var usedDefaultChar);
        return written >= 0 && usedDefaultChar == 0;
    }

    private static bool IsSupportedCodePage(int codepage)
    {
        try
        {
            Encoding.GetEncoding(codepage);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return IsValidCodePage((uint)codepage);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int WideCharToMultiByte(uint codePage, uint flags,
        [MarshalAs(UnmanagedType.LPWStr)]
        string wideString, int wideLength, IntPtr multiByteString, int multiByteLength,
        IntPtr defaultChar, out int usedDefaultChar);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidCodePage(uint codePage);

    // Validates a caller .wxl for the given language and returns its String id
    // map. The file must exist, not be a reparse point, carry a matching
    // Culture attribute, and encode every string in the effective code page.
    internal static IReadOnlyDictionary<string, string> ReadCallerStrings(
        string path, WixLanguageInfo language, int codepage)
    {
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            !Path.GetExtension(path).Equals(".wxl", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"MSI locale file for '{language.Culture}' must be a real .wxl file: {path}");
        }
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new ArgumentException($"MSI locale file is not valid XML: {path}", exception);
        }
        var root = document.Root;
        if (root is null || root.Name != Wxl + "WixLocalization")
        {
            throw new ArgumentException(
                "MSI locale file must declare a WixLocalization root in the " +
                "http://schemas.microsoft.com/wix/2006/localization namespace: " + path);
        }
        var culture = (string?)root.Attribute("Culture");
        if (culture is null || !culture.Equals(language.Culture, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"MSI locale file Culture '{culture}' does not match language '{language.Culture}': {path}");
        }
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in root.Elements().Where(e => e.Name.LocalName == "String"))
        {
            var id = (string?)element.Attribute("Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException($"MSI locale file contains a String without an Id: {path}");
            }
            strings[id!] = element.Value;
        }
        var invalid = UnencodableIds(strings, codepage);
        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                $"MSI locale file strings cannot be encoded in code page {codepage}: " +
                string.Join(", ", invalid));
        }
        return strings;
    }

    internal static string WriteMerged(string path, WixLanguageInfo language, int codepage,
        string scopeRoot, IReadOnlyDictionary<string, string> callerStrings)
    {
        var merged = Defaults(language, scopeRoot);
        foreach (var pair in callerStrings)
        {
            merged[pair.Key] = pair.Value;
        }
        var invalid = UnencodableIds(merged, codepage);
        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                $"MSI strings for '{language.Culture}' cannot be encoded in code page {codepage}: " +
                string.Join(", ", invalid));
        }
        var root = new XElement(Wxl + "WixLocalization",
            new XAttribute("Culture", language.Culture),
            new XAttribute("Codepage", codepage));
        foreach (var pair in merged.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            root.Add(new XElement(Wxl + "String", new XAttribute("Id", pair.Key), pair.Value));
        }
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
        return path;
    }
}
