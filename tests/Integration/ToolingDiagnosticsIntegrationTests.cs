// 测试基建诊断缝的窄回归：BUNDLER_RESTORE_TRACE 逐 attempt 事件与
// ProcessRunner.Options.ProcessStarted 回调。
// 假件形态：Windows 上 CreateProcess 对无扩展名只补 .exe（dotnet.cmd 不参与解析），
// 故不用 fake 可执行——改用真 dotnet + 一次性 csproj 的 InitialTargets 注入：
// 经 FAKE_STATE 文件计数、FAKE_MODE=fail1|block1 模拟首 attempt 非零/阻塞，
// 只影响本项目文件，不触真实安装/提权/NuGet 包下载。
using Bundler.TestShared;

public sealed class ToolingDiagnosticsIntegrationTests : TempBase
{
    private const string LogEnv = "BUNDLER_FAKE_DOTNET_LOG";

    private static string FakeProjectContent => """
        <Project Sdk="Microsoft.NET.Sdk" InitialTargets="BundlerFakeRestore">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <NuGetAudit>false</NuGetAudit>
          </PropertyGroup>
          <Target Name="BundlerFakeRestore">
            <ReadLinesFromFile File="$(FAKE_STATE)" Condition="'$(FAKE_STATE)' != '' And Exists('$(FAKE_STATE)')">
              <Output TaskParameter="Lines" ItemName="_BundlerPrevLines" />
            </ReadLinesFromFile>
            <PropertyGroup>
              <BundlerFakeN>@(_BundlerPrevLines)</BundlerFakeN>
              <BundlerFakeN Condition="'$(BundlerFakeN)' == ''">0</BundlerFakeN>
              <BundlerFakeNext>$([MSBuild]::Add($(BundlerFakeN), 1))</BundlerFakeNext>
            </PropertyGroup>
            <WriteLinesToFile File="$(FAKE_STATE)" Lines="$(BundlerFakeNext)" Overwrite="true" Condition="'$(FAKE_STATE)' != ''" />
            <WriteLinesToFile File="$(BUNDLER_FAKE_DOTNET_LOG)" Lines="CALL restore attempt=$(BundlerFakeNext)" Overwrite="false" Condition="'$(BUNDLER_FAKE_DOTNET_LOG)' != ''" />
            <Error Text="BundlerFakeRestore attempt failure" Condition="'$(FAKE_MODE)' == 'fail1' And '$(BundlerFakeNext)' == '1'" />
            <Exec Command="powershell -NoProfile -Command &quot;Start-Sleep 30&quot;" Condition="'$(FAKE_MODE)' == 'block1' And '$(BundlerFakeNext)' == '1' And $([MSBuild]::IsOSPlatform('windows'))" />
            <Exec Command="sleep 30" Condition="'$(FAKE_MODE)' == 'block1' And '$(BundlerFakeNext)' == '1' And !$([MSBuild]::IsOSPlatform('windows'))" />
          </Target>
        </Project>
        """;

    private (string Project, string Log, string State, string NugetConfig) SetupFakeProject()
    {
        var dir = Temp("tooling-diag").FullName;
        var project = Path.Combine(dir, "Fake.csproj");
        File.WriteAllText(project, FakeProjectContent);
        // 钉死空源：零包项目 restore 本不需要源，clear 防测试意外走用户源联网。
        var nugetConfig = Path.Combine(dir, "NuGet.Config");
        File.WriteAllText(nugetConfig,
            "<configuration><packageSources><clear /></packageSources></configuration>");
        return (project, Path.Combine(dir, "calls.log"), Path.Combine(dir, "state.txt"),
            nugetConfig);
    }

    private static Dictionary<string, string?> FakeEnv(string log, string state,
        string? mode = null) => new()
    {
        [LogEnv] = log,
        ["FAKE_STATE"] = state,
        ["FAKE_MODE"] = mode ?? "ok",
    };

    private sealed class EnvVar(string name) : IDisposable
    {
        private readonly string? _prev = Environment.GetEnvironmentVariable(name);
        public void Set(string? value) => Environment.SetEnvironmentVariable(name, value);
        public void Dispose() => Environment.SetEnvironmentVariable(name, _prev);
    }

    [Fact]
    public void RestoreWithRetry_TraceLogsEveryAttempt()
    {
        var (project, log, state, cfg) = SetupFakeProject();
        var trace = TempPath("restore-trace.log");
        using var traceEnv = new EnvVar("BUNDLER_RESTORE_TRACE");
        traceEnv.Set(trace);
        var options = new ProcessRunner.Options
        {
            Environment = FakeEnv(log, state, "fail1"),
        };
        var result = Dotnet.RestoreWithRetry(["restore", project, "--configfile", cfg], "fake-restore", options);
        Assert.Equal(0, result.ExitCode);
        var lines = File.ReadAllLines(trace);
        Assert.Contains(lines, l => l.Contains("BEGIN") && l.Contains("attempt=1/6"));
        Assert.Contains(lines, l => l.Contains("SPAWN") && l.Contains("attempt=1"));
        Assert.Contains(lines,
            l => l.Contains("EXIT") && !l.Contains("rc=0") && l.Contains("attempt=1"));
        Assert.Contains(lines, l => l.Contains("RETRY") && l.Contains("next-attempt=2"));
        Assert.Contains(lines, l => l.Contains("BEGIN") && l.Contains("attempt=2/6"));
        Assert.Contains(lines, l => l.Contains("EXIT rc=0") && l.Contains("attempt=2"));
    }

