using DotNet.Bundler;
using DotNet.Bundler.AppImage;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Deb;
using DotNet.Bundler.MacApp;
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Rpm;
using DotNet.Bundler.Wix;

namespace DotNet.Bundler.Cli;

/// <summary>
/// Maps a package format to its bundler facade. Format-specific knobs default
/// here — the CLI configuration surface (bundler.json) lands in CLI-2.
/// </summary>
internal static class FormatDispatcher
{
    public static Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        PackageFormat format,
        BundleConfiguration singleFormatConfiguration,
        IBundleLogger logger,
        CancellationToken cancellationToken = default) => format switch
        {
            PackageFormat.Nsis => new NsisBundler(
                    options: new NsisBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Msi => new WixBundler(
                    options: new WixBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.App => new MacAppBundler(
                    options: new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Dmg => new MacDmgBundler(
                    options: new MacDmgBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Pkg => new MacPkgBundler(
                    options: new MacPkgBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Deb => new DebBundler(
                    options: new DebBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Rpm => new RpmBundler(
                    options: new RpmBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.AppImage => new AppImageBundler(
                    options: new AppImageBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Zip or PackageFormat.TarGz => new ArchiveBundler(
                    options: new ArchiveBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            _ => throw new NotSupportedException($"Unknown format '{format}'.")
        };
}
