using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotNet.Bundler.Wix;

// Regular-mode caller fragments are declarative MSI data only: a fixed core
// schema whitelist plus a caller-declared id prefix. Anything else (custom
// actions, sequences, UI, extension namespaces) requires expert mode.
internal static class WixExtensionValidator
{
    internal static readonly XNamespace WixCore = "http://schemas.microsoft.com/wix/2006/wi";

    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "Fragment", "Component", "ComponentGroup", "Directory", "DirectoryRef",
        "File", "RegistryKey", "RegistryValue", "RegistrySearch", "Environment",
        "Condition", "Shortcut", "Property", "CreateFolder", "RemoveFile",
        "RemoveFolder", "ProgId", "Extension", "Verb", "MIME", "Icon",
        "CopyFile", "IniFile", "ODBCDataSource", "ODBCSourceAttribute",
        "ODBCTranslator", "RegistrySearchGroup", "TypeLib", "AppId",
        "Class", "ServiceControl", "ServiceInstall", "ServiceDependency",
        "ShortcutProperty", "NativeImage", "WebAddress"
    };

    private static readonly Regex IdPattern =
        new(@"^[\w\.!@\$%\(\)\-\+]+$", RegexOptions.Compiled);

    private static readonly Regex ExtensionNamespace =
        new("\"http://schemas\\.microsoft\\.com/wix/(\\w+)\"", RegexOptions.Compiled);

    // Returns the caller string content for downstream extension detection.
    internal static string ValidateFragment(string path, string idPrefix)
    {
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            !Path.GetExtension(path).Equals(".wxs", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"MSI extension fragment must be a real .wxs file: {path}");
        }
        var content = File.ReadAllText(path);
        XDocument document;
        try
        {
            document = XDocument.Parse(content);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new ArgumentException($"MSI extension fragment is not valid XML: {path}", exception);
        }
        var root = document.Root;
        if (root is null || root.Name != WixCore + "Wix")
        {
            throw new ArgumentException(
                $"MSI extension fragment must be a <Wix> document in the core schema: {path}");
        }
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element == root) continue;
            if (element.Name.Namespace != WixCore && element.Name.Namespace != string.Empty)
            {
                throw new ArgumentException(
                    $"MSI extension fragment element '{element.Name.LocalName}' uses a non-core " +
                    $"namespace '{element.Name.Namespace}'; WiX extensions require expert mode: {path}");
            }
            if (!AllowedElements.Contains(element.Name.LocalName))
            {
                throw new ArgumentException(
                    $"MSI extension fragment element '{element.Name.LocalName}' is not in the regular-mode " +
                    $"whitelist (custom actions, sequences, UI, Feature and Product level elements are " +
                    $"excluded); use expert mode for unrestricted WiX input: {path}");
            }
        }
        foreach (var attribute in root.DescendantsAndSelf().SelectMany(element => element.Attributes()))
        {
            if (attribute.Name != "Id") continue;
            if (!attribute.Value.StartsWith(idPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"MSI extension fragment id '{attribute.Value}' does not start with the declared " +
                    $"prefix '{idPrefix}': {path}");
            }
        }
        return content;
    }

    // Ref lists are caller-declared attachment points injected into the managed
    // Feature; every id must carry the caller's prefix.
    internal static void ValidateRef(string id, string idPrefix, string kind)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
        {
            throw new ArgumentException($"MSI extension {kind} ref id '{id}' is not a valid WiX identifier.");
        }
        if (!id.StartsWith(idPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"MSI extension {kind} ref id '{id}' does not start with the declared prefix '{idPrefix}'.");
        }
    }

    internal static string ValidateIdPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !IdPattern.IsMatch(prefix) ||
            !prefix.EndsWith(".", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "MSI extension id prefix must be a WiX-legal identifier ending in '.', " +
                $"for example 'Acme.': '{prefix}'");
        }
        var reserved = new[] { "Cmp", "Fil", "Rem", "WixUI_", "Wix", "Bundler" };
        if (reserved.Any(name => prefix.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"MSI extension id prefix '{prefix}' collides with a reserved Bundler/WiX prefix.");
        }
        return prefix;
    }

    // Tauri-compatible behavior: detect xmlns="http://schemas.microsoft.com/wix/<Name>"
    // in caller content and expose the matching bundled Wix<Name>.dll for -ext.
    internal static IReadOnlyList<string> DetectExtensionDlls(string content, string toolsetDirectory)
    {
        return ExtensionNamespace.Matches(content).Cast<Match>()
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !name.Equals("wi", StringComparison.OrdinalIgnoreCase))
            .Select(name => Path.Combine(toolsetDirectory, "Wix" + name + ".dll"))
            .Where(File.Exists)
            .ToArray();
    }
}
