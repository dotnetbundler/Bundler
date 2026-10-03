// 宿主平台判定辅助：供各测试的 Assert.Skip* 门控使用
internal static class TestPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();
}
