using DotNet.Bundler;

namespace Bundler.UnitTests.Abstractions;

public sealed class ArtifactNamingTests
{
    private static BundleTarget Target(string text) => BundleTarget.Parse(text);

    [Theory]
    [InlineData("windows-x86_64", PackageFormat.Nsis, "x64")]
    [InlineData("windows-i686", PackageFormat.Nsis, "x86")]
    [InlineData("windows-aarch64", PackageFormat.Nsis, "arm64")]
    [InlineData("windows-x86_64", PackageFormat.Msi, "x64")]
    [InlineData("windows-aarch64", PackageFormat.Msi, "arm64")]
    [InlineData("macos-x86_64", PackageFormat.App, "x86_64")]
    [InlineData("macos-aarch64", PackageFormat.App, "arm64")]
    [InlineData("macos-universal", PackageFormat.App, "universal")]
    [InlineData("macos-aarch64", PackageFormat.Dmg, "arm64")]
    [InlineData("macos-aarch64", PackageFormat.Pkg, "arm64")]
    [InlineData("linux-x86_64", PackageFormat.AppImage, "x86_64")]
    [InlineData("linux-i686", PackageFormat.AppImage, "i686")]
    [InlineData("linux-aarch64", PackageFormat.AppImage, "aarch64")]
    [InlineData("linux-x86_64", PackageFormat.Deb, "amd64")]
    [InlineData("linux-i686", PackageFormat.Deb, "i386")]
    [InlineData("linux-aarch64", PackageFormat.Deb, "arm64")]
    [InlineData("linux-armv7hf", PackageFormat.Deb, "armhf")]
    [InlineData("linux-armv7sf", PackageFormat.Deb, "armel")]
    [InlineData("linux-riscv64", PackageFormat.Deb, "riscv64")]
    [InlineData("linux-ppc64le", PackageFormat.Deb, "ppc64el")]
    [InlineData("linux-s390x", PackageFormat.Deb, "s390x")]
    [InlineData("linux-x86_64", PackageFormat.Rpm, "x86_64")]
    [InlineData("linux-armv7hf", PackageFormat.Rpm, "armv7hl")]
    [InlineData("linux-loongarch64", PackageFormat.Rpm, "loongarch64")]
    [InlineData("linux-musl-x86_64", PackageFormat.AlpineApk, "x86_64")]
    [InlineData("linux-musl-i686", PackageFormat.AlpineApk, "x86")]
    [InlineData("linux-musl-aarch64", PackageFormat.AlpineApk, "aarch64")]
    [InlineData("linux-musl-armv7hf", PackageFormat.AlpineApk, "armv7")]
    [InlineData("linux-musl-armv7sf", PackageFormat.AlpineApk, "armel")]
    [InlineData("linux-musl-riscv64", PackageFormat.AlpineApk, "riscv64")]
    [InlineData("linux-musl-loongarch64", PackageFormat.AlpineApk, "loongarch64")]
    [InlineData("linux-musl-ppc64le", PackageFormat.AlpineApk, "ppc64le")]
    [InlineData("linux-musl-s390x", PackageFormat.AlpineApk, "s390x")]
    [InlineData("windows-x86_64", PackageFormat.Zip, "windows-x64")]
    [InlineData("windows-aarch64", PackageFormat.TarGz, "windows-arm64")]
    [InlineData("macos-x86_64", PackageFormat.Zip, "macos-x86_64")]
    [InlineData("macos-aarch64", PackageFormat.TarGz, "macos-arm64")]
    [InlineData("macos-universal", PackageFormat.Zip, "macos-universal")]
    [InlineData("linux-x86_64", PackageFormat.Zip, "linux-x86_64")]
    [InlineData("linux-aarch64", PackageFormat.TarGz, "linux-aarch64")]
    [InlineData("linux-riscv64", PackageFormat.Zip, "linux-riscv64")]
    public void ArchToken_FormatTimesArch_EcosystemWord(string target, PackageFormat format, string expected)
    {
        Assert.Equal(expected, ArtifactNaming.ArchToken(Target(target), format));
    }

