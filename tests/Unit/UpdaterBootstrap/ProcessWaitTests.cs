using System.Diagnostics;
using DotNet.Bundler.Updater.Bootstrap;
using Xunit;

namespace Bundler.UnitTests.UpdaterBootstrap;

/// <summary>
/// <see cref="ProcessWait"/> 的契约面：快出进程即报已退、超时界真生效、
/// 超时后再杀仍可被观察到退出（轮询直探内核，不走易丢唤醒的事件链）。
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

    private static Process StartSleep(int seconds)
    {
        return OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo("cmd", "/c ping -n " + (seconds + 1) + " 127.0.0.1 >nul")
            { UseShellExecute = false, CreateNoWindow = true })!
            : Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep " + seconds + "\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
    }
}
