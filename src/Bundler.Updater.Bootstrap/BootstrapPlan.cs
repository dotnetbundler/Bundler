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
        var payloadDir = options.PayloadDirectory.Length > 0
            ? Path.GetFullPath(options.PayloadDirectory)
            : null;
        var backupDir = Path.GetFullPath(
            options.BackupDirectory ?? installDir.TrimEnd('/', '\\') + ".bundler-backup");

        if (!Directory.Exists(installDir))
        {
            throw new UsageException($"install directory '{installDir}' does not exist.");
        }
        if (!options.Rollback && (payloadDir is null || !Directory.Exists(payloadDir)))
        {
            throw new UsageException($"payload directory '{options.PayloadDirectory}' does not exist.");
        }
        if (payloadDir is not null &&
            (IsSubpathOf(backupDir, installDir) || IsSubpathOf(backupDir, payloadDir) ||
             IsSubpathOf(installDir, backupDir) || IsSubpathOf(payloadDir, backupDir)))
        {
            throw new UsageException("backup/install/payload directories must not nest inside each other.");
        }
        // 回滚模式不嵌套校验 payload——它本就不存在。
        if (options.Rollback && (IsSubpathOf(backupDir, installDir) || IsSubpathOf(installDir, backupDir)))
        {
            throw new UsageException("backup and install directories must not nest inside each other.");
        }

        if (options.WaitPid is { } pid)
        {
            WaitForExit(pid, options.WaitTimeoutSeconds, log);
        }

        // macOS .app 三项门禁（签名完好/身份连续/剥 quarantine）：动备份前拒绝，零变更安全。
        // 回滚不验——备份目录是上次换包前的自家产物，非外来载荷。
        if (!options.Rollback && OperatingSystem.IsMacOS() &&
            payloadDir is not null && MacAppGate.LooksLikeAppBundle(payloadDir))
        {
            MacAppGate.CheckAndStrip(installDir, payloadDir, log);
        }

        // 崩溃恢复：marker 存在即上一轮死在备份与换包之间——安装目录可能是半成品，先从备份还原。
        var markerPath = installDir.TrimEnd('/', '\\') + ".bundler-swap";
        var recovered = false;
        if (File.Exists(markerPath))
        {
            log("bundler-updater: interrupted swap detected, restoring backup first");
            if (!Directory.Exists(backupDir))
            {
                throw new UpdateRejectedException(
                    $"swap marker '{markerPath}' exists but backup '{backupDir}' is missing — cannot recover safely.");
            }
            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }
            MoveTree(backupDir, installDir, log);
            File.Delete(markerPath);
            recovered = true;
        }

        // 回滚：备份复制回安装目录（备份保留可重试），不再二次备份——免得用待回滚的版本覆盖备份。
        if (options.Rollback)
        {
            if (!Directory.Exists(backupDir))
            {
                // 崩线恢复刚把备份还原回安装目录——备份移入即耗尽，此时安装目录已是目标态。
                if (recovered)
                {
                    log("bundler-updater: crash recovery already restored the backup");
                    return 0;
                }
                throw new UpdateRejectedException($"no rollback backup at '{backupDir}'.");
            }
            log($"bundler-updater: rollback '{backupDir}' → '{installDir}'");
            Directory.Delete(installDir, recursive: true);
            CopyTree(backupDir, installDir);
            if (options.AppPath is { Length: > 0 } rollbackApp)
            {
                Restart(rollbackApp, installDir, log);
            }
            log("bundler-updater: done");
            return 0;
        }

        log($"bundler-updater: backup '{installDir}' → '{backupDir}'");
        if (Directory.Exists(backupDir))
        {
            Directory.Delete(backupDir, recursive: true);
        }
        File.WriteAllText(markerPath, "swap in progress");
        MoveTree(installDir, backupDir, log);

        try
        {
            if (options.KeepPayload)
            {
                // 保留载荷用于调试与组合场景：复制换入而非移动。
                log($"bundler-updater: copy in '{payloadDir}' → '{installDir}'");
                CopyTree(payloadDir!, installDir);
            }
            else
            {
                log($"bundler-updater: swap in '{payloadDir}' → '{installDir}'");
                MoveTree(payloadDir!, installDir, log);
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
            File.Delete(markerPath);
            throw;
        }
        File.Delete(markerPath);

        if (options.AppPath is { Length: > 0 } app)
        {
            Restart(app, installDir, log);
        }
        if (!options.KeepPayload && payloadDir is not null && Directory.Exists(payloadDir))
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
        // 分离子进程：引导程序退出后新进程继续存活；macOS .app 目录件交给 open 走 LaunchServices。
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(appPath)
            { UseShellExecute = true, WorkingDirectory = workingDirectory };
        }
        else if (OperatingSystem.IsMacOS() && MacAppGate.LooksLikeAppBundle(appPath))
        {
            startInfo = new ProcessStartInfo("/usr/bin/open", ["-n", appPath])
            { UseShellExecute = false, WorkingDirectory = workingDirectory };
        }
        else
        {
            startInfo = new ProcessStartInfo("/bin/sh", ["-c", $"nohup \"{appPath}\" >/dev/null 2>&1 &"])
            { UseShellExecute = false, WorkingDirectory = workingDirectory };
        }
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
            var target = file.Replace(source, destination);
            if (File.Exists(target))
            {
                File.Delete(target);
            }
            var info = new FileInfo(file);
            // 软链按链接重建而非解引用成普通文件（File.Copy 的默认行为会破坏 AppRun 类链接）。
            if (info.LinkTarget is { } linkTarget)
            {
                File.CreateSymbolicLink(target, linkTarget);
                continue;
            }
            File.Copy(file, target, overwrite: true);
            // unix 执行位随文件走——exec 载荷跨卷复制后仍可启动。
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
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
            if (name is "--rollback")
            {
                options.Rollback = true;
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
        if (options.InstallDirectory.Length == 0)
        {
            throw new UsageException("--install-dir is required.");
        }
        if (!options.Rollback && options.PayloadDirectory.Length == 0)
        {
            throw new UsageException("--payload is required unless --rollback.");
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
    public bool Rollback;
    public int WaitTimeoutSeconds = 120;
}

internal sealed class UsageException(string message) : Exception(message);
internal sealed class WaitTimeoutException : Exception;
