using System.Diagnostics;
using System.Runtime.InteropServices;
using DotNet.Bundler.Updater.Protocol;

namespace DotNet.Bundler.Updater;

/// <summary>
/// 应用面分派：同格式同通道三大语义——
///   installer-replay：nsis `/UPDATE`（被动默认）/ msiexec major upgrade；
///   file-swap：zip/targz 解包 → 引导程序三段式换包；appimage 单件视为目录内文件同款；
///   pkg-replay：deb/rpm/alpineapk 系统包经 SyspkgReplay 编排——预检/留存/提权退化链
///     拉起安装位 `bundler-updater pkg-apply`（不走 file-swap，包管理器账本即状态）。
/// 引导件优先取注入安装目录的 `bundler-updater[.exe]`，缺失退 `bundler-updater.sh`；
/// file-swap 先复制到临时目录再启动——引导件自身不能被它要换的目录锁死（Windows）；
/// pkg-replay 不搬移安装目录，注入件原地直跑。
/// </summary>
internal sealed class UpdateApplier
{
    internal static Process Apply(
        UpdateInstallIdentity identity, UpdateFeedArtifact artifact, string artifactPath,
        string installDirectory, ApplyOptions options, Action<string>? log)
    {
        // B-N12：IsSyspkgFormat 判定大小写不敏感，分派 switch 对齐同口径——
        // emitter 恒产小写 format，大小写漂移件统一走到 syspkg 分支内的
        // 词面一致性闸而非"no apply semantics"。
        switch (artifact.Format?.ToLowerInvariant())
        {
            case "nsis":
                return StartReplay(artifactPath,
                    options.SilentInstaller
                        ? UpdateArgv.Join(new[] { "/UPDATE", "/S" })
                        : "/UPDATE", log);
            case "msi":
                return StartReplay(
                    ResolveMsiexec(),
                    UpdateArgv.Join(new[]
                    {
                        "/i", artifactPath,
                        options.SilentInstaller ? "/qn" : "/passive",
                    }),
                    log);
            case "zip":
            case "app": // .app 以 zip 件分发：解出内层 .app 目录走同一换包通道
            {
                var payload = ExtractZip(artifactPath, options, log);
                return RunBootstrapper(payload, installDirectory, options, log);
            }
            case "targz":
            {
                var payload = ExtractTarGz(artifactPath, options, log);
                return RunBootstrapper(payload, installDirectory, options, log);
            }
            // --- 系统包 replay（deb/rpm/alpineapk，docs/design/update.md §2.2）---
            // 预检三件套 → 新包留存 retention（pkg+sig 成对 + 副本再验签）→
            // 三级提权退化链（pkexec → 问密码+sudo -S → 裸 sudo）拉起安装位
            // bundler-updater 的 pkg-apply，拉起即返回；alpineapk 在动词面映射 apk。
            case "deb":
            case "rpm":
            case "alpineapk":
                // B5 词面一致性闸：清单 artifact.Format 与身份 format 必须逐字一致——
                // SelectArtifact 的不敏感匹配放进来的大小写漂移件（"DEB" vs "deb"）
                // 是手造清单信号，确定性拒绝（emitter 双侧恒小写不会触发）。
                if (!string.Equals(artifact.Format, identity.Format, StringComparison.Ordinal))
                {
                    throw new UpdateException(
                        $"artifact format '{artifact.Format}' does not literally match " +
                        $"install identity format '{identity.Format}' — refused.");
                }
                return SyspkgReplay.Apply(
                    identity, artifactPath, artifact.Signature, installDirectory, options, log);
            case "appimage":
            {
                // 单文件载荷：AppImage 的安装单元就是文件本身——install 目标是 .AppImage 文件路径，
                // 引导件走文件级换包（目录级会把宿主目录里无关文件一起清掉）。
                var staging = PrepareStaging(options);
                var staged = Path.Combine(staging, Path.GetFileName(artifactPath));
                File.Copy(artifactPath, staged, overwrite: true);
                MakeExecutable(staged);
                return RunBootstrapper(staged, installDirectory, options, log);
            }
            default:
                throw new UpdateException($"format '{artifact.Format}' has no apply semantics.");
        }
    }

