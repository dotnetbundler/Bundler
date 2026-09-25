namespace DotNet.Bundler;

public interface IBundleBackend
{
    PackageFormat Format { get; }
    DesktopOperatingSystem OperatingSystem { get; }

    // One plan item may fan out to several artifacts (for example one MSI per
    // configured language); backends return every artifact they produced.
    Task<IReadOnlyList<BundleArtifact>> BuildAsync(
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
