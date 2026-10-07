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
        var installDir = CanonicalPath(options.InstallDirectory)
            ?? throw new UsageException($"install path '{options.InstallDirectory}' resolves to a cyclic link.");
        var payloadDir = options.PayloadDirectory.Length > 0
            ? CanonicalPath(options.PayloadDirectory)
                ?? throw new UsageException($"payload path '{options.PayloadDirectory}' resolves to a cyclic link.")
            : null;
        // 备份/保留是输出路径：父链物理化保留中间链接语义，但叶段必须留字面——
        // 叶段若是符号链接，解析后 rm/Delete 会清掉链接目标（配置路径之外的真实目录），
        // 而按字面删除只移除链接本身。与 POSIX 侧 norm_parent 同义。
        var backupDir = options.BackupDirectory is { Length: > 0 }
            ? CanonicalParentPath(options.BackupDirectory)
                ?? throw new UsageException($"backup path '{options.BackupDirectory}' resolves to a cyclic link.")
            : installDir.TrimEnd('/', '\\') + ".bundler-backup";
        // 关系判一律用全物理名——默认备份位同样可能是预置叶链，
        // 字面拼写与 retainCmp 的物理名对不上号会同址逃逸（换包移链后保留操作删新备份）。
        // 文件操作仍走上面的叶字面拼写（叶链只被删链本身不触目标）。
        var backupDirCmp = CanonicalPath(backupDir)
            ?? throw new UsageException($"backup path '{backupDir}' resolves to a cyclic link.");
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

        // install↔payload↔backup 同址/互嵌是抹数据的形状：备份移走后 swap 以空载荷覆盖再删源，
        // 等值或载荷为安装祖先时连备份一并清掉。
        // 这组判断只做路径关系运算、不依赖文件系统，必须先于 marker 恢复与等待——
        // 恢复会删半成品的安装目录，载荷嵌在其中时会把本轮输入先抹掉再拒绝（为时已晚）。
        if (payloadDir is not null &&
            (SameOrInside(backupDirCmp, installDir) || SameOrInside(backupDirCmp, payloadDir) ||
             SameOrInside(installDir, backupDirCmp) || SameOrInside(payloadDir, backupDirCmp) ||
             SameOrInside(installDir, payloadDir) || SameOrInside(payloadDir, installDir)))
        {
            throw new UsageException("backup/install/payload directories must not nest inside each other.");
        }
        if (options.RetainBackupDirectory is { Length: > 0 } retainPath)
        {
            // 操作拼写（父物理化+叶字面）写回 options 供 RetainOrRemoveBackup 使用；
            // 关系判另取全物理名，叶链指向安装/载荷/备份叶都能命中。
            options.RetainBackupDirectory = CanonicalParentPath(retainPath)
                ?? throw new UsageException($"retained-backup path '{retainPath}' resolves to a cyclic link.");
            var retainCmp = CanonicalPath(retainPath)
                ?? throw new UsageException($"retained-backup path '{retainPath}' resolves to a cyclic link.");
            if (SameOrInside(retainCmp, installDir) || (payloadDir is not null && SameOrInside(retainCmp, payloadDir)) ||
                SameOrInside(installDir, retainCmp) || (payloadDir is not null && SameOrInside(payloadDir, retainCmp)) ||
                SameOrInside(backupDirCmp, retainCmp) || SameOrInside(retainCmp, backupDirCmp))
            {
                throw new UsageException("retained-backup directory must not nest inside install/payload/backup directories.");
            }
        }
        // 回滚模式不嵌套校验 payload——它本就不存在。
        if (options.Rollback && (SameOrInside(backupDirCmp, installDir) || SameOrInside(installDir, backupDirCmp)))
        {
            throw new UsageException("backup and install directories must not nest inside each other.");
        }

        if (options.WaitPid is { } pid)
        {
            WaitForExit(pid, options.WaitTimeoutSeconds, log);
        }

        // marker 槽的叶链一律删除：真 marker 只会是本进程写的普通文件。
        // 预置链会让 Exists 顺链触发假崩溃恢复、写 marker 时顺链写穿污染目标。
        DeleteLinkNodeIfPresent(markerPath);
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
                MoveFile(backupDir, installDir);
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
            CopyTree(backupDir, installDir, log);
            if (options.AppPath is { Length: > 0 } rollbackApp)
            {
                Restart(rollbackApp, installDir, log);
            }
            log("bundler-updater: done");
            return 0;
        }

        log($"bundler-updater: backup '{installDir}' → '{backupDir}'");
        // 备份槽既有节点按 lstat 语义清掉：叶链节点删链本身不触目标（POSIX rm 同义）——
        // 指向保护区外的良性叶链不该让 MoveTree 撞 "already exists" 楔形。
        DeleteNodeIfPresent(backupDir);
        File.WriteAllText(markerPath, "swap in progress");
        // 楔形防线：marker 写在备份移位之前，备份未成立时异常不可留 marker——残留会让
        // 后续每次 apply 误判崩溃恢复而楔形（POSIX `mv || { rm -f MARKER; exit 4; }` 同义）。
        // 两段式 MoveTree 保证备份槽只呈现"未开始"或"全本"两态——存在即可安全恢复。
        try
        {
            MoveTree(installDir, backupDir, log);
            if (options.KeepPayload)
            {
                // 保留载荷用于调试与组合场景：复制换入而非移动。
                log($"bundler-updater: copy in '{payloadDir}' → '{installDir}'");
                CopyTree(payloadDir!, installDir, log);
            }
            else
            {
                log($"bundler-updater: swap in '{payloadDir}' → '{installDir}'");
                MoveTree(payloadDir!, installDir, log);
            }
        }
        catch
        {
            if (Directory.Exists(backupDir))
            {
                log("bundler-updater: swap failed, restoring backup");
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, recursive: true);
                }
                MoveTree(backupDir, installDir, log);
            }
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
            DeleteNodeIfPresent(target);
            log($"bundler-updater: retain backup '{backupPath}' → '{target}'");
            if (Directory.Exists(backupPath))
            {
                MoveTree(backupPath, target, log);
            }
            else
            {
                MoveFile(backupPath, target);
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
        // 同目录级：备份槽叶链节点删链本身（File.Exists 顺链探测会漏挂链）。
        DeleteNodeIfPresent(backupPath);
        File.WriteAllText(markerPath, "swap in progress");
        var backupTaken = false;
        try
        {
            MoveFile(installPath, backupPath);
            backupTaken = true;
            if (options.KeepPayload)
            {
                log($"bundler-updater: copy in '{payloadPath}' → '{installPath}'");
                File.Copy(payloadPath, installPath, overwrite: true);
            }
            else
            {
                log($"bundler-updater: swap in '{payloadPath}' → '{installPath}'");
                MoveFile(payloadPath, installPath);
            }
        }
        catch
        {
            if (backupTaken)
            {
                log("bundler-updater: swap failed, restoring backup");
                if (File.Exists(installPath))
                {
                    File.Delete(installPath);
                }
                MoveFile(backupPath, installPath);
            }
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

    // 槽位既有节点按 lstat 语义清掉：叶链/联接只删节点本身不触目标（POSIX rm 同义）；
    // 普通目录递归清、普通文件直删。Exists 系探测顺链解引用，挂链会漏判为缺席——
    // 那正是"already exists"楔形的成因。
    private static void DeleteNodeIfPresent(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }
        }
        else if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void DeleteLinkNodeIfPresent(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteNodeIfPresent(path);
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
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

    // 文件级同名语义：File.Move 跨卷退化为 copy+unlink，半途失败会在目标名上留下
    // 截断半成品（Exists 恢复判据会把它当全本还原）——与 MoveTree 两段式同义。
    private static void MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
            return;
        }
        catch (IOException)
        {
        }
        DeleteNodeIfPresent(destination);
        var staged = destination + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Copy(source, staged);
            File.Move(staged, destination);
        }
        catch
        {
            DeleteNodeIfPresent(staged);
            throw;
        }
        File.Delete(source);
    }

    // 同卷 rename(2)/MoveFile 原子就位；跨卷两段式：先 CopyTree 到同级临时名再原子
    // rename 就位——destination 只呈现"未开始"或"全本"两态，调用方靠 Exists 即可判
    // 备份是否成立（半成品永远在临时名下清走，不会污染目标槽）。
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
        var staged = destination + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            CopyTree(source, staged, log);
            Directory.Move(staged, destination);
        }
        catch
        {
            try
            {
                Directory.Delete(staged, recursive: true);
            }
            catch (Exception)
            {
            }
            throw;
        }
        Directory.Delete(source, recursive: true);
    }

    // `cp -a` 语义（与 POSIX bundler-updater.sh 平价）：链接按链接重建不解引用实体化、
    // 权限位/xattr 随文件走。POSIX 一律走宿主工具：macOS `ditto`（xattr/ACL/链接全保真——
    // managed 复制丢目录级 xattr，签名 .app 经保留/跨卷/回滚路会被洗白；与提取侧
    // PR #34 同策略用 Apple 系统工具），其余 POSIX `cp -a`（coreutils/busybox 均有）。
    // Windows 无 xattr 语义，走 managed 复制。
    private static void CopyTree(string source, string destination, Action<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateDirectory(destination);
                ProcessStartInfo startInfo = OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("/usr/bin/ditto", [source, destination])
                    : new ProcessStartInfo("cp", ["-a", source + "/.", destination + "/"]);
                startInfo.RedirectStandardError = true;
                using var process = Process.Start(startInfo);
                process!.WaitForExit();
                if (process.ExitCode == 0)
                {
                    return;
                }
                DeleteNodeIfPresent(destination);
                log($"bundler-updater: WARN host copy failed (exit {process.ExitCode}) — falling back to managed copy");
            }
            catch (Exception exception)
            {
                log($"bundler-updater: WARN host copy unavailable ({exception.Message}) — falling back to managed copy");
            }
        }
        CopyTreeManaged(source, destination);
    }

    private static void CopyTreeManaged(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            var attributes = File.GetAttributes(entry);
            // 链接（含目录软链）按链接重建——EnumerateDirectories 会把目录软链遍历成
            // 实体目录（.app 内 Framework/版本链被洗白、环链死循环），必须 lstat 判链。
            var linkTarget = new DirectoryInfo(entry).LinkTarget ?? new FileInfo(entry).LinkTarget;
            if (linkTarget is not null)
            {
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.CreateSymbolicLink(target, linkTarget);
                }
                else
                {
                    File.CreateSymbolicLink(target, linkTarget);
                }
                continue;
            }
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                CopyTreeManaged(entry, target);
                continue;
            }
            File.Copy(entry, target);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(entry));
            }
        }
    }

    private static bool IsSubpathOf(string candidate, string parent) =>
        candidate.StartsWith(
            parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // 物理规范化：逐级解析已存在的符号链接段——路径关系判断必须比对物理位置而非拼写
    //（POSIX 侧 norm_path 的 cd -P 语义；`Path.GetFullPath` 只归一拼写不解链接）。
    // 不存在的段保持字面，链接解析失败退化为当前拼写。
    private static string? CanonicalPath(string path) =>
        CanonicalPath(path, new HashSet<string>(PathStringComparer));

    private static string? CanonicalPath(string path, HashSet<string> resolving)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var canonical = root;
        foreach (var segment in full[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            canonical = Path.Combine(canonical, segment);
            var resolved = ResolveLinkChain(canonical, resolving);
            if (resolved is null)
            {
                return null;
            }
            canonical = resolved;
        }
        return canonical;
    }

    // 输出路径（backup/retain）只物理化父目录、叶段留拼写：叶段为符号链接时
    // 解析后递归删除会清掉链接目标——配置路径之外的真实目录。
    // 父级不可解（环链）时返回 null 让调用方拒绝——字面回退会把 a→a/child 这类
    // 环链展开成永不存在的假字面链，绕过拒绝拖到写 marker 后才失败。
    private static string? CanonicalParentPath(string path) =>
        CanonicalParentPath(path, new HashSet<string>(PathStringComparer));

    private static string? CanonicalParentPath(string path, HashSet<string> resolving)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        var canonicalParent = parent is null ? null : CanonicalPath(parent, resolving);
        return parent is null ? full
            : canonicalParent is null ? null
            : Path.Combine(canonicalParent, Path.GetFileName(full));
    }

    // 逐级解符号链接：纯 LinkTarget 跳走（不依赖 Exists 语义——lstat 口径下环链
    // 也报存在），悬挂链接（目标已搬走进备份）照样解——崩溃恢复靠它找回 marker/备份。
    // 每跳目标按父物理化再接回叶名：/var 这类中间段自身是链接时，字面拼写会与
    // 比较对象的物理名错位逃逸互嵌/等值判。
    // resolving 是整条规范化调用栈共享的解算守卫：a→a/child 这类后代自指会让
    // CanonicalParentPath 重入同一拼写，visited（链内去环）管不到跨层重入。
    // 环链（自指/互指/解算链逾 40 跳=SYMLOOP_MAX）返回 null——调用方按无效输入拒绝。
    private static string? ResolveLinkChain(string path, HashSet<string> resolving)
    {
        if (!resolving.Add(path))
        {
            return null;
        }
        var current = path;
        var visited = new HashSet<string>(PathStringComparer) { current };
        var hops = 0;
        try
        {
            for (; hops < 40; hops++)
            {
                string? target;
                try
                {
                    target = new FileInfo(current).LinkTarget ?? new DirectoryInfo(current).LinkTarget;
                }
                catch (IOException)
                {
                    break;
                }
                if (target is null)
                {
                    break;
                }
                var next = Path.GetFullPath(
                    Path.IsPathRooted(target) ? target
                        : Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, target));
                var canonical = CanonicalParentPath(next, resolving);
                if (canonical is null || !visited.Add(canonical))
                {
                    return null;
                }
                current = canonical;
            }
            // 跳数耗尽后仍停在链接上才算超限——恰 40 跳收敛的合法链放行。
            if (hops >= 40 && IsLink(current))
            {
                return null;
            }
            return current;
        }
        finally
        {
            resolving.Remove(path);
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget) is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
