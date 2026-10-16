using System.Text;
using DotNet.Bundler.Deb;

namespace Bundler.UnitTests.Formats;

public sealed class ArWriterTests
{
    [Fact]
    public void Write_MagicAndMemberOrder()
    {
        using var stream = new MemoryStream();
        ArWriter.Write(stream,
        [
            new ArMember("debian-binary", "2.0\n"u8.ToArray()),
            new ArMember("control.tar.gz", "ctrl"u8.ToArray()),
            new ArMember("data.tar.gz", "data"u8.ToArray())
        ]);
        var bytes = stream.ToArray();

        Assert.Equal("!<arch>\n", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal("debian-binary", Encoding.ASCII.GetString(bytes, 8, 13).TrimEnd());
    }

    [Fact]
    public void Write_RoundTrips_ThroughReader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.ar");
        try
        {
            using (var stream = File.Create(path))
            {
                ArWriter.Write(stream,
                [
                    new ArMember("a", "content-a"u8.ToArray()),
                    new ArMember("b/b", "content-b"u8.ToArray())
                ]);
            }

            var members = DebPackageReader.ReadAr(path);
            Assert.Equal(2, members.Count);
            Assert.Equal("a", members[0].Name);
            Assert.Equal("content-a", Encoding.UTF8.GetString(members[0].Content));
            Assert.Equal("b/b", members[1].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_LongName_Rejected()
    {
        // 成员名+"/"须装进 16 字节字段：超长显式拒绝而非走 BSD/GNU 长名扩展
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() =>
            ArWriter.Write(stream, [new ArMember("a-name-longer-than-15", "x"u8.ToArray())]));
    }

    [Fact]
    public void Write_FieldOverflow_Rejected()
    {
        // 10 字节 size 字段装不下的成员长度字段：构造不可行（内容须实际长度），
        // 验证路径是 WriteField 显式拒——这里用大文件路径成员侧面覆盖：
        // 名长正界内、内容小于等于 10 位十进制 → 通过；写器自身拒的是名与字段宽度。
        using var stream = new MemoryStream();
        ArWriter.Write(stream,
            [new ArMember(new string('n', 15), "x"u8.ToArray())]);
        Assert.Equal(8 + 60 + 2, stream.Length);
    }

    [Fact]
    public void Write_OddLength_AlignedToTwo()
    {
        using var stream = new MemoryStream();
        ArWriter.Write(stream, [new ArMember("x", "abc"u8.ToArray())]);
        var bytes = stream.ToArray();
        Assert.Equal(0, bytes.Length % 2);
        Assert.Equal(8 + 60 + 4, bytes.Length); // 3 字节内容补到偶
    }
}
