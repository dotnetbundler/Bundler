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
}
