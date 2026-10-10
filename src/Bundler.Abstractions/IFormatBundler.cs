namespace DotNet.Bundler;

/// <summary>
/// Per-format entry point (the public `*Bundler` facades). A multi-format
/// fanout validates every requested format before building the first one:
/// configuration errors surface together, while host gates throw
/// <see cref="PlatformNotSupportedException"/> and stay per-format failures.
/// </summary>
public interface IFormatBundler
{
    /// <summary>
    /// 扇出前旋钮预检：本格式在共享校验之后、任何构建之前执行。
    /// 配置类错误抛 <see cref="ArgumentException"/> 族（入口聚合成一次报完），
    /// 宿主门禁抛 <see cref="PlatformNotSupportedException"/>（逐格式容错跳过），
    /// 不支持的格式抛 <see cref="NotSupportedException"/>。
    /// </summary>
    void Validate(BundleConfiguration bundle);

    /// <summary>
    /// 构建本格式产物。实现自身须先跑 <see cref="Validate"/> 的同一套校验
    /// （不经聚合入口直接调用时也保持配置错即 <see cref="ArgumentException"/> 族、
    /// 宿主门即 <see cref="PlatformNotSupportedException"/> 的异常契约），
    /// 其后才执行构建；构建期 IO/工具失败不属于这两类，逐格式冒泡。
    /// 经共享校验入口调用时，共享校验失败可抛 <see cref="BundleValidationException"/>。
    /// </summary>
    Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default);
}
