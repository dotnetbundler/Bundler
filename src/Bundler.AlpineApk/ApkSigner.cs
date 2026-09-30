using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;

namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// RSA signing for apk v2 packages: the signature segment carries a
/// <c>.SIGN.RSA.&lt;key-file-name&gt;.rsa.pub</c> member holding the raw
/// PKCS#1 v1.5 RSA signature over the control gzip stream (SHA1), matching
/// what abuild produces with <c>openssl dgst -sha1 -sign</c>. Signing is
/// entirely managed (BouncyCastle) so it runs on any host.
/// </summary>
internal static class ApkSigner
{
    internal static byte[] Sign(byte[] controlGzip, string keyFile, string? passphrase)
    {
        var key = LoadPrivateKey(keyFile, passphrase);
        var signer = new RsaDigestSigner(new Sha1Digest());
        signer.Init(true, key);
        signer.BlockUpdate(controlGzip, 0, controlGzip.Length);
        return signer.GenerateSignature();
    }

    private static AsymmetricKeyParameter LoadPrivateKey(string keyFile, string? passphrase)
    {
        var full = Path.GetFullPath(keyFile);
        object parsed;
        using (var reader = new StreamReader(full))
        {
            var pem = new PemReader(reader, new StaticPasswordFinder(passphrase ?? ""));
            parsed = pem.ReadObject()
                ?? throw new ArgumentException(
                    $"The .apk signing key is not a PEM file: {full}");
        }

        // "RSA PRIVATE KEY" yields a key pair; "PRIVATE KEY" (PKCS#8) yields
        // the private parameter set directly.
        var parameters = parsed switch
        {
            AsymmetricCipherKeyPair pair => pair.Private,
            AsymmetricKeyParameter single => single,
            _ => throw new ArgumentException(
                $"The .apk signing key is not an RSA private key: {full}")
        };
        if (parameters is not RsaPrivateCrtKeyParameters rsa)
        {
            throw new ArgumentException(
                $"The .apk signing key is not an RSA private key: {full}");
        }
        return rsa;
    }

    private sealed class StaticPasswordFinder(string password) : IPasswordFinder
    {
        public char[] GetPassword() => password.ToCharArray();
    }
}
