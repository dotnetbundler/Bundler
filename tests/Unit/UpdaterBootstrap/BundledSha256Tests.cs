using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler.Updater.Bootstrap;

namespace Bundler.UnitTests.UpdaterBootstrap;

/// <summary>
/// 纯托管 <see cref="BundledSha256"/> 的正确性面：musl 静态 AOT 上它就是
/// <c>pkg-apply --sha256</c> 复核的唯一摘要源——FIPS 180-4 标准向量钉定 +
/// 与 BCL <see cref="SHA256"/> 流式交叉校验（测试宿主有 OpenSSL，两边必须逐字节一致）。
/// </summary>
public sealed class BundledSha256Tests
{
    public static IEnumerable<object[]> Vectors()
    {
        // FIPS 180-4 / RFC 4634 标准向量 + 64 字节块边界 + 分块喂件（>1 块）。
        yield return [Array.Empty<byte>(), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"];
        yield return [Encoding.ASCII.GetBytes("abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"];
        yield return [Encoding.ASCII.GetBytes(new string('a', 55)), "9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318"];
        yield return [Encoding.ASCII.GetBytes(new string('a', 56)), "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a"];
        yield return [Encoding.ASCII.GetBytes(new string('a', 64)), "ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb"];
        yield return [Enumerable.Range(0, 1_000_000).Select(i => (byte)(i % 251)).ToArray(), ""];
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Hash_MatchesStandardVectors(byte[] input, string expectedHex)
    {
        using var stream = new MemoryStream(input);
        var hex = BundledSha256.Hex(stream);
        if (expectedHex.Length > 0)
        {
            Assert.Equal(expectedHex, hex);
        }
        Assert.Equal(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(), hex);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(100)]
    public void Hash_ShortReadStream_MatchesBcl(int chunkSize)
    {
        // 网络/管道流按任意短读返回——carry 补满分肢（buffered>0 路径）只在
        // 非 64 对齐喂件时才执行。1MB 内容按 chunkSize 切片，摘要须逐位一致。
        var input = Enumerable.Range(0, 1_000_000).Select(i => (byte)(i % 251)).ToArray();
        using var stream = new ChunkedStream(input, chunkSize);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(),
            BundledSha256.Hex(stream));
    }

    /// <summary>每次 Read 至多返回 <paramref name="chunkSize"/> 字节的包装流。</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _pos;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunkSize), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
