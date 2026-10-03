// 宿主平台判定辅助：供各 API 测试的 Assert.Skip* 门控使用
internal static class TestPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();

    // musl 宿主（如 alpine）跑不了随包内嵌的 glibc makensis
    public static bool IsMusl => IsLinux &&
        File.Exists("/etc/os-release") &&
        File.ReadAllText("/etc/os-release").Contains("alpine", StringComparison.OrdinalIgnoreCase);
}
