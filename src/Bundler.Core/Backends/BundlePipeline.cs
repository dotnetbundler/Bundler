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
        var producedPaths = new HashSet<string>(PathText.Comparer);
        // 崩溃进程留下的 <target>/<fmt>/<guid> 残骸无清理方——按 mtime 阈值清扫；
        // 并发在跑的同根构建不受影响（其工作目录 mtime 都在阈值内）。
        SweepStaleWorkDirectories(
            Path.Combine(configuration.OutputDirectory, ".bundler-work"));

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
                    if (artifact is null)
                    {
                        throw new InvalidOperationException(
                            $"The {backend.Format} backend violated its contract: produced list contains null.");
                    }
                    if (!File.Exists(artifact.Path) && !Directory.Exists(artifact.Path))
                    {
                        throw new InvalidOperationException(
                            $"The {backend.Format} backend returned a missing artifact: {artifact.Path}");
                    }
                    // 后端报告相对/冗余路径时归一再判重——
                    // "out/app.zip" 与 "out/./app.zip" 是同一碰撞。
                    if (!producedPaths.Add(Path.GetFullPath(artifact.Path)))
                    {
                        throw new InvalidOperationException(
                            $"Artifact path collision: '{artifact.Path}' was already produced in this run" +
                            " (a custom name override such as archiveName can bypass the default per-target naming).");
                    }

                    // 文件产物统一挂 sha256sum 格式旁车——发射点收在管线单源，
                    // 不再依赖各后端自觉；目录产物（.app）不挂。
                    if (File.Exists(artifact.Path))
                    {
                        var hash = Hashes.Sha256HexFile(artifact.Path);
                        File.WriteAllText(artifact.Path + ".sha256",
                            hash + "  " + Path.GetFileName(artifact.Path) + "\n",
                            new System.Text.UTF8Encoding(false));
                        artifacts.Add(artifact);
                        _logger.Log(BundleLogLevel.Information,
                            $"Created {artifact.Path} (sha256 {hash}).");
                    }
                    else
                    {
                        artifacts.Add(artifact);
                        _logger.Log(BundleLogLevel.Information, $"Created {artifact.Path}.");
                    }
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
                       parent.StartsWith(scaffoldRoot + Path.DirectorySeparatorChar, PathText.Comparison))
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

    // 每次运行的工作目录是 <target>/<format>/<guid>——崩溃进程留下的残骸没有清理方，
    // 只按 mtime 阈值清扫（与 UpdateBootstrapper 提取根清扫同口径）；阈值内的
    // 在跑构建不受影响。清扫范围严格限定在 workRoot/<target>/<format>/<workdir> 三层。
    private static void SweepStaleWorkDirectories(string workRoot)
    {
        try
        {
            if (!Directory.Exists(workRoot))
            {
                return;
            }
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var targetDirectory in Directory.EnumerateDirectories(workRoot))
            {
                foreach (var formatDirectory in Directory.EnumerateDirectories(targetDirectory))
                {
                    foreach (var workDirectory in Directory.EnumerateDirectories(formatDirectory))
                    {
                        try
                        {
                            if (Directory.GetLastWriteTimeUtc(workDirectory) < cutoff)
                            {
                                Directory.Delete(workDirectory, recursive: true);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 清扫是纯辅助动作——任何失败都不该挡住构建主流程。
        }
    }
}
