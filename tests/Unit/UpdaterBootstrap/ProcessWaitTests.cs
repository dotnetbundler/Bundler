using System.Diagnostics;
using DotNet.Bundler.Updater.Bootstrap;
using Xunit;

namespace Bundler.UnitTests.UpdaterBootstrap;

/// <summary>
/// <see cref="ProcessWait"/> 的契约面：快出进程即报已退、超时界真生效、
/// 超时后再杀仍可被观察到退出，spawn 异常腿原样穿透。
/// </summary>
public sealed class ProcessWaitTests
{
    [Fact]
    public void ReturnsTrueForExitedProcess()
    {
        using var process = StartSleep(0);
        Assert.True(ProcessWait.WaitForExit(process, 10_000));
    }

    [Fact]
    public void ReturnsFalseAtTimeoutAndObservesLaterKill()
    {
        using var process = StartSleep(60);
        Assert.False(ProcessWait.WaitForExit(process, 200));
        try { process.Kill(entireProcessTree: true); } catch (Exception) { }
        // 杀后回收不挂死：僵尸/未落地事件都能被下一趟轮询探到。
        Assert.True(ProcessWait.WaitForExit(process, 10_000));
    }

    [Fact]
    public void StartReturnsProcessForFastSpawn()
    {
        using var process = ProcessWait.Start(
            new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
                OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"")
            { UseShellExecute = false, CreateNoWindow = true });
        Assert.NotNull(process);
        Assert.True(ProcessWait.WaitForExit(process, 10_000));
    }

    [Fact]
    public void StartPropagatesSpawnFailure()
    {
        // spawn 内阻塞无法从托管侧脚本化（锁死在运行时内部）——可测面是其余两腿：
        // 正常 spawn 返回进程、Start 抛出的异常经 Task 包装后原样穿透。
        var never = new Process
        {
            StartInfo = new ProcessStartInfo(
                OperatingSystem.IsWindows() ? "C:\\definitely\\missing.exe" : "/definitely/missing/binary")
            { UseShellExecute = false }
        };
        Assert.ThrowsAny<Exception>(() => ProcessWait.Start(never, 10_000));
    }

    private static Process StartSleep(int seconds)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c ping -n " + (seconds + 1) + " 127.0.0.1 >nul")
            { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"sleep " + seconds + "\"")
            { UseShellExecute = false, CreateNoWindow = true };
        return ProcessWait.Start(info)
            ?? throw new InvalidOperationException("test subject spawn timed out");
    }
}
