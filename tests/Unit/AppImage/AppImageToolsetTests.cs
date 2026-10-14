using DotNet.Bundler.AppImage;

namespace Bundler.UnitTests.AppImage;

/// <summary>AppImageToolset 纯面：内嵌默认图标与宿主门。</summary>
public sealed class AppImageToolsetTests
{
    [Fact]
    public void DefaultIcon_IsRealPng()
    {
        var bytes = AppImageToolset.DefaultIcon();
        Assert.True(bytes.Length > 8);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]); // PNG magic
    }

    [Fact]
    public void RequireLinuxHost_GatesNonLinux()
    {
        if (OperatingSystem.IsLinux())
        {
            AppImageToolset.RequireLinuxHost();
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(AppImageToolset.RequireLinuxHost);
        }
    }
}
