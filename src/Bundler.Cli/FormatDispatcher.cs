using DotNet.Bundler;
using DotNet.Bundler.AlpineApk;
using DotNet.Bundler.Archive;
using DotNet.Bundler.Deb;
using DotNet.Bundler.MacApp;
#if BUNDLER_HOST_MACOS
using DotNet.Bundler.MacDmg;
using DotNet.Bundler.MacPkg;
#endif
using DotNet.Bundler.Nsis;
using DotNet.Bundler.Rpm;
#if BUNDLER_HOST_WINDOWS
using DotNet.Bundler.Wix;
#endif
#if BUNDLER_HOST_LINUX
using DotNet.Bundler.AppImage;
#endif

namespace DotNet.Bundler.Cli;

/// <summary>
/// Maps a package format to its bundler facade, carrying the format's
/// bundler.json section (or defaults when absent).
/// </summary>
internal static class FormatDispatcher
{
    public static IFormatBundler Create(
        PackageFormat format,
        CliResolvedConfiguration resolved,
        IBundleLogger logger) => format switch
        {
            PackageFormat.Nsis => new NsisBundler(
                resolved.Nsis, new NsisBundlerOptions { Logger = logger }),
#if BUNDLER_HOST_WINDOWS
            PackageFormat.Msi => new WixBundler(
                resolved.Msi, new WixBundlerOptions { Logger = logger }),
#endif
            PackageFormat.App => new MacAppBundler(
                resolved.App, new MacAppBundlerOptions { Logger = logger }),
#if BUNDLER_HOST_MACOS
            PackageFormat.Dmg => new MacDmgBundler(
                resolved.Dmg, resolved.App, new MacDmgBundlerOptions { Logger = logger }),
            PackageFormat.Pkg => new MacPkgBundler(
                resolved.Pkg, resolved.App, new MacPkgBundlerOptions { Logger = logger }),
#endif
            PackageFormat.Deb => new DebBundler(
                resolved.Deb, new DebBundlerOptions { Logger = logger }),
            PackageFormat.Rpm => new RpmBundler(
                resolved.Rpm, new RpmBundlerOptions { Logger = logger }),
#if BUNDLER_HOST_LINUX
            PackageFormat.AppImage => new AppImageBundler(
                resolved.AppImage, new AppImageBundlerOptions { Logger = logger }),
#endif
            PackageFormat.Zip or PackageFormat.TarGz => new ArchiveBundler(
                resolved.Archive, new ArchiveBundlerOptions { Logger = logger }),
            PackageFormat.AlpineApk => new AlpineApkBundler(
                resolved.AlpineApk, new AlpineApkBundlerOptions { Logger = logger }),
            // The arm for each host-restricted format is compiled only into its
            // own host build; anywhere else the format name still parses but
            // dispatch fails closed with an explicit platform error.
#if !BUNDLER_HOST_WINDOWS
            PackageFormat.Msi =>
                throw new PlatformNotSupportedException(
                    "Package format 'msi' requires a Windows host."),
#endif
#if !BUNDLER_HOST_MACOS
            PackageFormat.Dmg =>
                throw new PlatformNotSupportedException(
                    "Package format 'dmg' requires a macOS host."),
            PackageFormat.Pkg =>
                throw new PlatformNotSupportedException(
                    "Package format 'pkg' requires a macOS host."),
#endif
#if !BUNDLER_HOST_LINUX
            PackageFormat.AppImage =>
                throw new PlatformNotSupportedException(
                    "Package format 'appimage' requires a Linux host."),
#endif
            _ => throw new NotSupportedException($"Unknown format '{format}'.")
        };
}
