using DotNet.Bundler.Core.Update;
using System.Security.Cryptography;

namespace DotNet.Bundler.Update;

/// <summary>
/// ECDSA P-256/SHA-256 分离签名器。`.sig` 统一落 64 字节 IEEE-P1363（r||s 各 32B），
/// 与平台无关——BCL 在不同宿主各出一种格式（OpenSsl→DER、CNG→P1363），此处归一。
/// 验签与编码归一实现收在 <see cref="UpdateSignatureVerifier"/>（打包与应用两侧共用）。
/// </summary>
public static class EcdsaSigner
{
    public static byte[] Sign(byte[] data, UpdateKeyMaterial key)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(key.ToPrivateParameters());
        return UpdateSignatureVerifier.ToP1363(ecdsa.SignData(data, HashAlgorithmName.SHA256));
    }

    public static byte[] SignFile(string path, UpdateKeyMaterial key)
    {
        using var stream = File.OpenRead(path);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(key.ToPrivateParameters());
        return UpdateSignatureVerifier.ToP1363(ecdsa.SignData(stream, HashAlgorithmName.SHA256));
    }

    public static bool Verify(byte[] data, byte[] signature64, UpdateKeyMaterial publicKey) =>
        UpdateSignatureVerifier.Verify(data, signature64, publicKey);

    public static bool VerifyFile(string path, byte[] signature64, UpdateKeyMaterial publicKey) =>
        UpdateSignatureVerifier.VerifyFile(path, signature64, publicKey);

    // 编码归一的双向转换仍在此处保留历史调用签名，转调共享实现。
    internal static byte[] ToP1363(byte[] signature) =>
        UpdateSignatureVerifier.ToP1363(signature);

    internal static byte[] ToDer(byte[] signature64) =>
        UpdateSignatureVerifier.ToDer(signature64);
}
