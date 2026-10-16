// 提权探测与执行：POSIX 侧 sudo -n；Windows 侧管理员身份。
// 不可用即 Assert.Skip——诚实门禁，不静默降级。
using System.Runtime.InteropServices;

internal static class ElevatedRunner
{
    private static readonly Lazy<bool> _sudo = new(() =>
    {
        if (!TestPlatform.IsLinux) return false;
        try
        {
            return ProcessRunner.Run("sudo", ["-n", "true"]).ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 宿主没有 sudo 二进制：探测失败即不可用，不向外抛。
            return false;
        }
        catch (System.IO.FileNotFoundException)
        {
            return false;
        }
    });

    // 无密 sudo 可用（用于写 /etc、装包等宿主级操作）。
    public static bool CanSudo => _sudo.Value;

    public static void RequireSudo(string? reason = null)
        => Assert.SkipWhen(!CanSudo,
            "SKIP: passwordless sudo unavailable; " + (reason ?? "elevated leg not exercised."));

    // Windows 管理员身份（msiexec /i、HKLM 写入需要）。
    public static bool IsWindowsAdministrator
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    public static void RequireWindowsAdministrator(string? reason = null)
        => Assert.SkipWhen(!IsWindowsAdministrator,
            "SKIP: elevation required; " + (reason ?? "elevated leg not exercised."));

    public static ProcessRunner.Result Sudo(string fileName, params string[] args)
        => ProcessRunner.Run("sudo", ["-n", fileName, .. args]);
}
