using System.Text;

namespace DotNet.Bundler.MacDmg;

/// <summary>
/// Builds the udifrez XML plist that embeds a Software License Agreement into a UDIF image.
/// Resource layout follows Apple's classic SLA scheme: 'LPic' maps languages to per-language
/// 'STR#' button labels plus a 'TEXT'/'RTF ' license body, all at resource id 5000 (English).
/// </summary>
internal static class MacDmgLicenseResources
{
    private static readonly string[] ButtonStrings =
    [
        "English",
        "Agree",
        "Disagree",
        "Print",
        "Save",
        "If you agree with the terms of this license, click \"Agree\" to install the software. " +
        "If you do not agree, press \"Disagree\"."
    ];

    /// <summary>Returns the udifrez XML for the given license file (.txt → TEXT, .rtf → 'RTF ').</summary>
    internal static string BuildPlist(string licenseFile, IBundleLogger logger)
    {
        var isRtf = licenseFile.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase);
        var bodyType = isRtf ? "RTF " : "TEXT";
        byte[] body;
        if (isRtf)
        {
            body = File.ReadAllBytes(licenseFile);
        }
        else
        {
            // 'TEXT' resources render as MacRoman; ASCII replaces unmappable chars with '?'.
            var text = File.ReadAllText(licenseFile);
            if (text.Any(c => c > 0x7F))
            {
                logger.Log(
                    BundleLogLevel.Warning,
                    "The .txt license contains non-ASCII characters that cannot be represented in " +
                    "a TEXT resource; use a .rtf license for full fidelity.");
            }
            body = Encoding.ASCII.GetBytes(text);
        }

        var plist = new StringBuilder();
        plist.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        plist.AppendLine(
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" " +
            "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        plist.AppendLine("<plist version=\"1.0\">");
        plist.AppendLine("<dict>");
        WriteResourceArray(plist, "LPic", (5000, "", BuildLPic()));
        WriteResourceArray(plist, "STR#", (5000, "English", BuildStringList()));
        WriteResourceArray(plist, bodyType, (5000, "English SLA", body));
        plist.AppendLine("</dict>");
        plist.AppendLine("</plist>");
        return plist.ToString();
    }

    /// <summary>LPic 5000: default language + per-language {langID, resID-5000, doubleByte} entries.</summary>
    private static byte[] BuildLPic()
    {
        // English only: default=0, count=1, entry {lang=0, offset=0, doubleByte=0}.
        return [0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    }

    /// <summary>STR# 5000: uint16 count followed by Pascal strings (length-prefixed MacRoman).</summary>
    private static byte[] BuildStringList()
    {
        var buffer = new List<byte> { 0x00, (byte)ButtonStrings.Length };
        foreach (var value in ButtonStrings)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        return buffer.ToArray();
    }

    private static void WriteResourceArray(
        StringBuilder plist, string type, params (int Id, string Name, byte[] Data)[] entries)
    {
        plist.Append("    <key>").Append(type).AppendLine("</key>");
        plist.AppendLine("    <array>");
        foreach (var (id, name, data) in entries)
        {
            plist.AppendLine("        <dict>");
            plist.AppendLine("            <key>Attributes</key>");
            plist.AppendLine("            <string>0x0000</string>");
            plist.AppendLine("            <key>Data</key>");
            plist.AppendLine("            <data>");
            plist.Append("            ");
            plist.AppendLine(Convert.ToBase64String(data));
            plist.AppendLine("            </data>");
            plist.Append("            <key>ID</key><string>").Append(id).AppendLine("</string>");
            plist.Append("            <key>Name</key><string>")
                .Append(XmlEscape(name)).AppendLine("</string>");
            plist.AppendLine("        </dict>");
        }
        plist.AppendLine("    </array>");
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
