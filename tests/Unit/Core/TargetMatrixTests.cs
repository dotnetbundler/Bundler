using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class TargetMatrixTests
{
    [Theory]
    [InlineData(PackageFormat.Nsis, true)]
    [InlineData(PackageFormat.Msi, true)]
    [InlineData(PackageFormat.Zip, true)]
    [InlineData(PackageFormat.TarGz, true)]
    [InlineData(PackageFormat.App, false)]
    [InlineData(PackageFormat.Dmg, false)]
    [InlineData(PackageFormat.Pkg, false)]
    [InlineData(PackageFormat.Deb, false)]
    [InlineData(PackageFormat.Rpm, false)]
    [InlineData(PackageFormat.AppImage, false)]
    [InlineData(PackageFormat.AlpineApk, false)]
    public void Windows_Supports_InstallersAndArchivesOnly(PackageFormat format, bool expected)
    {
        Assert.Equal(expected,
            TargetMatrix.Supports(TargetOperatingSystem.Windows, format));
    }

    [Theory]
    [InlineData(PackageFormat.App, true)]
    [InlineData(PackageFormat.Dmg, true)]
    [InlineData(PackageFormat.Pkg, true)]
    [InlineData(PackageFormat.Zip, true)]
    [InlineData(PackageFormat.TarGz, true)]
    [InlineData(PackageFormat.Nsis, false)]
    [InlineData(PackageFormat.Msi, false)]
    [InlineData(PackageFormat.Deb, false)]
    [InlineData(PackageFormat.Rpm, false)]
    [InlineData(PackageFormat.AppImage, false)]
    [InlineData(PackageFormat.AlpineApk, false)]
    public void MacOS_Supports_AppDmgPkgAndArchives(PackageFormat format, bool expected)
    {
        Assert.Equal(expected,
            TargetMatrix.Supports(TargetOperatingSystem.MacOS, format));
    }

    [Theory]
    [InlineData(PackageFormat.Deb, true)]
    [InlineData(PackageFormat.Rpm, true)]
    [InlineData(PackageFormat.AppImage, true)]
    [InlineData(PackageFormat.Zip, true)]
    [InlineData(PackageFormat.TarGz, true)]
    [InlineData(PackageFormat.Nsis, false)]
    [InlineData(PackageFormat.Msi, false)]
    [InlineData(PackageFormat.App, false)]
    [InlineData(PackageFormat.Dmg, false)]
    [InlineData(PackageFormat.Pkg, false)]
    [InlineData(PackageFormat.AlpineApk, false)]
    public void LinuxGlibc_Supports_DebRpmAppImageAndArchives(PackageFormat format, bool expected)
    {
        Assert.Equal(expected,
            TargetMatrix.Supports(TargetOperatingSystem.Linux, LibC.Glibc, format));
    }

    [Theory]
    [InlineData(PackageFormat.AlpineApk, true)]
    [InlineData(PackageFormat.Zip, true)]
    [InlineData(PackageFormat.TarGz, true)]
    [InlineData(PackageFormat.AppImage, true)]
    [InlineData(PackageFormat.Deb, false)]
    [InlineData(PackageFormat.Rpm, false)]
    [InlineData(PackageFormat.Nsis, false)]
    [InlineData(PackageFormat.Msi, false)]
    public void LinuxMusl_Supports_ApkArchivesAndAppImage(PackageFormat format, bool expected)
    {
        Assert.Equal(expected,
            TargetMatrix.Supports(TargetOperatingSystem.Linux, LibC.Musl, format));
    }

    [Fact]
    public void Supports_BundleTarget_RequiresArchTokenToo()
    {
        // matrix 通过但 arch 词表不收 → 整体不支持（musl armv7hf 无 apk 词？armv7hf→armv7 有词，
        // 用真正无词组合：nsis 在 armv7hf）
        Assert.False(TargetMatrix.Supports(
            BundleTarget.Parse("windows-armv7hf"), PackageFormat.Nsis));
        Assert.True(TargetMatrix.Supports(
            BundleTarget.Parse("windows-aarch64"), PackageFormat.Nsis));
        // AppImage i686：矩阵曾靠词表放行、后端再拒——现在词表不产词，规划期即剪
        Assert.False(TargetMatrix.Supports(
            BundleTarget.Parse("linux-i686"), PackageFormat.AppImage));
        Assert.True(TargetMatrix.Supports(
            BundleTarget.Parse("linux-x86_64"), PackageFormat.AppImage));
    }

    [Fact]
    public void Supports_Musl_OnlyMuslFormats()
    {
        var musl = BundleTarget.Parse("linux-musl-x86_64");
        Assert.True(TargetMatrix.Supports(musl, PackageFormat.AlpineApk));
        Assert.False(TargetMatrix.Supports(musl, PackageFormat.Deb));
    }
}
