namespace DotNet.Bundler;

public enum DesktopOperatingSystem
{
    Windows,
    MacOS,
    Linux,
    LinuxMusl
}

public enum CpuArchitecture
{
    X64,
    Arm64,
    X86,
    Universal,
    Armv7Hf,
    Armv7Sf,
    Riscv64,
    Loongarch64,
    Ppc64le,
    S390x
}

public enum PackageFormat
{
    Nsis,
    Msi,
    App,
    Dmg,
    Pkg,
    Deb,
    Rpm,
    AppImage,
    Zip,
    TarGz,
    AlpineApk
}
