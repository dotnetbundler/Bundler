// CI 测试驱动（scripts/ci/run-tests.{sh,ps1}）的 fake-dotnet 端到端腿：
// 每用例在独立临时目录搭最小 repo 壳（scripts/ci + 空 dll 占位 + src/Fake/Fake.csproj），
// PATH 前置本轮 fake dotnet——只记录调用 argv 并按需吐 `-list classes` 名单，
// 不触真实 dotnet 构建/NuGet/安装。断言以脚本退出码与调用记录为准，不只查文本。
using Bundler.TestShared;

public sealed class CiTestDriverIntegrationTests : TempBase
{
    private const string LogEnv = "BUNDLER_FAKE_DOTNET_LOG";

    private static string FakeDotnetContent =>
        OperatingSystem.IsWindows()
            ? "@echo off\r\n" +
              $"echo CALL %* >> \"%{LogEnv}%\"\r\n" +
              "echo %*| findstr /C:\"-list classes\" >nul\r\n" +
              "if %errorlevel%==0 (\r\n" +
              "  echo FakeFirstTests\r\n" +
              "  echo FakeSecondTests\r\n" +
              ")\r\n" +
              "exit /b 0\r\n"
            : "#!/bin/sh\n" +
              $"printf 'CALL %s\\n' \"$*\" >> \"${LogEnv}\"\n" +
              "case \"$*\" in\n" +
              "  *\"-list classes\"*) printf 'FakeFirstTests\\nFakeSecondTests\\n' ;;\n" +
              "esac\n" +
              "exit 0\n";

    // 最小 repo 壳：driver 脚本会 cd 到 scripts/ci 上两级作为 ROOT。
    private (string Repo, string Log) SetupFakeRepo()
    {
        var repo = Temp("ci-driver").FullName;
        var log = Path.Combine(repo, "dotnet-calls.log");
        var scripts = Path.Combine(repo, "scripts", "ci");
        Directory.CreateDirectory(scripts);
        File.Copy(Path.Combine(RepositoryLayout.Root, "scripts", "ci", "run-tests.ps1"),
            Path.Combine(scripts, "run-tests.ps1"));
        File.Copy(Path.Combine(RepositoryLayout.Root, "scripts", "ci", "run-tests.sh"),
            Path.Combine(scripts, "run-tests.sh"));
        // 四层 dll 占位（fake dotnet 不读内容）+ pack glob 锚点 src/Fake/Fake.csproj。
        foreach (var (layer, name) in new[]
        {
            ("Unit", "Bundler.UnitTests"), ("Api", "Bundler.ApiTests"),
            ("Integration", "Bundler.IntegrationTests"), ("Package", "Bundler.PackageTests")
        })
        {
            var dll = Path.Combine(repo, "tests", layer, "bin", "Release", "net10.0",
                name + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
            File.WriteAllText(dll, "");
        }
        var fake = Path.Combine(repo, "src", "Fake", "Fake.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
        File.WriteAllText(fake, "<Project />");
        var fakeBin = Path.Combine(repo, "fakebin");
        Directory.CreateDirectory(fakeBin);
        var fakeDotnet = Path.Combine(fakeBin,
            OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet");
        File.WriteAllText(fakeDotnet, FakeDotnetContent);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeDotnet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(scripts, "run-tests.sh"),
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        return (repo, log);
    }

    private ProcessRunner.Result RunDriver(string repo, string log, string suite,
        string? classFilter)
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = Path.Combine(repo, "fakebin") + Path.PathSeparator +
                Environment.GetEnvironmentVariable("PATH"),
            [LogEnv] = log,
            // 防继承外层 CI 诊断 env 击穿 fake 现场：driver 开头会截断/创建
            // BUNDLER_TOOL_TRACE 指向的文件，必须钉在本 repo 内、其余两项移出子环境。
            ["BUNDLER_TOOL_TRACE"] = Path.Combine(repo, "artifacts", "ci",
                "fake-tool-trace.log"),
            ["BUNDLER_RESTORE_TRACE"] = null,
            ["BUNDLER_NSIS_DIAGNOSTICS"] = null,
        };
        var options = new ProcessRunner.Options
        {
            WorkingDirectory = repo,
            Timeout = TimeSpan.FromMinutes(1),
            Environment = env,
        };
        if (OperatingSystem.IsWindows())
        {
            var shell = HasOnPath("pwsh.exe") ? "pwsh" : "powershell";
            var args = new List<string>
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(repo, "scripts", "ci", "run-tests.ps1"),
                "-Suite", suite,
            };
            if (!string.IsNullOrEmpty(classFilter))
            {
                args.AddRange(["-ClassFilter", classFilter]);
            }
            return ProcessRunner.Run(shell, args, options);
        }
        return ProcessRunner.Run("bash",
            [Path.Combine(repo, "scripts", "ci", "run-tests.sh"), suite, classFilter ?? ""],
            options);
    }

