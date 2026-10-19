using DotNet.Bundler.Updater.Bootstrap;
using Xunit;

namespace Bundler.UnitTests.UpdaterBootstrap;

/// <summary>
/// <see cref="ToolProcess"/> 的契约面：快出进程即报已退、超时界真生效、
/// 超时后再杀仍可被观察到退出、exec 失败抛 Win32Exception、std 管道可读、
/// Unix 面树杀覆盖孙代。
/// </summary>
public sealed class ToolProcessTests
{
    [Fact]
    public void ReturnsTrueForExitedProcess()
    {
        using var process = StartSleep(0);
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void ReturnsFalseAtTimeoutAndObservesLaterKill()
    {
        using var process = StartSleep(60);
        Assert.False(process.WaitForExit(200));
        try { process.Kill(entireProcessTree: true); } catch (Exception) { }
        // 杀后回收不挂死：waitpid/退出旗标下一趟即探到。
        Assert.True(process.WaitForExit(10_000));
    }

    [Fact]
    public void StartReturnsProcessForFastSpawn()
    {
        using var process = ToolProcess.Start(new ToolProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
            ArgumentLine = OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"",
        });
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(10_000));
    }

    [Fact]
    public void StartPropagatesSpawnFailure()
    {
        // exec 失败经结果管回报 errno——与 Process.Start 同语义按异常穿透。
        var info = new ToolProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? "C:\\definitely\\missing.exe" : "/definitely/missing/binary",
        };
        Assert.ThrowsAny<Exception>(() => ToolProcess.Start(info));
    }

    [Fact]
    public void CapturesRedirectedStdout()
    {
        var info = new ToolProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
            ArgumentLine = OperatingSystem.IsWindows()
                ? "/c echo hello-tool" : "-c \"echo hello-tool\"",
            RedirectStandardOutput = true,
        };
        using var process = ToolProcess.Start(info);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10_000));
        Assert.Contains("hello-tool", output);
    }

    [Fact]
    public void EmptyWorkingDirectoryFallsBackToParentCwd()
    {
        // BCL ProcessStartInfo.WorkingDirectory 默认 ""——空串与 null 同为"不 chdir"；
        // 裸文件名经 PATH 解析时基准须按父件 cwd，不得抛 ArgumentException（CI 实证回归）。
        var info = new ToolProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd" : "sh",
            ArgumentLine = OperatingSystem.IsWindows()
                ? "/c echo cwd-ok" : "-c \"echo cwd-ok\"",
            WorkingDirectory = "",
            RedirectStandardOutput = true,
        };
        using var process = ToolProcess.Start(info);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10_000));
        Assert.Contains("cwd-ok", output);
    }

    [Fact]
    public void RelativePathEntryResolvesAgainstParentCwd()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // NativeSpawnUnix 的 PATH 相对项语义——Windows 腿由 BCL 自管不重复验。
        }
        // PATH 含相对项且 WorkingDirectory 为 ""（不 chdir）时，解析基准=父件 cwd——
        // 上一条测试只钉异常点，本条直接断言拼接路径正确。
        var dirName = $"toolpath-{Guid.NewGuid():N}";
        var dir = Path.Combine(Environment.CurrentDirectory, dirName);
        Directory.CreateDirectory(dir);
        try
        {
            var tool = Path.Combine(dir, "fake-tool-x9");
            File.WriteAllText(tool, "#!/bin/sh\necho rel-cwd-ok\n");
            var mode = File.GetUnixFileMode(tool);
            File.SetUnixFileMode(tool, mode | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            var info = new ToolProcessStartInfo
            {
                FileName = "fake-tool-x9",
                WorkingDirectory = "",
                RedirectStandardOutput = true,
            };
            info.Environment["PATH"] = dirName;
            using var process = ToolProcess.Start(info);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(10_000));
            Assert.Contains("rel-cwd-ok", output);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CompleteEnvironmentBlockDropsInheritedVars()
    {
        // EnvironmentIsComplete：袋子即完整环境块——父件变量不漏进子件
        //（BCL ProcessStartInfo.Environment.Clear() 的同位语义；缺它时
        // DEBIAN_FRONTEND 这类宿主变量会污染被测子件环境，CI 实证）。
        var marker = $"BUNDLER_TP_{Guid.NewGuid():N}"[..27];
        System.Environment.SetEnvironmentVariable(marker, "host-value");
        try
        {
            var info = new ToolProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd" : "sh",
                RedirectStandardOutput = true,
                EnvironmentIsComplete = true,
            };
            info.Arguments.AddRange(OperatingSystem.IsWindows()
                ? ["/c", $"echo M=%{marker}%"]
                : ["-c", $"echo M=${marker}"]);
            info.Environment["PATH"] = OperatingSystem.IsWindows()
                ? System.Environment.GetEnvironmentVariable("PATH")!
                : "/usr/bin:/bin";
            using var process = ToolProcess.Start(info);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(10_000));
            Assert.DoesNotContain("host-value", output);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(marker, null);
        }
    }

    [Fact]
    public void ExitedEventFiresOnExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 事件面 Unix waiter 专属路径的覆盖——Windows 透传 Process 不重复验。
        }
        using var process = StartSleep(0);
        var fired = new ManualResetEventSlim();
        process.Exited += (_, _) => fired.Set();
        Assert.True(fired.Wait(10_000, TestContext.Current.CancellationToken),
            "Exited 事件应在子件退出后由 reaper 派发");
    }

    [Fact]
    public void CapturesRedirectedStderr()
    {
        var info = new ToolProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
            ArgumentLine = OperatingSystem.IsWindows()
                ? "/c echo err-line 1>&2" : "-c \"echo err-line >&2\"",
            RedirectStandardError = true,
        };
        using var process = ToolProcess.Start(info);
        Assert.NotNull(process);
        var output = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000));
        Assert.Contains("err-line", output);
    }

    [Fact]
    public void WritesRedirectedStdin()
    {
        var info = new ToolProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "findstr" : "/bin/cat",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentLine = "/n .*";
        }
        using var process = ToolProcess.Start(info);
        Assert.NotNull(process);
        process.StandardInput.WriteLine("stdin-payload");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10_000));
        Assert.Contains("stdin-payload", output);
    }

    [Fact]
    public void KillTakesProcessGroupOnPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 组杀面 POSIX-only（SETPGROUP+kill(-pgid)）；Windows 杀树走 Kill(tree)。
        }
        // 子件起孙件 sleep——Kill 后孙件不能活（组杀经 kill(-pid) 覆盖整树）。
        var info = new ToolProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
        };
        info.Arguments.AddRange(new[] { "-c", "sleep 300 & echo $!; sleep 300" });
        using var process = ToolProcess.Start(info);
        Assert.NotNull(process);
        Assert.True(int.TryParse(process.StandardOutput.ReadLine(), out var grandchild));
        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(10_000));
        Assert.True(process.ExitCode != 0);
        // 孙件死亡最多给一次 reaper 轮询宽限——kill(0) 口径下僵尸也算在。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ProcessAlive(grandchild) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }
        Assert.False(ProcessAlive(grandchild));
    }

    [Fact]
    public void DetachedChildIsReapedWithoutWaitOrDispose()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // reaper 收尸面 Unix-only——Windows 由 OS 句柄回收无僵尸态。
        }
        // 拉起即退的分离腿：不等不 Dispose——reaper 轮询线程到期必收，
        // 退出后不留 <defunct>（这正是分离 spawn 需要的兜底语义；
        // kill(0) 口径下僵尸也算在——未收尸必被此断言抓出）。
        var info = new ToolProcessStartInfo { FileName = "/bin/sleep" };
        info.Arguments.Add("0.1");
        var process = ToolProcess.Start(info);
        Assert.NotNull(process);
        var pid = process.Id;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ProcessAlive(pid) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }
        Assert.False(ProcessAlive(pid));
    }

    private static bool ProcessAlive(int pid)
    {
        try { _ = System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    private static ToolProcess StartSleep(int seconds)
    {
        var info = new ToolProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
            ArgumentLine = OperatingSystem.IsWindows()
                ? "/c ping -n " + (seconds + 1) + " 127.0.0.1 >nul"
                : "-c \"sleep " + seconds + "\"",
        };
        return ToolProcess.Start(info)
            ?? throw new InvalidOperationException("test subject spawn timed out");
    }
}
