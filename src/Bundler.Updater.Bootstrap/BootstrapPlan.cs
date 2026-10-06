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
        var markerPath = installDir.TrimEnd('/', '\\') + ".bundler-swap";
        // 重启目标与日志路径都按本进程 cwd 绝对化——相对路径会在换包后的临时工作目录里静默错位。
        if (options.AppPath is { Length: > 0 })
        {
            options.AppPath = Path.GetFullPath(options.AppPath);
        }
        if (options.LogFile is { Length: > 0 })
        {
            options.LogFile = Path.GetFullPath(options.LogFile);
        }

        if (!options.Rollback && payloadDir is null)
        {
            throw new UsageException("--payload is required unless --rollback.");
        }

        if (options.WaitPid is { } pid)
        {
            WaitForExit(pid, options.WaitTimeoutSeconds, log);
        }

        // 崩溃恢复先于存在性检查：上轮死在备份与换包之间时安装目标可能缺失/半成品，
        // 先按 marker 还原再谈 install 在不在。
        var recovered = false;
        if (File.Exists(markerPath))
        {
            log("bundler-updater: interrupted swap detected, restoring backup first");
            if (File.Exists(backupDir))
            {
                if (File.Exists(installDir))
                {
                    File.Delete(installDir);
                }
                File.Move(backupDir, installDir);
            }
            else if (Directory.Exists(backupDir))
            {
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, recursive: true);
                }
                MoveTree(backupDir, installDir, log);
            }
            else
            {
                throw new UpdateRejectedException(
                    $"swap marker '{markerPath}' exists but backup '{backupDir}' is missing — cannot recover safely.");
            }
            File.Delete(markerPath);
            recovered = true;
        }

        // 文件级语义：安装目标是单文件（AppImage 单件）或回滚备份是文件——
        // 同协议、粒度换成文件：marker/备份为 <file>.bundler-{swap,backup}。
        if (File.Exists(installDir) || (options.Rollback && File.Exists(backupDir)))
        {
            return ApplyFile(options, log, installDir, payloadDir, backupDir, markerPath, recovered);
        }

        if (!Directory.Exists(installDir))
        {
            throw new UsageException($"install directory '{installDir}' does not exist.");
        }
        if (!options.Rollback && !Directory.Exists(payloadDir!))
        {
            throw new UsageException($"payload directory '{options.PayloadDirectory}' does not exist.");
        }
        // install↔payload 同址/互嵌同样是抹数据的形状：备份移走后 swap 会以空载荷覆盖再删源，
        // 等值或载荷为安装祖先时连备份一并清掉——必须在动备份前拒绝。
        if (payloadDir is not null &&
            (SameOrInside(backupDir, installDir) || SameOrInside(backupDir, payloadDir) ||
             SameOrInside(installDir, backupDir) || SameOrInside(payloadDir, backupDir) ||
             SameOrInside(installDir, payloadDir) || SameOrInside(payloadDir, installDir)))
        {
            throw new UsageException("backup/install/payload directories must not nest inside each other.");
        }
        if (options.RetainBackupDirectory is { Length: > 0 } retainPath)
        {
            var retain = Path.GetFullPath(retainPath);
            if (SameOrInside(retain, installDir) || (payloadDir is not null && SameOrInside(retain, payloadDir)) ||
                SameOrInside(installDir, retain) || SameOrInside(backupDir, retain) || SameOrInside(retain, backupDir))
            {
                throw new UsageException("retained-backup directory must not nest inside install/payload/backup directories.");
            }
        }
        // 回滚模式不嵌套校验 payload——它本就不存在。
        if (options.Rollback && (SameOrInside(backupDir, installDir) || SameOrInside(installDir, backupDir)))
        {
            throw new UsageException("backup and install directories must not nest inside each other.");
        }

        // macOS .app 三项门禁（签名完好/身份连续/剥 quarantine）：动备份前拒绝，零变更安全。
        // 回滚不验——备份目录是上次换包前的自家产物，非外来载荷。
        if (!options.Rollback && OperatingSystem.IsMacOS() &&
            payloadDir is not null && MacAppGate.LooksLikeAppBundle(payloadDir))
        {
            MacAppGate.CheckAndStrip(installDir, payloadDir, log);
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

        TryRetainOrRemoveBackup(options, log, backupDir, retainedName: null);

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

    // 备份的最终去向：--retain-backup-to 给了目录就迁过去当回滚点，不给就删——
    // 默认不保留回滚点；换包期备份无论如何都建（崩溃恢复与原子性的载体）。
    private static void RetainOrRemoveBackup(
        BootstrapOptions options, Action<string> log, string backupPath, string? retainedName)
    {
        if (options.RetainBackupDirectory is { Length: > 0 } retain)
        {
            var target = retainedName is null
                ? Path.GetFullPath(retain)
                : Path.Combine(Path.GetFullPath(retain), retainedName);
            var parent = Path.GetDirectoryName(target);
            if (parent is { Length: > 0 })
            {
                Directory.CreateDirectory(parent);
            }
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
            log($"bundler-updater: retain backup '{backupPath}' → '{target}'");
            if (Directory.Exists(backupPath))
            {
                MoveTree(backupPath, target, log);
            }
            else
            {
                File.Move(backupPath, target);
            }
            return;
        }
        if (Directory.Exists(backupPath))
        {
            Directory.Delete(backupPath, recursive: true);
        }
        else if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
    }

    // 文件级换包：AppImage 等单文件安装单元——安装目标与载荷都是文件，
    // 目录级语义会把宿主目录里无关文件一起清掉，故必须单件替换。
    private static int ApplyFile(BootstrapOptions options, Action<string> log,
        string installPath, string? payloadPath, string backupPath, string markerPath,
        bool recovered)
    {
        if (options.Rollback)
        {
            if (recovered)
            {
                log("bundler-updater: crash recovery already restored the backup");
                return 0;
            }
            if (!File.Exists(backupPath))
            {
                throw new UpdateRejectedException($"no rollback backup at '{backupPath}'.");
            }
            log($"bundler-updater: rollback '{backupPath}' → '{installPath}'");
            File.Copy(backupPath, installPath, overwrite: true);
            if (options.AppPath is { Length: > 0 } rollbackApp)
            {
                Restart(rollbackApp, Path.GetDirectoryName(installPath) ?? ".", log);
            }
            log("bundler-updater: done");
            return 0;
        }

        if (payloadPath is null || !File.Exists(payloadPath))
        {
            throw new UsageException("file-swap payload must be a file.");
        }
        if (SameOrInside(payloadPath, installPath))
        {
            throw new UsageException("install and payload must not be the same file.");
        }
        log($"bundler-updater: backup '{installPath}' → '{backupPath}'");
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
        File.WriteAllText(markerPath, "swap in progress");
        File.Move(installPath, backupPath);

        try
        {
            if (options.KeepPayload)
            {
                log($"bundler-updater: copy in '{payloadPath}' → '{installPath}'");
                File.Copy(payloadPath, installPath, overwrite: true);
            }
            else
            {
                log($"bundler-updater: swap in '{payloadPath}' → '{installPath}'");
                File.Move(payloadPath, installPath);
            }
        }
        catch
        {
            log("bundler-updater: swap failed, restoring backup");
            if (File.Exists(installPath))
            {
                File.Delete(installPath);
            }
            File.Move(backupPath, installPath);
            File.Delete(markerPath);
            throw;
        }
        File.Delete(markerPath);

        // 文件级备份保留时按安装文件真名落在保留目录里。
        TryRetainOrRemoveBackup(options, log, backupPath, Path.GetFileName(installPath));

        var workingDirectory = Path.GetDirectoryName(installPath) ?? ".";
        if (options.AppPath is { Length: > 0 } app)
        {
            Restart(app, workingDirectory, log);
        }
        if (!options.KeepPayload && File.Exists(payloadPath))
        {
            File.Delete(payloadPath);
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
            // 直启不经 shell：appPath 只作 argv 传递，路径里的 shell 替换元字符不构成注入面。
            startInfo = new ProcessStartInfo(appPath)
            { UseShellExecute = false, WorkingDirectory = workingDirectory };
        }
        // 重启失败不致命：换包/回滚已完成，重启只是便利步骤——与 POSIX `|| true` 对齐为 WARN。
        try
        {
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            log($"bundler-updater: WARN restart failed ({exception.Message})");
        }
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

    // 严格子路径判不出等值路径——备份/保留目录与安装目录同址同样是抹数据的形状。
    private static bool SameOrInside(string candidate, string parent) =>
        IsSubpathOf(candidate, parent) ||
        string.Equals(
            candidate.TrimEnd(Path.DirectorySeparatorChar),
            parent.TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // 保留迁移失败不该让换包白做：瞬备留在原处仍能回滚（Rollback 定位兄弟位优先），降级不阻断。
    private static void TryRetainOrRemoveBackup(
        BootstrapOptions options, Action<string> log, string backupPath, string? retainedName)
    {
        try
        {
            RetainOrRemoveBackup(options, log, backupPath, retainedName);
        }
        catch (Exception exception)
        {
            log($"bundler-updater: WARN retain/remove backup failed ({exception.Message}); transient backup left at '{backupPath}'.");
        }
    }

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
                case "--retain-backup-to": options.RetainBackupDirectory = value; break;
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
    public string? RetainBackupDirectory;
    public string? LogFile;
    public bool KeepPayload;
    public bool Rollback;
    public int WaitTimeoutSeconds = 120;
}

internal sealed class UsageException(string message) : Exception(message);
internal sealed class WaitTimeoutException : Exception;
