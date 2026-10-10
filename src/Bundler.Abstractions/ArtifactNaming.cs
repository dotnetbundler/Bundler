namespace DotNet.Bundler;

/// <summary>
/// 产物命名单一来源：自描述格式统一 <c>{name}-{ver}-{platarch}.{ext}</c>，
/// 归档格式用 <c>os-arch[-musl]</c> 段；deb/rpm/apk 保各自生态原生命名。
/// 规划期查重与后端产件都走这里，保证断言覆盖未来后端。
/// </summary>
public static class ArtifactNaming
{
    /// <summary>后端惯用架构词；不支持的组合返回 null（矩阵/规划期应已拦截）。</summary>
    public static string? ArchToken(BundleTarget target, PackageFormat format)
    {
        var arch = target.Architecture;
        return format switch
        {
            PackageFormat.Nsis or PackageFormat.Msi => arch switch
            {
                CpuArchitecture.X64 => "x64",
                CpuArchitecture.X86 => "x86",
                CpuArchitecture.Arm64 => "arm64",
                _ => null
            },
            PackageFormat.App or PackageFormat.Dmg or PackageFormat.Pkg => arch switch
            {
                CpuArchitecture.X64 => "x86_64",
                CpuArchitecture.Arm64 => "arm64",
                CpuArchitecture.Universal => "universal",
                _ => null
            },
            PackageFormat.AppImage => arch switch
            {
                CpuArchitecture.X64 => "x86_64",
                CpuArchitecture.X86 => "i686",
                CpuArchitecture.Arm64 => "aarch64",
                _ => null
            },
            PackageFormat.Deb => arch switch
            {
                CpuArchitecture.X64 => "amd64",
                CpuArchitecture.X86 => "i386",
                CpuArchitecture.Arm64 => "arm64",
                CpuArchitecture.Armv7Hf => "armhf",
                CpuArchitecture.Armv7Sf => "armel",
                CpuArchitecture.Riscv64 => "riscv64",
                CpuArchitecture.Ppc64le => "ppc64el",
                CpuArchitecture.S390x => "s390x",
                _ => null
            },
            PackageFormat.Rpm => arch switch
            {
                CpuArchitecture.X64 => "x86_64",
                CpuArchitecture.X86 => "i686",
                CpuArchitecture.Arm64 => "aarch64",
                CpuArchitecture.Armv7Hf => "armv7hl",
                CpuArchitecture.Riscv64 => "riscv64",
                CpuArchitecture.Loongarch64 => "loongarch64",
                CpuArchitecture.Ppc64le => "ppc64le",
                CpuArchitecture.S390x => "s390x",
                _ => null
            },
            PackageFormat.AlpineApk => arch switch
            {
                CpuArchitecture.X64 => "x86_64",
                CpuArchitecture.X86 => "x86",
                CpuArchitecture.Arm64 => "aarch64",
                CpuArchitecture.Armv7Hf => "armv7",
                CpuArchitecture.Riscv64 => "riscv64",
                CpuArchitecture.Loongarch64 => "loongarch64",
                CpuArchitecture.Ppc64le => "ppc64le",
                CpuArchitecture.S390x => "s390x",
                _ => null
            },
            PackageFormat.Zip or PackageFormat.TarGz => target.OperatingSystem switch
            {
                DesktopOperatingSystem.Windows => $"windows-{BundleTarget.ArchName(arch)}",
                DesktopOperatingSystem.MacOS => $"macos-{BundleTarget.ArchName(arch)}",
                DesktopOperatingSystem.Linux or DesktopOperatingSystem.LinuxMusl =>
                    $"linux-{BundleTarget.ArchName(arch)}",
                _ => null
            },
            _ => null
        };
    }

    /// <summary>
    /// 产物文件名（不含目录）。<paramref name="release"/> 仅 deb/rpm/apk 用（包管理器
    /// 修订段）；<paramref name="languageSuffix"/> 仅多语言 msi 用；
    /// <paramref name="archToken"/> 为跨架构覆盖场景显式指定架构词。
    /// </summary>
    public static string FileName(
        string productName,
        string version,
        BundleTarget target,
        PackageFormat format,
        string? release = null,
        string? languageSuffix = null,
        string? archToken = null)
    {
        var arch = archToken ?? ArchToken(target, format)
            ?? throw new ArgumentException($"Architecture '{target.Architecture}' is not supported for {format}.");
        var musl = target.IsMusl ? "-musl" : "";
        return format switch
        {
            PackageFormat.Nsis => $"{productName}-{version}-{arch}-setup.exe",
            PackageFormat.Msi => $"{productName}-{version}-{arch}{languageSuffix}.msi",
            PackageFormat.App => $"{productName}-{version}-{arch}.app",
            PackageFormat.Dmg => $"{productName}-{version}-{arch}.dmg",
            PackageFormat.Pkg => $"{productName}-{version}-{arch}.pkg",
            PackageFormat.AppImage => $"{productName}-{version}-{arch}{musl}.AppImage",
            PackageFormat.Zip => $"{productName}-{version}-{arch}{musl}.zip",
            PackageFormat.TarGz => $"{productName}-{version}-{arch}{musl}.tar.gz",
            PackageFormat.Deb => $"{productName}_{version}-{release}_{arch}.deb",
            PackageFormat.Rpm => $"{productName}-{version}-{release}.{arch}.rpm",
            PackageFormat.AlpineApk => $"{productName}-{version}-{release}.{arch}.apk",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }
}
