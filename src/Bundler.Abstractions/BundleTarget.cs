namespace DotNet.Bundler;

/// <summary>
/// 打包目标三元组 {os, arch, libc?}——rid 概念已废，canonical 字符串形如
/// <c>windows-x86_64</c>、<c>macos-universal</c>、<c>linux-musl-aarch64</c>。
/// libc 只在 linux 下细分：glibc 为默认（linux），musl 显式声明（linux-musl）。
/// ABI 差异吸收进 arch 粒度（armv7hf/armv7sf 是两个不同 arch）。
/// </summary>
public sealed record BundleTarget(
    string Target,
    DesktopOperatingSystem OperatingSystem,
    CpuArchitecture Architecture)
{
    // 输入别名 → canonical arch；canonical 词本身直接命中 switch。
    private static readonly IReadOnlyDictionary<string, CpuArchitecture> ArchAliases =
        new Dictionary<string, CpuArchitecture>(StringComparer.Ordinal)
        {
            ["x86_64"] = CpuArchitecture.X64,
            ["amd64"] = CpuArchitecture.X64,
            ["x64"] = CpuArchitecture.X64,
            ["i686"] = CpuArchitecture.X86,
            ["i386"] = CpuArchitecture.X86,
            ["x86"] = CpuArchitecture.X86,
            ["386"] = CpuArchitecture.X86,
            ["aarch64"] = CpuArchitecture.Arm64,
            ["arm64"] = CpuArchitecture.Arm64,
            ["armv7hf"] = CpuArchitecture.Armv7Hf,
            ["armhf"] = CpuArchitecture.Armv7Hf,
            ["armv7"] = CpuArchitecture.Armv7Hf,
            ["armv7sf"] = CpuArchitecture.Armv7Sf,
            ["armel"] = CpuArchitecture.Armv7Sf,
            ["riscv64"] = CpuArchitecture.Riscv64,
            ["loongarch64"] = CpuArchitecture.Loongarch64,
            ["loong64"] = CpuArchitecture.Loongarch64,
            ["ppc64le"] = CpuArchitecture.Ppc64le,
            ["s390x"] = CpuArchitecture.S390x,
        };

    /// <summary>canonical arch 词（GNU/uname 风格），进文件名与文档表。</summary>
    public static string ArchName(CpuArchitecture architecture) => architecture switch
    {
        CpuArchitecture.X64 => "x86_64",
        CpuArchitecture.X86 => "i686",
        CpuArchitecture.Arm64 => "aarch64",
        CpuArchitecture.Universal => "universal",
        CpuArchitecture.Armv7Hf => "armv7hf",
        CpuArchitecture.Armv7Sf => "armv7sf",
        CpuArchitecture.Riscv64 => "riscv64",
        CpuArchitecture.Loongarch64 => "loongarch64",
        CpuArchitecture.Ppc64le => "ppc64le",
        CpuArchitecture.S390x => "s390x",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture))
    };

    /// <summary>canonical os 词（归档命名段）。</summary>
    public static string OsName(DesktopOperatingSystem os) => os switch
    {
        DesktopOperatingSystem.Windows => "windows",
        DesktopOperatingSystem.MacOS => "macos",
        DesktopOperatingSystem.Linux => "linux",
        DesktopOperatingSystem.LinuxMusl => "linux-musl",
        _ => throw new ArgumentOutOfRangeException(nameof(os))
    };

    /// <summary>解析 <c>{os}[-{libc}]-{arch}</c>；不合法返回 false。</summary>
    public static bool TryParse(string? text, out BundleTarget? target)
    {
        target = TryParseCore(text?.Trim().ToLowerInvariant());
        return target is not null;
    }

    /// <summary>解析 <c>{os}[-{libc}]-{arch}</c>；不合法抛 <see cref="ArgumentException"/>。</summary>
    public static BundleTarget Parse(string text) =>
        TryParseCore(text?.Trim().ToLowerInvariant())
        ?? throw new ArgumentException(
            $"'{text}' is not a valid target ({'{'}os{'}'}[-{'{'}libc{'}'}]-{'{'}arch{'}'}, e.g. linux-musl-aarch64).");

    private static BundleTarget? TryParseCore(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var separator = text!.LastIndexOf('-');
        if (separator < 0)
        {
            return null;
        }

        var archText = text!.Substring(separator + 1);
        var osText = text.Substring(0, separator);
        if (archText == "universal" && osText == "macos")
        {
            return new("macos-universal", DesktopOperatingSystem.MacOS, CpuArchitecture.Universal);
        }

        var operatingSystem = osText switch
        {
            "windows" => DesktopOperatingSystem.Windows,
            "macos" => DesktopOperatingSystem.MacOS,
            "linux" => DesktopOperatingSystem.Linux,
            "linux-musl" => DesktopOperatingSystem.LinuxMusl,
            _ => (DesktopOperatingSystem?)null
        };
        if (operatingSystem is null ||
            !ArchAliases.TryGetValue(archText, out var architecture) ||
            architecture == CpuArchitecture.Universal)
        {
            return null;
        }

        var canonical = $"{osText}-{ArchName(architecture)}";
        return new(canonical, operatingSystem.Value, architecture);
    }

    /// <summary>musl 变体标记——用户显式打 musl 目标才挂。</summary>
    public bool IsMusl => OperatingSystem == DesktopOperatingSystem.LinuxMusl;
}
