using System.Text;

namespace DotNet.Bundler.Deb;

/// <summary>A single named member inside an ar archive.</summary>
internal sealed class ArMember
{
    internal ArMember(string name, byte[] content)
    {
        Name = name;
        _content = content;
    }

    private ArMember(string name, string filePath)
    {
        Name = name;
        FilePath = filePath;
    }

    /// <summary>文件后备成员：ar 头只需尺寸，内容装配时流式拷贝——大件不经内存。</summary>
    internal static ArMember FromFile(string name, string filePath) =>
        new(name, filePath);

    internal string Name { get; }

    /// <summary>内容字节——文件后备成员按访问惰性物化；写入路径只走 <see cref="FilePath"/>。</summary>
    internal byte[] Content => _content ??= File.ReadAllBytes(FilePath!);
    private byte[]? _content;
    internal string? FilePath { get; }
    internal long Length => _content?.Length ?? new FileInfo(FilePath!).Length;
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
            WriteField(output, member.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture), 10);
            output.WriteByte(0x60);
            output.WriteByte(0x0A);
            if (member.FilePath is { } memberPath)
            {
                using (var input = File.OpenRead(memberPath))
                {
                    input.CopyTo(output);
                }
            }
            else
            {
                var content = member.Content;
                output.Write(content, 0, content.Length);
            }
            if ((member.Length & 1) == 1)
            {
                output.WriteByte(0x0A);
            }
        }
    }

    private static void WriteField(Stream output, string value, int width)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        // 字段宽度越界静默截断会产偏移全错的坏 ar——显式拒绝。
        if (bytes.Length > width)
        {
            throw new ArgumentException(
                $"The ar field value '{value}' does not fit in {width} bytes.");
        }
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
