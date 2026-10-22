namespace DotNet.Bundler;

public interface IBundleSigner
{
    Task SignAsync(BundleSigningRequest request, CancellationToken cancellationToken = default);
}

public sealed record BundleSigningRequest(
    string Path,
    BundleSigningArtifactKind ArtifactKind,
    string ProductName,
    string Target);

public enum BundleSigningArtifactKind
{
    PayloadExecutable,
    PayloadFile,
    NativeComponent,
    Installer,
    Uninstaller
}
