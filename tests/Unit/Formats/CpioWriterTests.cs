using System.Text;
using DotNet.Bundler.Rpm;

namespace Bundler.UnitTests.Formats;

public sealed class CpioWriterTests
{
    [Fact]
    public void Write_NewcMagic_AndTrailer()
    {
        var bytes = CpioWriter.Write(
        [
            new CpioWriter.Entry
            {
                Name = "/usr/lib/app/run",
                Mode = 0x81ED,
                Data = "bin"u8.ToArray()
            }
        ]);

        Assert.Equal("070701", Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.True(ContainsTrailer(bytes));
        Assert.Equal(0, bytes.Length % 512);
    }

    [Fact]
    public void Write_Deterministic()
    {
        CpioWriter.Entry[] Entries() =>
        [
            new() { Name = "/a", Mode = 0x81A4, Data = "x"u8.ToArray() },
            new() { Name = "/d", Mode = 0x41ED, Data = [] }
        ];
        Assert.Equal(CpioWriter.Write(Entries()), CpioWriter.Write(Entries()));
    }

    [Fact]
    public void Write_StreamingContent_MatchesBuffered()
    {
        var payload = new byte[10_000];
        new Random(2).NextBytes(payload);
        var streamed = CpioWriter.Write(
        [
            new CpioWriter.Entry
            {
                Name = "/f", Mode = 0x81A4,
                OpenContent = () => new MemoryStream(payload)
            }
        ]);
        var buffered = CpioWriter.Write(
        [
            new CpioWriter.Entry { Name = "/f", Mode = 0x81A4, Data = payload }
        ]);
        Assert.Equal(buffered, streamed);
    }

    private static bool ContainsTrailer(byte[] bytes)
    {
        var needle = Encoding.ASCII.GetBytes("TRAILER!!!");
        for (var i = 0; i <= bytes.Length - needle.Length; i++)
        {
            if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }
        return false;
    }
}
