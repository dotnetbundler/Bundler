namespace Bundler.Core.Models;

public static class DesktopTargetMatrix
{
    public static bool Supports(DesktopOperatingSystem operatingSystem, PackageFormat format) =>
        operatingSystem switch
        {
            DesktopOperatingSystem.Windows => format is PackageFormat.Nsis or PackageFormat.Msi,
            DesktopOperatingSystem.MacOS => format is PackageFormat.App or PackageFormat.Dmg,
            DesktopOperatingSystem.Linux => format is PackageFormat.Deb or PackageFormat.Rpm or PackageFormat.AppImage,
            _ => false
        };
}
