namespace DotNet.Bundler;

/// <summary>
/// Per-format entry point (the public `*Bundler` facades). A multi-format
/// fanout validates every requested format before building the first one:
/// configuration errors surface together, while host gates throw
/// <see cref="PlatformNotSupportedException"/> and stay per-format failures.
/// </summary>
public interface IFormatBundler
{
    void Validate(BundleConfiguration bundle);

    Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default);
}
