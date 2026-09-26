using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public static class DesktopTargetMatrix
{
    public static bool Supports(BundleTarget target, PackageFormat format) =>
        Supports(target.OperatingSystem, format) &&
        (target.Architecture != CpuArchitecture.X86 || format == PackageFormat.Msi);

    public static bool Supports(DesktopOperatingSystem operatingSystem, PackageFormat format) =>
        operatingSystem switch
        {
            DesktopOperatingSystem.Windows => format is PackageFormat.Nsis or PackageFormat.Msi,
            DesktopOperatingSystem.MacOS => format is PackageFormat.App or PackageFormat.Dmg or PackageFormat.Pkg,
            DesktopOperatingSystem.Linux => format is PackageFormat.Deb or PackageFormat.Rpm or PackageFormat.AppImage,
            _ => false
        };
}
