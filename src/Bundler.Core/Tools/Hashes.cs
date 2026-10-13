using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotNet.Bundler.Core;

/// <summary>Shared hashing helpers: lowercase-hex digests used by package writers and tool verification.</summary>
internal static class Hashes
{
    internal static string ToLowerHex(byte[] hash)
    {
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    internal static byte[] Sha256(byte[] content)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(content);
    }

    internal static byte[] Sha256(Stream stream)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(stream);
    }

    internal static string Sha256Hex(byte[] content) => ToLowerHex(Sha256(content));

    internal static string Sha256Hex(Stream stream) => ToLowerHex(Sha256(stream));

    internal static string Sha256HexFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Sha256Hex(stream);
    }

    internal static string Sha1Hex(byte[] content)
    {
        using var sha1 = SHA1.Create();
        return ToLowerHex(sha1.ComputeHash(content));
    }

    internal static string Sha1Hex(Stream stream)
    {
        using var sha1 = SHA1.Create();
        return ToLowerHex(sha1.ComputeHash(stream));
    }

    internal static string Md5Hex(byte[] content)
    {
        using var md5 = MD5.Create();
        return ToLowerHex(md5.ComputeHash(content));
    }

    internal static string Md5HexFile(string path)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ToLowerHex(md5.ComputeHash(stream));
    }
}
