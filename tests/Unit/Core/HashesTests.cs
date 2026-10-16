using System.Text;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class HashesTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("bundler");

    [Fact]
    public void Sha256_KnownVector()
    {
        // sha256("bundler") 外部预计算值（sha256sum 核对）
        Assert.Equal(
            "d4b91025af98f042008ed124ac3f3c798b014f3bbcb27ce2e94031d5132bae66",
            Hashes.Sha256Hex(Content));
        Assert.Equal(64, Hashes.Sha256Hex(Content).Length);
    }

    [Fact]
    public void Sha256_StreamMatchesBytes()
    {
        using var stream = new MemoryStream(Content);
        Assert.Equal(Hashes.Sha256Hex(Content), Hashes.Sha256Hex(stream));
    }

    [Fact]
    public void Sha256_FileMatchesBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bundler-hash-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, Content);
        try
        {
            Assert.Equal(Hashes.Sha256Hex(Content), Hashes.Sha256HexFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sha1_Md5_KnownLengths()
    {
        Assert.Equal(40, Hashes.Sha1Hex(Content).Length);
        Assert.Equal(32, Hashes.Md5Hex(Content).Length);
        Assert.Equal(Hashes.Sha1Hex(Content), Hashes.Sha1Hex(new MemoryStream(Content)));
    }

    [Fact]
    public void LowerUpperHex_PerByteFormat()
    {
        var bytes = new byte[] { 0x00, 0x0F, 0xA5, 0xFF };
        Assert.Equal("000fa5ff", Hashes.ToLowerHex(bytes));
        Assert.Equal("000FA5FF", Hashes.ToUpperHex(bytes));
    }
}
