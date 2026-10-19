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
    /// command -v 存在性/解析缝：BUNDLER_UPDATER_CMD_&lt;TOOL&gt; env 覆盖先判（见
    /// ResolveCommand 顺序），env 缺席时探针置非 null 即权威（返 null=命令缺席，
    /// 不回 PATH）；生产为 null 走 PATH 实查。
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

    /// <summary>
    /// 收权缝（会话 env 件 chmod 0600）：置非 null 即权威（返回 chmod 退出码——
    /// 非 0 按收权失败走降级清理路径，供测试注入失败点）；生产为 null 走
    /// Windows 跳过收权 + POSIX P/Invoke 实调。
    /// </summary>
    internal static Func<string, int>? ChmodProbe;

    /// <summary>
    /// 提权腿拉起缝：置非 null 即不真 spawn——由测试决定返回什么（任意退出码的
    /// 进程道具/抛异常），断言"spawn 抛异常才退化下一腿、已启动腿不重放"语义；
    /// 生产为 null 走 Process.Start。
    /// </summary>
    internal static Func<ProcessStartInfo, Process?>? SpawnProbe;

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

    // 拉起观察窗只用于退出码留痕与会话 env 删余，不是退化判据——Process.Start
    // 一旦返回有效句柄，该腿即终局：窗内任意 rc（含 pkexec 126 取消、127 鉴权
    // 失败、pkg-apply 3/4/5/6）都按"已拉起"交还句柄由调用方观察退出码，绝不
    // 补拉下一级（授权取消/拒绝/未知鉴权失败一律停，已执行安装器不重放）。
    // 超时仍在运行（鉴权提示等用户交互，分钟级合理）挂 Exited 钩同样只留痕+清理；
    // 窗内已退出（任意 rc）的已终结进程不保证派发 Exited——清理内联做。
    private const int LaunchObserveMilliseconds = 2_000;

    /// <summary>deb/rpm/alpineapk 判定——客户端身份 format 词面。</summary>
    internal static bool IsSyspkgFormat(string? format) =>
        string.Equals(format, "deb", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "rpm", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "alpineapk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// apply 编排：验签已过（UpdateClient 闸）→ 预检 → 留存回滚件（连签名件）→
    /// 留存副本再验签 → 提权退化链拉起安装位 bundler-updater。
    /// 返回的是提权器进程句柄（拉起即退语义）。signature = 清单上行 base64 签名。
    /// </summary>
    internal static Process Apply(
        UpdateInstallIdentity identity, string artifactPath, string signature,
        string installDirectory, ApplyOptions options, Action<string>? log)
        => StartLaunch(
            PlanApply(identity, artifactPath, signature, installDirectory, options, log), log);

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
    /// 解析安装位引导件 → 探测提权退化链 → 留存新包（pkg+sig 成对）+ applied 标记 →
    /// 留存副本再验签 → 会话 env 件 → verb argv。
    /// 规划半途失败不污染 retention/env 现场（尽力还原后透传拒绝）。
    /// signature = 清单上行 base64 签名——留存 .sig 与副本再验签都吃它。
    /// </summary>
    internal static SyspkgLaunch PlanApply(
        UpdateInstallIdentity identity, string artifactPath, string signature,
        string installDirectory, ApplyOptions options, Action<string>? log)
    {
        var format = VerbFormat(identity.Format);
        // 身份字段 pkg = 包管理器登记名（账本归属/预检用）；与 verb --pkg
        // （本地包文件路径）同名异义，勿混（C-N6）。
        var pkg = identity.Pkg ?? "";
        UpdateApplier.SweepStaleStaging(options.StagingRoot);
        PrecheckLedgerAndManagers(format, pkg);
        var updaterArgv = ResolveUpdaterArgv(options.BootstrapperPath, installDirectory);
        var app = RequireAppPath(format, options);
        var legs = PickElevationLegs(pkg);
        var pkgArg = Path.GetFullPath(artifactPath);
        var retentionDir = ResolveRetentionDirectory(installDirectory, options);
        var appliedBefore = ReadAppliedMarker(retentionDir);
        string? retained = null;
        string? envFile = null;
        string? envDir = null;
        try
        {
            retained = TryRetainPackage(artifactPath, retentionDir, signature, log);
            string? pkgSha256;
            if (retained is not null)
            {
                // B2 留存副本再验签：retention 目录与原件同为用户可写——验签对原路径做、
                // 装的是留存副本，验→拷→root 读之间存在同用户替换窗。直喂提权
                // pkg-apply 的件必须先自身过签：验不过即篡改/腐坏信号，确定性拒绝
                // 不绕道降级（留存失败的原始路径降级属 B-N3 登记的另一条口径）。
                // 验签同趟带出 sha256 交 --sha256——root 进程对暂存副本复核同值，
                // 客户端验签与 root 安装间的验后替换窗随字节绑定归零。
                if (!VerifyRetainedPackage(retained, identity.PublicKey, out pkgSha256, log))
                {
                    throw new UpdateException(
                        $"retained package '{retained}' failed signature re-verification — refused.");
                }
                pkgArg = retained;
            }
            else
            {
                // 原件降级腿同趟验签+哈希：清单签名本在手上，VerifyFile out 参
                // 单次读取一并出指纹——验签→root 读取之间的同用户替换窗与留存腿
                // 同口径归零（只哈希不验会把"签名仍有效"的断言留在规划时点）。
                if (!VerifyOriginalPackage(pkgArg, signature, identity.PublicKey, out pkgSha256, log))
                {
                    throw new UpdateException(
                        $"package '{pkgArg}' failed signature re-verification — refused.");
                }
            }
            envFile = TryWriteSessionEnvFile(options, out envDir, log);
            var verbArgv = BuildVerbArgv(
                updaterArgv, pkgArg, pkgSha256, format, app, envFile, rollback: false, LogFilePath(options));
            log?.Invoke($"update: pkg-apply '{pkgArg}' via {legs[0].Description}");
            var launch = BuildLaunch(legs[0], verbArgv, envFile, envDir);
            launch.Fallbacks = legs.Skip(1)
                .Select(leg => BuildLaunch(leg, verbArgv, envFile, envDir)).ToArray();
            // B1 还原上下文随拉起面走：全腿 spawn 失败时 StartLaunch 据此还原
            // applied 标记并删掉本轮新留存件（含 .sig）。
            launch.RetentionDir = retentionDir;
            launch.AppliedBefore = appliedBefore;
            launch.RetainedFile = retained;
            return launch;
        }
        catch
        {
            CleanupEnvFile(envFile, envDir);
            RestoreAppliedMarker(retentionDir, appliedBefore);
            TryDeleteRetained(retained);
            throw;
        }
    }

    /// <summary>
    /// rollback 拉起面规划：预检 → 留存集选旧包（缺席确定性拒绝）→ 引导件/提权腿 →
    /// verb argv 带 --rollback；applied 标记改记所选旧包（回滚后账本以它为当前）。
    /// 留存候选必须 pkg+sig 成对且对身份公钥再验过——retention 用户可写，
    /// 未过签件绝不进提权 pkg-apply（B2）。
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
        var selected = PickRollbackPackage(
                retentionDir, identity.PublicKey, log, out var appliedBefore, out var pkgSha256)
            ?? throw new UpdateException(
                $"no retained package to roll back '{pkg}' — " +
                "rollback replays a package retained by a previous update through this client.");
        var updaterArgv = ResolveUpdaterArgv(options.BootstrapperPath, installDirectory);
        var app = RequireAppPath(format, options);
        var legs = PickElevationLegs(pkg);
        string? envFile = null;
        string? envDir = null;
        try
        {
            envFile = TryWriteSessionEnvFile(options, out envDir, log);
            var verbArgv = BuildVerbArgv(
                updaterArgv, selected, pkgSha256, format, app, envFile, rollback: true, LogFilePath(options));
            TryWriteAppliedMarker(retentionDir, Path.GetFileName(selected), log);
            log?.Invoke($"update: pkg-apply rollback '{selected}' via {legs[0].Description}");
            var launch = BuildLaunch(legs[0], verbArgv, envFile, envDir);
            launch.Fallbacks = legs.Skip(1)
                .Select(leg => BuildLaunch(leg, verbArgv, envFile, envDir)).ToArray();
            // B1 还原上下文：rollback 无新建留存件（RetainedFile 留 null——
            // 所选旧包是既有件绝不能删），只还原 applied 标记翻记。
            launch.RetentionDir = retentionDir;
            launch.AppliedBefore = appliedBefore;
            return launch;
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
        // B-N4：dpkg -s 对 config-files 态（已卸未 purge 只剩配置件）同样退 0——
        // 方向安全：残留配置视同"账本有登记"，pkg-apply 重放安装原生覆盖，
        // 不会把漂移面误放进更新链。
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

    // 探测退化链：pkexec → zenity/kdialog 问密码 + sudo -S → 裸 sudo——
    // 这里只做可用性探测排出有序腿表；首选腿 spawn 抛（探测后工具被删/执行位
    // 丢失，Process.Start 未起成）时 StartLaunch 沿表退化下一腿——已 spawn 成
    // 功的腿无论退出码都不再换腿，不起二次重试循环。
    // 问密码管线腿硬需要图形会话通道——DISPLAY/WAYLAND_DISPLAY 全缺席（headless）
    // 时 zenity/kdialog 必败，探测期直接不进表（tty 密码提示/NOPASSWD 场景靠裸 sudo）。
    private static ElevationLeg[] PickElevationLegs(string pkg)
    {
        var legs = new List<ElevationLeg>(3);
        if (ResolveCommand("pkexec") is { } pkexec)
        {
            legs.Add(ElevationLeg.Direct(pkexec, "pkexec"));
        }
        var sudo = ResolveCommand("sudo");
        var hasDisplay = GetEnv("DISPLAY") is { Length: > 0 } ||
                         GetEnv("WAYLAND_DISPLAY") is { Length: > 0 };
        if (sudo is not null && hasDisplay)
        {
            var zenity = ResolveCommand("zenity");
            var kdialog = zenity is null ? ResolveCommand("kdialog") : null;
            if (zenity is not null || kdialog is not null)
            {
                legs.Add(ElevationLeg.Askpass(
                    zenity is not null
                        ? [zenity, "--password"]
                        : [kdialog!, "--password", AskpassPrompt],
                    sudo, (zenity is not null ? "zenity" : "kdialog") + "+sudo -S"));
            }
        }
        if (sudo is not null)
        {
            legs.Add(ElevationLeg.Direct(sudo, "sudo", ["--"]));
        }
        if (legs.Count == 0)
        {
            throw new UpdateException(
                "no privilege escalation mechanism available (probed pkexec / zenity+kdialog + sudo / sudo) — " +
                $"cannot replay package '{pkg}'.");
        }
        return legs.ToArray();
    }

    // verb argv 按 §2.3 协议签名逐字：
    //   bundler-updater pkg-apply --pkg <本地包> --sha256 <哈希> --format <deb|rpm|apk>
    //       --app <重启目标> [--env-file <会话env件>] [--rollback] [--log <件>]
    // updaterArgv 已含引导件直跑或 "/bin/sh <script>" 前缀。
    // --sha256 是 root 侧对暂存副本复核的字节指纹：客户端验签（留存副本）或
    // 直读哈希（原件降级路径）算出的值，把"客户端验的字节"钉到"root 装的字节"上。
    // C-N6 消歧：verb --pkg 传本地包文件路径（留存副本优先），与身份字段
    // pkg（包管理器登记名）同名异义——一个指件、一个指名，分层语义。
    // B-N10：argv 全程结构化传递（数组进 JoinArgv 引用、管线腿走 ShJoin 单引号），
    // 提权腿无 shell 注入面；--pkg/--app 均取自规划内路径不接受外部拼词。
    private static string[] BuildVerbArgv(
        string[] updaterArgv, string pkgPath, string? pkgSha256, string format, string app,
        string? envFile, bool rollback, string? logFile)
    {
        var argv = new List<string>();
        argv.AddRange(updaterArgv);
        argv.Add("pkg-apply");
        argv.Add("--pkg");
        argv.Add(pkgPath);
        if (pkgSha256 is { Length: > 0 })
        {
            argv.Add("--sha256");
            argv.Add(pkgSha256);
        }
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
            // 问密码腿为串行 sh 脚本：先 unset 清掉可能继承的同名 export 变量
            //——同名变量已 export 时赋值会保留 export 属性，口令会随环境进
            // sudo 等子进程；清完后捕获 zenity/kdialog 的 stdout 与退出码——
            // 对话框非零（用户取消/失败）一律清变量 exit 126，绝不调 sudo
            //（旧管线 sudo 与对话框并发，sudo 缓存票据会越过取消直接放行）；
            // 对话框成功才把密码经 command printf 管道喂 sudo -S，拿 sudo
            // 退出码清变量原样退出。command 是 POSIX 内建命令（builtin 是 bash
            // 私有、dash/busybox 全无），绕开函数遮蔽仍命中各 sh 内建 printf
            //——密码不外落外部进程 argv/环境变量/磁盘/日志，
            // 也不过本进程内存——$() 捕获在 sh 局部变量里。
            var script =
                "unset password\n" +
                "password=$(" + ShJoin(askpass) + ") || { unset password; exit 126; }\n" +
                "command printf '%s\\n' \"$password\" | " +
                ShJoin([leg.SudoPath!, "-S", "-p", "", "--", .. verbArgv]) + "\n" +
                "rc=$?\n" +
                "unset password\n" +
                "exit \"$rc\"";
            return new SyspkgLaunch
            {
                FileName = "/bin/sh",
                Arguments = ["-c", script],
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
    private static string UserBackupsRoot() =>
        UserBackupsRoot(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
                : OSPlatform.Linux);

    // 跨宿主断言缝：平台作形参直供——单测在任一宿主断言三平台根路径与 XDG 覆盖，
    // 不必虚拟 RuntimeInformation。
    internal static string UserBackupsRoot(OSPlatform platform)
    {
        if (platform == OSPlatform.Windows)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DotNet.Bundler", "backups");
        }
        if (platform == OSPlatform.OSX)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "DotNet.Bundler", "backups");
        }
        var xdg = GetEnv("XDG_DATA_HOME");
        // B-N8：XDG 规范只认绝对路径——相对值按缺席退化默认基址，
        // 不把留存根漂到调用方 cwd 下。
        var dataHome = xdg is { Length: > 0 } && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        return Path.Combine(dataHome, "dotnet-bundler", "backups");
    }

    // 留存本次下载的新包到 retention（按工件名落位）+ 签名件 + applied 标记。
    // 包管理器账本不替用户保留包文件——留存责任在客户端；写不进按 WARN 降级
    // 放行，rollback 腿自有"无留存件"确定性拒绝兜底。
    // B-N2：按工件文件名落位 + overwrite 覆盖——同名异版工件互相塌缩（同名覆盖后
    // 旧件连同其 .sig 一并顶掉）。工件名恒含版本/架构段使碰撞实际罕见，
    // 登记为可接受边界；留档位只增不剪不设上限（§2.2 开放边界）。
    // B-N3：留存失败降级直喂原始 artifactPath——下载位件若在拉起前被清则
    // pkg-apply 无包可装（悬空风险登记可接受）；原路径已验过签但与留存副本
    // 一样存在同用户替换窗，故留存成功时宁可喂副本（再过一次 B2 再验）。
    private static string? TryRetainPackage(
        string artifactPath, string retentionDir, string signature, Action<string>? log)
    {
        try
        {
            Directory.CreateDirectory(retentionDir);
            var retained = Path.Combine(retentionDir, Path.GetFileName(artifactPath));
            File.Copy(artifactPath, retained, overwrite: true);
            // .sig 旁车与包成对留存（feed .sig 同款原始签名字节）——rollback 选件与
            // apply 副本再验都以这对为准；签名写不进按留存整体失败降级。
            File.WriteAllBytes(retained + ".sig", Convert.FromBase64String(signature));
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

    // B2 留存件完整性再核：retention 目录与原件同为用户可写——直喂提权
    // pkg-apply 的候选必须 <name> 配 <name>.sig 成对且对身份公钥验过；
    // 签名缺失/畸形/验不过一律按不可信处理（调用方决定跳选还是拒绝）。
    // 验签同趟带出内容 sha256（VerifyFile out 参单次读取一并算）——哈希经
    // --sha256 交到 root 进程对暂存副本复核，"客户端验的字节"与"root 装的
    // 字节"逐位绑定，验签→root 读取之间的同用户替换窗归零（§2.3 协议）。
    private static bool VerifyRetainedPackage(
        string packagePath, string? publicKey, out string? sha256Hex, Action<string>? log)
    {
        sha256Hex = null;
        try
        {
            var signaturePath = packagePath + ".sig";
            if (publicKey is not { Length: > 0 } || !File.Exists(signaturePath))
            {
                log?.Invoke(
                    $"update: retained package '{packagePath}' rejected — " +
                    "missing identity key or .sig sidecar");
                return false;
            }
            if (!UpdateSignatureVerifier.VerifyFile(
                    packagePath, File.ReadAllBytes(signaturePath),
                    UpdateKeyMaterial.FromPublicPoint(publicKey), out var hash))
            {
                log?.Invoke(
                    $"update: retained package '{packagePath}' rejected — " +
                    "signature does not verify");
                return false;
            }
            sha256Hex = Sha256ToHex(hash);
            return true;
        }
        catch (Exception exception)
        {
            // 失败原因入日志：权限错/IO 错与伪造签不可分辨会把排障推向猜——
            // 拒绝结论不变，但拒绝理由必须是可观测的一等事实。
            log?.Invoke(
                $"update: retained package '{packagePath}' rejected — {exception.Message}");
            return false;
        }
    }

    // 原件降级腿的同趟验签+哈希：signature 是清单上行的 base64 签名（留存 .sig
    // 同源），VerifyFile out 参单次读取验签出指纹——验签→暂存→root 读取链上
    // 任一替换都同时推翻验签断言与哈希钉值。
    private static bool VerifyOriginalPackage(
        string packagePath, string signatureBase64, string? publicKey,
        out string? sha256Hex, Action<string>? log)
    {
        sha256Hex = null;
        try
        {
            if (publicKey is not { Length: > 0 })
            {
                log?.Invoke(
                    $"update: package '{packagePath}' rejected — no identity key");
                return false;
            }
            if (!UpdateSignatureVerifier.VerifyFile(
                    packagePath, Convert.FromBase64String(signatureBase64),
                    UpdateKeyMaterial.FromPublicPoint(publicKey), out var hash))
            {
                log?.Invoke(
                    $"update: package '{packagePath}' rejected — " +
                    "signature does not verify");
                return false;
            }
            sha256Hex = Sha256ToHex(hash);
            return true;
        }
        catch (Exception exception)
        {
            log?.Invoke(
                $"update: package '{packagePath}' rejected — {exception.Message}");
            return false;
        }
    }

    internal static string Sha256ToHex(byte[] hash)
    {
        var builder = new StringBuilder(64);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    // 删留存对（apply 侧失败还原用）：pkg 连同 .sig 一并删，不留孤儿签名件
    // 让同名旧包靠新签名"复活"进候选集。
    private static void TryDeleteRetained(string? retained)
    {
        if (retained is null)
        {
            return;
        }
        TryDeleteFile(retained);
        TryDeleteFile(retained + ".sig");
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

    // applied 标记还原（spawn 失败/验签拒绝时）：previous==null 表示该标记本
    // 不该存在→删；否则写回前值。注意残留语义：同名工件逐次覆盖（无版本号
    // 命名）叠加失败还原时，标记可能指向已被覆盖的留存名——悬空标记使后续
    // rollback 跳选该名，实质损害发生在覆盖当下而非此处，登记边界。
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

    // 回滚选件：retention 内除 applied 标记当前件外、pkg+sig 成对且过签者里
    // 最新落位的候选——首次 Bundler 链更新前/留存缺失/候选全不可信 → null
    //（调用方确定性拒绝不空转）。
    // B-N1 flip-flop 是规格：applied 标记随每次选件翻记，连续两次 rollback
    // 在两个最新可信件间往复——语义即"回到上一个"，不追求全历史栈。
    // B-N9：applied 件同为用户可写——篡改标记只改变选件排除集，引不进未授权包
    //（候选硬门槛是 .sig 成对 + 身份公钥过签，与标记无关）。
    // B2 起无 .sig 的旧留档不再作候选（本仓冻结前无历史包袱，确定性收口）。
    private static string? PickRollbackPackage(
        string retentionDir, string? publicKey, Action<string>? log,
        out string? appliedName, out string? sha256Hex)
    {
        appliedName = ReadAppliedMarker(retentionDir);
        sha256Hex = null;
        try
        {
            if (!Directory.Exists(retentionDir))
            {
                return null;
            }
            string? best = null;
            string? bestHash = null;
            var bestStamp = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(retentionDir))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, AppliedMarkerName, StringComparison.Ordinal) ||
                    name.EndsWith(".sig", StringComparison.Ordinal) ||
                    string.Equals(name, appliedName, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!VerifyRetainedPackage(file, publicKey, out var fileHash, log))
                {
                    log?.Invoke(
                        $"update: skipping retained package '{name}' — " +
                        "missing sidecar signature or verification failed");
                    continue;
                }
                var stamp = File.GetLastWriteTimeUtc(file);
                if (best is null || stamp > bestStamp ||
                    (stamp == bestStamp &&
                     string.CompareOrdinal(name, Path.GetFileName(best)) > 0))
                {
                    best = file;
                    bestHash = fileHash;
                    bestStamp = stamp;
                }
            }
            sha256Hex = bestHash;
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
    // B-N11：剥外层环境是提权器预期行为而非缺陷——会话 env 经文件通道传递
    // 是有意设计（sudo env_reset/pkexec 清环境都拦不住 --env-file 路径参数）。
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
            // B-N6：netstandard2.0 无"建件定 mode"API——WriteAllLines 按进程 umask
            // 建件到下方 chmod 0600 之间存在瞬时较宽权限窗（同用户/同组可读片刻）。
            // 窗内值是会话 env（XAUTHORITY 等，敏感度有限且本就同用户可见），
            // 登记为可接受边界。
            File.WriteAllLines(file, lines);
            var chmodFailed = ChmodProbe is { } chmod
                ? chmod(file) != 0
                : !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Chmod600(file) != 0;
            if (chmodFailed)
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
            // 先删件再删目录（CleanupEnvFile 同序）——非递归删非空目录恒抛，
            // 倒置会把已写出的 env 件按默认 umask 残留。
            TryDeleteFile(file);
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

    /// <summary>
    /// 提权拉起规格：首选腿启动文件/argv/待清理会话 env 件 + 退化腿表 +
    /// 规划期状态件还原上下文——纯数据无 spawn。
    /// </summary>
    internal sealed class SyspkgLaunch
    {
        public string FileName = "";
        public string[] Arguments = [];
        public string? EnvFile;
        public string? EnvFileDirectory;
        /// <summary>运行时退化腿：首选腿 spawn 抛异常（Process.Start 未起成）时依序补位；
        /// 已 spawn 成功的腿无论退出码均不换腿、不重放。</summary>
        public SyspkgLaunch[] Fallbacks = [];
        /// <summary>B1 还原上下文之一：retention 目录（applied 标记所在）。</summary>
        public string? RetentionDir;
        /// <summary>B1 还原上下文之二：本轮翻记前的 applied 标记值（null=原无标记）。</summary>
        public string? AppliedBefore;
        /// <summary>B1 还原上下文之三：apply 侧本轮新留存件（连 .sig 一并删）；
        /// rollback 侧恒 null——所选旧包是既有件绝不能删。</summary>
        public string? RetainedFile;
    }

    // 拉起面：沿探测腿表逐腿拉起——单腿 spawn 抛（探测后工具被删/执行位丢失，
    // Process.Start 未起成）才退化下一腿；一旦 spawn 返回有效句柄该腿即终局：
    // 观察窗内任意退出码（0/3/4/5/6/126/127/其他）或窗外退出都只做退出码分类
    // 留痕+会话 env 删余，原句柄交还调用方——授权取消/拒绝/未知鉴权失败一律停，
    // 已执行安装器不重放（用户需其他方式时显式重试）。
    // applied 标记/留存件按"最近交付工件"语义保留（诊断与回滚用，不代表安装
    // 成功——取消也不是安装成功），不为 126/127 加迟到的异步还原。
    // 全腿皆 spawn 失败才清场透传：env 件删余 + applied 标记回记规划前值 +
    // apply 侧新留存件连 .sig 一并删（B1——不留"标记指新件而装未成"的歪斜现场，
    // 否则下次 rollback 会把从未装上的新件排除在旧包候选外）。
    // 已拉起句柄做两件尽力事——会话 env 件删余（引导件读毕即删为主，
    // 此钩与下次 apply 的 24h staging 清扫是两道兜底）+ 退出码分类留痕
    //（拉起即退语义下 rc 主要靠 --log 件；活到读码的调用方也得分类口径）。
    // 存活腿挂 Exited 钩（ThreadPool 异步触发；本进程先退则钩不触发）；
    // 观察窗内已退出（任意 rc）的已终结进程不保证派发 Exited——两件直接内联做。
    private static Process StartLaunch(SyspkgLaunch launch, Action<string>? log)
    {
        var legs = new List<SyspkgLaunch> { launch };
        legs.AddRange(launch.Fallbacks);
        Exception? spawnFailure = null;
        var notes = new List<string>();
        foreach (var leg in legs)
        {
            Process? process;
            try
            {
                var startInfo = new ProcessStartInfo(leg.FileName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // 继承 stdio：裸 sudo 的 tty 密码提示与 pkexec/zenity 的 GUI 通道都靠它。
                    Arguments = JoinArgv(leg.Arguments),
                };
                process = SpawnProbe is { } spawn
                    ? spawn(startInfo)
                    : Process.Start(startInfo)
                        ?? throw new UpdateException($"failed to start '{leg.FileName}'.");
                if (process is null)
                {
                    throw new UpdateException($"failed to start '{leg.FileName}'.");
                }
            }
            catch (Exception exception)
            {
                // 单腿失败不还原状态件——还有后续腿；还原口径在函数尾统一执行。
                spawnFailure = exception;
                notes.Add($"{leg.FileName}: spawn failed ({exception.Message})");
                log?.Invoke(
                    $"update: elevation leg '{leg.FileName}' failed to spawn " +
                    $"({exception.Message}) — trying next leg");
                continue;
            }
            var envFile = leg.EnvFile;
            var envDir = leg.EnvFileDirectory;
            // 拉起后退出的两件尽力事：退出码分类留痕 + env 件删余。
            void OnLegExited()
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
            }
            if (ProcessWait.WaitForExit(process, LaunchObserveMilliseconds))
            {
                // 窗内已退出（任意 rc，含授权取消/拒绝/鉴权失败）=该腿终局：
                // 已终结进程不保证派发 Exited（POSIX 已 reap 路径实测不派发）——
                // 退出码留痕与 env 件清理必须内联做，否则 session.env 残留磁盘；
                // 非零不换腿，原句柄交还调用方观察退出码。
                OnLegExited();
                return process;
            }
            // WaitForExit/EnableRaisingEvents 在 spawn try-catch 之外：此处抛异常会
            // 越过退化与函数尾还原——生产 Process.Start 产物上实际不可达，仅
            // SpawnProbe 测试缝误用可触发（登记边界，不为此加 try 扰动主链）。
            process.Exited += (_, _) => OnLegExited();
            process.EnableRaisingEvents = true;
            return process;
        }
        CleanupEnvFile(launch.EnvFile, launch.EnvFileDirectory);
        if (launch.RetentionDir is { } retentionDir)
        {
            RestoreAppliedMarker(retentionDir, launch.AppliedBefore);
        }
        TryDeleteRetained(launch.RetainedFile);
        // spawnFailure 是最后一条腿的 spawn 异常——异常主信息里并入 notes 全腿
        // spawn 失败明细（能走到这里说明每腿都是 spawn 前就抛了）。
        if (spawnFailure is not null)
        {
            throw new UpdateException(
                $"no privilege escalation leg could launch pkg-apply: {spawnFailure.Message} " +
                $"({string.Join("; ", notes)}).",
                spawnFailure);
        }
        throw new UpdateException(
            $"no privilege escalation leg could launch pkg-apply ({string.Join("; ", notes)}).");
    }

    // pkg-apply 退出码语义（§2.3）：0 成功；3 参数错；4 安装失败；5 包管理器缺席；
    // 6 重启失败（安装已提交）。126/127 属提权器自身码——126 是用户取消或问密码
    // 提示失败（pkexec 取消/问密码腿对话框失败归一值），127 是鉴权/拉起失败
    //（含工具链自身失败，不妄断为"无 agent"）；其余按提权/拉起失败归类。
    internal static string DescribeExitCode(int exitCode) => exitCode switch
    {
        0 => "success",
        3 => "usage error",
        4 => "package install failed",
        5 => "no package manager available",
        6 => "installed; restart failed — relaunch manually",
        126 => "authorization cancelled or password prompt failed",
        127 => "authorization/launcher failure",
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
            if (!ProcessWait.WaitForExit(process, 60_000))
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
            // 抽干收尾要有界：孙进程继承管道写端会让 ReadToEnd 在进程退出后
            // 仍挂等 EOF——输出本不消费（只为防缓冲卡死），超时弃读照报退出码。
            Task.WaitAll(new Task[] { stdout, stderr }, 10_000);
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
    // 覆盖值按引导件同款口径复核（-f -x：存在+普通文件+执行位）——客户端侧无提权
    // 跨越，但流浪 env 不能让三件套/提权链探测对调用方会话造假：一个指向不存在
    // 路径的 BUNDLER_UPDATER_CMD_DPKG 会让"账本归属预检"静默走样成"装不了"的
    // 错误方向，双侧同判才保持单一语义。
    private static string? ResolveCommand(string name)
    {
        var overridePath = GetEnv("BUNDLER_UPDATER_CMD_" + CommandEnvName(name));
        if (overridePath is { Length: > 0 } path && IsExecutableFile(path))
        {
            return path;
        }
        if (CommandProbe is { } probe)
        {
            return probe(name);
        }
        return FindOnPath(name);
    }

    // 覆盖路径执行性判据（PkgApplyPlan.IsExecutableFile 同口径）：存在+普通文件
    //（fifo/目录不算数），POSIX 另要任一执行位；Windows 无执行位面按普通文件即足。
    private static bool IsExecutableFile(string path) =>
        File.Exists(path) && UnixFileTypes.IsRegularFile(path) &&
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || HasExecutePermission(path));

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
            // 相对段会解析到 cwd 下同名件——与引导件 FindOnPath/sh command -v
            // 要求绝对路径命中同口径：PATH 相对段一律跳过，不收 cwd 命运。
            if (!Path.IsPathRooted(dir))
            {
                continue;
            }
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
            else if (File.Exists(candidate) && UnixFileTypes.IsRegularFile(candidate) &&
                     HasExecutePermission(candidate))
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

    // argv 拼写委托 UpdateArgv（Core/Update 同源链接件）——CommandLineToArgvW
    // 转义与 POSIX 单引号引用全链单源：客户端 spawn/Exec、问密码管线腿、
    // 引导件 su -c 串同算法，任何一处演化不漂移。
    internal static string JoinArgv(IEnumerable<string> argv) => UpdateArgv.Join(argv);

    private static string ShJoin(IEnumerable<string> argv) => UpdateArgv.ShJoin(argv);
}
