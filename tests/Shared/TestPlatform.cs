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

    // Linux 宿主的 RID：musl 上 glibc 件链接不出可跑产物，须用 musl RID；
    // 架构随宿主——arm64 宿主本机产物须是 arm64 才能执行/AOT 链接。
    public static string LinuxRuntimeIdentifier => (IsMusl ? "linux-musl-" : "linux-") +
        (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");

    // macOS 宿主 RID：集成 fixture 默认 osx-arm64 是历史遗留（首轮宿主全是 arm64 Mac），
    // 要执行/安装的本机载荷必须匹配宿主 CPU——Intel Mac 上 arm64 件 Bad CPU type。
    public static string OsxRuntimeIdentifier =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";

    // 宿主是否 x64：仓内 linux/win fixture 载荷只产 x64 工件——执行类腿在非 x64 宿主只能跳过
    // （组装类断言不执行二进制，不受影响）。
    public static bool IsX64 =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64;

    // root 无视 chmod 权限位，权限失败类断言会被直接绕过
    public static bool IsRoot => IsLinux && GetEuid() == 0;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
}
