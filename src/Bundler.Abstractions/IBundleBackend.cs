namespace DotNet.Bundler;

public interface IBundleBackend
{
    PackageFormat Format { get; }
    DesktopOperatingSystem OperatingSystem { get; }

    Task<BundleArtifact> BuildAsync(
        BundleBuildContext context,
        CancellationToken cancellationToken = default);
}

public sealed record BundleBuildContext(
    BundleConfiguration Configuration,
    BundlePlanItem Item,
    string WorkDirectory,
    IBundleLogger Logger);

public sealed record BundleArtifact(
    PackageFormat Format,
    string RuntimeIdentifier,
    string Path);
