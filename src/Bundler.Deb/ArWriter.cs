using System.Text;

namespace DotNet.Bundler.Deb;

/// <summary>A single named member inside an ar archive.</summary>
internal sealed class ArMember
{
    internal ArMember(string name, byte[] content)
    {
        Name = name;
        Content = content;
    }

    internal string Name { get; }
    internal byte[] Content { get; }
}

/// <summary>
/// Writes System V "ar" archives in the Debian variant (short names only,
/// no string table). Member names are stored as <c>name/</c>.
/// </summary>
internal static class ArWriter
{
    internal static void Write(Stream output, IReadOnlyList<ArMember> members)
    {
        var magic = Encoding.ASCII.GetBytes("!<arch>\n");
        output.Write(magic, 0, magic.Length);
        foreach (var member in members)
        {
            var name = Encoding.ASCII.GetBytes(member.Name + "/");
            if (name.Length > 16)
            {
                throw new ArgumentException($"ar member name is too long: {member.Name}");
            }
            output.Write(name, 0, name.Length);
            WritePadding(output, (byte)' ', 16 - name.Length);
            WriteField(output, "0", 12);                 // mtime (fixed for determinism)
            WriteField(output, "0", 6);                  // uid
            WriteField(output, "0", 6);                  // gid
            WriteField(output, "100644", 8);             // mode (octal, left-aligned)
            WriteField(output, member.Content.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture), 10);
            output.WriteByte(0x60);
            output.WriteByte(0x0A);
            output.Write(member.Content, 0, member.Content.Length);
            if ((member.Content.Length & 1) == 1)
            {
                output.WriteByte(0x0A);
            }
        }
    }

    private static void WriteField(Stream output, string value, int width)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        output.Write(bytes, 0, bytes.Length);
        WritePadding(output, (byte)' ', width - bytes.Length);
    }

    private static void WritePadding(Stream output, byte padding, int count)
    {
        for (var i = 0; i < count; i++)
        {
            output.WriteByte(padding);
        }
    }
}
