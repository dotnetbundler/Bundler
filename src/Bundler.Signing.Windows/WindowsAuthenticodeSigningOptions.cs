using System.Security.Cryptography.X509Certificates;

namespace DotNet.Bundler.Signing.Windows;

public sealed class WindowsAuthenticodeSigningOptions
{
    public string? PfxFile { get; init; }
    public string? PfxPassword { get; init; }
    public string? CertificateThumbprint { get; init; }
    public StoreLocation CertificateStoreLocation { get; init; } = StoreLocation.CurrentUser;
    public string? TimestampUrl { get; init; }
}
