using Bundler.Core.Configuration;
using Bundler.Core.Models;
using Bundler.Core.Planning;

namespace Bundler.Core.Backends;

public sealed class BundlePipeline(IEnumerable<IBundleBackend> backends)
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
            cancellationToken.ThrowIfCancellationRequested();
            if (!_backends.TryGetValue((item.Target.OperatingSystem, item.Format), out var backend))
            {
                throw new NotSupportedException(
                    $"No backend is registered for {item.Target.OperatingSystem}/{item.Format}.");
            }

            Directory.CreateDirectory(item.OutputDirectory);
            var workDirectory = Path.Combine(
                configuration.OutputDirectory,
                ".bundler-work",
                item.Target.RuntimeIdentifier,
                item.Format.ToString().ToLowerInvariant(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);

            try
            {
                var artifact = await backend.BuildAsync(
                    new BundleBuildContext(configuration, item, workDirectory),
                    cancellationToken);
                if (!File.Exists(artifact.Path) && !Directory.Exists(artifact.Path))
                {
                    throw new InvalidOperationException(
                        $"The {backend.Format} backend returned a missing artifact: {artifact.Path}");
                }

                artifacts.Add(artifact);
            }
            finally
            {
                if (Directory.Exists(workDirectory))
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
            }
        }

        return artifacts;
    }
}
