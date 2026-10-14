using DotNet.Bundler.Rpm;

namespace Bundler.UnitTests.Formats;

public sealed class RpmHeaderTests
{
    [Fact]
    public void Write_StartsWithRpmHeaderMagic()
    {
        var bytes = RpmHeaderWriter.Write(
            [RpmHeaderWriter.Str(1000, "pkg")], regionTag: 63);

        Assert.Equal(0x8e, bytes[0]);
        Assert.Equal(0xad, bytes[1]);
        Assert.Equal(0xe8, bytes[2]);
        Assert.Equal(0x01, bytes[3]);
    }

    [Fact]
    public void Write_EntryIndexAndStore_Consistent()
    {
        var bytes = RpmHeaderWriter.Write(
            [
                RpmHeaderWriter.Str(1000, "name"),
                RpmHeaderWriter.Int32s(1004, 42)
            ],
            regionTag: 63);

        // 头布局：magic(8) + indexCount(4) + storeBytes(4) + index(n*16) + store
        var indexCount = Read32(bytes, 8);
        var storeBytes = Read32(bytes, 12);
        Assert.Equal(3, indexCount); // 2 entries + region trailer
        Assert.Equal(16 + indexCount * 16 + storeBytes, bytes.Length);
    }

    [Fact]
    public void Str_NullTerminated()
    {
        var entry = RpmHeaderWriter.Str(1000, "abc");
        Assert.Equal(1, entry.Count);
        Assert.Equal(0, entry.Data[^1]);
        Assert.Equal(4, entry.Data.Length);
    }

    [Fact]
    public void Strings_CountMatchesElements()
    {
        var entry = RpmHeaderWriter.Strings(1049, ["a", "b", "c"]);
        Assert.Equal(3, entry.Count);
        Assert.Equal(6, entry.Data.Length); // "a\0b\0c\0"
    }

    [Fact]
    public void Int32s_BigEndian()
    {
        var entry = RpmHeaderWriter.Int32s(1004, 0x01020304);
        Assert.Equal([0x01, 0x02, 0x03, 0x04], entry.Data);
    }

    [Fact]
    public void Write_RegionTrailer_NegativeOffset()
    {
        var bytes = RpmHeaderWriter.Write(
            [RpmHeaderWriter.Str(1000, "x")], regionTag: 62);

        var indexCount = Read32(bytes, 8);
        var storeBytes = Read32(bytes, 12);
        Assert.Equal(2, indexCount);
        // trailer 记录躺在 store 尾部：{regionTag, BIN(7), -indexBytes, 16}
        var trailer = bytes[^16..];
        Assert.Equal(62, Read32(trailer, 0));
        Assert.Equal(7, Read32(trailer, 4));
        Assert.Equal(-indexCount * 16, Read32(trailer, 8));
        Assert.Equal(16, Read32(trailer, 12));
        Assert.Equal(storeBytes, bytes.Length - 16 - indexCount * 16);
    }

    private static int Read32(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) |
        (bytes[offset + 2] << 8) | bytes[offset + 3];
}
