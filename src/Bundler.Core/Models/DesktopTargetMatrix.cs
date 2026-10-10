using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public static class DesktopTargetMatrix
{
    public static bool Supports(BundleTarget target, PackageFormat format) =>
        Supports(target.OperatingSystem, format) &&
        ArtifactNaming.ArchToken(target, format) is not null;

    public static bool Supports(DesktopOperatingSystem operatingSystem, PackageFormat format) =>
        operatingSystem switch
        {
            DesktopOperatingSystem.Windows => format is PackageFormat.Nsis or PackageFormat.Msi
                or PackageFormat.Zip or PackageFormat.TarGz,
            DesktopOperatingSystem.MacOS => format is PackageFormat.App or PackageFormat.Dmg or PackageFormat.Pkg
                or PackageFormat.Zip or PackageFormat.TarGz,
            DesktopOperatingSystem.Linux => format is PackageFormat.Deb or PackageFormat.Rpm or PackageFormat.AppImage
                or PackageFormat.Zip or PackageFormat.TarGz,
            DesktopOperatingSystem.LinuxMusl => format is PackageFormat.Zip or PackageFormat.TarGz
                or PackageFormat.AlpineApk or PackageFormat.AppImage,
            _ => false
        };
}
