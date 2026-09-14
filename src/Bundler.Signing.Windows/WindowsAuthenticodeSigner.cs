using DotNet.Bundler;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DotNet.Bundler.Signing.Windows;

public sealed class WindowsAuthenticodeSigner : IBundleSigner
{
    private const uint SignerSubjectFile = 1;
    private const uint SignerCertStore = 2;
    private const uint SignerCertPolicyChainNoRoot = 8;
    private const uint SignerNoAttr = 0;
    private const uint SignerTimestampRfc3161 = 2;
    private const uint CalgSha256 = 0x0000800c;
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    private readonly WindowsAuthenticodeSigningOptions options;

    public WindowsAuthenticodeSigner(WindowsAuthenticodeSigningOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        var hasPfx = !string.IsNullOrWhiteSpace(options.PfxFile);
        var hasThumbprint = !string.IsNullOrWhiteSpace(options.CertificateThumbprint);
        if (hasPfx == hasThumbprint)
        {
            throw new ArgumentException(
                "Configure exactly one signing certificate source: PfxFile or CertificateThumbprint.",
                nameof(options));
        }
    }

    public Task SignAsync(BundleSigningRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "The built-in Authenticode signer requires Windows. Use a custom IBundleSigner on other hosts.");
        }

        var path = Path.GetFullPath(request.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The file to sign was not found.", path);
        }

        using var certificate = LoadCertificate();
        ValidateCertificate(certificate);
        SignFile(path, certificate, options.TimestampUrl);
        VerifyEmbeddedCertificate(path, certificate);
        return Task.CompletedTask;
    }

    private X509Certificate2 LoadCertificate()
    {
        if (!string.IsNullOrWhiteSpace(options.PfxFile))
        {
            var path = Path.GetFullPath(options.PfxFile!);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The signing PFX file was not found.", path);
            }

            return new X509Certificate2(
                File.ReadAllBytes(path),
                options.PfxPassword,
                X509KeyStorageFlags.DefaultKeySet);
        }

        var requested = NormalizeThumbprint(options.CertificateThumbprint!);
        using var store = new X509Store(StoreName.My, options.CertificateStoreLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificate = store.Certificates
            .Cast<X509Certificate2>()
            .FirstOrDefault(item => NormalizeThumbprint(item.Thumbprint) == requested);
        return certificate is null
            ? throw new InvalidOperationException(
                $"No certificate with thumbprint '{requested}' was found in {options.CertificateStoreLocation}/My.")
            : new X509Certificate2(certificate);
    }

    private static void ValidateCertificate(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("The signing certificate does not have an accessible private key.");
        }
        if (DateTime.Now < certificate.NotBefore || DateTime.Now > certificate.NotAfter)
        {
            throw new InvalidOperationException("The signing certificate is not currently valid.");
        }

        var enhancedKeyUsage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (enhancedKeyUsage is not null &&
            !enhancedKeyUsage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CodeSigningOid))
        {
            throw new InvalidOperationException("The certificate is not valid for code signing.");
        }
    }

    private static void SignFile(string path, X509Certificate2 certificate, string? timestampUrl)
    {
        var fileName = IntPtr.Zero;
        var fileInfoPointer = IntPtr.Zero;
        var indexPointer = IntPtr.Zero;
        var certInfoPointer = IntPtr.Zero;
        var signerContext = IntPtr.Zero;
        try
        {
            fileName = Marshal.StringToHGlobalUni(path);
            var fileInfo = new SignerFileInfo
            {
                Size = (uint)Marshal.SizeOf<SignerFileInfo>(),
                FileName = fileName,
                FileHandle = IntPtr.Zero
            };
            fileInfoPointer = AllocateStructure(fileInfo);
            indexPointer = Marshal.AllocHGlobal(sizeof(uint));
            Marshal.WriteInt32(indexPointer, 0);
            var subject = new SignerSubjectInfo
            {
                Size = (uint)Marshal.SizeOf<SignerSubjectInfo>(),
                Index = indexPointer,
                SubjectChoice = SignerSubjectFile,
                FileInfo = fileInfoPointer
            };

            var certInfo = new SignerCertStoreInfo
            {
                Size = (uint)Marshal.SizeOf<SignerCertStoreInfo>(),
                SigningCertificate = certificate.Handle,
                CertPolicy = SignerCertPolicyChainNoRoot,
                CertStore = IntPtr.Zero
            };
            certInfoPointer = AllocateStructure(certInfo);
            var signerCert = new SignerCert
            {
                Size = (uint)Marshal.SizeOf<SignerCert>(),
                CertChoice = SignerCertStore,
                CertStoreInfo = certInfoPointer,
                WindowHandle = IntPtr.Zero
            };
            var signature = new SignerSignatureInfo
            {
                Size = (uint)Marshal.SizeOf<SignerSignatureInfo>(),
                HashAlgorithm = CalgSha256,
                AttrChoice = SignerNoAttr
            };

            var timestamp = string.IsNullOrWhiteSpace(timestampUrl) ? null : timestampUrl;
            var result = SignerSignEx2(
                0,
                ref subject,
                ref signerCert,
                ref signature,
                IntPtr.Zero,
                timestamp is null ? 0 : SignerTimestampRfc3161,
                timestamp is null ? null : Sha256Oid,
                timestamp,
                IntPtr.Zero,
                IntPtr.Zero,
                out signerContext,
                IntPtr.Zero,
                IntPtr.Zero);
            if (result != 0)
            {
                throw new Win32Exception(result, $"Authenticode signing failed for '{path}' (HRESULT 0x{result:X8}).");
            }
        }
        finally
        {
            if (signerContext != IntPtr.Zero)
            {
                SignerFreeSignerContext(signerContext);
            }
            if (certInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(certInfoPointer);
            if (indexPointer != IntPtr.Zero) Marshal.FreeHGlobal(indexPointer);
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPointer);
            if (fileName != IntPtr.Zero) Marshal.FreeHGlobal(fileName);
        }
    }

    private static void VerifyEmbeddedCertificate(string path, X509Certificate2 expected)
    {
        using var embedded = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        if (!string.Equals(
                NormalizeThumbprint(embedded.Thumbprint),
                NormalizeThumbprint(expected.Thumbprint),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The signed file does not contain the requested signing certificate.");
        }
    }

    private static IntPtr AllocateStructure<T>(T value)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value!, pointer, false);
        return pointer;
    }

    private static string NormalizeThumbprint(string value) =>
        new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SignerFileInfo
    {
        public uint Size;
        public IntPtr FileName;
        public IntPtr FileHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SignerSubjectInfo
    {
        public uint Size;
        public IntPtr Index;
        public uint SubjectChoice;
        public IntPtr FileInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SignerCertStoreInfo
    {
        public uint Size;
        public IntPtr SigningCertificate;
        public uint CertPolicy;
        public IntPtr CertStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SignerCert
    {
        public uint Size;
        public uint CertChoice;
        public IntPtr CertStoreInfo;
        public IntPtr WindowHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SignerSignatureInfo
    {
        public uint Size;
        public uint HashAlgorithm;
        public uint AttrChoice;
        public IntPtr AuthCode;
        public IntPtr AuthenticatedAttributes;
        public IntPtr UnauthenticatedAttributes;
    }

    [DllImport("mssign32.dll", CharSet = CharSet.Unicode)]
    private static extern int SignerSignEx2(
        uint flags,
        ref SignerSubjectInfo subjectInfo,
        ref SignerCert signerCert,
        ref SignerSignatureInfo signatureInfo,
        IntPtr providerInfo,
        uint timestampFlags,
        [MarshalAs(UnmanagedType.LPStr)] string? timestampAlgorithmOid,
        string? timestampUrl,
        IntPtr request,
        IntPtr sipData,
        out IntPtr signerContext,
        IntPtr cryptoPolicy,
        IntPtr reserved);

    [DllImport("mssign32.dll")]
    private static extern int SignerFreeSignerContext(IntPtr signerContext);
}
