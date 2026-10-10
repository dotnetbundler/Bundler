using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public sealed class BundlePipeline(
    IEnumerable<IBundleBackend> backends,
    IBundleLogger? logger = null)
{
    private readonly IBundleLogger _logger = logger ?? NullBundleLogger.Instance;
    private readonly IReadOnlyDictionary<(DesktopOperatingSystem, PackageFormat), IBundleBackend> _backends =
        backends.GroupBy(backend => (backend.OperatingSystem, backend.Format))
            .ToDictionary(group => group.Key, group =>
                group.Count() == 1 ? group.Single()
                    : throw new ArgumentException(
                        $"Duplicate backend registered for {group.Key.Item1}/{group.Key.Item2}."));

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var plan = BundlePlanner.Create(configuration);
        var artifacts = new List<BundleArtifact>();
        var producedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                item.Target.Target,
                item.Format.ToString().ToLowerInvariant(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            _logger.Log(BundleLogLevel.Information, $"Building {item.Format} package for {item.Target.Target}.");

            try
            {
                var produced = await backend.BuildAsync(
                    new BundleBuildContext(configuration, item, workDirectory, _logger),
                    cancellationToken);
                if (produced is null)
                {
                    throw new InvalidOperationException(
                        $"The {backend.Format} backend violated its contract: BuildAsync returned null.");
                }
                foreach (var artifact in produced)
                {
                    if (!File.Exists(artifact.Path) && !Directory.Exists(artifact.Path))
                    {
                        throw new InvalidOperationException(
                            $"The {backend.Format} backend returned a missing artifact: {artifact.Path}");
                    }
                    if (!producedPaths.Add(artifact.Path))
                    {
                        throw new ArgumentException(
                            $"Artifact path collision: '{artifact.Path}' was already produced in this run" +
                            " (a custom name override such as archiveName can bypass the default per-target naming).");
                    }

                    artifacts.Add(artifact);
                    _logger.Log(BundleLogLevel.Information, $"Created {artifact.Path}.");
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workDirectory))
                    {
                        Directory.Delete(workDirectory, recursive: true);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 清理失败只能告警：遮蔽原构建异常反而丢掉真正的失败原因。
                    _logger.Log(BundleLogLevel.Warning,
                        $"Failed to clean up work directory '{workDirectory}': {exception.Message}");
                }
            }
        }

        return artifacts;
    }
}
