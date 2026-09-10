using Bundler.Core.Configuration;

namespace Bundler.Core.Backends;

public sealed class BundleOrchestrator(IEnumerable<IBundleBackend> backends)
{
    private readonly BundlePipeline _pipeline = new(backends);

    public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        _pipeline.BuildAsync(configuration, cancellationToken);
}
