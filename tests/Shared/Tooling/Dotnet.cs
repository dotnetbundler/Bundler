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

    // Pack-MsiTestPackages 的 C# 形态：先杀常驻 MSBuild 节点释放任务程序集文件锁，
    // 再 build+pack，断言全部 nupkg 落在目标目录。
    public static void BuildAndPack(string packageDirectory, string configuration = "Release")
    {
        Directory.CreateDirectory(packageDirectory);
        ShutdownBuildServers();
        BuildSolution(configuration);
        PackSolution(packageDirectory, configuration);
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

    public static void ExtractNupkgEntry(string nupkgPath, string entryName, string destinationPath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(nupkgPath);
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    public static void Restore(string project, IEnumerable<string> properties, string what)
        => RestoreWithRetry(["restore", project, .. properties], what);

    // NuGet 还原在共享 runner 上偶发连接层挂死（零输出卡到进程超时，
    // CI windows-11-arm 实证一单挂满 10min）——restore 幂等，超时/非零退出有界重试。
    // 挂死特征是打完 "Determining projects to restore..." 即零输出僵住：
    // 3 分钟陪跑已给足正常还原十倍冗余，到点杀进程换枪（attemptTimeout 可显式覆盖；
    // 本方法接管 options.Timeout——调用方传的 Timeout 不生效）。
    // 刻意失败的负例调用勿走此口：继续用 Run 单发，避免确定性失败陪跑重试。
    public static ProcessRunner.Result RestoreWithRetry(IEnumerable<string> args, string what,
        ProcessRunner.Options? options = null, int maxAttempts = 4,
        TimeSpan? attemptTimeout = null)
    {
        options ??= new ProcessRunner.Options();
        options.Timeout = attemptTimeout ?? TimeSpan.FromMinutes(3);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = Run(args, options);
                if (result.ExitCode == 0)
                {
                    return result;
                }
                if (attempt >= maxAttempts)
                {
                    ProcessRunner.AssertSuccess(result, what);
                    return result;
                }
            }
            catch (TimeoutException) when (attempt < maxAttempts)
            {
            }
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

    public static ProcessRunner.Result Test(string project, string configuration,
        string filterClass, string what, Dictionary<string, string?>? environment = null)
        => Checked(
            ["test", project, "-c", configuration, "--", "--filter-class", filterClass],
            what, new ProcessRunner.Options { Environment = environment });
}
