using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

internal static class SignedFileCertificates
{
    private const uint CertQueryObjectFile = 0x00000001;
    private const uint CertQueryContentFlagPkcs7SignedEmbed = 0x00000400;
    private const uint CertQueryFormatFlagBinary = 0x00000002;

    public static bool EmbeddedSignatureContains(string path, X509Certificate2 certificate)
    {
        var signature = ReadEmbeddedSignature(path);
        return signature.AsSpan().IndexOf(certificate.RawData) >= 0;
    }

    // CryptQueryObject 对内嵌签名的返回口径随版本不同：ppvContext 直接给 PKCS7
    // blob；有的宿主只填 phCertStore，此时逐张枚举证书——证书 DER 会原样出现在
    // PKCS7 blob 中，拼接后仍可用同一处子串断言。
    private static byte[] ReadEmbeddedSignature(string path)
    {
        var queried = CryptQueryObject(
            CertQueryObjectFile,
            path,
            CertQueryContentFlagPkcs7SignedEmbed,
            CertQueryFormatFlagBinary,
            0,
            out _,
            out _,
            out _,
            out var certStore,
            out var message,
            out var context);
        try
        {
            if (!queried)
            {
                throw new InvalidOperationException($"Embedded Authenticode signature was not found in '{path}'.");
            }
            if (context != IntPtr.Zero)
            {
                var blob = Marshal.PtrToStructure<CryptoApiBlob>(context);
                var bytes = new byte[checked((int)blob.DataLength)];
                Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            if (certStore != IntPtr.Zero)
            {
                return ReadStoreCertificates(certStore);
            }
            throw new InvalidOperationException($"Embedded Authenticode signature was not found in '{path}'.");
        }
        finally
        {
            if (context != IntPtr.Zero) CryptMemFree(context);
            if (message != IntPtr.Zero) CryptMsgClose(message);
            if (certStore != IntPtr.Zero) CertCloseStore(certStore, 0);
        }
    }

    private static byte[] ReadStoreCertificates(IntPtr certStore)
    {
        using var stream = new MemoryStream();
        var previous = IntPtr.Zero;
        try
        {
            while (true)
            {
                var current = CertEnumCertificatesInStore(certStore, previous);
                previous = current;
                if (current == IntPtr.Zero)
                {
                    break;
                }
                var context = Marshal.PtrToStructure<CertContext>(current);
                var certificate = new byte[checked((int)context.EncodedLength)];
                Marshal.Copy(context.EncodedData, certificate, 0, certificate.Length);
                stream.Write(certificate, 0, certificate.Length);
            }
        }
        finally
        {
            if (previous != IntPtr.Zero) CertFreeCertificateContext(previous);
        }
        if (stream.Length == 0)
        {
            throw new InvalidOperationException("The embedded signature contains no certificates.");
        }
        return stream.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptoApiBlob
    {
        public uint DataLength;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertContext
    {
        public uint EncodingType;
        public IntPtr EncodedData;
        public uint EncodedLength;
        public IntPtr CertInfo;
        public IntPtr CertStore;
    }

    [DllImport("crypt32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptQueryObject(
        uint objectType,
        string objectName,
        uint expectedContentTypeFlags,
        uint expectedFormatTypeFlags,
        uint flags,
        out uint encodingType,
        out uint contentType,
        out uint formatType,
        out IntPtr certStore,
        out IntPtr message,
        out IntPtr context);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern void CryptMemFree(IntPtr buffer);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern bool CryptMsgClose(IntPtr message);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern IntPtr CertEnumCertificatesInStore(IntPtr certStore, IntPtr previousCertificateContext);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern bool CertFreeCertificateContext(IntPtr certificateContext);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern bool CertCloseStore(IntPtr certStore, uint flags);
}
