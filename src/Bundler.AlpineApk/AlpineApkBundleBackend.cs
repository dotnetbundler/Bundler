using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// Backend for the (Linux+musl, AlpineApk) pair — the format only exists on musl
/// targets, so RequiredLibC=Musl keeps glibc targets out of this backend.
/// </summary>
internal sealed class AlpineApkBundleBackend(AlpineApkBundleConfiguration settings) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.AlpineApk;
    public TargetOperatingSystem OperatingSystem => TargetOperatingSystem.Linux;
    public LibC? RequiredLibC => LibC.Musl;

    public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        var result = ApkPackageWriter.Build(
            context.Configuration, context.Item, settings,
            context.WorkDirectory, context.Logger, cancellationToken);
        return Task.FromResult<IReadOnlyList<BundleArtifact>>(
            [new BundleArtifact(Format, context.Item.Target.Target, result.Path)]);
    }
}
