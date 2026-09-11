namespace DotNet.Bundler;

public sealed record BundleTarget(
    string RuntimeIdentifier,
    DesktopOperatingSystem OperatingSystem,
    CpuArchitecture Architecture)
{
    public static bool TryParse(string? runtimeIdentifier, out BundleTarget? target)
    {
        target = runtimeIdentifier?.ToLowerInvariant() switch
        {
            "win-x64" => new("win-x64", DesktopOperatingSystem.Windows, CpuArchitecture.X64),
            "win-arm64" => new("win-arm64", DesktopOperatingSystem.Windows, CpuArchitecture.Arm64),
            "osx-x64" => new("osx-x64", DesktopOperatingSystem.MacOS, CpuArchitecture.X64),
            "osx-arm64" => new("osx-arm64", DesktopOperatingSystem.MacOS, CpuArchitecture.Arm64),
            "linux-x64" => new("linux-x64", DesktopOperatingSystem.Linux, CpuArchitecture.X64),
            "linux-arm64" => new("linux-arm64", DesktopOperatingSystem.Linux, CpuArchitecture.Arm64),
            _ => null
        };

        return target is not null;
    }
}
