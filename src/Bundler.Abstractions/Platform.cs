namespace DotNet.Bundler;

// 命名避让：System.OperatingSystem 在 .NET 有 IsWindows()/IsLinux() 等 API，
// 枚举叫 OperatingSystem 会在 DotNet.Bundler 命名空间内抢占绑定，故用 TargetOperatingSystem。
public enum TargetOperatingSystem
{
    Windows,
    MacOS,
    Linux
}

// libc 是 OS 的旁维度不是 OS 值（Rust triple env 段、dpkg musl-linux-* 同理）；仅 Linux 有意义。
public enum LibC
{
    Glibc,
    Musl
}

// 排序：x86 族升序 → ARM 族升序 → 其余按字母 → universal 特殊值垫底。
public enum CpuArchitecture
{
    X86,
    X64,
    Armv7Sf,
    Armv7Hf,
    Arm64,
    Loongarch64,
    Ppc64le,
    Riscv64,
    S390x,
    Universal
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
