using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Updater.Bootstrap;

/// <summary>
/// Sparkle 三段式换包的宿主执行面：等宿主退出 → 旧目录改名备份 → 新载荷就位 → 重启。
/// 任意一步失败都尽力把备份还原——绝不留下半个安装目录。
/// </summary>
internal static class BootstrapPlan
{
    // 测试缝：每个文件复制/移动操作点前回调（源，目标）——测试内写截断字节再抛异常
    // 即可等价模拟半途 IO 失败（ENOSPC），验证两段式清场契约；生产为 null 零开销。
    internal static Action<string, string>? IoFaultProbe;

    // 测试缝：强制 managed 复制——宿主工具（cp -a/ditto）是子进程注不进缝，
    // 置 true 让 POSIX 也走可注入的 CopyTreeManaged。
    internal static bool ForceManagedCopy;

    // 测试缝：覆盖空间预检的卷剩余探测——返回小值即断言拒换包，抛异常即断言
    // WARN 放行；生产为 null 走真 DriveInfo。
    internal static Func<string, long?>? FreeSpaceProbe;

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
        // Windows 比较面再加 NT 物理化：卷挂载点别名（C:\mnt 挂载 D:\）在
        // CanonicalPath 下仍是两个拼写，同址互嵌会漏判——\Device\… 名在对象层归一。
        // POSIX 侧由文件系统语义天然归一，恒等透传。NT 化按"对"降级：
        // 一侧 NT 一侧字面的混合命名空间必然漏判，任一侧取不到即退回该对的规范字面判。
        var installCmp = NtCmpPath(installDir);
        var payloadCmp = payloadDir is null ? null : NtCmpPath(payloadDir);
        var backupCmp = NtCmpPath(backupDirCmp);
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
        // 门禁挂在字面名上：payloadCmp（NT 名）可空不跳过整组判——
        // payload 一侧 NT 化失败时 payload 相关对退回字面判，其余对不受影响。
        if (payloadDir is not null &&
            (SameOrInsideCmp(backupCmp, backupDirCmp, installCmp, installDir) ||
             SameOrInsideCmp(backupCmp, backupDirCmp, payloadCmp, payloadDir) ||
             SameOrInsideCmp(installCmp, installDir, backupCmp, backupDirCmp) ||
             SameOrInsideCmp(payloadCmp, payloadDir, backupCmp, backupDirCmp) ||
             SameOrInsideCmp(installCmp, installDir, payloadCmp, payloadDir) ||
             SameOrInsideCmp(payloadCmp, payloadDir, installCmp, installDir)))
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
            var retainNt = NtCmpPath(retainCmp);
            if (SameOrInsideCmp(retainNt, retainCmp, installCmp, installDir) ||
                (payloadDir is not null && SameOrInsideCmp(retainNt, retainCmp, payloadCmp, payloadDir)) ||
                SameOrInsideCmp(installCmp, installDir, retainNt, retainCmp) ||
                (payloadDir is not null && SameOrInsideCmp(payloadCmp, payloadDir, retainNt, retainCmp)) ||
                SameOrInsideCmp(backupCmp, backupDirCmp, retainNt, retainCmp) ||
                SameOrInsideCmp(retainNt, retainCmp, backupCmp, backupDirCmp))
            {
                throw new UsageException("retained-backup directory must not nest inside install/payload/backup directories.");
            }
        }
        // 回滚模式不嵌套校验 payload——它本就不存在。
        if (options.Rollback && (SameOrInsideCmp(backupCmp, backupDirCmp, installCmp, installDir) ||
                               SameOrInsideCmp(installCmp, installDir, backupCmp, backupDirCmp)))
        {
            throw new UsageException("backup and install directories must not nest inside each other.");
        }

        // 并发互斥：同一 install 的换包/回滚全程串行——Squirrel 式等锁语义，
        // 第二实例等到 --lock-timeout 超时即拒（rc=3），绝不双换包（在飞 marker
        // 被当崩溃标记“恢复”会把别人换一半的包拆掉）。崩溃残留锁按 owner pid 存活夺锁。
        var lockPath = AcquireLock(installDir, options, log);
        try
        {
            return ApplyLocked(options, log, installDir, payloadDir, backupDir, markerPath);
        }
        finally
        {
            ReleaseLock(lockPath, log);
        }
    }

    // 锁内主体：等宿主退出 → 崩溃恢复 → 空间预检 → 换包/回滚。
    private static int ApplyLocked(BootstrapOptions options, Action<string> log,
        string installDir, string? payloadDir, string backupDir, string markerPath)
    {
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

        // 空间预检：只挡会真写盘的量——同卷两段式/回滚复制按树体积估，同卷原子
        // 改名不计（rename 不占额外空间）。估不出卷/大小只 WARN 不拒。
        CheckFreeSpace(options, log, installDir, payloadDir, backupDir);

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

    // 并发锁：同 install 的换包/回滚全程串行。锁件是 <install>.bundler-lock
    // 普通文件（CreateNew 原子位）内含持锁者 pid——与 bundler-updater.sh 的
    // noclobber `>` + `kill -0` 锁同形同协议（跨实现互斥：AOT 与 sh 实例互斥）。
    // 撞锁按 owner 存活轮询：活则等到 --lock-timeout 超时拒（rc=3），死则夺锁。
    private static string AcquireLock(string installDir, BootstrapOptions options, Action<string> log)
    {
        var lockPath = installDir.TrimEnd('/', '\\') + ".bundler-lock";
        DeleteLinkNodeIfPresent(lockPath);
        var deadline = DateTime.UtcNow.AddSeconds(options.LockTimeoutSeconds);
        var announced = false;
        while (true)
        {
            try
            {
                using (var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write))
                {
                    var owner = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString());
                    stream.Write(owner, 0, owner.Length);
                }
                // 写入后回验 owner——锁文件被并发夺锁者删掉重写时它已非本进程件，
                // 不验就会与持锁者同时进 swap；回验失败跌入下方等锁路径。
                if (TryReadLockOwner(lockPath) == Environment.ProcessId)
                {
                    return lockPath;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (!announced)
            {
                log($"bundler-updater: another update holds '{lockPath}' — waiting up to {options.LockTimeoutSeconds}s");
                announced = true;
            }
            if (TryReadLockOwner(lockPath) is { } ownerPid && !ProcessAlive(ownerPid))
            {
                // 持锁进程已死——崩溃残留夺锁（与 sh `kill -0` 判死同义）。删前复读
                // owner 未变且仍死才删：双实例同时撞上死锁时，防一方删掉另一方刚写的
                // 新锁件致双实例同入 swap。
                if (TryReadLockOwner(lockPath) == ownerPid && !ProcessAlive(ownerPid))
                {
                    DeleteNodeIfPresent(lockPath);
                }
                continue;
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw new WaitTimeoutException(
                    $"another updater holds the lock '{lockPath}' — timed out waiting.");
            }
            Thread.Sleep(50);
        }
    }

    private static void ReleaseLock(string lockPath, Action<string> log)
    {
        try
        {
            DeleteNodeIfPresent(lockPath);
        }
        catch (Exception exception)
        {
            // 放锁失败只留死锁风险不损数据——下次来锁按 owner 存活自然回收。
            log($"bundler-updater: WARN lock release failed ({exception.Message})");
        }
    }

    private static int? TryReadLockOwner(string lockPath)
    {
        try
        {
            var text = File.ReadAllText(lockPath).Trim();
            return int.TryParse(text, out var pid) ? pid : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 空间预检（Sparkle 式 fail-fast）：只挡会真写盘的量——同卷原子 rename 不占
    // 额外空间不计入；跨卷两段式暂存、KeepPayload 复制、回滚复制按树体积估到
    // 目标槽所在卷。探测失败只 WARN 放行（拒错不如不拒），需求明确不够才拒。
    private static void CheckFreeSpace(BootstrapOptions options, Action<string> log,
        string installDir, string? payloadDir, string backupDir)
    {
        try
        {
            var demands = new List<(long Bytes, string Anchor)>();
            if (options.Rollback)
            {
                // 回滚恒为备份→install 的全本复制。
                demands.Add((NodeBytes(backupDir), installDir));
            }
            else
            {
                if (!SameVolume(installDir, backupDir))
                {
                    demands.Add((NodeBytes(installDir), backupDir));
                }
                if (payloadDir is not null &&
                    (options.KeepPayload || !SameVolume(payloadDir, installDir)))
                {
                    demands.Add((NodeBytes(payloadDir), installDir));
                }
            }
            // 同卷多腿需求合并——备份槽与 install 槽可能同卷。
            var perVolume = new Dictionary<string, (long Required, string Anchor)>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var (bytes, anchor) in demands)
            {
                var volume = FindVolume(anchor);
                if (volume is null)
                {
                    continue;
                }
                var key = volume.RootDirectory.FullName;
                perVolume.TryGetValue(key, out var existing);
                perVolume[key] = (existing.Required + bytes, anchor);
            }
            foreach (var (root, (required, anchor)) in perVolume)
            {
                var free = FreeSpaceProbe?.Invoke(anchor) ?? FindVolume(anchor)!.AvailableFreeSpace;
                log($"bundler-updater: free-space check on '{root}' — need ~{required} bytes, {free} available");
                if (required > free)
                {
                    throw new UpdateRejectedException(
                        $"insufficient free space on volume '{root}': swap requires ~{required} bytes, only {free} available.");
                }
            }
        }
        catch (UpdateRejectedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            log($"bundler-updater: WARN free-space check skipped ({exception.Message})");
        }
    }

    private static long NodeBytes(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }
        if (!Directory.Exists(path))
        {
            return 0;
        }
        long total = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var linkTarget = new DirectoryInfo(entry).LinkTarget ?? new FileInfo(entry).LinkTarget;
            if (linkTarget is not null)
            {
                continue; // 链接重建不占目标体积
            }
            if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
            {
                total += NodeBytes(entry);
            }
            else
            {
                total += new FileInfo(entry).Length;
            }
        }
        return total;
    }

    // 目标锚点所在的卷：最长前缀匹配的挂载点；取不到返回 null 由调用方跳过。
    private static DriveInfo? FindVolume(string anchor)
    {
        var full = Path.GetFullPath(anchor);
        DriveInfo? best = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.RootDirectory.FullName;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (full.StartsWith(root, comparison) &&
                // 前缀命中还要过路径段边界：/a/bb 不能算挂到 /a/b。
                (full.Length == root.Length ||
                 full[root.Length] is '/' or '\\' ||
                 root.EndsWith("/") || root.EndsWith("\\")) &&
                (best is null || root.Length > best.RootDirectory.FullName.Length))
            {
                best = drive;
            }
        }
        return best;
    }

    // 同卷判定：Windows 走 GetVolumePathName（覆盖目录挂载卷）；POSIX 的
    // Path.GetPathRoot 恒 "/" 无法判——两端已存在祖先间做 rename 探针，EXDEV 即跨卷。
    // 探测失败抛出让外层 WARN 跳过（宁肯不检也不错拒）。
    private static bool SameVolume(string a, string b)
    {
        if (OperatingSystem.IsWindows())
        {
            var volumeA = WindowsVolumeRoot(a);
            if (volumeA is null)
            {
                throw new IOException($"cannot resolve volume root of '{a}'.");
            }
            return string.Equals(volumeA, WindowsVolumeRoot(b), StringComparison.OrdinalIgnoreCase);
        }
        var probeName = ".bundler-volprobe-" + Guid.NewGuid().ToString("N")[..8];
        var probeA = Path.Combine(ExistingAncestor(a), probeName);
        var probeB = Path.Combine(ExistingAncestor(b), probeName);
        try
        {
            File.WriteAllBytes(probeA, []);
            return PosixRename(probeA, probeB) == 0;
        }
        finally
        {
            DeleteNodeIfPresent(probeA);
            DeleteNodeIfPresent(probeB);
        }
    }

    private static string ExistingAncestor(string path)
    {
        var probe = Path.GetFullPath(path);
        while (!Directory.Exists(probe) && !File.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            probe = parent ?? throw new IOException($"no existing ancestor for '{path}'.");
        }
        return File.Exists(probe) ? Path.GetDirectoryName(probe)! : probe;
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
        throw new WaitTimeoutException(
            $"the target process (pid {pid}) did not exit within {timeoutSeconds}s.");
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

    // 文件级同名语义：File.Move 跨卷内部退化为 copy+delete——进程半途被杀会在
    // 目标名上留下截断半成品（Exists 恢复判据会把它当全本还原）——故只有确证
    // 同卷（原子 rename）才直移，其余一律两段式（与 MoveTree 同义）。
    private static void MoveFile(string source, string destination)
    {
        // 注入点抛 IOException 与原子移动失败同义——落两段式（MoveTree 同款约定）。
        var atomicAvailable = true;
        try
        {
            IoFaultProbe?.Invoke(source, destination);
        }
        catch (IOException)
        {
            atomicAvailable = false;
        }
        if (atomicAvailable && TryAtomicMove(source, destination))
        {
            return;
        }
        DeleteNodeIfPresent(destination);
        var staged = destination + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            IoFaultProbe?.Invoke(source, staged);
            File.Copy(source, staged);
            IoFaultProbe?.Invoke(staged, destination);
            File.Move(staged, destination);
        }
        catch
        {
            DeleteNodeIfPresent(staged);
            throw;
        }
        File.Delete(source);
    }

    // 原子移动探测：POSIX rename(2) 成功即原子就位（EXDEV 等失败一律走两段式，
    // File.Copy 会抛出真实错误）；Windows 需确证同卷 MoveFile 才是 rename 语义——
    // 跨卷（异盘符/异 UNC share/挂载在目录下的卷）MoveFile 内部同样 copy+delete
    // 半途留截断件。判卷用 GetVolumePathName：盘符比较会漏 NTFS 卷挂载点。
    private static bool TryAtomicMove(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            var sourceVolume = WindowsVolumeRoot(source);
            var destinationVolume = WindowsVolumeRoot(destination);
            // 判不出卷→保守两段式；卷根不同（含目录挂载卷）→跨卷两段式
            if (sourceVolume is null
                || !string.Equals(sourceVolume, destinationVolume, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            File.Move(source, destination);
            return true;
        }
        return PosixRename(source, destination) == 0;
    }

    private static string? WindowsVolumeRoot(string path)
    {
        var buffer = new StringBuilder(capacity: 260);
        return GetVolumePathName(Path.GetFullPath(path), buffer, buffer.Capacity)
            ? buffer.ToString()
            : null;
    }

    // 比较名成对判定：两侧都有 NT 物理名用对象层判（别名归一）；任一侧 NT 化
    // 失败退回该对的规范字面判——混合命名空间比较必然漏判（payload 超长在
    // NT 侧漏嵌判，恢复删活件即此事故形状）。POSIX 侧 nt 即规范名恒走前者。
    private static bool SameOrInsideCmp(string? ntCandidate, string candidate, string? ntParent, string parent) =>
        ntCandidate is not null && ntParent is not null
            ? SameOrInside(ntCandidate, ntParent)
            : SameOrInside(candidate, parent);

    // 比较用物理名：Windows 上 deepest-existing 前缀开句柄取 \Device\… NT 名、
    // 缺失叶段按字面接回——挂载点/junction/subst 别名在对象层归一，
    // install C:\mnt\app 与 backup D:\app（mnt 挂 D:）判同址拒绝。
    // 取不到（全程不存在/打不开/超 32K）返回 null 由成对判退回字面。
    private static string? NtCmpPath(string path) =>
        OperatingSystem.IsWindows() ? NtPhysicalPath(path) : path;

    private static string? NtPhysicalPath(string path)
    {
        var probe = Path.GetFullPath(path);
        var tail = new List<string>();
        while (!File.Exists(probe) && !Directory.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (parent is null || parent == probe)
            {
                return null;
            }
            tail.Insert(0, Path.GetFileName(probe));
            probe = parent;
        }
        // FILE_FLAG_BACKUP_SEMANTICS 才开得了目录句柄——.NET FileOptions 无此项，
        // 只能 CreateFile 直调；0 访问权限足以查询对象名。
        var handle = CreateFile(probe, 0, 0x7 /* READ|WRITE|DELETE share */, IntPtr.Zero,
            3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return null;
        }
        try
        {
            // 缓冲不足时返回需要的长度——按需扩容重试到内核路径上限 64K，
            // 超长路径不再静默退回字面（混合命名空间漏判的事故源）。
            var capacity = 512;
            string? nt = null;
            while (capacity <= 64 * 1024)
            {
                var buffer = new StringBuilder(capacity: capacity);
                var length = GetFinalPathNameByHandle(
                    handle, buffer, (uint)buffer.Capacity, 0x1 /* VOLUME_NAME_NT */);
                if (length == 0)
                {
                    return null;
                }
                if (length < (uint)buffer.Capacity)
                {
                    nt = buffer.ToString(0, (int)length).TrimEnd('\\');
                    break;
                }
                capacity = (int)Math.Min(length + 1, 64 * 1024 + 1);
            }
            if (nt is null)
            {
                return null;
            }
            foreach (var segment in tail)
            {
                nt += "\\" + segment;
            }
            return nt;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string lpszFileName, StringBuilder lpszVolumePathName, int nBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName, char[] lpszVolumePathNames, uint cchBufferLength, ref uint lpcchReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("libc", EntryPoint = "rename", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int PosixRename(string oldPath, string newPath);

    // 同卷 rename(2)/MoveFile 原子就位；跨卷两段式：先 CopyTree 到同级临时名再原子
    // rename 就位——destination 只呈现"未开始"或"全本"两态，调用方靠 Exists 即可判
    // 备份是否成立（半成品永远在临时名下清走，不会污染目标槽）。
    private static void MoveTree(string source, string destination, Action<string> log)
    {
        try
        {
            IoFaultProbe?.Invoke(source, destination);
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
            IoFaultProbe?.Invoke(staged, destination);
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
        if (!OperatingSystem.IsWindows() && !ForceManagedCopy)
        {
            try
            {
                Directory.CreateDirectory(destination);
                ProcessStartInfo startInfo = OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("/usr/bin/ditto", [source, destination])
                    : new ProcessStartInfo("cp", ["-a", source + "/.", destination + "/"]);
                startInfo.RedirectStandardError = true;
                using var process = Process.Start(startInfo);
                // 必须先排空 stderr——cp/ditto 大量报错写满管道会反压阻塞子进程，
                // WaitForExit 将永久卡死。ReadToEnd 排水直到子进程退出再取码。
                var stderr = process!.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    return;
                }
                DeleteNodeIfPresent(destination);
                var detail = stderr.Trim();
                if (detail.Length > 240) { detail = "…" + detail[^240..]; }
                log($"bundler-updater: WARN host copy failed (exit {process.ExitCode}){(detail.Length > 0 ? $" — {detail}" : "")} — falling back to managed copy");
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
                IoFaultProbe?.Invoke(entry, target);
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
            IoFaultProbe?.Invoke(entry, target);
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
    internal static string? CanonicalPath(string path) =>
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
                // Windows 卷挂载点（mountvol）：reparse target 是 `\??\Volume{GUID}`
                // 或 .NET 剥前缀后的裸名——NT 对象名而非文件系统路径，MOUNT_POINT
                // 等 tag 时按普通目录透传不跟跳（跟跳会把 `Volume{guid}` 当目录名
                // 拼进字面路径，静默写偏到别的卷）。tag 探不出（权限/竞争）不猜方向：
                // 透传会让 install 叶链按链节点被搬走（真应用孤儿化），解成路径又拼
                // 假路径逃逸守卫——返回 null 拒绝是最干净的落点。
                var next = target;
                if (OperatingSystem.IsWindows() && IsNtObjectTarget(target))
                {
                    var info = ReparseInfoOf(current);
                    if (info is null)
                    {
                        return null;
                    }
                    if (info.Value.Tag != IoReparseTagSymlink)
                    {
                        break;
                    }
                    // .NET LinkTarget 对 `\\?\`/` \??\` 前缀目标统一剥前缀返回裸名
                    //（win 腿探针实证）——`mklink /D link \\?\Volume{GUID}` 的实形是
                    // 裸名+绝对 reparse flag。裸名根段 Volume{GUID} 且 flag 绝对时按
                    // 卷真实挂载名解真；flag 相对则是用户真写的相对名，照旧拼父级。
                    if (!info.Value.Relative && HasVolumeGuidRoot(target))
                    {
                        var dos = VolumeDosPath(target[..44]);
                        var tail = target[44..].TrimStart('\\', '/');
                        if (dos is null)
                        {
                            return null;
                        }
                        next = Path.GetFullPath(dos.TrimEnd('\\') + @"\" + tail);
                    }
                }
                next = AbsoluteLinkTarget(current, next);
                if (next is null)
                {
                    return null;
                }
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

    // `\\?\`/`\??\` 扩展长度前缀的链接目标不能走 GetFullPath 规范化——.NET 对
    // 非盘符设备段（`Volume{GUID}`、`GLOBALROOT` 等）会剥前缀后按相对名拼回
    // 父级（win 腿实证：父级若恰好存在同名目录还会静默解到错目录）。手工规范
    // 三类合法形：`UNC\s\p`→`\\s\p`、`X:\…`→去前缀照常、`Volume{GUID}[\sub]`
    // →查卷真实挂载名替根段；不认识的设备形与无 DOS 名的卷返回 null 拒绝。
    // `\Device\…` NT 路径无托管拼写可拼，同样拒绝。
    private static string? AbsoluteLinkTarget(string current, string target)
    {
        if (OperatingSystem.IsWindows()
            && target.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!OperatingSystem.IsWindows()
            || (!target.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !target.StartsWith(@"\??\", StringComparison.Ordinal)))
        {
            return Path.GetFullPath(
                Path.IsPathRooted(target) ? target
                    : Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, target));
        }
        var body = target[4..];
        if (body.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(@"\\" + body[4..]);
        }
        if (body.Length >= 2 && char.IsLetter(body[0]) && body[1] == ':')
        {
            return Path.GetFullPath(body);
        }
        if (HasVolumeGuidRoot(body))
        {
            var dosName = VolumeDosPath(body[..44]);
            var tail = body[44..].TrimStart('\\', '/');
            return dosName is null ? null
                : Path.GetFullPath(
                    dosName.TrimEnd('\\') + (tail.Length == 0 ? @"\" : @"\" + tail));
        }
        return null;
    }

    // `Volume{GUID}` 是否占据路径根段（整名、尾分隔符或带子路径都算）。
    private static bool HasVolumeGuidRoot(string path) =>
        path.Length >= 44 && IsVolumeGuidName(path[..44])
        && (path.Length == 44 || path[44] == '\\' || path[44] == '/');

    // `Volume{GUID}` → 卷的 DOS 可见挂载名（`D:\`/`C:\mntv\`，多挂载取第一个）；
    // 卷只按 GUID 可达（无挂载名）返回 null——托管 IO 没有能拼它的拼写。
    private static string? VolumeDosPath(string volumeGuidName)
    {
        var volumeName = $@"\\?\{volumeGuidName}\";
        var required = 0u;
        GetVolumePathNamesForVolumeName(volumeName, Array.Empty<char>(), 0, ref required);
        if (required == 0)
        {
            return null;
        }
        var buffer = new char[required];
        if (!GetVolumePathNamesForVolumeName(volumeName, buffer, (uint)buffer.Length, ref required))
        {
            return null;
        }
        var names = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return names.Length == 0 ? null : names[0];
    }

    // NT 对象名的三种表面：`\\?\`/`\??\` 前缀、`\Device\…`、以及 .NET 剥前缀后
    // 露出的裸 `Volume{GUID}\`（卷挂载点 reparse target 在 LinkTarget 上的实形——
    // win 腿实证 .NET 去掉 `\??\` 后按裸名返回，跟跳会把它当目录名拼进字面路径）。
    // `\\?\C:\…`/`\\?\UNC\…` 这类扩展长度路径是合法拼写，照常解。
    private static bool IsNtObjectTarget(string target)
    {
        if (target.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (target.StartsWith(@"\\?\", StringComparison.Ordinal)
            || target.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            var body = target[4..];
            return HasVolumeGuidRoot(body)
                || (!(body.Length >= 2 && char.IsLetter(body[0]) && body[1] == ':')
                    && !body.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase));
        }
        return HasVolumeGuidRoot(target);
    }

    // `Volume{xxxxxxxx-xxxx-…}` 裸名——卷 GUID 路径的根段，
    // 允许尾巴一根目录分隔符，不接受它当普通目录名。
    private static bool IsVolumeGuidName(string name)
    {
        var trimmed = name.TrimEnd('\\');
        return trimmed.Length == 44
            && trimmed.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)
            && trimmed[43] == '}';
    }

    // reparse tag 三态：FSCTL_GET_REPARSE_POINT 读节点真实 tag——
    // SYMLINK 确证链接可解；MOUNT_POINT（junction/卷挂）的 NT 形目标
    // 按对象透传；探不出返回 null，调用方 fail-closed 同样透传。
    // OPEN_REPARSE_POINT 开链节点自身（不顺链），BACKUP_SEMANTICS 开目录。
    private const uint IoReparseTagSymlink = 0xA000000C;
    private const uint FsctlGetReparsePoint = 0x000900A8;

    private static ReparseInfo? ReparseInfoOf(string path)
    {
        var handle = CreateFile(path, 0, 0x7 /* READ|WRITE|DELETE share */, IntPtr.Zero,
            3 /* OPEN_EXISTING */, 0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return null;
        }
        try
        {
            var buffer = Marshal.AllocHGlobal(16384);
            try
            {
                if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0,
                        buffer, 16384, out _, IntPtr.Zero))
                {
                    return null;
                }
                var tag = (uint)Marshal.ReadInt32(buffer);
                // SYMLINK 的 Flags 在 reparse 头偏移 16：bit0=SYMLINK_FLAG_RELATIVE，
                // 区分 `mklink link Volume{GUID}`（相对拼写）与 `mklink link
                // \\?\Volume{GUID}`（NT 绝对对象）——LinkTarget 两者同显裸名。
                var relative = tag == IoReparseTagSymlink
                    && (Marshal.ReadInt32(buffer, 16) & 0x1) != 0;
                return new ReparseInfo(tag, relative);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private readonly struct ReparseInfo(uint tag, bool relative)
    {
        internal uint Tag { get; } = tag;
        internal bool Relative { get; } = relative;
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
                    // 非正 pid 同样拒：TryParse 放进来的 "-5"/"0" 会让
                    // GetProcessById 抛 ArgumentException 被判成"已退出"——
                    // 宿主仍在跑就换包（sh 侧 ''|*[!0-9]* 与 0 拒同口径）。
                    if (!int.TryParse(value, out var pid) || pid <= 0)
                    {
                        throw new UsageException($"--wait-pid expects a positive numeric pid, got '{value}'.");
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
                case "--lock-timeout":
                    if (!int.TryParse(value, out var lockSeconds) || lockSeconds < 0)
                    {
                        throw new UsageException($"--lock-timeout expects a non-negative number, got '{value}'.");
                    }
                    options.LockTimeoutSeconds = lockSeconds;
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
    public int LockTimeoutSeconds = 30;
}

internal sealed class UsageException(string message) : Exception(message);
internal sealed class WaitTimeoutException : Exception
{
    public WaitTimeoutException() { }
    public WaitTimeoutException(string message) : base(message) { }
}
