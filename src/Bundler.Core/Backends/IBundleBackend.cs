using Bundler.Core.Configuration;
using Bundler.Core.Models;
using Bundler.Core.Planning;

namespace Bundler.Core.Backends;

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
    string WorkDirectory);

public sealed record BundleArtifact(
    PackageFormat Format,
    string RuntimeIdentifier,
    string Path);
