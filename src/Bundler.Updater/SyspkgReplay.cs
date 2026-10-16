using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DotNet.Bundler.Updater.Protocol;

namespace DotNet.Bundler.Updater;

/// <summary>
/// deb/rpm/alpineapk 系统包格式的客户端 replay 编排（docs/design/update.md §2.2/§2.4）：
/// 预检三件套 → 留存回滚件 → 三级提权退化链拉起安装位 `bundler-updater pkg-apply` → 返回。
/// 本族不走 file-swap 三段式：包管理器账本即状态，换包执行体是引导件编排的
/// apt-get/dpkg、dnf/zypper/rpm、apk 前端退化链；客户端只做确定性编排。
/// 拉起即返回——降权重启与账务全归引导件侧，本进程随即退出让包管理器收尾。
/// </summary>
internal static class SyspkgReplay
{
    // ---- 测试缝（与引导件 PkgApplyPlan 同名同源约定）----

    /// <summary>
    /// command -v 存在性/解析缝：置非 null 即权威（返 null=命令缺席，不回 PATH）；
    /// 生产为 null 走 BUNDLER_UPDATER_CMD_&lt;TOOL&gt; env 覆盖 → PATH 实查。
    /// </summary>
    internal static Func<string, string?>? CommandProbe;

    /// <summary>
    /// 环境变量读取缝：置非 null 即权威（会话 env 收集与 BUNDLER_UPDATER_* 全经此），
    /// 生产为 null 走 Environment.GetEnvironmentVariable。
    /// </summary>
    internal static Func<string, string?>? EnvProbe;

    /// <summary>
    /// 同步执行缝（账本归属查询 dpkg -s / rpm -q / apk info -e）：置非 null 即不真
    /// spawn，由测试记录 argv 并回假退出码。
    /// </summary>
    internal static Func<string, string[], int>? ExecProbe;

    // 会话 env 件的固定键集——在场才写行，不在场跳过（§2.2 "等在"口径）。
    private static readonly string[] SessionEnvKeys =
    {
        "HOME", "DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS",
        "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "SSH_AUTH_SOCK",
    };

    // retention 内的 applied 标记件：记"最近一次交给 pkg-apply 的包名"，
    // rollback 选件时把它排除在旧包候选外（逐次新装即逐次新记）。
    private const string AppliedMarkerName = "applied";

    private const string AskpassPrompt = "Authentication required to install the update";

