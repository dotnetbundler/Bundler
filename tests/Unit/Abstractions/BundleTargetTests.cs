using DotNet.Bundler;

namespace Bundler.UnitTests.Abstractions;

public sealed class BundleTargetTests
{
    [Theory]
    [InlineData("windows-x86_64", TargetOperatingSystem.Windows, CpuArchitecture.X64, LibC.Glibc)]
    [InlineData("windows-i686", TargetOperatingSystem.Windows, CpuArchitecture.X86, LibC.Glibc)]
    [InlineData("windows-aarch64", TargetOperatingSystem.Windows, CpuArchitecture.Arm64, LibC.Glibc)]
    [InlineData("macos-x86_64", TargetOperatingSystem.MacOS, CpuArchitecture.X64, LibC.Glibc)]
    [InlineData("macos-aarch64", TargetOperatingSystem.MacOS, CpuArchitecture.Arm64, LibC.Glibc)]
    [InlineData("macos-universal", TargetOperatingSystem.MacOS, CpuArchitecture.Universal, LibC.Glibc)]
    [InlineData("linux-x86_64", TargetOperatingSystem.Linux, CpuArchitecture.X64, LibC.Glibc)]
    [InlineData("linux-aarch64", TargetOperatingSystem.Linux, CpuArchitecture.Arm64, LibC.Glibc)]
    [InlineData("linux-armv7hf", TargetOperatingSystem.Linux, CpuArchitecture.Armv7Hf, LibC.Glibc)]
    [InlineData("linux-armv7sf", TargetOperatingSystem.Linux, CpuArchitecture.Armv7Sf, LibC.Glibc)]
    [InlineData("linux-riscv64", TargetOperatingSystem.Linux, CpuArchitecture.Riscv64, LibC.Glibc)]
    [InlineData("linux-loongarch64", TargetOperatingSystem.Linux, CpuArchitecture.Loongarch64, LibC.Glibc)]
    [InlineData("linux-ppc64le", TargetOperatingSystem.Linux, CpuArchitecture.Ppc64le, LibC.Glibc)]
    [InlineData("linux-s390x", TargetOperatingSystem.Linux, CpuArchitecture.S390x, LibC.Glibc)]
    [InlineData("linux-musl-x86_64", TargetOperatingSystem.Linux, CpuArchitecture.X64, LibC.Musl)]
    [InlineData("linux-musl-aarch64", TargetOperatingSystem.Linux, CpuArchitecture.Arm64, LibC.Musl)]
    public void TryParse_CanonicalWords_RoundTrips(
        string text, TargetOperatingSystem os, CpuArchitecture arch, LibC libc)
    {
        Assert.True(BundleTarget.TryParse(text, out var target));
        Assert.Equal(os, target!.OperatingSystem);
        Assert.Equal(arch, target.Architecture);
        Assert.Equal(libc, target.LibC);
        Assert.Equal(text, target.Target);
    }

    [Theory]
    [InlineData("windows-amd64", "windows-x86_64")]
    [InlineData("windows-x64", "windows-x86_64")]
    [InlineData("windows-i386", "windows-i686")]
    [InlineData("windows-x86", "windows-i686")]
    [InlineData("windows-386", "windows-i686")]
    [InlineData("windows-arm64", "windows-aarch64")]
    [InlineData("linux-armv7", "linux-armv7hf")]
    [InlineData("linux-armhf", "linux-armv7hf")]
    [InlineData("linux-armel", "linux-armv7sf")]
    [InlineData("linux-loong64", "linux-loongarch64")]
    [InlineData("linux-musl-amd64", "linux-musl-x86_64")]
    public void TryParse_ArchAlias_NormalizesToCanonical(string text, string canonical)
    {
        Assert.True(BundleTarget.TryParse(text, out var target));
        Assert.Equal(canonical, target!.Target);
    }

    [Theory]
    [InlineData(" WINDOWS-X64 ")]
    [InlineData("Linux-Musl-AArch64")]
    public void TryParse_CaseAndWhitespace_Normalizes(string text)
    {
        Assert.True(BundleTarget.TryParse(text, out var target));
        Assert.Equal(target!.Target, target.Target.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("windows")]
    [InlineData("x86_64")]
    [InlineData("win-x64")]
    [InlineData("osx-arm64")]
    [InlineData("linux-glibc-x86_64")]
    [InlineData("windows-universal")]
    [InlineData("linux-universal")]
    [InlineData("linux-musl-universal")]
    [InlineData("windows-musl-x86_64")]
    [InlineData("macos-musl-x86_64")]
    [InlineData("freebsd-x86_64")]
    [InlineData("linux-mips64")]
    [InlineData("-")]
    [InlineData("linux-")]
    [InlineData("-x86_64")]
    public void TryParse_InvalidWords_Rejected(string? text)
    {
        Assert.False(BundleTarget.TryParse(text, out var target));
        Assert.Null(target);
    }

    [Fact]
    public void Parse_InvalidInput_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => BundleTarget.Parse("win-x64"));
        Assert.Throws<ArgumentException>(() => BundleTarget.Parse(""));
    }

    [Fact]
    public void IsMusl_OnlyTrueForMuslVariant()
    {
        Assert.True(BundleTarget.Parse("linux-musl-x86_64").IsMusl);
        Assert.False(BundleTarget.Parse("linux-x86_64").IsMusl);
        Assert.False(BundleTarget.Parse("windows-x86_64").IsMusl);
    }

    [Theory]
    [InlineData(CpuArchitecture.X64, "x86_64")]
    [InlineData(CpuArchitecture.X86, "i686")]
    [InlineData(CpuArchitecture.Arm64, "aarch64")]
    [InlineData(CpuArchitecture.Universal, "universal")]
    [InlineData(CpuArchitecture.Armv7Hf, "armv7hf")]
    [InlineData(CpuArchitecture.Armv7Sf, "armv7sf")]
    [InlineData(CpuArchitecture.Riscv64, "riscv64")]
    [InlineData(CpuArchitecture.Loongarch64, "loongarch64")]
    [InlineData(CpuArchitecture.Ppc64le, "ppc64le")]
    [InlineData(CpuArchitecture.S390x, "s390x")]
    public void ArchName_AllEnumValues_Map(CpuArchitecture arch, string name)
    {
        Assert.Equal(name, BundleTarget.ArchName(arch));
    }

    [Fact]
    public void ArchName_UnknownValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BundleTarget.ArchName((CpuArchitecture)999));
    }
}