    [Theory]
    // 生态词表外组合：回退规范 GNU 词而非 null（归档是通用容器）
    [InlineData("windows-armv7hf", PackageFormat.Zip, "windows-armv7hf")]
    [InlineData("macos-i686", PackageFormat.TarGz, "macos-i686")]
    // ArchToken 纯 format×arch 查表不管 OS——OS 边界是 TargetMatrix 的职责；
    // 跨 OS 组合仍解析出架构词
    [InlineData("windows-x86_64", PackageFormat.Deb, "amd64")]
    [InlineData("macos-aarch64", PackageFormat.Nsis, "arm64")]
    [InlineData("linux-x86_64", PackageFormat.AlpineApk, "x86_64")]
    [InlineData("linux-musl-x86_64", PackageFormat.Nsis, "x64")]
    // 真正不支持的架构才返回 null
    [InlineData("linux-armv7sf", PackageFormat.Rpm, null)]
    [InlineData("linux-armv7sf", PackageFormat.Deb, "armel")]
    [InlineData("windows-armv7hf", PackageFormat.Nsis, null)]
    [InlineData("windows-armv7hf", PackageFormat.Msi, null)]
    public void ArchToken_UnsupportedCombo_ReturnsNullOrFallback(
        string target, PackageFormat format, string? expected)
    {
        Assert.Equal(expected, ArtifactNaming.ArchToken(Target(target), format));
    }

    [Theory]
    [InlineData("MyApp", "1.2.3", "windows-x86_64", PackageFormat.Nsis, "MyApp-1.2.3-x64-setup.exe")]
    [InlineData("MyApp", "1.2.3", "windows-aarch64", PackageFormat.Msi, "MyApp-1.2.3-arm64.msi")]
    [InlineData("MyApp", "1.2.3", "macos-aarch64", PackageFormat.App, "MyApp-1.2.3-arm64.app")]
    [InlineData("MyApp", "1.2.3", "macos-universal", PackageFormat.Dmg, "MyApp-1.2.3-universal.dmg")]
    [InlineData("MyApp", "1.2.3", "macos-x86_64", PackageFormat.Pkg, "MyApp-1.2.3-x86_64.pkg")]
    [InlineData("MyApp", "1.2.3", "linux-x86_64", PackageFormat.AppImage, "MyApp-1.2.3-x86_64.AppImage")]
    [InlineData("MyApp", "1.2.3", "linux-musl-aarch64", PackageFormat.AppImage, "MyApp-1.2.3-aarch64-musl.AppImage")]
    [InlineData("MyApp", "1.2.3", "linux-x86_64", PackageFormat.Zip, "MyApp-1.2.3-linux-x86_64.zip")]
    [InlineData("MyApp", "1.2.3", "linux-musl-x86_64", PackageFormat.TarGz, "MyApp-1.2.3-linux-x86_64-musl.tar.gz")]
    [InlineData("MyApp", "1.2.3", "windows-x86_64", PackageFormat.Zip, "MyApp-1.2.3-windows-x64.zip")]
    [InlineData("my-app", "1.2.3", "linux-x86_64", PackageFormat.Deb, "my-app_1.2.3-1_amd64.deb")]
    [InlineData("my-app", "1.2.3", "linux-x86_64", PackageFormat.Rpm, "my-app-1.2.3-1.x86_64.rpm")]
    [InlineData("my-app", "1.2.3", "linux-musl-x86_64", PackageFormat.AlpineApk, "my-app-1.2.3-r0.x86_64.apk")]
    public void FileName_PerFormat_EcosystemShape(
        string name, string version, string target, PackageFormat format, string expected)
    {
        var release = format switch
        {
            PackageFormat.Deb => "1",
            PackageFormat.Rpm => "1",
            PackageFormat.AlpineApk => "r0",
            _ => null
        };
        var languageSuffix = format == PackageFormat.Msi ? "" : null;
        Assert.Equal(expected, ArtifactNaming.FileName(name, version, Target(target), format, release, languageSuffix));
    }

    [Fact]
    public void FileName_MsiLanguageSuffix_Embedded()
    {
        var name = ArtifactNaming.FileName(
            "App", "1.0", Target("windows-x86_64"), PackageFormat.Msi, languageSuffix: "-zh-CN");
        Assert.Equal("App-1.0-x64-zh-CN.msi", name);
    }

    [Fact]
    public void FileName_ExplicitArchToken_OverridesTarget()
    {
        var name = ArtifactNaming.FileName(
            "App", "1.0", Target("windows-x86_64"), PackageFormat.Msi, archToken: "arm64");
        Assert.Equal("App-1.0-arm64.msi", name);
    }

    [Fact]
    public void FileName_UnsupportedArch_Throws()
    {
        Assert.Throws<ArgumentException>(() => ArtifactNaming.FileName(
            "App", "1.0", Target("linux-armv7sf"), PackageFormat.Rpm, release: "1"));
    }
}
