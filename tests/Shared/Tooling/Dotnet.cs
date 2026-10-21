// dotnet CLI 包装：集成腿的所有 dotnet build/pack/publish/restore/test 走这里。
using System.IO.Compression;

internal static class Dotnet
{
    public static ProcessRunner.Result Run(params string[] args)
        => ProcessRunner.Run("dotnet", args);

    public static ProcessRunner.Result Run(IEnumerable<string> args, ProcessRunner.Options options)
        => ProcessRunner.Run("dotnet", args, options);

    public static ProcessRunner.Result Checked(IEnumerable<string> args, string what,
        ProcessRunner.Options? options = null)
    {
        var result = Run(args, options ?? new ProcessRunner.Options());
        ProcessRunner.AssertSuccess(result, what);
        return result;
    }

    // 测试进程内绝不能 build/pack 整个 slnx：MSBuild 会重建测试工程自身，
    // 向运行中进程锁住的 bin\ 目录写 src dll（MSB3021/MSB3027）。逐 src 工程操作可根除。
    public static IEnumerable<string> SourceProjectPaths()
        => Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, "src"),
            "*.csproj", SearchOption.AllDirectories);

    public static void BuildSolution(string configuration = "Release")
    {
        foreach (var project in SourceProjectPaths())
            Checked(["build", project, "-c", configuration], $"dotnet build {Path.GetFileName(project)}");
    }

    public static void PackSolution(string packageDirectory, string configuration = "Release",
        IEnumerable<string>? extraArgs = null)
    {
        var extra = extraArgs?.ToArray() ?? [];
        foreach (var project in SourceProjectPaths())
            Checked(["pack", project, "-c", configuration, "-o", packageDirectory, ..extra],
                $"dotnet pack {Path.GetFileName(project)}");
    }

    // nodeReuse 文件锁只在 Windows 存在：常驻 MSBuild 节点锁住任务程序集时 pack 无法覆盖。
    // POSIX 允许覆盖使用中的文件无需 shutdown，且 musl 上 VBCSCompiler -shutdown
    // 子进程间歇性不退出会让 muxer 挂死——shutdown 因此限 Windows。
    public static void ShutdownBuildServers()
    {
        if (OperatingSystem.IsWindows())
        {
            Run(["build-server", "shutdown"]);
        }
    }

    public static void AssertPackageExists(string packageDirectory, string packageId, string version)
    {
        var path = Path.Combine(packageDirectory, $"{packageId}.{version}.nupkg");
        Assert.True(File.Exists(path), $"Package not found: {path}");
    }

    // nupkg 条目清单（等价 ZipFile 遍历 FullName）。
    public static IReadOnlyList<string> NupkgEntries(string nupkgPath)
    {
        Assert.True(File.Exists(nupkgPath), $"NuGet package is missing: {nupkgPath}");
        using var archive = System.IO.Compression.ZipFile.OpenRead(nupkgPath);
        return archive.Entries.Select(e => e.FullName).ToArray();
    }

    // NuGet 还原在共享 runner 上偶发连接层挂死（零输出卡到进程超时——
    // CI windows-11-arm 实证一单挂满 10min、verify run 39552269251 一次
    // ~1.5h 劣化窗口 20/31 次 attempt 全超时）——三层缓解：
    //   1) 统一注入 -p:NuGetAudit=false：audit 每次 restore 向每个 HTTP 源
    //      拉漏洞数据（SDK10 默认 mode=all），是缓存命中路径上唯一保证
    //      存在的网络触点，测试语境零价值；
    //   2) IdleTimeout 看门狗：沉默即杀换枪（挂死签名是
    //      "Determining projects to restore..." 后零输出僵住），
    //      同预算内换枪次数翻倍；慢但有进展的 restore 反而拿满
    //      attemptTimeout 不被误杀——看门狗与墙钟分工见 ProcessRunner.IdleTimeout；
    //   3) 有界重试：restore 幂等，超时/非零退出换新进程。
    // 本方法接管 options.Timeout/IdleTimeout——调用方传的这两项不生效
    // （attemptTimeout/idleTimeout 可显式覆盖）。
    // 刻意失败的负例调用勿走此口：继续用 Run 单发，避免确定性失败陪跑重试。
    // BUNDLER_RESTORE_TRACE 置位时逐 attempt 落事件（BEGIN/SPAWN/EXIT/TIMEOUT/RETRY）：
    // "Determining projects" 后零输出卡到超时的取证面——只记 pid/attempt/rc/elapsed/
    // 已有 what 标签，不落 argv/环境/输出（防秘密入档）。
    public static ProcessRunner.Result RestoreWithRetry(IEnumerable<string> args, string what,
        ProcessRunner.Options? options = null, int maxAttempts = 6,
        TimeSpan? attemptTimeout = null, TimeSpan? idleTimeout = null)
    {
        options ??= new ProcessRunner.Options();
        options.Timeout = attemptTimeout ?? TimeSpan.FromMinutes(3);
        options.IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(75);
        // 统一关 audit——见上头注第 1 条；追加在调用方 -p 之后亦无害（后写胜）。
        var argv = args.ToList();
        argv.Add("-p:NuGetAudit=false");
        var tracePath = Environment.GetEnvironmentVariable("BUNDLER_RESTORE_TRACE");
        var tracing = !string.IsNullOrEmpty(tracePath);
        var originalStarted = options.ProcessStarted;
        var attempt = 0;
        if (tracing)
        {
            // 包装（非替换）调用方 ProcessStarted：本钩子只加 SPAWN 行，退出时还原。
            options.ProcessStarted = pid =>
            {
                TraceRestore(tracePath!, $"SPAWN pid={pid} attempt={attempt}");
                originalStarted?.Invoke(pid);
            };
        }
        try
        {
            for (attempt = 1; ; attempt++)
            {
                var attemptStart = DateTime.UtcNow;
                TraceRestore(tracePath,
                    $"BEGIN pid={Environment.ProcessId} what=\"{what}\" attempt={attempt}/{maxAttempts} timeout={options.Timeout.TotalSeconds:F0}s");
                try
                {
                    var result = Run(argv, options);
                    var elapsed = (DateTime.UtcNow - attemptStart).TotalSeconds;
                    TraceRestore(tracePath, $"EXIT rc={result.ExitCode} elapsed={elapsed:F1}s attempt={attempt}");
                    if (result.ExitCode == 0)
                    {
                        return result;
                    }
                    if (attempt >= maxAttempts)
                    {
                        ProcessRunner.AssertSuccess(result, what);
                        return result;
                    }
                    TraceRestore(tracePath, $"RETRY next-attempt={attempt + 1}");
                }
                catch (TimeoutException)
                {
                    // 终试也记 TIMEOUT 再原样抛——中间 attempt 不再无迹吞掉。
                    TraceRestore(tracePath,
                        $"TIMEOUT elapsed={(DateTime.UtcNow - attemptStart).TotalSeconds:F1}s attempt={attempt}");
                    if (attempt >= maxAttempts)
                    {
                        throw;
                    }
                    TraceRestore(tracePath, $"RETRY next-attempt={attempt + 1}");
                }
            }
        }
        finally
        {
            options.ProcessStarted = originalStarted;
        }
    }

    private static void TraceRestore(string? tracePath, string line)
    {
        if (string.IsNullOrEmpty(tracePath))
        {
            return;
        }
        try
        {
            File.AppendAllText(tracePath, $"[{DateTime.UtcNow:o}] {line}\n");
        }
        catch
        {
            // 诊断落盘失败不遮 restore 主异常。
        }
    }

    public static ProcessRunner.Result Publish(string project, string configuration,
        IEnumerable<string> properties, string what, bool noRestore = true,
        Dictionary<string, string?>? environment = null)
    {
        var args = new List<string> { "publish", project, "-c", configuration };
        if (noRestore)
        {
            args.Add("--no-restore");
        }
        args.AddRange(properties);
        return Checked(args, what, new ProcessRunner.Options { Environment = environment });
    }
}
