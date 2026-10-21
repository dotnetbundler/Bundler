using System.Text;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class GzipTests
{
    [Fact]
    public void Compress_Deterministic_SameInputSameOutput()
    {
        var content = Encoding.UTF8.GetBytes("determinism check");
        Assert.Equal(Gzip.Compress(content), Gzip.Compress(content));
    }

    [Fact]
    public void Compress_RoundTrips()
    {
        var content = Encoding.UTF8.GetBytes("round-trip payload");
        var compressed = Gzip.Compress(content);
        using var gzip = new System.IO.Compression.GZipStream(
            new MemoryStream(compressed), System.IO.Compression.CompressionMode.Decompress);
        using var result = new MemoryStream();
        gzip.CopyTo(result);
        Assert.Equal(content, result.ToArray());
    }
}

public sealed class TarWriterTests
{
    private static byte[] Write(params TarEntry[] entries) =>
        WriteWithOptions(false, entries);

    private static byte[] WriteWithOptions(bool omitEnd, params TarEntry[] entries)
    {
        using var stream = new MemoryStream();
        TarWriter.Write(stream, entries, omitEnd);
        return stream.ToArray();
    }

    private static string Octal(byte[] header, int offset, int width)
    {
        var text = Encoding.ASCII.GetString(header, offset, width).Trim('\0', ' ');
        return text;
    }

    [Fact]
    public void Write_UstarHeaderFields()
    {
        var bytes = Write(new TarEntry
        {
            Name = "usr/lib/app/run", Kind = TarEntryKind.File, Mode = 0x1ED /* 0755 */,
            Content = Encoding.UTF8.GetBytes("hi")
        });

        Assert.Equal("usr/lib/app/run", Encoding.ASCII.GetString(bytes, 0, 20).TrimEnd('\0'));
        Assert.Equal("0000755", Octal(bytes, 100, 8));
        Assert.Equal("0000000", Octal(bytes, 108, 8));   // uid
        Assert.Equal("0000000", Octal(bytes, 116, 8));   // gid
        Assert.Equal("00000000002", Octal(bytes, 124, 12));
        Assert.Equal(Convert.ToString(TarWriter.EntryMtime, 8).PadLeft(11, '0'),
            Octal(bytes, 136, 12));
        Assert.Equal((byte)'0', bytes[156]);
        Assert.Equal("ustar", Encoding.ASCII.GetString(bytes, 257, 5));
        Assert.Equal("root", Encoding.ASCII.GetString(bytes, 265, 4).TrimEnd('\0'));
        Assert.Equal("root", Encoding.ASCII.GetString(bytes, 297, 4).TrimEnd('\0'));
    }

    [Fact]
    public void Write_Checksum_FieldMatchesSum()
    {
        var bytes = Write(new TarEntry
        {
            Name = "a", Kind = TarEntryKind.File, Mode = 0x1A4, Content = [1]
        });

        var stored = Convert.ToInt32(Octal(bytes, 148, 8), 8);
        // 校验和计算时字段格为八个空格——先还原再求和比对
        var header = bytes[..512];
        for (var i = 148; i < 156; i++)
        {
            header[i] = (byte)' ';
        }
        Assert.Equal(header.Sum(b => (int)b), stored);
    }

    [Fact]
    public void Write_DirectoryEntry_TypeFlag5_AndSlash()
    {
        var bytes = Write(new TarEntry
        {
            Name = "usr/lib/app", Kind = TarEntryKind.Directory, Mode = 0x1ED
        });
        Assert.Equal((byte)'5', bytes[156]);
        Assert.Equal('/', (char)bytes[11]); // 名以 '/' 收尾
    }

    [Fact]
    public void Write_Symlink_TypeFlag2_LinkTarget()
    {
        var bytes = Write(new TarEntry
        {
            Name = "usr/bin/app", Kind = TarEntryKind.Symlink, Mode = 0x1ED,
            LinkTarget = "../lib/app/run"
        });
        Assert.Equal((byte)'2', bytes[156]);
        Assert.Equal("../lib/app/run", Encoding.ASCII.GetString(bytes, 157, 14).TrimEnd('\0'));
    }

