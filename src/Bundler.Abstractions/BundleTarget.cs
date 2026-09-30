namespace DotNet.Bundler;

public sealed record BundleTarget(
    string RuntimeIdentifier,
    DesktopOperatingSystem OperatingSystem,
    CpuArchitecture Architecture)
{
    // RID 语法解析：<os>[-<variant>]-<arch>，不做组合白名单——能否产出由后端能力决定。
    public static bool TryParse(string? runtimeIdentifier, out BundleTarget? target)
    {
        target = runtimeIdentifier?.ToLowerInvariant() is { } rid
            ? Parse(rid)
            : null;
        return target is not null;
    }

    private static BundleTarget? Parse(string runtimeIdentifier)
    {
        if (runtimeIdentifier == "osx")
        {
            return new("osx", DesktopOperatingSystem.MacOS, CpuArchitecture.Universal);
        }

        var separator = runtimeIdentifier.LastIndexOf('-');
        if (separator < 0)
        {
            return null;
        }

        var operatingSystem = runtimeIdentifier.Substring(0, separator) switch
        {
            "win" => DesktopOperatingSystem.Windows,
            "osx" => DesktopOperatingSystem.MacOS,
            "linux" => DesktopOperatingSystem.Linux,
            "linux-musl" => DesktopOperatingSystem.LinuxMusl,
            _ => (DesktopOperatingSystem?)null
        };
        var architecture = runtimeIdentifier.Substring(separator + 1) switch
        {
            "x86" => CpuArchitecture.X86,
            "x64" => CpuArchitecture.X64,
            "arm64" => CpuArchitecture.Arm64,
            _ => (CpuArchitecture?)null
        };
        return operatingSystem is { } os && architecture is { } arch
            ? new(runtimeIdentifier, os, arch)
            : null;
    }
}
