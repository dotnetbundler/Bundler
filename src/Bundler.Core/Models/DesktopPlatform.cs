namespace Bundler.Core.Models;

public enum DesktopOperatingSystem
{
    Windows,
    MacOS,
    Linux
}

public enum CpuArchitecture
{
    X64,
    Arm64
}

public enum PackageFormat
{
    Nsis,
    Msi,
    App,
    Dmg,
    Deb,
    Rpm,
    AppImage
}
