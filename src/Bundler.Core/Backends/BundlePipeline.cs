using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public sealed class BundlePipeline(
    IEnumerable<IBundleBackend> backends,
    IBundleLogger? logger = null)
{
    private readonly IBundleLogger _logger = logger ?? NullBundleLogger.Instance;
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
            if (!_backends.ContainsKey((item.Target.OperatingSystem, item.Format)))
            {
                throw new NotSupportedException(
                    $"No backend is registered for {item.Target.OperatingSystem}/{item.Format}.");
            }
        }

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backend = _backends[(item.Target.OperatingSystem, item.Format)];

            Directory.CreateDirectory(item.OutputDirectory);
            var workDirectory = Path.Combine(
                configuration.OutputDirectory,
                ".bundler-work",
                item.Target.RuntimeIdentifier,
                item.Format.ToString().ToLowerInvariant(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            _logger.Log(BundleLogLevel.Information, $"Building {item.Format} package for {item.Target.RuntimeIdentifier}.");

            try
            {
                var produced = await backend.BuildAsync(
                    new BundleBuildContext(configuration, item, workDirectory, _logger),
                    cancellationToken);
                foreach (var artifact in produced)
                {
                    if (!File.Exists(artifact.Path) && !Directory.Exists(artifact.Path))
                    {
                        throw new InvalidOperationException(
                            $"The {backend.Format} backend returned a missing artifact: {artifact.Path}");
                    }

                    artifacts.Add(artifact);
                    _logger.Log(BundleLogLevel.Information, $"Created {artifact.Path}.");
                }
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
