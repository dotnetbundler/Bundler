// 仓库布局解析：测试进程的工作目录不固定，统一从 AppContext.BaseDirectory 向上找含
// Bundler.slnx 的根；与脚本的 "$PSScriptRoot\..\.." 等价。
internal static class RepositoryLayout
{
    private static readonly Lazy<string> _root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bundler.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate the repository root above {AppContext.BaseDirectory}");
    });

    public static string Root => _root.Value;
    public static string ArtifactsRoot => Path.Combine(Root, "artifacts");
    public static string PackageDirectory => Path.Combine(ArtifactsRoot, "packages");
    public static string TestsDirectory => Path.Combine(Root, "tests");
    public static string FixturesDirectory => Path.Combine(TestsDirectory, "Bundler.IntegrationTests", "Fixtures");

    private static string? _packageVersion;

    // 等价于 Get-BundlerPackageVersion：从根 Directory.Build.props 读 BundlerPackageVersion。
    public static string PackageVersion => _packageVersion ??= ReadPackageVersion();

    private static string ReadPackageVersion()
    {
        var props = Path.Combine(Root, "Directory.Build.props");
        var doc = System.Xml.Linq.XDocument.Load(props);
        var version = doc.Root?.Element("PropertyGroup")?.Element("BundlerPackageVersion")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(version),
            $"BundlerPackageVersion is missing from {props}");
        return version.Trim();
    }

    // 等价于 Assert-UnderIntegrationRoot：拒绝清理不属于集成根的路径。
    public static void AssertUnderRoot(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.True(fullPath.StartsWith(prefix, StringComparison.Ordinal) ||
                    string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), prefix.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal),
            $"Refusing to modify a path outside the integration root: {fullPath}");
    }
}