    private static bool HasOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, fileName)))
            {
                return true;
            }
        }
        return false;
    }

    private static string[] Calls(string log) =>
        File.Exists(log) ? File.ReadAllLines(log) : [];

    private static int CountCalls(string log, string needle) =>
        Calls(log).Count(line => line.Contains(needle, StringComparison.Ordinal));

    [Fact]
    public void IntegrationSingleClassFilter_RunsOnlyThatClass()
    {
        var (repo, log) = SetupFakeRepo();
        var result = RunDriver(repo, log, "integration", "FakeFirstTests");
        Assert.Equal(0, result.ExitCode);
        Assert.True(CountCalls(log, "-list classes") >= 1);
        Assert.Equal(1, CountCalls(log, "-class FakeFirstTests"));
        Assert.Equal(0, CountCalls(log, "-class FakeSecondTests"));
    }

    [Fact]
    public void IntegrationMultiClassFilter_RunsEachRequestedOnce()
    {
        var (repo, log) = SetupFakeRepo();
        var result = RunDriver(repo, log, "integration", "FakeFirstTests, FakeSecondTests");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, CountCalls(log, "-class FakeFirstTests"));
        Assert.Equal(1, CountCalls(log, "-class FakeSecondTests"));
    }

    [Fact]
    public void PackageClassFilter_PacksThenRunsOnlyThatClass()
    {
        var (repo, log) = SetupFakeRepo();
        var result = RunDriver(repo, log, "package", "FakeFirstTests");
        Assert.Equal(0, result.ExitCode);
        Assert.True(CountCalls(log, "pack") >= 1, "package suite must pack src first");
        Assert.Equal(1, CountCalls(log, "-class FakeFirstTests"));
        Assert.Equal(0, CountCalls(log, "-class FakeSecondTests"));
    }

    [Fact]
    public void UnknownClass_RejectedBeforeRunningTests()
    {
        var (repo, log) = SetupFakeRepo();
        var result = RunDriver(repo, log, "integration", "NoSuchClass");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, CountCalls(log, "-class "));
    }

    [Fact]
    public void ClassFilterOnUnitSuite_Rejected()
    {
        var (repo, log) = SetupFakeRepo();
        var result = RunDriver(repo, log, "unit", "FakeFirstTests");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Calls(log));
    }

    [Fact]
    public void EmptyFilter_DefaultCoverageUnchanged()
    {
        var (repo, log) = SetupFakeRepo();
        var result = RunDriver(repo, log, "all", null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, CountCalls(log, "Bundler.UnitTests.dll -longRunning"));
        Assert.Equal(1, CountCalls(log, "Bundler.ApiTests.dll -longRunning"));
        Assert.Equal(1, CountCalls(log, "-class FakeFirstTests"));
        Assert.Equal(1, CountCalls(log, "-class FakeSecondTests"));
        Assert.True(CountCalls(log, "pack") >= 1);
        Assert.Equal(1, CountCalls(log, "Bundler.PackageTests.dll -longRunning"));
    }

    // 拒绝面：仅空白、大小写不符、通配符都必须非零且零 -class 调用——
    // 跨宿主一致（PS -ccontains / sh 字面 = 均区分大小写、read -a 不做 glob）。
    [Theory]
    [InlineData("   ")]
    [InlineData("fakefirsttests")]
    [InlineData("*")]
    public void InvalidClassFilter_RejectedWithoutRunningTests(string filter)
    {
        var (repo, log) = SetupFakeRepo();
        var result = RunDriver(repo, log, "integration", filter);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, CountCalls(log, "-class "));
    }

    [Fact]
    public void DuplicateClassRequest_RunsOnceInOrder()
    {
        var (repo, log) = SetupFakeRepo();
        var result = RunDriver(repo, log, "integration",
            "FakeSecondTests,FakeFirstTests,FakeSecondTests");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, CountCalls(log, "-class FakeSecondTests"));
        Assert.Equal(1, CountCalls(log, "-class FakeFirstTests"));
        // 保序：FakeSecondTests 先于 FakeFirstTests 执行。
        var calls = Calls(log).Where(l => l.Contains("-class ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains("FakeSecondTests", calls[0]);
        Assert.Contains("FakeFirstTests", calls[1]);
    }

    // 隔离回归：外层 CI env（BUNDLER_TOOL_TRACE）泄漏时 driver 会截断真实诊断文件——
    // RunDriver 必须把该变量重定向进 fake repo，外层 sentinel 内容一字不动。
    [Fact]
    public void DriverRun_DoesNotTouchOuterToolTrace()
    {
        var (repo, log) = SetupFakeRepo();
        var sentinelDir = Temp("outer-trace").FullName;
        var sentinel = Path.Combine(sentinelDir, "sentinel.log");
        File.WriteAllText(sentinel, "SENTINEL-KEEP");
        using var traceEnv = new EnvVar("BUNDLER_TOOL_TRACE");
        traceEnv.Set(sentinel);
        var result = RunDriver(repo, log, "unit", null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("SENTINEL-KEEP", File.ReadAllText(sentinel));
    }

    private sealed class EnvVar(string name) : IDisposable
    {
        private readonly string? _prev = Environment.GetEnvironmentVariable(name);
        public void Set(string? value) => Environment.SetEnvironmentVariable(name, value);
        public void Dispose() => Environment.SetEnvironmentVariable(name, _prev);
    }
}
