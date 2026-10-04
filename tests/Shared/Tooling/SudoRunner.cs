// 提权腿：POSIX 一律先 `sudo -n true` 探测，无 NOPASSWD 即 Skip——诚实门禁，
// 不再像脚本那样"无 sudo 静默降级"。
internal static class SudoRunner
{
    private static bool? _canSudo;

    public static bool CanSudoNonInteractive
    {
        get
        {
            _canSudo ??= Probe();
            return _canSudo.Value;
        }
    }

    private static bool Probe()
    {
        if (OperatingSystem.IsWindows() || !ExternalTools.Has("sudo"))
        {
            return false;
        }
        try
        {
            var result = ProcessRunner.Run("sudo", ["-n", "true"],
                new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(15) });
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Require(string leg)
        => Assert.SkipWhen(!CanSudoNonInteractive,
            $"SKIP: '{leg}' requires passwordless sudo on this host.");

    // sudo -n sh -c '<script>'：dpkg/rpm 真实装卸等腿在容器外用。
    public static ProcessRunner.Result RunScript(string script, TimeSpan? timeout = null)
        => ProcessRunner.Run("sudo", ["-n", "sh", "-c", script],
            new ProcessRunner.Options { Timeout = timeout ?? TimeSpan.FromMinutes(5) });

    public static ProcessRunner.Result CheckedScript(string script, string what, TimeSpan? timeout = null)
    {
        var result = RunScript(script, timeout);
        ProcessRunner.AssertSuccess(result, what);
        return result;
    }
}