    /// <summary>回滚钩子：引导程序 `--rollback` 模式——备份复制回安装目录且不二次备份。
    /// 备份位置与换包时的保留决策一致：开启保留指数据区目录，未开启指兄弟位瞬备（多半已删→拒绝）。
    /// deb/rpm/alpineapk 身份改走 SyspkgReplay：同提权链重放留存旧包并带 --rollback。</summary>
    internal static Process Rollback(
        UpdateInstallIdentity identity,
        string installDirectory, ApplyOptions options, Action<string>? log)
    {
        if (SyspkgReplay.IsSyspkgFormat(identity.Format))
        {
            return SyspkgReplay.Rollback(identity, installDirectory, options, log);
        }
        var backup = BackupLocationForRollback(installDirectory, options);
        log?.Invoke("update: rolling back to '" + backup + "'");
        return RunBootstrapper(null, installDirectory, options, log, rollback: true, backupDirectory: backup);
    }

    // 引导程序 = 换包执行体；从安装目录/包内取件复制到临时目录执行，防自锁。
    private static Process RunBootstrapper(
        string? payloadDirectory, string installDirectory,
        ApplyOptions options, Action<string>? log, bool rollback = false,
        string? backupDirectory = null)
    {
        SweepStaleStaging(options.StagingRoot);
        var runDir = Path.Combine(
            options.StagingRoot, "bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        // argv 经 UpdateArgv.Join 逐元转义——路径含 "/串尾 \ 在裸拼接下会撕裂
        // 参数边界（最坏情形把 --install-dir/--backup-dir 解析到别的目录）。
        var argv = new List<string> { "apply", "--install-dir", installDirectory };
        if (payloadDirectory is { Length: > 0 } payload)
        {
            argv.Add("--payload");
            argv.Add(payload);
        }
        if (rollback)
        {
            argv.Add("--rollback");
        }
        if (backupDirectory is { Length: > 0 } bd)
        {
            argv.Add("--backup-dir");
            argv.Add(bd);
        }
        if (!rollback && options.KeepRollbackBackup)
        {
            argv.Add("--retain-backup-to");
            argv.Add(ResolveRetentionDirectory(installDirectory, options));
        }
        if (options.WaitPid is { } pid)
        {
            argv.Add("--wait-pid");
            argv.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            // 换包不等宿主进程退出=安装目录正被运行中应用占用——文件替换/目录
            // 搬移会竞态撕裂（应用中途读到半新半旧件）。API 面不强填当前 pid
            //（调用方可能是管理器进程而非应用本体），但无 wait 目标必须可观测。
            log?.Invoke(
                "update: WARN no --wait-pid — swap may race a running instance");
        }
        // --app 必须在客户端 cwd 绝对化——引导件以临时目录为工作目录，相对路径会静默解析失败。
        if (options.AppPath is { Length: > 0 } app)
        {
            argv.Add("--app");
            argv.Add(Path.GetFullPath(app));
        }
        if (options.KeepPayload)
        {
            argv.Add("--keep-payload");
        }
        // --log 同理按客户端 cwd 绝对化——相对路径会落进引导件临时目录随清理丢失。
        if (options.LogFile is { Length: > 0 } lf)
        {
            argv.Add("--log");
            argv.Add(Path.GetFullPath(lf));
        }
        var arguments = UpdateArgv.Join(argv);

        if (ResolveBootstrapper(options.BootstrapperPath, installDirectory) is { } binary)
        {
            var staged = Path.Combine(runDir, Path.GetFileName(binary));
            File.Copy(binary, staged, overwrite: true);
            MakeExecutable(staged);
            log?.Invoke($"update: bootstrapper '{staged}' {arguments}");
            return Start(staged, arguments, runDir, shellExecute: false);
        }
        if (ResolveBootstrapperScript(options.BootstrapperPath, installDirectory) is { } script)
        {
            var staged = Path.Combine(runDir, "bundler-updater.sh");
            File.Copy(script, staged, overwrite: true);
            log?.Invoke($"update: bootstrapper(script) '{staged}' {arguments}");
            return Start("/bin/sh",
                UpdateArgv.Join(new[] { staged }) + " " + arguments, runDir, shellExecute: false);
        }
        if (File.Exists(installDirectory))
        {
            // 文件级安装（AppImage）装位里塞不进注入件——自动解析注定失败，
            // 与"目录装位未带 update 件"分两路报错，免得调用方查错方向。
            throw new UpdateException(
                $"no bootstrapper for file-level install '{installDirectory}' — " +
                "pass ApplyOptions.BootstrapperPath explicitly or ship bundler-updater " +
                "next to the image.");
        }
        throw new UpdateException(
            "no bootstrapper found (bundler-updater[.exe]/bundler-updater.sh) — " +
            "package was not built with update support.");
    }

    // per-target 二进制优先；次 posix 脚本降级件。SyspkgReplay 同入口复用——
    // 系统包装位注入件原地直跑（pkg-apply 不搬移安装目录，无需 runDir 防自锁复制）。
    internal static string? ResolveBootstrapper(string? overridePath, string installDirectory)
    {
        if (overridePath is { Length: > 0 } over && File.Exists(over) &&
            !over.EndsWith(".sh", StringComparison.Ordinal))
        {
            return over;
        }
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "bundler-updater.exe" : "bundler-updater";
        foreach (var candidate in new[]
                 {
                     Path.Combine(installDirectory, name),
                     Path.Combine(installDirectory, "Contents", "MacOS", name),
                 })
        {
            if (File.Exists(candidate) &&
                !candidate.EndsWith(".sh", StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        return null;
    }

    internal static string? ResolveBootstrapperScript(string? overridePath, string installDirectory)
    {
        if (overridePath is { Length: > 0 } over &&
            over.EndsWith(".sh", StringComparison.Ordinal) && File.Exists(over))
        {
            return over;
        }
        foreach (var candidate in new[]
                 {
                     Path.Combine(installDirectory, "bundler-updater.sh"),
                     Path.Combine(installDirectory, "Contents", "MacOS", "bundler-updater.sh"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    // zip/targz 解包到暂存并挑唯一顶层目录当载荷根（我们的归档恒含单顶层 stem）。
    private static string ExtractZip(string zipPath, ApplyOptions options, Action<string>? log)
    {
        var staging = PrepareStaging(options);
        ArchiveExtractor.ExtractZipToDirectory(zipPath, staging, log);
        log?.Invoke($"update: extracted '{zipPath}' → '{staging}'");
        return SingleTopDirectory(staging, zipPath);
    }

    private static string ExtractTarGz(string tarGzPath, ApplyOptions options, Action<string>? log)
    {
        var staging = PrepareStaging(options);
        ArchiveExtractor.ExtractTarGzToDirectory(tarGzPath, staging, log);
        log?.Invoke($"update: extracted '{tarGzPath}' → '{staging}'");
        return SingleTopDirectory(staging, tarGzPath);
    }

    // 仅"顶层恰好一个目录且无根级文件"才算 wrapper——"目录内容直压"形状
    // （zip -ry <目录>/`tar -C <目录> .`）的单目录+多文件顶层必须整个暂存当载荷，
    // 误选子目录会把同级文件静默丢掉（换包后应用残缺）。
    internal static string SingleTopDirectory(string staging, string sourcePath)
    {
        var directories = Directory.GetDirectories(staging);
        var files = Directory.GetFiles(staging);
        if (directories.Length == 1 && files.Length == 0)
        {
            return directories[0];
        }
        if (files.Length > 0 || directories.Length > 0)
        {
            return staging;
        }
        throw new UpdateException($"archive '{sourcePath}' produced no payload content.");
    }

    // 暂存根默认路径：POSIX %TEMP%=/tmp 跨用户共享——按用户名分槽。
    // 分槽名必须单射（非允许字符一律 _xNNNN 编码，含 '_' 自身——"a b"/"a_b"
    // 这类洗净同形不并槽）；用户名缺席（最小容器无 passwd 条目）回退
    // euid 数字槽——仍保 per-user 语义不回落共享名。
    // UpdateClientOptions.DownloadDirectory 同根复用（同一暂存域）。
    internal static string DefaultStagingRoot()
    {
        var user = SanitizePathSegment(Environment.UserName);
        if (user.Length == 0 && UnixFileTypes.TryGetEUid(out var uid))
        {
            // '_u' 前缀保证与任何真实用户名编码结果不并槽（'_' 恒被编码）。
            user = "_u" + uid;
        }
        var name = user.Length > 0 ? "bundler-update-" + user : "bundler-update";
        return Path.Combine(Path.GetTempPath(), name);
    }

    // internal：单射性有单测断言（"a b"/"a_b"/"a_x0020b" 三名不并槽）。
    internal static string SanitizePathSegment(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '-')
            {
                builder.Append(c);
            }
            else
            {
                // 单射编码：'_' 也在编码面内——字面 "_x0020" 与转义结果不并槽。
                builder.Append("_x").Append(((int)c).ToString("x4"));
            }
        }
        return builder.ToString();
    }

    private static string PrepareStaging(ApplyOptions options)
    {
        var staging = Path.Combine(options.StagingRoot, "payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        return staging;
    }

    // `bootstrap-<guid>`/`payload-<guid>`/`pkg-<guid>`（会话 env 件目录）暂存目录
    // 按进程唯一命名——发起方死在半路时无清理方，残件在 StagingRoot 下无限积累。
    // 新建暂存前按 mtime 清扫 24h 前的同类目录；在飞目录（在飞引导件的 runDir、
    // 在飞 pkg-apply 未读完的 env 件）理论上也会被扫——现实里 >24h 仍在飞的换包
    // 概率近零（换包是分钟级操作），且 POSIX unlink 打开中文件照删不误并非
    // "删除会失败自然跳过"——登记为可接受边界，全路径 best-effort 不挡主流程。
    internal static void SweepStaleStaging(string stagingRoot)
    {
        try
        {
            if (!Directory.Exists(stagingRoot))
            {
                return;
            }
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var directory in Directory.GetDirectories(stagingRoot))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith("bootstrap-", StringComparison.Ordinal) &&
                    !name.StartsWith("payload-", StringComparison.Ordinal) &&
                    !name.StartsWith("pkg-", StringComparison.Ordinal))
                {
                    continue;
                }
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static string ResolveMsiexec() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    // 回滚时的备份位置：开启保留 → 数据区目录（文件级换包备份=保留目录内的同名文件）；
    // 未开启 → 兄弟位瞬备路径（正常已被换包成功清掉，到不了这）。
    // 兄弟位瞬备仍在时优先它——那是换包中途崩溃的现场，回滚顺带完成恢复。
    private static string BackupLocationForRollback(string installDirectory, ApplyOptions options)
    {
        var trimmed = installDirectory.TrimEnd('/', '\\');
        var sibling = trimmed + ".bundler-backup";
        if (Directory.Exists(sibling) || File.Exists(sibling))
        {
            return sibling;
        }
        if (!options.KeepRollbackBackup)
        {
            return sibling;
        }
        var retain = ResolveRetentionDirectory(installDirectory, options);
        return File.Exists(installDirectory)
            ? Path.Combine(retain, Path.GetFileName(trimmed))
            : retain;
    }

    // 回滚点集中存放区：per-machine 安装（Program Files//Applications//opt//usr//Library）落机器数据目录，
    // 其余落用户数据目录；目录名=安装目录名+路径哈希缀（同名应用多装位不撞）。
    internal static string ResolveRetentionDirectory(string installDirectory, ApplyOptions options)
    {
        if (options.RollbackBackupDirectory is { Length: > 0 } explicitDir)
        {
            return Path.GetFullPath(explicitDir);
        }
        var full = Path.GetFullPath(installDirectory);
        return Path.Combine(RetentionRoot(full), RetentionDirName(full));
    }

    // 留存目录名 = 安装目录名 + 路径哈希缀——SyspkgReplay 的用户空间留存目录共用此口径。
    internal static string RetentionDirName(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        var hashBytes = System.Security.Cryptography.SHA256.Create()
            .ComputeHash(System.Text.Encoding.UTF8.GetBytes(fullPath));
        var hex = new System.Text.StringBuilder(8);
        for (var i = 0; i < 4; i++)
        {
            hex.Append(hashBytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return name + "-" + hex;
    }

    private static string RetentionRoot(string installPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var machine = PathStartsWithSegment(installPath, programFiles,
                StringComparison.OrdinalIgnoreCase);
            return Path.Combine(
                Environment.GetFolderPath(machine
                    ? Environment.SpecialFolder.CommonApplicationData
                    : Environment.SpecialFolder.LocalApplicationData),
                "DotNet.Bundler", "backups");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var machine = PathStartsWithSegment(installPath, "/Applications", StringComparison.Ordinal) ||
                          PathStartsWithSegment(installPath, "/Library", StringComparison.Ordinal);
            var root = machine
                ? "/Library/Application Support"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support");
            return Path.Combine(root, "DotNet.Bundler", "backups");
        }
        var machineLinux = PathStartsWithSegment(installPath, "/opt", StringComparison.Ordinal) ||
                           PathStartsWithSegment(installPath, "/usr", StringComparison.Ordinal) ||
                           PathStartsWithSegment(installPath, "/snap", StringComparison.Ordinal);
        if (machineLinux)
        {
            return Path.Combine("/var/lib", "dotnet-bundler", "backups");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        // XDG 规范只认绝对路径——相对值按缺席退化默认基址，
        // 不把留存根漂到调用方 cwd 下（与 SyspkgReplay.UserBackupsRoot 同口径）。
        var dataHome = xdg is { Length: > 0 } && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "dotnet-bundler", "backups");
    }

    // 前缀命中还要过路径段边界："/optimal" 不能算落在 "/opt"——
    // 与 VolumeFreeSpace/FindVolume 同款断言，防字面前缀误认 per-machine 安装。
    private static bool PathStartsWithSegment(string path, string prefix, StringComparison comparison) =>
        path.StartsWith(prefix, comparison) &&
        (path.Length == prefix.Length || path[prefix.Length] is '/' or '\\');

    private static Process StartReplay(string file, string arguments, Action<string>? log)
    {
        log?.Invoke($"update: replay '{file}' {arguments}");
        return Start(file, arguments, Path.GetDirectoryName(file) ?? ".", shellExecute: true);
    }

    private static Process Start(string file, string arguments, string workingDirectory,
        bool shellExecute)
    {
        var startInfo = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = shellExecute,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = !shellExecute,
        };
        // 分离 spawn（bootstrap/重启腿）——迟到完成产出的子件是正当重启目标，不扑杀。
        return ProcessWait.Start(startInfo, killLateSpawned: false)
            ?? throw new UpdateException($"failed to start '{file}'.");
    }

    private static void MakeExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        try
        {
            using var process = ProcessWait.Start(new ProcessStartInfo(
                "/bin/chmod", UpdateArgv.Join(new[] { "+x", path }))
            { UseShellExecute = false });
            if (process is not null)
            {
                ProcessWait.WaitForExit(process, 10_000);
            }
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>应用参数：等谁退出、重启哪个、引导件/暂存/日志覆盖点。</summary>
public sealed class ApplyOptions
{
    /// <summary>引导程序的等待对象——通常是发起更新的当前进程（Environment.ProcessId）。
    /// 不填时换包不等任何进程退出：只在你确定安装目录此刻无运行中实例时才可省略，
    /// 客户端会打 WARN 可观测。</summary>
    public int? WaitPid { get; init; }
    /// <summary>换包完成后重启的应用路径（.app 目录件在 mac 走 open）。</summary>
    public string? AppPath { get; init; }
    /// <summary>安装器式更新是否静默（nsis /S、msiexec /qn）。</summary>
    public bool SilentInstaller { get; init; }
    /// <summary>保留暂存载荷（默认换包后清掉）。</summary>
    public bool KeepPayload { get; init; }
    /// <summary>换包成功后保留回滚备份（默认 false 不保留——备份仅作换包期崩溃恢复载体）。
    /// syspkg 族（deb/rpm/alpineapk）不受此约束：回滚留存件（pkg+sig+applied 标记）恒写
    /// retention 目录——包管理器账本不替用户保留包文件，留存责任在客户端。</summary>
    public bool KeepRollbackBackup { get; init; }
    /// <summary>回滚备份显式目录——默认按安装层级解析到用户/机器数据目录下的 DotNet.Bundler/backups。
    /// syspkg 族兼作留存根：留存的新包、签名旁车与 applied 标记同样落此目录。</summary>
    public string? RollbackBackupDirectory { get; init; }
    /// <summary>引导件显式路径——默认探测安装目录内注入件。</summary>
    public string? BootstrapperPath { get; init; }
    /// <summary>暂存根——默认系统临时目录下的 per-user `bundler-update[-&lt;user&gt;]`：
    /// POSIX 的 /tmp 跨用户共享，先建者的 0700/0755 权限会卡死后到用户的暂存目录，
    /// 目录名按用户名分槽规避（Windows TEMP 本已 per-user，同口径零成本）。</summary>
    public string StagingRoot { get; init; } = UpdateApplier.DefaultStagingRoot();
    /// <summary>引导程序日志文件。</summary>
    public string? LogFile { get; init; }
}
