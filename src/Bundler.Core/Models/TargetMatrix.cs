using DotNet.Bundler;

namespace DotNet.Bundler.Core;

public static class TargetMatrix
{
    public static bool Supports(BundleTarget target, PackageFormat format) =>
        Supports(target.OperatingSystem, target.LibC, format) &&
        ArtifactNaming.ArchToken(target, format) is not null;

    // 矩阵按 (os, libc) 登记：musl 是 linux 的 libc 变体不是 OS——
    // linux-musl 段只允许 musl 生态件（alpineapk）与 libc 无关件（appimage/归档）。
    public static bool Supports(TargetOperatingSystem operatingSystem, PackageFormat format) =>
        Supports(operatingSystem, LibC.Glibc, format);

    public static bool Supports(TargetOperatingSystem operatingSystem, LibC libc, PackageFormat format) =>
        (operatingSystem, libc) switch
        {
            (TargetOperatingSystem.Windows, _) => format is PackageFormat.Nsis or PackageFormat.Msi
                or PackageFormat.Zip or PackageFormat.TarGz,
            (TargetOperatingSystem.MacOS, _) => format is PackageFormat.App or PackageFormat.Dmg or PackageFormat.Pkg
                or PackageFormat.Zip or PackageFormat.TarGz,
            (TargetOperatingSystem.Linux, LibC.Glibc) => format is PackageFormat.Deb or PackageFormat.Rpm
                or PackageFormat.AppImage or PackageFormat.Zip or PackageFormat.TarGz,
            (TargetOperatingSystem.Linux, LibC.Musl) => format is PackageFormat.Zip or PackageFormat.TarGz
                or PackageFormat.AlpineApk or PackageFormat.AppImage,
            _ => false
        };
}
