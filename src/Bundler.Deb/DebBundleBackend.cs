using DotNet.Bundler;

namespace DotNet.Bundler.Deb;

internal sealed class DebBundleBackend(DebBundleConfiguration settings) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.Deb;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Linux;

    public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        // The writer is pure managed code: no host-OS gate and no external tools.
        var result = DebPackageWriter.Build(
            context.Configuration, context.Item, settings, context.Logger);
        return Task.FromResult<IReadOnlyList<BundleArtifact>>(
            [new BundleArtifact(PackageFormat.Deb, context.Item.Target.Target,
                result.OutputPath)]);
    }
}
