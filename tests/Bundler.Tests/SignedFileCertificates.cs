using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

internal static class SignedFileCertificates
{
    private const uint CertQueryObjectFile = 0x00000001;
    private const uint CertQueryContentFlagPkcs7SignedEmbed = 0x00000200;
    private const uint CertQueryFormatFlagBinary = 0x00000002;

    public static bool EmbeddedSignatureContains(string path, X509Certificate2 certificate)
    {
        var signature = ReadEmbeddedSignature(path);
        return signature.AsSpan().IndexOf(certificate.RawData) >= 0;
    }

    private static byte[] ReadEmbeddedSignature(string path)
    {
        if (!CryptQueryObject(
                CertQueryObjectFile,
                path,
                CertQueryContentFlagPkcs7SignedEmbed,
                CertQueryFormatFlagBinary,
                0,
                IntPtr.Zero,
                out _,
                out _,
                out _,
                IntPtr.Zero,
                IntPtr.Zero,
                out var context) ||
            context == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Embedded Authenticode signature was not found in '{path}'.");
        }

        try
        {
            var blob = Marshal.PtrToStructure<CryptoApiBlob>(context);
            var bytes = new byte[checked((int)blob.DataLength)];
            Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            CryptMemFree(context);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptoApiBlob
    {
        public uint DataLength;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptQueryObject(
        uint objectType,
        string objectName,
        uint expectedContentTypeFlags,
        uint expectedFormatTypeFlags,
        uint flags,
        IntPtr reserved,
        out uint encodingType,
        out uint contentType,
        out uint formatType,
        IntPtr certStore,
        IntPtr message,
        out IntPtr context);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern void CryptMemFree(IntPtr buffer);
}
