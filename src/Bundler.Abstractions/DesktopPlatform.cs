namespace DotNet.Bundler;

public enum DesktopOperatingSystem
{
    Windows,
    MacOS,
    Linux
}

public enum CpuArchitecture
{
    X64,
    Arm64,
    X86
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
    TarGz
}