    [Fact]
    public void RestoreWithRetry_TraceTimeoutThenRetrySucceeds()
    {
        var (project, log, state, cfg) = SetupFakeProject();
        var trace = TempPath("restore-trace-timeout.log");
        using var traceEnv = new EnvVar("BUNDLER_RESTORE_TRACE");
        traceEnv.Set(trace);
        var options = new ProcessRunner.Options
        {
            Environment = FakeEnv(log, state, "block1"),
        };
        var result = Dotnet.RestoreWithRetry(["restore", project, "--configfile", cfg], "fake-restore",
            options, attemptTimeout: TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        var lines = File.ReadAllLines(trace);
        Assert.Contains(lines, l => l.Contains("TIMEOUT") && l.Contains("attempt=1"));
        Assert.Contains(lines, l => l.Contains("RETRY") && l.Contains("next-attempt=2"));
        Assert.Contains(lines, l => l.Contains("EXIT rc=0") && l.Contains("attempt=2"));
    }

    [Fact]
    public void RestoreWithRetry_IdleWatchdogKillsSilentHangEarly()
    {
        // block1 fake 在 attempt=1 跑静默 sleep 30s——零输出挂死签名。
        // idleTimeout=5s 应远早于 attemptTimeout=60s 墙钟就杀进程换枪；
        // 全程耗时 << 60s 即证明是空闲判死而非陪跑满墙钟。
        var (project, log, state, cfg) = SetupFakeProject();
        var trace = TempPath("restore-trace-idle.log");
        using var traceEnv = new EnvVar("BUNDLER_RESTORE_TRACE");
        traceEnv.Set(trace);
        var options = new ProcessRunner.Options
        {
            Environment = FakeEnv(log, state, "block1"),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Dotnet.RestoreWithRetry(["restore", project, "--configfile", cfg], "fake-restore",
            options, attemptTimeout: TimeSpan.FromSeconds(60), idleTimeout: TimeSpan.FromSeconds(5));
        sw.Stop();
        Assert.Equal(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(45),
            $"idle-kill 应在 5s 级触发，实际全程 {sw.Elapsed}——疑似陪跑到 60s 墙钟");
        var lines = File.ReadAllLines(trace);
        Assert.Contains(lines, l => l.Contains("TIMEOUT") && l.Contains("attempt=1"));
        Assert.Contains(lines, l => l.Contains("EXIT rc=0") && l.Contains("attempt=2"));
    }

    [Fact]
    public void RestoreWithRetry_TraceDoesNotLeakSecrets()
    {
        var (project, log, state, cfg) = SetupFakeProject();
        var trace = TempPath("restore-trace-secrets.log");
        using var traceEnv = new EnvVar("BUNDLER_RESTORE_TRACE");
        traceEnv.Set(trace);
        var options = new ProcessRunner.Options
        {
            Environment = FakeEnv(log, state),
        };
        options.Environment["FAKE_SECRET"] = "SECRET-ENV-MARKER";
        var result = Dotnet.RestoreWithRetry(
            ["restore", project, "--configfile", cfg, "-p:BundlerMarker=SECRET-ARG-MARKER"],
            "fake-restore", options);
        Assert.Equal(0, result.ExitCode);
        var content = File.ReadAllText(trace);
        Assert.DoesNotContain("SECRET-ARG-MARKER", content);
        Assert.DoesNotContain("SECRET-ENV-MARKER", content);
    }

    [Fact]
    public void RestoreWithRetry_ProcessStartedChainedAndRestored()
    {
        var (project, log, state, cfg) = SetupFakeProject();
        var trace = TempPath("restore-trace-cb.log");
        using var traceEnv = new EnvVar("BUNDLER_RESTORE_TRACE");
        traceEnv.Set(trace);
        var callerPids = new List<int>();
        Action<int> original = pid => callerPids.Add(pid);
        var options = new ProcessRunner.Options
        {
            Environment = FakeEnv(log, state),
            ProcessStarted = original,
        };
        var result = Dotnet.RestoreWithRetry(["restore", project, "--configfile", cfg], "fake-restore", options);
        Assert.Equal(0, result.ExitCode);
        // 包装链到调用方回调，且退出后原字段恢复（不残留诊断包装）。
        Assert.Single(callerPids);
        Assert.Same(original, options.ProcessStarted);
    }

    [Fact]
    public void ProcessStarted_CallbackFailureDoesNotBreakSpawnedProcess()
    {
        var (project, log, _, cfg) = SetupFakeProject();
        var options = new ProcessRunner.Options
        {
            Environment = FakeEnv(log, ""),
            ProcessStarted = _ => throw new InvalidOperationException("diag boom"),
        };
        var result = Dotnet.RestoreWithRetry(["restore", project, "--configfile", cfg], "fake-restore", options);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CALL", File.ReadAllText(log));
    }
}
