namespace DotNet.Bundler;

public interface IBundleBackend
{
    PackageFormat Format { get; }
    TargetOperatingSystem OperatingSystem { get; }

    // 后端服务哪个 libc 变体；null = 任意（不分 libc 的格式）。AlpineApk 声明 Musl。
    // 用显式成员不用接口默认实现——netstandard2.0 目标要在 .NET Framework MSBuild 里跑。
    LibC? RequiredLibC { get; }

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
    string Target,
    string Path);
