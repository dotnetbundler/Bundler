using System.Text;
using System.Xml.Linq;

namespace DotNet.Bundler.MacApp;

internal static class InfoPlist
{
    internal static void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        var elements = values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => new XElement[]
            {
                new("key", pair.Key),
                new("string", pair.Value)
            });
        var plist = new XElement("plist",
            new XAttribute("version", "1.0"),
            new XElement("dict", elements));

        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        builder.Append(plist.ToString());
        builder.Append('\n');
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    internal static IReadOnlyDictionary<string, string> ReadStringValues(string path)
    {
        var dict = XDocument.Load(path).Root?.Element("dict")
            ?? throw new InvalidDataException("Info.plist does not contain a dict root.");
        var entries = dict.Elements().ToArray();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < entries.Length; index += 2)
        {
            if (entries[index].Name == "key" && entries[index + 1].Name == "string")
            {
                values[entries[index].Value] = entries[index + 1].Value;
            }
        }
        return values;
    }
}
