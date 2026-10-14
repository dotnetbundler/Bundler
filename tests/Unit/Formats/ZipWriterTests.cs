using System.IO.Compression;
using System.Text;
using DotNet.Bundler.Archive;

namespace Bundler.UnitTests.Formats;

public sealed class ZipWriterTests
{
    private static byte[] Write(params ZipEntry[] entries)
    {
        using var stream = new MemoryStream();
        ZipWriter.Write(stream, entries);
        return stream.ToArray();
    }

    [Fact]
    public void Write_RoundTrips_WithSystemReader()
    {
        var bytes = Write(
            new ZipEntry { Name = "dir/", Kind = ZipEntryKind.Directory, Mode = 0x1ED },
            new ZipEntry { Name = "dir/app", Kind = ZipEntryKind.File, Mode = 0x1ED,
                Content = "bin"u8.ToArray() },
            new ZipEntry { Name = "dir/link", Kind = ZipEntryKind.Symlink, Mode = 0x1ED,
                LinkTarget = "app" });

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(3, zip.Entries.Count);
        var file = zip.GetEntry("dir/app");
        Assert.NotNull(file);
        using var reader = new StreamReader(file!.Open());
        Assert.Equal("bin", reader.ReadToEnd());
        Assert.NotNull(zip.GetEntry("dir/link"));
    }

    [Fact]
    public void Write_Deterministic()
    {
        ZipEntry[] Entries() =>
        [
            new() { Name = "b.txt", Kind = ZipEntryKind.File, Content = "b"u8.ToArray() },
            new() { Name = "a.txt", Kind = ZipEntryKind.File, Content = "a"u8.ToArray() }
        ];
        Assert.Equal(Write(Entries()), Write(Entries()));
    }

    [Fact]
    public void Write_NonAsciiName_Utf8Flag()
    {
        var bytes = Write(new ZipEntry
        {
            Name = "文件.txt", Kind = ZipEntryKind.File, Content = "x"u8.ToArray()
        });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("文件.txt"));
        // UTF-8 名走 general purpose flag 11
        var flags = BitConverter.ToUInt16(bytes, 6);
        Assert.True((flags & (1 << 11)) != 0);
    }

    [Fact]
    public void Write_ExecMode_InExternalAttributes()
    {
        var bytes = Write(new ZipEntry
        {
            Name = "run", Kind = ZipEntryKind.File, Mode = 0x1ED,
            Content = "x"u8.ToArray()
        });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("run")!;
        // Unix mode 0755|regular 进 external attributes 高 16 位
        var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        Assert.Equal(0x81ED, mode);
    }

    [Fact]
    public void Write_StreamingEntry_WritesLengthAndCrc()
    {
        var payload = new byte[100_000];
        new Random(1).NextBytes(payload);
        var bytes = Write(new ZipEntry
        {
            Name = "big.bin", Kind = ZipEntryKind.File,
            OpenContent = () => new MemoryStream(payload)
        });

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new MemoryStream();
        zip.GetEntry("big.bin")!.Open().CopyTo(reader);
        Assert.Equal(payload, reader.ToArray());
    }

    [Fact]
    public void Write_Zip64Eocd_ForHugeEntryCounts()
    {
        // 65536 条空目录项推 EOCD 的 16 位计数字段超限 → Zip64 EOCD+locator 尾
        var entries = Enumerable.Range(0, 65536)
            .Select(i => new ZipEntry { Name = $"d{i}/", Kind = ZipEntryKind.Directory });
        var bytes = Write(entries.ToArray());

        // 末尾结构：EOCD64 signature 0x06064b50 应出现
        var tail = bytes[^256..];
        Assert.True(ScanForSignature(bytes, 0x06064b50),
            "Zip64 EOCD signature missing for >64k entries");
        // 经典 EOCD 的计数字段应是 0xFFFF 哨兵
        var eocdOffset = FindSignature(bytes, 0x06054b50, fromEnd: true);
        Assert.True(eocdOffset >= 0);
        Assert.Equal(0xFFFF, BitConverter.ToUInt16(bytes, eocdOffset + 10));
    }

    private static bool ScanForSignature(byte[] data, uint signature) =>
        FindSignature(data, signature, fromEnd: false) >= 0;

    private static int FindSignature(byte[] data, uint signature, bool fromEnd)
    {
        var pattern = BitConverter.GetBytes(signature);
        for (var i = fromEnd ? data.Length - pattern.Length : 0;
             fromEnd ? i >= 0 : i <= data.Length - pattern.Length;
             i += fromEnd ? -1 : 1)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }
        return -1;
    }
}
