using Xunit;

using System.Text;

namespace Bundler.TestShared;

/// <summary>
/// 测试侧最小 ar 读器——Api 层禁 internal 读器，用独立实现反向验证包结构。
/// 只支持 BSD 长名与常规成员。
/// </summary>
public static class MiniAr
{
    public sealed record Member(string Name, byte[] Data);

    public static IReadOnlyList<Member> Read(string path) => Read(File.ReadAllBytes(path));

    public static IReadOnlyList<Member> Read(byte[] blob)
    {
        Assert.Equal("!<arch>\n"u8.ToArray(), blob[..8]);
        var members = new List<Member>();
        var longNames = Array.Empty<byte>();
        var offset = 8;
        while (offset + 60 <= blob.Length)
        {
            var header = blob.AsSpan(offset, 60);
            var name = Encoding.ASCII.GetString(header[..16]).TrimEnd();
            var size = int.Parse(Encoding.ASCII.GetString(header[48..58]).Trim());
            var data = blob.AsSpan(offset + 60, size).ToArray();
            if (name == "#1/")
            {
                var nameLength = int.Parse(name[3..]);
                name = Encoding.UTF8.GetString(data[..nameLength]);
                data = data[nameLength..];
            }
            else if (name is "//" or "__.SYMDEF")
            {
                longNames = data;
            }
            else if (name.StartsWith('/'))
            {
                var nameOffset = int.Parse(name[1..]);
                var end = Array.IndexOf(longNames, (byte)'\n', nameOffset);
                name = Encoding.UTF8.GetString(longNames[nameOffset..end]).TrimEnd('/');
            }
            members.Add(new Member(name.TrimEnd('/'), data));
            offset += 60 + size + (size % 2);
        }
        return members;
    }
}
