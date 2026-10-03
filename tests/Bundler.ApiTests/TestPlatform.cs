// 宿主平台判定辅助：供各 API 测试的 Assert.Skip* 门控使用
internal static class TestPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();

    // musl 宿主跑不了随包内嵌的 glibc makensis：查 musl 动态加载器（各 musl 发行版通用），
    // 再以 /etc/os-release 的 alpine/musl 字样兜底
    public static bool IsMusl => IsLinux &&
        ((Directory.Exists("/lib") &&
          Directory.EnumerateFiles("/lib", "ld-musl-*.so.1").Any()) ||
         (File.Exists("/etc/os-release") &&
          File.ReadAllText("/etc/os-release") is { } osRelease &&
          (osRelease.Contains("alpine", StringComparison.OrdinalIgnoreCase) ||
           osRelease.Contains("musl", StringComparison.OrdinalIgnoreCase))));
}
