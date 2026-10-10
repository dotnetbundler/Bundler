using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// Backend for the (LinuxMusl, AlpineApk) pair — the format only exists on musl
/// targets, so the pipeline's (OS, format) key keeps glibc targets out.
/// </summary>
internal sealed class AlpineApkBundleBackend(AlpineApkBundleConfiguration settings) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.AlpineApk;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.LinuxMusl;

    public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        var result = ApkPackageWriter.Build(context.Configuration, context.Item, settings, context.Logger);
        return Task.FromResult<IReadOnlyList<BundleArtifact>>(
            [new BundleArtifact(Format, context.Item.Target.Target, result.Path)]);
    }
}
