using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DotNet.Bundler.MacApp;

/// <summary>
/// Typed plist model. Values are restricted to: string, bool, long, double, byte[],
/// DateTimeOffset, List&lt;object&gt; and Dictionary&lt;string, object&gt;.
/// </summary>
internal static class InfoPlist
{
    internal static void Write(string path, IReadOnlyDictionary<string, object> values)
    {
        var dict = new XElement("dict", ValuesToElements(values));
        var plist = new XElement("plist", new XAttribute("version", "1.0"), dict);

        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        builder.Append(plist.ToString());
        builder.Append('\n');
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    internal static Dictionary<string, object> ReadDictionary(string path, string displayName = "Info.plist")
    {
        XDocument document;
        try
        {
            using (var reader = XmlReader.Create(path, LenientReaderSettings()))
            {
                document = XDocument.Load(reader);
            }
        }
        catch (Exception exception) when (exception is XmlException or IOException)
        {
            throw new InvalidDataException($"{displayName}: not a readable XML plist ({exception.Message}).");
        }
        return ParseDocument(document, displayName);
    }

    /// <summary>Accepts a full <c>&lt;plist&gt;</c> document or a bare <c>&lt;dict&gt;</c> root.</summary>
    internal static Dictionary<string, object> ParseXml(string xml, string displayName)
    {
        XDocument document;
        try
        {
            using (var reader = XmlReader.Create(new StringReader(xml), LenientReaderSettings()))
            {
                document = XDocument.Load(reader);
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"{displayName}: invalid plist XML ({exception.Message}).");
        }
        return ParseDocument(document, displayName);
    }

    /// <summary>String view used by callers and tests: bool renders "true"/"false", numbers invariant.</summary>
    internal static IReadOnlyDictionary<string, string> ReadStringValues(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in ReadDictionary(path))
        {
            if (TryToString(pair.Value, out var rendered))
            {
                values[pair.Key] = rendered;
            }
        }
        return values;
    }

    internal static bool TryToString(object value, out string rendered)
    {
        switch (value)
        {
            case string text:
                rendered = text;
                return true;
            case bool flag:
                rendered = flag ? "true" : "false";
                return true;
            case long integer:
                rendered = integer.ToString(CultureInfo.InvariantCulture);
                return true;
            case double real:
                rendered = real.ToString("R", CultureInfo.InvariantCulture);
                return true;
            default:
                rendered = "";
                return false;
        }
    }

    private static Dictionary<string, object> ParseDocument(XDocument document, string displayName)
    {
        var root = document.Root;
        var dict = root?.Name.LocalName == "plist"
            ? root.Elements().FirstOrDefault(element => element.Name.LocalName == "dict")
            : root;
        if (dict is null || dict.Name.LocalName != "dict")
        {
            throw new InvalidDataException($"{displayName}: a plist <dict> root is required.");
        }
        return (Dictionary<string, object>)ParseValue(dict, displayName);
    }

    private static object ParseValue(XElement element, string displayName)
    {
        switch (element.Name.LocalName)
        {
            case "dict":
                return ParseDict(element, displayName);
            case "array":
                return element.Elements().Select(child => ParseValue(child, displayName)).ToList<object>();
            case "string":
                return element.Value;
            case "integer":
                if (!long.TryParse(element.Value.Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var integer))
                {
                    throw new InvalidDataException($"{displayName}: invalid <integer> value '{element.Value}'.");
                }
                return integer;
            case "real":
                if (!double.TryParse(element.Value.Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var real))
                {
                    throw new InvalidDataException($"{displayName}: invalid <real> value '{element.Value}'.");
                }
                return real;
            case "true":
                return true;
            case "false":
                return false;
            case "data":
                try
                {
                    return Convert.FromBase64String(element.Value);
                }
                catch (FormatException)
                {
                    throw new InvalidDataException($"{displayName}: <data> is not base64.");
                }
            case "date":
                if (!DateTimeOffset.TryParse(element.Value.Trim(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                {
                    throw new InvalidDataException($"{displayName}: invalid <date> value '{element.Value}'.");
                }
                return date;
            default:
                throw new InvalidDataException(
                    $"{displayName}: unsupported plist element <{element.Name.LocalName}>.");
        }
    }

    private static Dictionary<string, object> ParseDict(XElement dict, string displayName)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var entries = dict.Elements().ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Name.LocalName != "key")
            {
                throw new InvalidDataException($"{displayName}: <dict> entries must start with <key>.");
            }
            var key = entries[index].Value;
            if (index + 1 >= entries.Length)
            {
                throw new InvalidDataException($"{displayName}: key '{key}' has no value element.");
            }
            values[key] = ParseValue(entries[++index], displayName);
        }
        return values;
    }

    private static XmlReaderSettings LenientReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null
    };

    private static IEnumerable<XElement> ValuesToElements(IReadOnlyDictionary<string, object> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => new[]
            {
                new XElement("key", pair.Key),
                ValueToElement(pair.Value)
            });

    private static XElement ValueToElement(object value)
    {
        switch (value)
        {
            case string text:
                return new XElement("string", text);
            case bool flag:
                return new XElement(flag ? "true" : "false");
            case long integer:
                return new XElement("integer", integer.ToString(CultureInfo.InvariantCulture));
            case int smallInteger:
                return new XElement("integer", smallInteger.ToString(CultureInfo.InvariantCulture));
            case double real:
                return new XElement("real", real.ToString("R", CultureInfo.InvariantCulture));
            case byte[] data:
                return new XElement("data", Convert.ToBase64String(data));
            case DateTimeOffset date:
                return new XElement("date", date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture));
            case IReadOnlyDictionary<string, object> dict:
                return new XElement("dict", ValuesToElements(dict));
            case IEnumerable<object> items:
                return new XElement("array", items.Select(ValueToElement));
            default:
                throw new ArgumentException(
                    $"Unsupported Info.plist value of type {value?.GetType().Name ?? "null"}.");
        }
    }
}
