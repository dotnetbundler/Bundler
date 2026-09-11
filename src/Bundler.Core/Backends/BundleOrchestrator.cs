using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public sealed class BundleOrchestrator(
    IEnumerable<IBundleBackend> backends,
    IBundleLogger? logger = null)
{
    private readonly BundlePipeline _pipeline = new(backends, logger);

    public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        _pipeline.BuildAsync(configuration, cancellationToken);
}
