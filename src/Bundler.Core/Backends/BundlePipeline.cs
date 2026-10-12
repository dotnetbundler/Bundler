using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public sealed class BundlePipeline(
    IEnumerable<IBundleBackend> backends,
    IBundleLogger? logger = null)
{
    private readonly IBundleLogger _logger = logger ?? NullBundleLogger.Instance;
    // (os, format) 下可挂多个按 libc 区分的后端（如将来 musl 专属 zip 变体）；
    // RequiredLibC 相同的才算重复注册。
    private readonly IReadOnlyDictionary<(TargetOperatingSystem, PackageFormat), IReadOnlyList<IBundleBackend>> _backends =
        backends.GroupBy(backend => (backend.OperatingSystem, backend.Format))
            .ToDictionary(group => group.Key, group =>
            {
                var list = (IReadOnlyList<IBundleBackend>)group.ToList();
                var duplicate = list.GroupBy(backend => backend.RequiredLibC)
                    .FirstOrDefault(candidates => candidates.Count() > 1);
                return duplicate is null ? list
                    : throw new ArgumentException(
                        $"Duplicate backend registered for {group.Key.Item1}/{group.Key.Item2} (libc={duplicate.Key}).");
            });

    private IBundleBackend? ResolveBackend(BundleTarget target, PackageFormat format)
    {
        if (!_backends.TryGetValue((target.OperatingSystem, format), out var candidates))
        {
            return null;
        }
        return candidates.FirstOrDefault(b => b.RequiredLibC == target.LibC)
            ?? candidates.FirstOrDefault(b => b.RequiredLibC is null);
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var plan = BundlePlanner.Create(configuration);
        var artifacts = new List<BundleArtifact>();
        var producedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in plan.Items)
        {
            if (ResolveBackend(item.Target, item.Format) is null)
            {
                throw new NotSupportedException(
                    $"No backend is registered for {item.Target.Target}/{item.Format}.");
            }
        }

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backend = ResolveBackend(item.Target, item.Format)!;

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
                // 逐层回收空脚手架父目录（<fmt>→<target>→.bundler-work）：
                // 非空目录的删除会抛错即停——只清 .bundler-work 壳层，绝不越界动 output 本体。
                var scaffoldRoot = Path.Combine(configuration.OutputDirectory, ".bundler-work");
                var parent = Path.GetDirectoryName(workDirectory);
                while (parent is not null && parent != scaffoldRoot &&
                       parent.StartsWith(scaffoldRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    try
                    {
                        Directory.Delete(parent);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }
                    parent = Path.GetDirectoryName(parent);
                }
                try
                {
                    Directory.Delete(scaffoldRoot);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // .bundler-work 根非空或已消失都不需要告警——空壳而已。
                }
            }
        }

        return artifacts;
    }
}
