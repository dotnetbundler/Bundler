// 外部工具探测：脚本 "command -v → SKIP" 的测试形态。
internal static class ExternalTools
{
    public static bool Has(string tool)
    {
        try
        {
            var probe = OperatingSystem.IsWindows() ? "where.exe" : "/bin/sh";
            var args = OperatingSystem.IsWindows() ? (IEnumerable<string>)[tool] : new[] { "-c", $"command -v {tool} >/dev/null 2>&1" };
            var result = ProcessRunner.Run(probe, args,
                new ProcessRunner.Options { Timeout = TimeSpan.FromSeconds(15) });
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Require(string tool, string because = "")
    {
        Assert.SkipWhen(!Has(tool),
            $"SKIP: required tool '{tool}' is not installed on this host.{(because.Length > 0 ? " " + because : "")}");
    }
}
