using System.Diagnostics;

namespace DotNet.Bundler.Updater.Bootstrap;

/// <summary>
/// Sparkle 三段式换包的宿主执行面：等宿主退出 → 旧目录改名备份 → 新载荷就位 → 重启。
/// 任意一步失败都尽力把备份还原——绝不留下半个安装目录。
/// </summary>
internal static class BootstrapPlan
{
    internal static int Run(string[] args)
    {
        var options = Parse(args);
        var log = options.LogFile is { Length: > 0 } logFile
            ? new Action<string>(line => File.AppendAllText(logFile, line + "\n"))
            : new Action<string>(line => Console.Out.WriteLine(line));
        var result = Apply(options, log);
        return result;
    }

    internal static int Apply(BootstrapOptions options, Action<string> log)
    {
        var installDir = Path.GetFullPath(options.InstallDirectory);
        var payloadDir = Path.GetFullPath(options.PayloadDirectory);
        var backupDir = Path.GetFullPath(
            options.BackupDirectory ?? installDir.TrimEnd('/', '\\') + ".bundler-backup");

        if (!Directory.Exists(installDir))
        {
            throw new UsageException($"install directory '{installDir}' does not exist.");
        }
        if (!Directory.Exists(payloadDir))
        {
            throw new UsageException($"payload directory '{payloadDir}' does not exist.");
        }
        if (IsSubpathOf(backupDir, installDir) || IsSubpathOf(backupDir, payloadDir) ||
            IsSubpathOf(installDir, backupDir) || IsSubpathOf(payloadDir, backupDir))
        {
            throw new UsageException("backup/install/payload directories must not nest inside each other.");
        }

        if (options.WaitPid is { } pid)
        {
            WaitForExit(pid, options.WaitTimeoutSeconds, log);
        }

        log($"bundler-updater: backup '{installDir}' → '{backupDir}'");
        if (Directory.Exists(backupDir))
        {
            Directory.Delete(backupDir, recursive: true);
        }
        MoveTree(installDir, backupDir, log);

        try
        {
            if (options.KeepPayload)
            {
                // 保留载荷用于调试与组合场景：复制换入而非移动。
                log($"bundler-updater: copy in '{payloadDir}' → '{installDir}'");
                CopyTree(payloadDir, installDir);
            }
            else
            {
                log($"bundler-updater: swap in '{payloadDir}' → '{installDir}'");
                MoveTree(payloadDir, installDir, log);
            }
        }
        catch
        {
            log("bundler-updater: swap failed, restoring backup");
            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }
            MoveTree(backupDir, installDir, log);
            throw;
        }

        if (options.AppPath is { Length: > 0 } app)
        {
            Restart(app, installDir, log);
        }
        if (!options.KeepPayload && Directory.Exists(payloadDir))
        {
            Directory.Delete(payloadDir, recursive: true);
        }
        log("bundler-updater: done");
        return 0;
    }

    private static void WaitForExit(int pid, int timeoutSeconds, Action<string> log)
    {
        log($"bundler-updater: waiting for pid {pid} to exit");
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return; // 进程不存在即视为已退出
            }
            Thread.Sleep(200);
        }
        throw new WaitTimeoutException();
    }

    private static void Restart(string appPath, string workingDirectory, Action<string> log)
    {
        log($"bundler-updater: restart '{appPath}'");
        // 分离子进程：引导程序退出后新进程继续存活。
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(appPath) { UseShellExecute = true, WorkingDirectory = workingDirectory }
            : new ProcessStartInfo("/bin/sh", ["-c", $"nohup \"{appPath}\" >/dev/null 2>&1 &"])
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
        Process.Start(startInfo);
    }

    // 同卷 rename(2)/MoveFile 原子就位；跨卷退化为复制+删除。
    private static void MoveTree(string source, string destination, Action<string> log)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
        }
        CopyTree(source, destination);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination), overwrite: true);
        }
    }

    private static bool IsSubpathOf(string candidate, string parent) =>
        candidate.StartsWith(
            parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static BootstrapOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not "apply")
        {
            throw new UsageException("expected the 'apply' command.");
        }
        var options = new BootstrapOptions();
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (name is "--keep-payload")
            {
                options.KeepPayload = true;
                continue;
            }
            var value = i + 1 < args.Length ? args[++i] :
                throw new UsageException($"option '{name}' requires a value.");
            switch (name)
            {
                case "--install-dir": options.InstallDirectory = value; break;
                case "--payload": options.PayloadDirectory = value; break;
                case "--wait-pid":
                    if (!int.TryParse(value, out var pid))
                    {
                        throw new UsageException($"--wait-pid expects a numeric pid, got '{value}'.");
                    }
                    options.WaitPid = pid;
                    break;
                case "--app": options.AppPath = value; break;
                case "--backup-dir": options.BackupDirectory = value; break;
                case "--log": options.LogFile = value; break;
                case "--wait-timeout":
                    if (!int.TryParse(value, out var seconds) || seconds <= 0)
                    {
                        throw new UsageException($"--wait-timeout expects a positive number, got '{value}'.");
                    }
                    options.WaitTimeoutSeconds = seconds;
                    break;
                default: throw new UsageException($"unknown option '{name}'.");
            }
        }
        if (options.InstallDirectory.Length == 0 || options.PayloadDirectory.Length == 0)
        {
            throw new UsageException("--install-dir and --payload are required.");
        }
        return options;
    }
}

internal sealed class BootstrapOptions
{
    public string InstallDirectory = "";
    public string PayloadDirectory = "";
    public int? WaitPid;
    public string? AppPath;
    public string? BackupDirectory;
    public string? LogFile;
    public bool KeepPayload;
    public int WaitTimeoutSeconds = 120;
}

internal sealed class UsageException(string message) : Exception(message);
internal sealed class WaitTimeoutException : Exception;
