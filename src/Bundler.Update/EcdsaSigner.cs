using DotNet.Bundler.Core.Update;
using System.Security.Cryptography;

namespace DotNet.Bundler.Update;

/// <summary>
/// ECDSA P-256/SHA-256 分离签名器。`.sig` 统一落 64 字节 IEEE-P1363（r||s 各 32B），
/// 与平台无关——BCL 在不同宿主各出一种格式（OpenSsl→DER、CNG→P1363），此处归一；
/// 验签时两种格式都试，任一通过即真。
/// </summary>
public static class EcdsaSigner
{
    public static byte[] Sign(byte[] data, UpdateKeyMaterial key)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(key.ToPrivateParameters());
        return ToP1363(ecdsa.SignData(data, HashAlgorithmName.SHA256));
    }

    public static byte[] SignFile(string path, UpdateKeyMaterial key)
    {
        using var stream = File.OpenRead(path);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(key.ToPrivateParameters());
        return ToP1363(ecdsa.SignData(stream, HashAlgorithmName.SHA256));
    }

    public static bool Verify(byte[] data, byte[] signature64, UpdateKeyMaterial publicKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(publicKey.ToPublicParameters());
        return TryVerify(ecdsa, signature64, stream => stream.Write(data, 0, data.Length));
    }

    public static bool VerifyFile(string path, byte[] signature64, UpdateKeyMaterial publicKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(publicKey.ToPublicParameters());
        return TryVerify(ecdsa, signature64, stream =>
        {
            using var input = File.OpenRead(path);
            input.CopyTo(stream);
        });
    }

    private static bool TryVerify(ECDsa ecdsa, byte[] signature64, Action<Stream> writeData)
    {
        foreach (var candidate in Candidates(signature64))
        {
            try
            {
                using var buffer = new MemoryStream();
                writeData(buffer);
                buffer.Position = 0;
                if (ecdsa.VerifyData(buffer, candidate, HashAlgorithmName.SHA256))
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

    private static IEnumerable<byte[]> Candidates(byte[] signature64)
    {
        yield return signature64;
        if (signature64.Length == 64)
        {
            yield return ToDer(signature64);
        }
    }

    // IEEE-P1363（r||s 定长拼接）↔ DER（SEQUENCE{INTEGER r, INTEGER s}）双向归一。
    internal static byte[] ToP1363(byte[] signature)
    {
        if (signature.Length == 64)
        {
            return signature;
        }
        // DER: 30 <len> 02 <rlen> <r> 02 <slen> <s>
        if (signature.Length < 8 || signature[0] != 0x30)
        {
            throw new InvalidOperationException("Unexpected ECDSA signature encoding.");
        }
        var offset = signature[1] == 0x81 ? 3 : 2; // 长/短长度前缀
        if (signature[offset] != 0x02)
        {
            throw new InvalidOperationException("Malformed ECDSA DER signature.");
        }
        var rLen = signature[offset + 1];
        var r = new byte[rLen];
        Buffer.BlockCopy(signature, offset + 2, r, 0, rLen);
        var sOffset = offset + 2 + rLen;
        if (signature[sOffset] != 0x02)
        {
            throw new InvalidOperationException("Malformed ECDSA DER signature.");
        }
        var sLen = signature[sOffset + 1];
        var s = new byte[sLen];
        Buffer.BlockCopy(signature, sOffset + 2, s, 0, sLen);
        var result = new byte[64];
        CopyPadded(r, result, 0);
        CopyPadded(s, result, 32);
        return result;
    }

    internal static byte[] ToDer(byte[] signature64)
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