    /// <summary>deb/rpm/alpineapk 判定——客户端身份 format 词面。</summary>
    internal static bool IsSyspkgFormat(string? format) =>
        string.Equals(format, "deb", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "rpm", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "alpineapk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// apply 编排：验签已过（UpdateClient 闸）→ 预检 → 留存回滚件 → 提权退化链
    /// 拉起安装位 bundler-updater。返回的是提权器进程句柄（拉起即退语义）。
    /// </summary>
    internal static Process Apply(
        UpdateInstallIdentity identity, string artifactPath,
        string installDirectory, ApplyOptions options, Action<string>? log)
        => StartLaunch(PlanApply(identity, artifactPath, installDirectory, options, log), log);

    /// <summary>
    /// rollback 编排：同路径同提权链重放留存旧包并带 --rollback——
    /// 无留存件确定性拒绝，不做空转 replay。
    /// </summary>
    internal static Process Rollback(
        UpdateInstallIdentity identity,
        string installDirectory, ApplyOptions options, Action<string>? log)
        => StartLaunch(PlanRollback(identity, installDirectory, options, log), log);

    /// <summary>
    /// apply 拉起面规划（纯规划无 spawn——单测可逐字断言）：预检三件套 →
    /// 解析安装位引导件 → 选提权腿 → 留存新包 + applied 标记 → 会话 env 件 → verb argv。
    /// 规划半途失败不污染 retention/env 现场（尽力还原后透传拒绝）。
    /// </summary>
    internal static SyspkgLaunch PlanApply(
        UpdateInstallIdentity identity, string artifactPath,
        string installDirectory, ApplyOptions options, Action<string>? log)
    {
        var format = VerbFormat(identity.Format);
        var pkg = identity.Pkg ?? "";
        UpdateApplier.SweepStaleStaging(options.StagingRoot);
        PrecheckLedgerAndManagers(format, pkg);
        var updaterArgv = ResolveUpdaterArgv(options.BootstrapperPath, installDirectory);
        var app = RequireAppPath(format, options);
        var leg = PickElevationLeg(pkg);
        var pkgArg = Path.GetFullPath(artifactPath);
        var retentionDir = ResolveRetentionDirectory(installDirectory, options);
        var appliedBefore = ReadAppliedMarker(retentionDir);
        string? retained = null;
        string? envFile = null;
        string? envDir = null;
        try
        {
            retained = TryRetainPackage(artifactPath, retentionDir, log);
            if (retained is not null)
            {
                pkgArg = retained;
            }
            envFile = TryWriteSessionEnvFile(options, out envDir, log);
            var verbArgv = BuildVerbArgv(
                updaterArgv, pkgArg, format, app, envFile, rollback: false, LogFilePath(options));
            log?.Invoke($"update: pkg-apply '{pkgArg}' via {leg.Description}");
            return BuildLaunch(leg, verbArgv, envFile, envDir);
        }
        catch
        {
            CleanupEnvFile(envFile, envDir);
            RestoreAppliedMarker(retentionDir, appliedBefore);
            if (retained is not null)
            {
                TryDeleteFile(retained);
            }
            throw;
        }
    }

    /// <summary>
    /// rollback 拉起面规划：预检 → 留存集选旧包（缺席确定性拒绝）→ 引导件/提权腿 →
    /// verb argv 带 --rollback；applied 标记改记所选旧包（回滚后账本以它为当前）。
    /// </summary>
    internal static SyspkgLaunch PlanRollback(
        UpdateInstallIdentity identity,
        string installDirectory, ApplyOptions options, Action<string>? log)
    {
        var format = VerbFormat(identity.Format);
        var pkg = identity.Pkg ?? "";
        UpdateApplier.SweepStaleStaging(options.StagingRoot);
        PrecheckLedgerAndManagers(format, pkg);
        var retentionDir = ResolveRetentionDirectory(installDirectory, options);
        var selected = PickRollbackPackage(retentionDir, out var appliedBefore)
            ?? throw new UpdateException(
                $"no retained package to roll back '{pkg}' — " +
                "rollback replays a package retained by a previous update through this client.");
        var updaterArgv = ResolveUpdaterArgv(options.BootstrapperPath, installDirectory);
        var app = RequireAppPath(format, options);
        var leg = PickElevationLeg(pkg);
        string? envFile = null;
        string? envDir = null;
        try
        {
            envFile = TryWriteSessionEnvFile(options, out envDir, log);
            var verbArgv = BuildVerbArgv(
                updaterArgv, selected, format, app, envFile, rollback: true, LogFilePath(options));
            TryWriteAppliedMarker(retentionDir, Path.GetFileName(selected), log);
            log?.Invoke($"update: pkg-apply rollback '{selected}' via {leg.Description}");
            return BuildLaunch(leg, verbArgv, envFile, envDir);
        }
        catch
        {
            CleanupEnvFile(envFile, envDir);
            RestoreAppliedMarker(retentionDir, appliedBefore);
            throw;
        }
    }

    // ---- 预检 ----

    // 预检三件套之②③：包管理器在位（前端退化链至少一腿 command -v 可达）+
    // 账本归属双检（dpkg -s / rpm -q / apk info -e 确证 pkg 由该包管理器登记）。
    // ① bundler-update.json 旁车在场 + pkg 非空由 UpdateClient.FromInstallDirectory 与
    // 构造闸保证——这里消费的是构造过的身份，不重读旁车。
    // 缺任一 → "不参与更新"口径确定性拒绝（防 repack/手动铺文件的漂移更新）。
    private static void PrecheckLedgerAndManagers(string format, string pkg)
    {
        var chain = ManagerChain(format);
        var reachable = false;
        foreach (var tool in chain)
        {
            if (ResolveCommand(tool) is not null)
            {
                reachable = true;
                break;
            }
        }
        if (!reachable)
        {
            throw new UpdateException(
                $"no package manager frontend for '{format}' found (probed {string.Join("/", chain)}) — " +
                "this installation does not participate in updates.");
        }
        var ledgerTool = LedgerTool(format);
        var ledgerPath = ResolveCommand(ledgerTool)
            ?? throw new UpdateException(
                $"ledger tool '{ledgerTool}' not found — cannot verify that package '{pkg}' " +
                "is registered; this installation does not participate in updates.");
        var rc = Exec(ledgerPath, LedgerArgs(format, pkg));
        if (rc != 0)
        {
            throw new UpdateException(
                $"package '{pkg}' is not registered in the {ledgerTool} ledger (probe exit {rc}) — " +
                "this installation does not participate in updates.");
        }
    }

    // 前端退化链与账本工具按引导件 PkgApplyPlan 同款口径：apt-get→dpkg、
    // dnf→zypper→rpm、apk。账本工具恒在链内（dpkg/rpm/apk 既是账本又是末级前端）。
    private static string[] ManagerChain(string format) => format switch
    {
        "deb" => ["apt-get", "dpkg"],
        "rpm" => ["dnf", "zypper", "rpm"],
        _ => ["apk"],
    };

    private static string LedgerTool(string format) => format switch
    {
        "deb" => "dpkg",
        "rpm" => "rpm",
        _ => "apk",
    };

    private static string[] LedgerArgs(string format, string pkg) => format switch
    {
        "deb" => ["-s", pkg],
        "rpm" => ["-q", pkg],
        _ => ["info", "-e", pkg],
    };

    // ---- 身份 format → 动词 format / 拉起面 ----

    // 身份 format 词 → pkg-apply 动词 --format 词：alpineapk 在动词面用 apk。
    private static string VerbFormat(string? format) => format?.ToLowerInvariant() switch
    {
        "deb" => "deb",
        "rpm" => "rpm",
        "alpineapk" => "apk",
        _ => throw new UpdateException($"format '{format}' has no pkg-apply semantics."),
    };

    // 引导件解析：安装位注入件 bundler-updater 直跑（包管理器换包不自锁——
    // pkg-apply 不搬移安装目录，安装位原地可用）；显式/落位 .sh 件经 /bin/sh 包裹。
    private static string[] ResolveUpdaterArgv(string? overridePath, string installDirectory)
    {
        if (UpdateApplier.ResolveBootstrapper(overridePath, installDirectory) is { } binary)
        {
            return [binary];
        }
        if (UpdateApplier.ResolveBootstrapperScript(overridePath, installDirectory) is { } script)
        {
            return ["/bin/sh", script];
        }
        throw new UpdateException(
            "no bootstrapper found (bundler-updater) under '" + installDirectory + "' — " +
            "the package was not built with update support.");
    }

    // pkg-apply 重启语义硬需要 --app——缺席即编排不能成立，确定性拒绝。
    private static string RequireAppPath(string format, ApplyOptions options) =>
        options.AppPath is { Length: > 0 } app
            ? Path.GetFullPath(app)
            : throw new UpdateException(
                $"ApplyOptions.AppPath is required for '{format}' replay — " +
                "pkg-apply restarts the application after installing the package.");

    private static string? LogFilePath(ApplyOptions options) =>
        options.LogFile is { Length: > 0 } file ? Path.GetFullPath(file) : null;

    // ---- 三级提权退化链 ----

    private sealed class ElevationLeg
    {
        private ElevationLeg(string description)
        {
            Description = description;
        }

        internal string Description { get; }
        internal string? Tool;                 // 直拉腿：pkexec/sudo 解析路径
        internal string[] ToolArgvPrefix = []; // 直拉腿 argv 前缀（sudo 腿带 "--"）
        internal string[]? AskpassArgv;        // 管线腿：问密码命令 argv（zenity/kdialog）
        internal string? SudoPath;             // 管线腿：sudo 解析路径

        internal static ElevationLeg Direct(string tool, string description, string[]? prefix = null) =>
            new(description) { Tool = tool, ToolArgvPrefix = prefix ?? [] };

        internal static ElevationLeg Askpass(string[] askpassArgv, string sudoPath, string description) =>
            new(description) { AskpassArgv = askpassArgv, SudoPath = sudoPath };
    }

    // 选腿：pkexec → zenity/kdialog 问密码 + sudo -S → 裸 sudo。
    // 这里只做可用性探测选腿；哪级拉起成功即定，不起二次重试循环。
    private static ElevationLeg PickElevationLeg(string pkg)
    {
        if (ResolveCommand("pkexec") is { } pkexec)
        {
            return ElevationLeg.Direct(pkexec, "pkexec");
        }
        var zenity = ResolveCommand("zenity");
        var kdialog = zenity is null ? ResolveCommand("kdialog") : null;
        string[]? askpass = zenity is not null
            ? [zenity, "--password"]
            : kdialog is not null
                ? [kdialog, "--password", AskpassPrompt]
                : null;
        var sudo = ResolveCommand("sudo");
        if (askpass is not null && sudo is not null)
        {
            return ElevationLeg.Askpass(askpass, sudo,
                (zenity is not null ? "zenity" : "kdialog") + "+sudo -S");
        }
        if (sudo is not null)
        {
            return ElevationLeg.Direct(sudo, "sudo", ["--"]);
        }
        throw new UpdateException(
            "no privilege escalation mechanism available (probed pkexec / zenity+kdialog + sudo / sudo) — " +
            $"cannot replay package '{pkg}'.");
    }

    // verb argv 按 §2.3 协议签名逐字：
    //   bundler-updater pkg-apply --pkg <本地包> --format <deb|rpm|apk> --app <重启目标>
    //       [--env-file <会话env件>] [--rollback] [--log <件>]
    // updaterArgv 已含引导件直跑或 "/bin/sh <script>" 前缀。
    private static string[] BuildVerbArgv(
        string[] updaterArgv, string pkgPath, string format, string app,
        string? envFile, bool rollback, string? logFile)
    {
        var argv = new List<string>();
        argv.AddRange(updaterArgv);
        argv.Add("pkg-apply");
        argv.Add("--pkg");
        argv.Add(pkgPath);
        argv.Add("--format");
        argv.Add(format);
        argv.Add("--app");
        argv.Add(app);
        if (envFile is not null)
        {
            argv.Add("--env-file");
            argv.Add(envFile);
        }
        if (rollback)
        {
            argv.Add("--rollback");
        }
        if (logFile is not null)
        {
            argv.Add("--log");
            argv.Add(logFile);
        }
        return argv.ToArray();
    }

    private static SyspkgLaunch BuildLaunch(
        ElevationLeg leg, string[] verbArgv, string? envFile, string? envDir)
    {
        if (leg.AskpassArgv is { } askpass)
        {
            // 问密码腿经 POSIX 管道：zenity/kdialog 把密码行直接喂 sudo -S——
            // 密码不过本进程内存；管线退出码按末级（sudo 起起的 pkg-apply）算。
            var pipeline = ShJoin(askpass) + " | " +
                ShJoin([leg.SudoPath!, "-S", "-p", "", "--", .. verbArgv]);
            return new SyspkgLaunch
            {
                FileName = "/bin/sh",
                Arguments = ["-c", pipeline],
                EnvFile = envFile,
                EnvFileDirectory = envDir,
            };
        }
        return new SyspkgLaunch
        {
            FileName = leg.Tool!,
            Arguments = [.. leg.ToolArgvPrefix, .. verbArgv],
            EnvFile = envFile,
            EnvFileDirectory = envDir,
        };
    }

    // ---- 留存 ----

    // 留存目录：显式 RollbackBackupDirectory 优先，否则一律落用户数据区
    // （客户端以普通权限跑——机器区 /var/lib 写不进，与 file-swap 的
    // RetentionRoot 机器分支刻意不同路）；目录名沿用 "<装位叶名>-<路径哈希>"。
    internal static string ResolveRetentionDirectory(string installDirectory, ApplyOptions options)
    {
        if (options.RollbackBackupDirectory is { Length: > 0 } explicitDir)
        {
            return Path.GetFullPath(explicitDir);
        }
        var full = Path.GetFullPath(installDirectory);
        return Path.Combine(UserBackupsRoot(), UpdateApplier.RetentionDirName(full));
    }

    // per-user 数据根：与 UpdateApplier.RetentionRoot 的用户分支同口径
    //（Windows=%LOCALAPPDATA%\DotNet.Bundler、macOS=~/Library/Application Support/
    //  DotNet.Bundler、Linux=$XDG_DATA_HOME 或 ~/.local/share 下的 dotnet-bundler）。
    private static string UserBackupsRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DotNet.Bundler", "backups");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "DotNet.Bundler", "backups");
        }
        var xdg = GetEnv("XDG_DATA_HOME");
        return Path.Combine(
            xdg is { Length: > 0 } ? xdg
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share"),
            "dotnet-bundler", "backups");
    }

    // 留存本次下载的新包到 retention（按工件名落位）+ applied 标记。
    // 包管理器账本不替用户保留包文件——留存责任在客户端；写不进按 WARN 降级
    // 放行，rollback 腿自有"无留存件"确定性拒绝兜底。
    private static string? TryRetainPackage(string artifactPath, string retentionDir, Action<string>? log)
    {
        try
        {
            Directory.CreateDirectory(retentionDir);
            var retained = Path.Combine(retentionDir, Path.GetFileName(artifactPath));
            File.Copy(artifactPath, retained, overwrite: true);
            TryWriteAppliedMarker(retentionDir, Path.GetFileName(retained), log);
            log?.Invoke($"update: retained rollback artifact '{retained}'");
            return retained;
        }
        catch (Exception exception)
        {
            log?.Invoke(
                $"update: WARN cannot retain rollback artifact ({exception.Message}) — " +
                "rollback may have nothing to replay");
            return null;
        }
    }

    private static string? ReadAppliedMarker(string retentionDir)
    {
        try
        {
            var marker = Path.Combine(retentionDir, AppliedMarkerName);
            if (File.Exists(marker))
            {
                return File.ReadAllText(marker).Trim();
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static void TryWriteAppliedMarker(string retentionDir, string packageName, Action<string>? log)
    {
        try
        {
            File.WriteAllText(Path.Combine(retentionDir, AppliedMarkerName), packageName + "\n");
        }
        catch (Exception exception)
        {
            log?.Invoke($"update: WARN cannot write applied marker ({exception.Message})");
        }
    }

    private static void RestoreAppliedMarker(string retentionDir, string? previous)
    {
        try
        {
            var marker = Path.Combine(retentionDir, AppliedMarkerName);
            if (previous is null)
            {
                if (File.Exists(marker))
                {
                    File.Delete(marker);
                }
            }
            else
            {
                File.WriteAllText(marker, previous + "\n");
            }
        }
        catch (Exception)
        {
        }
    }

    // 回滚选件：retention 内除 applied 标记当前件外最新落位者——
    // 首次 Bundler 链更新前/留存缺失 → null（调用方确定性拒绝不空转）。
    private static string? PickRollbackPackage(string retentionDir, out string? appliedName)
    {
        appliedName = ReadAppliedMarker(retentionDir);
        try
        {
            if (!Directory.Exists(retentionDir))
            {
                return null;
            }
            string? best = null;
            var bestStamp = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(retentionDir))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, AppliedMarkerName, StringComparison.Ordinal) ||
                    string.Equals(name, appliedName, StringComparison.Ordinal))
                {
                    continue;
                }
                var stamp = File.GetLastWriteTimeUtc(file);
                if (best is null || stamp > bestStamp ||
                    (stamp == bestStamp &&
                     string.CompareOrdinal(name, Path.GetFileName(best)) > 0))
                {
                    best = file;
                    bestStamp = stamp;
                }
            }
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- 会话 env 件 ----

    // 会话 env 件：<StagingRoot>/pkg-<guid>/session.env，逐行 K=V，0600 仅属主可读——
    // pkexec/sudo 剥环境后引导件经 --env-file 回灌会话变量做降权重启。
    // 全部键缺席或写入/收权失败时降级为不带 --env-file（引导件按最小集处理），
    // 失败现场随清扫口径不残留。
    private static string? TryWriteSessionEnvFile(
        ApplyOptions options, out string? envDir, Action<string>? log)
    {
        envDir = null;
        var lines = new List<string>();
        foreach (var key in SessionEnvKeys)
        {
            var value = GetEnv(key);
            // 换行/回车会撕裂行格式（引导件逐行解析）——坏值跳过不进件。
            if (value is null || value.Length == 0 ||
                value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
            {
                continue;
            }
            lines.Add(key + "=" + value);
        }
        if (lines.Count == 0)
        {
            return null;
        }
        var dir = Path.Combine(options.StagingRoot, "pkg-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "session.env");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(file, lines);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Chmod600(file) != 0)
            {
                throw new IOException($"chmod 0600 failed (errno {Marshal.GetLastWin32Error()})");
            }
            envDir = dir;
            return file;
        }
        catch (Exception exception)
        {
            log?.Invoke(
                $"update: WARN cannot write session env file ({exception.Message}) — " +
                "restart falls back to the minimal environment set");
            TryDeleteDirectory(dir);
            return null;
        }
    }

    // netstandard2.0 无 Unix mode API——与 UpdateBootstrapper.Chmod755 同源 P/Invoke 口径；
    // 仅非 Windows 调用（Windows 文件权限语义无关，写入照常、跳过收权）。
    private static int Chmod600(string path) => Chmod(path, 0x180 /* 0600 */);

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Chmod(string path, ushort mode);

    // command -v 语义含可执行位——netstandard2.0 走 libc access(X_OK)；
    // access 缺席（异类 libc）按存在即收，让 spawn 侧自己的错误浮出。
    private static bool HasExecutePermission(string path)
    {
        try
        {
            return Access(path, 1 /* X_OK */) == 0;
        }
        catch (Exception)
        {
            return File.Exists(path);
        }
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Access(string path, int mode);

    // ---- 进程面 ----

    /// <summary>提权拉起规格：启动文件/argv/待清理会话 env 件——纯数据无 spawn。</summary>
    internal sealed class SyspkgLaunch
    {
        public string FileName = "";
        public string[] Arguments = [];
        public string? EnvFile;
        public string? EnvFileDirectory;
    }

    // 拉起即返回：Exited 钩做两件尽力事——会话 env 件用完即删 +
    // 退出码分类留痕（拉起即退语义下 rc 主要靠 --log 件；活到读码的调用方
    // 也得分类口径）。hook 挂在 ThreadPool——句柄返回后异步触发。
    private static Process StartLaunch(SyspkgLaunch launch, Action<string>? log)
    {
        Process process;
        try
        {
            var startInfo = new ProcessStartInfo(launch.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // 继承 stdio：裸 sudo 的 tty 密码提示与 pkexec/zenity 的 GUI 通道都靠它。
                Arguments = JoinArgv(launch.Arguments),
            };
            process = Process.Start(startInfo)
                ?? throw new UpdateException($"failed to start '{launch.FileName}'.");
        }
        catch
        {
            CleanupEnvFile(launch.EnvFile, launch.EnvFileDirectory);
            throw;
        }
        var envFile = launch.EnvFile;
        var envDir = launch.EnvFileDirectory;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try
            {
                log?.Invoke(
                    $"update: pkg-apply launcher exited {process.ExitCode} " +
                    $"({DescribeExitCode(process.ExitCode)})");
            }
            catch (Exception)
            {
            }
            CleanupEnvFile(envFile, envDir);
        };
        return process;
    }

    // pkg-apply 退出码语义（§2.3）：0 成功；3 参数错；4 安装失败；5 包管理器缺席；
    // 6 重启失败（安装已提交）。其余为提权器自身码（pkexec 126/127 鉴权失败等）。
    internal static string DescribeExitCode(int exitCode) => exitCode switch
    {
        0 => "success",
        3 => "usage error",
        4 => "package install failed",
        5 => "no package manager available",
        6 => "installed; restart failed — relaunch manually",
        _ => "elevation/launcher failure",
    };

    // 账本归属查询的同步执行：stdio 抽走防输出串到应用面，60s 有界等待——
    // 挂死的工具按"未登记"拒绝而不是跟着挂。
    private static int Exec(string file, string[] args)
    {
        if (ExecProbe is { } probe)
        {
            return probe(file, args);
        }
        try
        {
            var startInfo = new ProcessStartInfo(file, JoinArgv(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                }
                return -1;
            }
            Task.WaitAll(stdout, stderr);
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // ---- 命令解析 / env ----

    // command -v 解析序：BUNDLER_UPDATER_CMD_<TOOL> env 覆盖 → CommandProbe 权威 → PATH 实查。
    // 与引导件 PkgApplyPlan.ResolveCommand 同名同源，供垫片测试同口径供给。
    private static string? ResolveCommand(string name)
    {
        var overridePath = GetEnv("BUNDLER_UPDATER_CMD_" + CommandEnvName(name));
        if (!string.IsNullOrEmpty(overridePath))
        {
            return overridePath;
        }
        if (CommandProbe is { } probe)
        {
            return probe(name);
        }
        return FindOnPath(name);
    }

    private static string CommandEnvName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            var upper = char.ToUpperInvariant(c);
            builder.Append(IsAsciiLetterOrDigit(upper) ? upper : '_');
        }
        return builder.ToString();
    }

    // netstandard2.0 无 char.IsAscii*——手写判据。
    private static bool IsAsciiLetterOrDigit(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static string? FindOnPath(string name)
    {
        var pathEnv = GetEnv("PATH");
        if (pathEnv is null || pathEnv.Length == 0)
        {
            return null;
        }
        foreach (var dir in pathEnv.Split(
                     new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var pathext = GetEnv("PATHEXT");
                var extensions = pathext is null || pathext.Length == 0
                    ? [".COM", ".EXE", ".BAT", ".CMD"]
                    : pathext.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var extension in extensions)
                {
                    var withExtension = candidate + extension;
                    if (File.Exists(withExtension))
                    {
                        return withExtension;
                    }
                }
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            else if (File.Exists(candidate) && HasExecutePermission(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? GetEnv(string name) =>
        EnvProbe is { } probe ? probe(name) : Environment.GetEnvironmentVariable(name);

    // ---- 小工具 ----

    private static void CleanupEnvFile(string? envFile, string? envDir)
    {
        if (envFile is not null)
        {
            TryDeleteFile(envFile);
        }
        if (envDir is not null)
        {
            TryDeleteDirectory(envDir);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            // 只清本编排建的 pkg-<guid> 目录（内只有 session.env）——非递归已够。
            Directory.Delete(dir);
        }
        catch (Exception)
        {
        }
    }

    // ProcessStartInfo.Arguments 是整条字符串（netstandard2.0 无 ArgumentList）——
    // 逐元双引号包裹空格连接，POSIX argv 还原按层透明。
    internal static string JoinArgv(IEnumerable<string> argv)
    {
        var builder = new StringBuilder();
        foreach (var arg in argv)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append('"').Append(arg).Append('"');
        }
        return builder.ToString();
    }

    // POSIX 单引号引用（' 折为 '\''）——与引导件 PkgApplyPlan.ShQuote 同算法。
    private static string ShQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string ShJoin(IEnumerable<string> argv) => string.Join(" ", argv.Select(ShQuote));
}
