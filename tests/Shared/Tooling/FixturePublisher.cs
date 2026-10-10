// fixture 发布与还原：每个集成测试类需要"拷贝 fixture 工程 → 用包源还原 → publish"
// 三步；合在一个类里等价于 Copy-MsiTestFixture + Restore-MsiTestFixture + dotnet publish。
internal static class FixturePublisher
{
    // 把 fixture 工程目录拷进工作区并返回工程文件路径（等价 Copy-MsiTestFixture：
    // 拷贝 *.csproj/*.wixproj/Program.cs/Assets/ 等全部源文件，排除 bin/obj/本地产物）。
    public static string CopyFixtureProject(string sourceDir, string destinationDir,
        string projectFileName)
    {
        Assert.True(Directory.Exists(sourceDir), $"Fixture project is missing: {sourceDir}");
        var destinationProject = Path.Combine(destinationDir, projectFileName);
        if (File.Exists(destinationProject))
        {
            return destinationProject;
        }
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            var target = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        return destinationProject;
    }

    // Restore-MsiTestFixture：包源 + 隔离缓存还原，附带 LocalRestoreAssert 契约核验。
    public static void RestoreFixture(string project, string packageDirectory, string packageCache,
        string packageVersion, string target, IReadOnlyList<string> requiredPackages)
    {
        var config = PinnedPackageSource.WriteConfig(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packageCache))!, "nuget-pin"),
            packageDirectory);
        Dotnet.Restore(project,
        [
            "--configfile", config,
            $"-p:BundlerPackageSource={packageDirectory}",
            $"-p:RestorePackagesPath={packageCache}",
            $"-p:BundlerPackageVersion={packageVersion}",
            $"-p:BundlerTarget={target}",
        ], $"fixture restore failed for {project}");
        LocalRestoreAssert.Verify(project, packageVersion, packageDirectory, packageCache,
            requiredPackages);
    }

    public static ProcessRunner.Result PublishFixture(string project, string output,
        string packageDirectory, string packageCache, string packageVersion,
        string target, IEnumerable<string> extraProperties, string what,
        string configuration = "Release")
        => Dotnet.Publish(project, configuration,
        [
            $"-p:BundlerPackageSource={packageDirectory}",
            $"-p:RestorePackagesPath={packageCache}",
            $"-p:BundlerPackageVersion={packageVersion}",
            $"-p:BundlerTarget={target}",
            $"-p:BundlerOutputPath={output}",
            .. extraProperties,
        ], what);
}
