using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace DotNet.Bundler.Rpm;

/// <summary>
/// Produces the OpenPGP signature packet carried by <c>RPMSIGTAG_PGP</c>
/// (tag 1002): a v3 binary-document signature over the concatenated
/// main header + compressed payload bytes — the same bytes <c>rpm -K</c>
/// and <c>rpmsign</c> verify. Signing is entirely managed (BouncyCastle);
/// the signing key never leaves the caller-supplied file.
/// </summary>
internal static class RpmSigner
{
    internal static byte[] Sign(byte[] signedData, string keyFile, string? passphrase)
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
                "Check SigningKeyPassphrase.", error);
        }
        var generator = new PgpV3SignatureGenerator(
            PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        generator.InitSign(PgpSignature.BinaryDocument, privateKey);
        generator.Update(signedData);
        return generator.Generate().GetEncoded();
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
            "Point SigningKeyFile at an ASCII-armored or binary OpenPGP secret key.");
    }
}
