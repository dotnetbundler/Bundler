// 外部工具探测：脚本 "command -v → SKIP" 的测试形态。
// 探测经进程内 PATH 扫描而非 spawn where.exe/sh——共享 runner 高载时
// where.exe 扫整条 PATH 曾超出 15s 探测超时被误判为"工具缺失"（CI 实证），
// 进程内扫描不产子进程、无超时面。
internal static class ExternalTools
{
    public static bool Has(string tool)
    {
        if (tool.Length == 0 || tool.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (OperatingSystem.IsWindows())
        {
            // where.exe/CreateProcess 语义：当前目录先于 PATH。
            var dirs = new List<string> { Environment.CurrentDirectory };
            dirs.AddRange(pathDirs);
            var names = Path.HasExtension(tool)
                ? [tool]
                : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(ext => tool + ext).ToArray();
            return dirs.Any(dir => names.Any(name => File.Exists(Path.Combine(dir, name))));
        }
        // command -v/execvp 语义：只搜 PATH，不搜当前目录。
        return pathDirs.Any(dir => HasPosixExecuteBit(Path.Combine(dir, tool)));
    }

    private static bool HasPosixExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path) || Directory.Exists(path))
        {
            return false;
        }
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
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
