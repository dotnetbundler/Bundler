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
/// Maps a package format to its bundler facade, carrying the format's
/// bundler.json section (or defaults when absent).
/// </summary>
internal static class FormatDispatcher
{
    public static Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        PackageFormat format,
        BundleConfiguration singleFormatConfiguration,
        CliResolvedConfiguration resolved,
        IBundleLogger logger,
        CancellationToken cancellationToken = default) => format switch
        {
            PackageFormat.Nsis => new NsisBundler(
                    resolved.Nsis, new NsisBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Msi => new WixBundler(
                    resolved.Msi, new WixBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.App => new MacAppBundler(
                    resolved.App, new MacAppBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Dmg => new MacDmgBundler(
                    resolved.Dmg, resolved.App, new MacDmgBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Pkg => new MacPkgBundler(
                    resolved.Pkg, resolved.App, new MacPkgBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Deb => new DebBundler(
                    resolved.Deb, new DebBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Rpm => new RpmBundler(
                    resolved.Rpm, new RpmBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.AppImage => new AppImageBundler(
                    resolved.AppImage, new AppImageBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            PackageFormat.Zip or PackageFormat.TarGz => new ArchiveBundler(
                    resolved.Archive, new ArchiveBundlerOptions { Logger = logger })
                .BuildAsync(singleFormatConfiguration, cancellationToken),
            _ => throw new NotSupportedException($"Unknown format '{format}'.")
        };
}
