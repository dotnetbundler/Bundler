using System.Runtime.InteropServices;
using DotNet.Bundler.Core;

namespace Bundler.UnitTests.Core;

public sealed class UnixFileTypesTests : TempBase
{
    [Fact]
    public void IsRegularFile_RegularFile_True()
    {
        var path = TempPath($"bundler-reg-{Guid.NewGuid():N}");
        File.WriteAllText(path, "x");
        try
        {
            Assert.True(UnixFileTypes.IsRegularFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsRegularFile_MissingFile_True()
    {
        // stat 失败按"无法判定"放行——拷贝路径自己报错
        Assert.True(UnixFileTypes.IsRegularFile(
            TempPath($"bundler-missing-{Guid.NewGuid():N}")));
    }

    [Fact]
    public void IsRegularFile_Directory_FalseOnUnix()
    {
        var dir = Temp("bundler-dir");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Skip("目录 stat 语义只在 Unix 宿主可验（非 Unix 一律 true）");
        }
        Assert.False(UnixFileTypes.IsRegularFile(dir.FullName));
    }

    [Fact]
    public void IsRegularFile_NonUnixHost_AlwaysTrue()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Skip("非 Unix 分支只在 Windows 宿主可验");
        }
        Assert.True(UnixFileTypes.IsRegularFile("anything"));
    }

    [Fact]
    public void IsRegularFile_Fifo_FalseOnLinux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Skip("FIFO 造样本需 Linux");
        }
        var fifo = TempPath($"bundler-fifo-{Guid.NewGuid():N}");
        // mkfifo 不经 shell：libc mkfifo(3)
        if (mkfifo(fifo, 0x180 /* 0600 */) != 0)
        {
            Assert.Skip("mkfifo 不可用");
        }
        try
        {
            Assert.False(UnixFileTypes.IsRegularFile(fifo));
        }
        finally
        {
            File.Delete(fifo);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);
}
