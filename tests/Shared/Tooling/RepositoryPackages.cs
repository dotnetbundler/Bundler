// 共享 nupkg 供给：同一测试进程内只 build+pack 一次（POSIX 腿共用 artifacts/packages；
// MSI 腿走自己独立的包目录，因为 Pack-MsiTestPackages 还要核对 Wix 分发哈希）。
internal static class RepositoryPackages
{
    private static readonly Lazy<string> _shared = new(() =>
    {
        var dir = RepositoryLayout.PackageDirectory;
        System.IO.Directory.CreateDirectory(dir);
        // 并行测试进程共用同一包目录：跨进程文件锁包住 build+pack 全程，
        // 先来的进程产包，后来的等锁后直接用产物。
        var lockPath = Path.Combine(dir, ".pack.lock");
        using var lockStream = new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Dotnet.ShutdownBuildServers();
        Dotnet.BuildSolution();
        Dotnet.Checked(
            ["pack", Path.Combine(RepositoryLayout.Root, "Bundler.slnx"), "-c", "Release", "-o", dir],
            "dotnet pack Bundler.slnx");
        return dir;
    });

    public static string DirectoryPath => _shared.Value;
    public static string Version => RepositoryLayout.PackageVersion;
}
