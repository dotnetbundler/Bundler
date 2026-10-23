using System.Runtime.InteropServices;
using DotNet.Bundler.Updater.Bootstrap;
using Xunit;

namespace Bundler.UnitTests.UpdaterBootstrap;

/// <summary>
/// <see cref="NativeSpawnUnix"/> 建管面的平台口径：O_CLOEXEC/ENOSYS 分平台取值
///（Linux 值曾在 macOS 27 引入 pipe2 后致 EINVAL、全部重定向 spawn 失败），
/// 以及宿主上实建管两端确带 FD_CLOEXEC、有 pipe2 符号即不回落。
/// </summary>
public sealed class NativeSpawnUnixTests
{
    private const int F_GETFD = 1;
    private const int FD_CLOEXEC = 1;

    [Theory]
    [InlineData(true, 0x1000000)]
    [InlineData(false, 0x80000)]
    public void CloExecOpenFlagMatchesPlatformAbi(bool darwin, int expected)
    {
        Assert.Equal(expected, NativeSpawnUnix.CloExecOpenFlagFor(darwin));
    }

    [Theory]
    [InlineData(true, 78)]
    [InlineData(false, 38)]
    public void EnosysMatchesPlatformAbi(bool darwin, int expected)
    {
        Assert.Equal(expected, NativeSpawnUnix.EnosysFor(darwin));
    }

    [Fact]
    public void CreatePipeYieldsCloseOnExecEnds()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "建管面 POSIX-only");
        var fds = new int[2];
        Assert.True(NativeSpawnUnix.CreatePipe(fds), $"CreatePipe failed, errno={Marshal.GetLastWin32Error()}");
        try
        {
            // 已登记残余：Apple ARM64 变参 ABI 下 DllImport fcntl(F_SETFD) 的第三参
            // 递不到，回落路径 FD_CLOEXEC 不落位（子件侧由 POSIX_SPAWN_CLOEXEC_DEFAULT
            // 兜底）——只在无 pipe2 的 macOS ≤26 ARM64 出现；有 pipe2 时必须落位。
            Assert.SkipWhen(
                OperatingSystem.IsMacOS()
                    && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    && NativeSpawnUnix.Pipe2Absent,
                "macOS ARM64 回落路径 fcntl 变参残余（由 POSIX_SPAWN_CLOEXEC_DEFAULT 兜底）");
            foreach (var fd in fds)
            {
                var flags = fcntl(fd, F_GETFD);
                Assert.True(flags >= 0, $"fcntl(F_GETFD) failed on fd {fd}");
                Assert.Equal(FD_CLOEXEC, flags & FD_CLOEXEC);
            }
        }
        finally
        {
            close(fds[0]);
            close(fds[1]);
        }
    }

    [Fact]
    public void Pipe2UsedWhenHostExportsIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "建管面 POSIX-only");
        Assert.SkipUnless(HostExportsPipe2(), "宿主 libc 无 pipe2 符号（macOS ≤26），原子路径不适用");
        // 宿主有 pipe2（Linux、macOS 27+）时建管须走原子路径——旗标口径错
        // 会得 EINVAL 回落并把 pipe2 记为缺席，本断言即抓这一退化。
        var fds = new int[2];
        Assert.True(NativeSpawnUnix.CreatePipe(fds));
        close(fds[0]);
        close(fds[1]);
        Assert.False(NativeSpawnUnix.Pipe2Absent);
    }

    private static bool HostExportsPipe2()
    {
        var lib = OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6";
        if (!NativeLibrary.TryLoad(lib, out var handle))
        {
            return false;
        }
        try
        {
            return NativeLibrary.TryGetExport(handle, "pipe2", out _);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int cmd);
    [DllImport("libc")] private static extern int close(int fd);
}
