using System.Security.Cryptography;

#if BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Protocol;
#else
namespace DotNet.Bundler.Core.Update;
#endif

/// <summary>
/// ECDSA P-256/SHA-256 验签面——应用内更新库与打包侧共用同一实现（链接编译）。
/// `.sig` 归一 64 字节 IEEE-P1363；验签对 P1363/DER 两种编码都试，任一通过即真。
/// </summary>
public static class UpdateSignatureVerifier
{
    public static bool Verify(byte[] data, byte[] signature, UpdateKeyMaterial publicKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(publicKey.ToPublicParameters());
        return TryVerify(ecdsa, signature, () => new MemoryStream(data, writable: false), out _);
    }

    public static bool VerifyFile(string path, byte[] signature, UpdateKeyMaterial publicKey) =>
        VerifyFile(path, signature, publicKey, out _);

    /// <summary>
    /// 验签同趟输出内容 sha256——留存件复核这类"验签+字节指纹都要"的场景单次读取
    /// 一并产出（先 hash 再另验会把两个读取窗口并成可被替换的缝隙）。
    /// </summary>
    public static bool VerifyFile(
        string path, byte[] signature, UpdateKeyMaterial publicKey, out byte[] contentHash)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(publicKey.ToPublicParameters());
        return TryVerify(ecdsa, signature, () => File.OpenRead(path), out contentHash);
    }

    /// <summary>便捷面：清单里的 base64 签名 + 旁车里的 base64 公钥直接验文件。</summary>
    public static bool VerifyFile(string path, string signatureBase64, string publicPointBase64)
    {
        var signature = Convert.FromBase64String(signatureBase64);
        var publicKey = UpdateKeyMaterial.FromPublicPoint(publicPointBase64);
        return VerifyFile(path, signature, publicKey);
    }

    // 数据只过一次：流式算 SHA-256 后对每个编码候选 VerifyHash——
    // 旧实现按候选把整件复制进 MemoryStream，大工件内存翻倍且重复哈希。
    private static bool TryVerify(
        ECDsa ecdsa, byte[] signature, Func<Stream> openData, out byte[] contentHash)
    {
        byte[] hash;
        using (var sha = SHA256.Create())
        using (var input = openData())
        {
            hash = sha.ComputeHash(input);
        }
        contentHash = hash;
        foreach (var candidate in Candidates(signature))
        {
            try
            {
                if (ecdsa.VerifyHash(hash, candidate))
                {
                    return true;
                }
            }
            catch (CryptographicException)
            {
            }
        }
        return false;
    }

    private static IEnumerable<byte[]> Candidates(byte[] signature)
    {
        yield return signature;
        if (signature.Length == 64)
        {
            yield return ToDer(signature);
            // 恰 64B 的 DER 输入会被"长度=P1363"先判吞没——再补"按 DER 解出
            // P1363"候选（r/s 高位恰使 DER 总长落 64 的歧义形；ToP1363 对
            // 64B 输入先按 DER 试解、失败才按 P1363 直通）。
            var decoded = ToP1363(signature);
            if (!ReferenceEquals(decoded, signature))
            {
                yield return decoded;
            }
        }
        else if (signature.Length >= 8 && signature[0] == 0x30)
        {
            // 输入是 DER 时反向兜底 P1363——netstandard2.0 的 ECDsa 实现认哪边依平台而异。
            byte[] p1363;
            try
            {
                p1363 = ToP1363(signature);
            }
            catch (InvalidOperationException)
            {
                yield break;
            }
            yield return p1363;
        }
    }

    // IEEE-P1363（r||s 定长拼接）↔ DER（SEQUENCE{INTEGER r, INTEGER s}）双向归一。
    public static byte[] ToP1363(byte[] signature)
    {
        // 恰 64B 的歧义形：0x30 起头先按 DER 试解（DER 签名 r/s 高位恰使
        // 总长落 64 时不该被"长度=P1363"先判吞没）；解不出按 P1363 直通。
        if (signature.Length == 64 && signature[0] != 0x30)
        {
            return signature;
        }
        try
        {
            return DecodeDer(signature);
        }
        catch (InvalidOperationException) when (signature.Length == 64)
        {
            return signature;
        }
    }

    // DER: 30 <len> 02 <rlen> <r> 02 <slen> <s> ——P-256 签名总长 ≤72B，
    // DER 恒为短式长度；严收"长度字节=总长-2"的精确形状：非最短长式
    //（0x81）与尾随垃圾同属远端可控输入的畸形面，一律拒——
    // 严解不为兼容留缝（本链签名件恒由 Bundler 侧产出）。
    private static byte[] DecodeDer(byte[] signature)
    {
        if (signature.Length < 8 || signature[0] != 0x30 ||
            signature[1] != signature.Length - 2)
        {
            throw new InvalidOperationException("Unexpected ECDSA signature encoding.");
        }
        var offset = 2;
        // 逐段取前先做边界断言：本函数吃远端可控的 .sig 输入，越界索引/
        // BlockCopy 会漏出 ArgumentException/IndexOutOfRangeException 穿透
        // Candidates 的 InvalidOperationException 过滤器——统一按畸形拒。
        if (signature[offset] != 0x02)
        {
            throw new InvalidOperationException("Malformed ECDSA DER signature.");
        }
        var rLen = signature[offset + 1];
        if (offset + 2 + rLen > signature.Length)
        {
            throw new InvalidOperationException("Malformed ECDSA DER signature.");
        }
        var r = new byte[rLen];
        Buffer.BlockCopy(signature, offset + 2, r, 0, rLen);
        var sOffset = offset + 2 + rLen;
        if (sOffset + 2 > signature.Length || signature[sOffset] != 0x02)
        {
            throw new InvalidOperationException("Malformed ECDSA DER signature.");
        }
        var sLen = signature[sOffset + 1];
        if (sOffset + 2 + sLen != signature.Length)
        {
            throw new InvalidOperationException("Malformed ECDSA DER signature.");
        }
        var s = new byte[sLen];
        Buffer.BlockCopy(signature, sOffset + 2, s, 0, sLen);
        var result = new byte[64];
        CopyPadded(r, result, 0);
        CopyPadded(s, result, 32);
        return result;
    }

    public static byte[] ToDer(byte[] signature64)
    {
        var r = TrimLeadingZeros(signature64, 0, 32);
        var s = TrimLeadingZeros(signature64, 32, 32);
        // 最高位置位的 INTEGER 需补 0x00 防被读成负数。
        var rPadded = NeedsPad(r) ? PrependZero(r) : r;
        var sPadded = NeedsPad(s) ? PrependZero(s) : s;
        var bodyLen = 2 + rPadded.Length + 2 + sPadded.Length;
        var der = new byte[2 + bodyLen];
        der[0] = 0x30;
        der[1] = (byte)bodyLen;
        der[2] = 0x02;
        der[3] = (byte)rPadded.Length;
        Buffer.BlockCopy(rPadded, 0, der, 4, rPadded.Length);
        der[4 + rPadded.Length] = 0x02;
        der[5 + rPadded.Length] = (byte)sPadded.Length;
        Buffer.BlockCopy(sPadded, 0, der, 6 + rPadded.Length, sPadded.Length);
        return der;
    }

    private static void CopyPadded(byte[] value, byte[] destination, int offset)
    {
        var trimmed = TrimLeadingZeros(value, 0, value.Length);
        var take = Math.Min(trimmed.Length, 32);
        Buffer.BlockCopy(trimmed, trimmed.Length - take, destination, offset + 32 - take, take);
    }

    private static byte[] TrimLeadingZeros(byte[] source, int offset, int count)
    {
        var index = offset;
        var end = offset + count;
        while (index < end - 1 && source[index] == 0)
        {
            index++;
        }
        var result = new byte[end - index];
        Buffer.BlockCopy(source, index, result, 0, result.Length);
        return result;
    }

    private static bool NeedsPad(byte[] integer) => (integer[0] & 0x80) != 0;

    private static byte[] PrependZero(byte[] integer)
    {
        var padded = new byte[integer.Length + 1];
        Buffer.BlockCopy(integer, 0, padded, 1, integer.Length);
        return padded;
    }
}
