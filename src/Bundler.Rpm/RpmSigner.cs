using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace DotNet.Bundler.Rpm;

/// <summary>
/// Produces the v3 binary-document signature packets carried by
/// <c>RPMSIGTAG_RSA</c> (tag 268, signed over the main header alone — the
/// form libzypp/zypper requires) and <c>RPMSIGTAG_PGP</c> (tag 1002, signed
/// over main header + compressed payload — what <c>rpm -K</c> and
/// <c>rpmsign</c> verify). Signing is entirely managed (BouncyCastle);
/// the signing key never leaves the caller-supplied file.
/// </summary>
internal static class RpmSigner
{
    internal static byte[] Sign(byte[] signedData, string keyFile, string? passphrase)
    {
        var generator = CreateGenerator(keyFile, passphrase);
        generator.Update(signedData);
        return generator.Generate().GetEncoded();
    }

    /// <summary>前缀+流两段喂入：RPMSIGTAG_PGP 签 主头+压缩载荷——载荷文件不驻内存。</summary>
    internal static byte[] Sign(byte[] prefix, Stream rest, string keyFile, string? passphrase)
    {
        var generator = CreateGenerator(keyFile, passphrase);
        generator.Update(prefix);
        var buffer = new byte[81920];
        int read;
        while ((read = rest.Read(buffer, 0, buffer.Length)) > 0)
        {
            generator.Update(buffer, 0, read);
        }
        return generator.Generate().GetEncoded();
    }

    private static PgpV3SignatureGenerator CreateGenerator(string keyFile, string? passphrase)
    {
        var secretKey = LoadSigningKey(keyFile);
        PgpPrivateKey privateKey;
        try
        {
            privateKey = secretKey.ExtractPrivateKey(passphrase?.ToCharArray());
        }
        catch (Exception error) when (error is not FileNotFoundException && error is not DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"RPM signing failed: cannot unlock '{keyFile}'. " +
                "Check Signing.Passphrase.", error);
        }
        var generator = new PgpV3SignatureGenerator(
            PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        generator.InitSign(PgpSignature.BinaryDocument, privateKey);
        return generator;
    }

    private static PgpSecretKey LoadSigningKey(string keyFile)
    {
        using var input = PgpUtilities.GetDecoderStream(File.OpenRead(keyFile));
        var bundle = new PgpSecretKeyRingBundle(input);
        foreach (PgpSecretKeyRing ring in bundle.GetKeyRings())
        {
            foreach (PgpSecretKey key in ring.GetSecretKeys())
            {
                if (key.IsSigningKey && !key.IsPrivateKeyEmpty)
                {
                    return key;
                }
            }
        }
        throw new InvalidOperationException(
            $"RPM signing failed: '{keyFile}' contains no usable signing key. " +
            "Point Signing.KeyFile at an ASCII-armored or binary OpenPGP secret key.");
    }
}