    [Fact]
    public void Write_LongSymlinkTarget_EmitsPaxLinkpath()
    {
        // ustar linkname 只有 100B——超长目标改走 pax linkpath，ustar 字段写截断值
        var target = "../" + new string('t', 120) + "/run";
        var bytes = Write(new TarEntry
        {
            Name = "usr/bin/app", Kind = TarEntryKind.Symlink, Mode = 0x1ED,
            LinkTarget = target
        });
        Assert.Equal((byte)'x', bytes[156]);   // pax 扩展头先行
        var paxBody = Encoding.UTF8.GetString(bytes, 512, 512);
        Assert.Contains("linkpath=" + target, paxBody);
    }

    [Fact]
    public void Write_LongName_SplitsIntoPrefix()
    {
        var longName = "a/" + new string('b', 120) + "/" + new string('c', 50);
        var bytes = Write(new TarEntry
        {
            Name = longName, Kind = TarEntryKind.File, Mode = 0x1A4, Content = [1]
        });
        // prefix 段非空（名字进了 345..500），name 段承载叶名
        Assert.Equal(new string('c', 50), Encoding.ASCII.GetString(bytes, 0, 50));
        Assert.NotEqual((byte)0, bytes[345]);
    }

    [Fact]
    public void Write_NameBeyondUstar_Throws()
    {
        // 无 '/' 可拆的超长名 → 不可表达
        Assert.Throws<ArgumentException>(() => Write(new TarEntry
        {
            Name = new string('x', 200), Kind = TarEntryKind.File, Mode = 0x1A4, Content = [1]
        }));
    }

    [Fact]
    public void Write_EndBlocks_PresentUnlessOmitted()
    {
        var full = Write(new TarEntry { Name = "a", Kind = TarEntryKind.File, Content = [1] });
        var partial = WriteWithOptions(true,
            new TarEntry { Name = "a", Kind = TarEntryKind.File, Content = [1] });

        Assert.Equal(1024, full.Length - partial.Length);
        // 末尾 1024 字节全零
        Assert.All(full.Skip(full.Length - 1024), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Write_StreamingContent_ExactLength()
    {
        var payload = new byte[1000];
        new Random(42).NextBytes(payload);
        var bytes = Write(new TarEntry
        {
            Name = "big.bin", Kind = TarEntryKind.File, Mode = 0x1A4,
            OpenContent = () => new MemoryStream(payload)
        });

        Assert.Equal(512 + 1024 + 1024, bytes.Length); // 头 + 1024 内容块 + 末尾零块
        var written = bytes.Skip(512).Take(1000).ToArray();
        Assert.Equal(payload, written);
    }

    [Fact]
    public void Write_StreamingContent_ShortStream_Throws()
    {
        Assert.Throws<EndOfStreamException>(() => Write(new TarEntry
        {
            Name = "short.bin", Kind = TarEntryKind.File, Mode = 0x1A4,
            OpenContent = () => new FakeLengthStream(declared: 100, actual: 10)
        }));
    }

    private sealed class FakeLengthStream : MemoryStream
    {
        private readonly long _declared;
        public FakeLengthStream(long declared, int actual)
        {
            _declared = declared;
            Write(new byte[actual], 0, actual);
            Position = 0;
        }
        public override long Length => _declared;
    }

    [Fact]
    public void Write_Deterministic()
    {
        TarEntry[] Entries() =>
        [
            new() { Name = "d", Kind = TarEntryKind.Directory, Mode = 0x1ED },
            new() { Name = "d/f", Kind = TarEntryKind.File, Mode = 0x1A4, Content = [1, 2, 3] },
            new() { Name = "d/l", Kind = TarEntryKind.Symlink, Mode = 0x1ED, LinkTarget = "f" }
        ];
        Assert.Equal(Write(Entries()), Write(Entries()));
    }
}
