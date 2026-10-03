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

    public static void BuildSolution(string configuration = "Release")
        => Checked(["build", Path.Combine(RepositoryLayout.Root, "Bundler.slnx"), "-c", configuration],
            "dotnet build Bundler.slnx");

    // Pack-MsiTestPackages 的 C# 形态：先杀常驻 MSBuild 节点释放任务程序集文件锁，
    // 再 build+pack，断言全部 nupkg 落在目标目录。
    public static void BuildAndPack(string packageDirectory, string configuration = "Release")
    {
        Directory.CreateDirectory(packageDirectory);
        Run(["build-server", "shutdown"]);
        BuildSolution(configuration);
        Checked(["pack", Path.Combine(RepositoryLayout.Root, "Bundler.slnx"), "-c", configuration,
                "-o", packageDirectory], "dotnet pack Bundler.slnx");
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
        => Checked(["restore", project, .. properties], what);

    public static ProcessRunner.Result Publish(string project, string configuration,
        IEnumerable<string> properties, string what, bool noRestore = true)
    {
        var args = new List<string> { "publish", project, "-c", configuration };
        if (noRestore)
        {
            args.Add("--no-restore");
        }
        args.AddRange(properties);
        return Checked(args, what);
    }

    public static ProcessRunner.Result Test(string project, string configuration,
        string filterClass, string what, Dictionary<string, string?>? environment = null)
        => Checked(
            ["test", project, "-c", configuration, "--", "--filter-class", filterClass],
            what, new ProcessRunner.Options { Environment = environment });
}
