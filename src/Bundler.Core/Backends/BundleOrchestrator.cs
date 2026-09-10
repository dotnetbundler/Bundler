using Bundler.Core.Configuration;
using Bundler.Core.Models;
using Bundler.Core.Planning;

namespace Bundler.Core.Backends;

public sealed class BundleOrchestrator(IEnumerable<IBundleBackend> backends)
{
    private readonly IReadOnlyDictionary<(DesktopOperatingSystem, PackageFormat), IBundleBackend> _backends =
        backends.ToDictionary(backend => (backend.OperatingSystem, backend.Format));

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var plan = BundlePlanner.Create(configuration);
        var artifacts = new List<BundleArtifact>();

        foreach (var item in plan.Items)
        {
            if (!_backends.TryGetValue((item.Target.OperatingSystem, item.Format), out var backend))
            {
                throw new NotSupportedException(
                    $"No backend is registered for {item.Target.OperatingSystem}/{item.Format}.");
            }

            artifacts.Add(await backend.BuildAsync(configuration, item, cancellationToken));
        }

        return artifacts;
    }
}
