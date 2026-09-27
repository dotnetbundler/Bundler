using DotNet.Bundler;

namespace DotNet.Bundler.Rpm;

internal sealed class RpmBundleBackend(RpmBundleConfiguration settings) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.Rpm;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Linux;

    public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        // The writer is pure managed code: no host-OS gate and no external tools.
        var result = RpmPackageWriter.Build(
            context.Configuration, context.Item, settings, context.Logger);
        return Task.FromResult<IReadOnlyList<BundleArtifact>>(
            [new BundleArtifact(PackageFormat.Rpm, context.Item.Target.RuntimeIdentifier,
                result.OutputPath)]);
    }
}
