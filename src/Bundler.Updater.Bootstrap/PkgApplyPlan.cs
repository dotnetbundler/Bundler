using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DotNet.Bundler.Updater.Protocol;

namespace DotNet.Bundler.Updater.Bootstrap;

/// <summary>
/// <c>pkg-apply</c> 动词：deb/rpm/apk 本地系统包的特权安装执行体。
/// 由客户端经 pkexec/sudo 以 root 拉起，动词本体只做两件事：
/// <list type="number">
/// <item>按前端退化链调包管理器装本地包——包管理器账本即状态，
/// 不引入三段式搬移/marker/锁/崩溃恢复，也不在引导件重复验签（客户端已验）。</item>
/// <item>装成功后降权重启 <c>--app</c>：<c>PKEXEC_UID</c>→<c>SUDO_UID</c> 取原用户
///（都不在=本来就 root 直跑，按当前权限直接 spawn），
/// setpriv → sudo → runuser → su 退化链降权，回灌 <c>--env-file</c> 会话环境。
/// env 件须是调用者所有的可读普通件（open+fstat 三重校验，不过归 rc3 拒绝），
/// 且是一次性输入——读毕即删件与 <c>pkg-*</c> 暂存目录，装失败不消费也删。</item>
/// </list>
/// 退出码：0 成功；3 参数错；4 安装失败（在场前端全败）；5 包管理器全缺席；
/// 6 重启失败（安装已提交——客户端据此报"已装待手动重启"）。
/// 与 tools/posix/bundler-updater.sh 的 pkg-apply 支路逐字同协议同退出码。
/// </summary>
internal static class PkgApplyPlan
{
    // 测试缝：command -v 存在性/解析——置非 null 即权威（返回 null=命令缺席，
    // 不再回退 PATH）；生产为 null 走 env 覆盖 → PATH 实查。
    internal static Func<string, string?>? CommandProbe;

    // 测试缝：环境变量读取——置非 null 即权威（PKEXEC_UID/SUDO_UID/SUDO_GID/
    // BUNDLER_UPDATER_* 全部经此），生产为 null 走 Environment。
    internal static Func<string, string?>? EnvProbe;

    // 测试缝：同步执行（包管理器安装命令）——置非 null 即不真 spawn，
    // 由测试记录 argv/env 并返回假退出码。
    internal static Func<ExecSpec, int>? ExecProbe;

    // 测试缝：分离 spawn（重启腿）——置非 null 即不真 spawn，返回成败。
    internal static Func<ExecSpec, bool>? SpawnProbe;

    internal static int Run(string[] args)
    {
        PkgApplyOptions options;
        try
        {
            options = Parse(args);
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine($"bundler-updater: {exception.Message}");
            PrintUsage(Console.Error);
            return 3;
        }
        // 日志路径同 apply 口径：建委托前按本进程 cwd 绝对化。
        var logFile = options.LogFile is { Length: > 0 } lf ? Path.GetFullPath(lf) : null;
        var log = BootstrapPlan.CreateLog(logFile);
        try
        {
            return Apply(options, log);
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine($"bundler-updater: {exception.Message}");
            PrintUsage(Console.Error);
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"bundler-updater: {exception.Message}");
            return 4;
        }
    }

    internal static int Apply(PkgApplyOptions options, Action<string> log)
    {
        // 重启目标按本进程 cwd 绝对化——相对路径在特权 exec 语境里静默错位。
        var pkg = Path.GetFullPath(options.PackagePath);
        var app = Path.GetFullPath(options.AppPath);
        // -f 同口径：仅普通文件——fifo/设备/套接件 File.Exists 照收（只排目录），
        // sh -f 判拒同走本拒绝（消息逐字保一致）。
        // 复核登记（N10）：File.Exists/-f 双侧都跟随符号链接——包路径属调用方
        // 受托输入（root 本就受托装这包），刻意不做 --env-file 的三重校验：
        // env 件内容越信任边界灌进降权 app 才须硬化，包件本身不越界。
        if (!IsRegularFile(pkg))
        {
            throw new UsageException($"package '{options.PackagePath}' does not exist.");
        }

        // 原用户 uid 前置解析（sh PA_UID 段同位：装包前解好）——env 件属主
        // 复核（st_uid==调用 uid，缺席按本进程 euid）与重启降权两边都要用。
        // AOT vs sh 缺分流项：uid 解析在进程内做不存在"解析器缺席"态——
        // sh 侧 awk 缺席走 PA_UID_FAIL→拒重启（rc6）的分流在 AOT 天然无对应物。
        var uid = ParseUid(GetEnv("PKEXEC_UID")) ?? ParseUid(GetEnv("SUDO_UID"));
        var passwd = uid is { } id ? LookupPasswd(id) : null;
        // SIGKILL 留下的上轮暂存目兜底清扫（finally/EXIT trap 管不到强杀路径）。
        SweepStaleStageDirs();
        var staged = "";
        try
        {
            // --env-file 校验+读定在装包前（sh _pa_env_validate 同位，rc3 拒绝
            // 口径）：显式给了就必须是"调用者所有的可读普通件"——O_NOFOLLOW
            // open + fstat S_IFREG + st_uid 三重收口，任不过抛 UsageException
            // 归 rc3，绝不静默降级成"件缺席"：旧判存/open 之间窗内 fifo 可
            // 挂死 root 进程、symlink 可把 root 私有件内容灌进降权 app 的 env
            //（用户经 /proc/<app>/environ 读回）。
            var envLines = ReadEnvFile(options.EnvFile, uid);
            // root 侧核验+暂存（客户端验签→root 读件 TOCTOU 收口）：包先复制进
            // root 私有临时目录，--sha256 给了就对暂存副本复核——客户端验签算出的
            // 哈希与 root 实际要装的字节逐位绑定，验后替换窗归零；装期间原路径被
            // 改写也影响不了本轮回放（装的是快照非引用）。
            staged = StagePackage(pkg, options.PackageSha256, log);
            var attempted = false;
            foreach (var step in InstallChain(options, staged))
            {
                var tool = ResolveCommand(step.Tool);
                if (tool is null)
                {
                    log($"bundler-updater: {step.Tool} not found — skipping");
                    continue;
                }
                attempted = true;
                log($"bundler-updater: install '{pkg}' via {step.Tool}");
                var rc = Exec(new ExecSpec
                {
                    FileName = tool,
                    Arguments = step.Arguments,
                    Environment = step.Environment,
                });
                if (rc == 0)
                {
                    return Restart(app, options, uid, passwd, envLines, log) ? 0 : 6;
                }
                log($"bundler-updater: {step.Tool} failed (exit {rc}) — falling back");
            }
            if (!attempted)
            {
                Console.Error.WriteLine(
                    $"bundler-updater: no package manager available for format '{options.Format}'.");
                return 5;
            }
            Console.Error.WriteLine("bundler-updater: package install failed via all available frontends.");
            return 4;
        }
        finally
        {
            // 暂存副本随进程回收——成功/失败/拒绝路径全清（sh _pa_stage_cleanup 同义）。
            if (staged.Length > 0)
            {
                CleanupStage(Path.GetDirectoryName(staged)!);
            }
            // --env-file 是客户端 <staging>/pkg-<guid>/session.env 一次性输入——
            // 消费完（或装失败确定不消费）即删件与 pkg-* 暂存目录；客户端拉起即退
            // 后删不了，残件兜底在客户端拉起腿退出删余（Exited 钩/速退内联）与 24h staging 清扫。
            CleanupEnvFile(options.EnvFile);
        }
    }

    // 暂存到 root 私有目录 + --sha256 复核（sh pkg_apply 暂存段同义）：
    // 返回暂存副本路径（其父目录即待清暂存根）。核验失败/复制失败抛异常归 rc4——
    // 字节不符是篡改/腐坏信号，确定性拒绝不降级装原件。
    private static string StagePackage(string pkg, string? expectedHex, Action<string> log)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bundler-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception)
            {
            }
        }
        var staged = Path.Combine(dir, Path.GetFileName(pkg));
        try
        {
            CopyPackage(pkg, staged);
            if (expectedHex is { Length: > 0 })
            {
                var actual = Sha256Hex(staged);
                if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateRejectedException(
                        $"package sha256 mismatch — expected {expectedHex}, staged copy hashed {actual}.");
                }
                log($"bundler-updater: package sha256 verified ({actual.Substring(0, 12)}…)");
            }
            return staged;
        }
        catch
        {
            CleanupStage(dir);
            throw;
        }
    }

    private static void CleanupStage(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    // 判存→open 的类型替换窗收口（与 --env-file 同一威胁形）：包件是用户可写
    // 路径，-f 判存后 open 前被换成 fifo/设备会让 root 进程挂死 open 或无限量
    // 读设备件。POSIX 走 OpenWithDeadline 有界开件（fifo 阻塞 5s 收口）+ fstat
    // 钉住 inode 复核 S_IFREG + 按 fstat 尺寸封顶拷贝——并发 append 流不进本
    // 轮副本（内容篡改另由 --sha256 闸收）。Windows 无同形 fifo 换件面，File.Copy。
    // 残余：NFS/FUSE 挂起读属内核 D-state，两侧（本循环/sh `timeout cp`）都杀
    // 不动——与用户态可控窗不同级，登记为宿主层边界。
    private static void CopyPackage(string source, string staged)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Copy(source, staged);
            return;
        }
        var fd = NativeMethods.OpenWithDeadline(source);
        if (fd == NativeMethods.InvalidFd)
        {
            throw new UpdateRejectedException(
                $"package '{source}' could not be opened for staging.");
        }
        try
        {
            if (!NativeMethods.TryFStat(fd, out var status) ||
                !NativeMethods.IsRegularMode(status))
            {
                throw new UpdateRejectedException(
                    $"package '{source}' is not a regular file — refusing to stage.");
            }
            // 句柄先行包、归属即移交——FileStream ctor 抛时只剩 SafeFileHandle
            // 终结器收口，finally 不再二次 Close 同一 fd 号（号复用窗口免疫）。
            var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
            fd = NativeMethods.InvalidFd;
            using var input = new FileStream(handle, FileAccess.Read);
            using var output = new FileStream(
                staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // 按 fstat 尺寸封顶：读满即停（提前 EOF→sha256 闸收；追加内容不进来）。
            var remaining = status.Size;
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    break;
                }
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            if (fd != NativeMethods.InvalidFd)
            {
                NativeMethods.Close(fd);
            }
        }
    }

    // SIGKILL 残件兜底：EXIT/finally 清不到被强杀的进程——按名+龄清扫本轮
    // 之前的暂存目（默认 24h，客户端 SweepStaleStaging 同龄口径）。root 侧
    // /tmp 跨进程共享，只动 bundler-pkg-* 本契约名。
    private static void SweepStaleStageDirs()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        string tempRoot;
        try
        {
            tempRoot = Path.GetTempPath();
        }
        catch (Exception)
        {
            return;
        }
        string[] dirs;
        try
        {
            // sh 腿建 bundler-pkg.XXXX（mktemp -d）、AOT 建 bundler-pkg-<guid>——
            // 两种本契约名都收（跨实现残件互不漏）。
            dirs = Directory.GetDirectories(tempRoot, "bundler-pkg*")
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name.StartsWith("bundler-pkg-", StringComparison.Ordinal) ||
                           name.StartsWith("bundler-pkg.", StringComparison.Ordinal);
                }).ToArray();
        }
        catch (Exception)
        {
            return;
        }
        foreach (var dir in dirs)
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    // musl 静态 AOT 无 OpenSSL——摘要走纯托管 BundledSha256（sh 侧 _pa_sha256 工具链同义）。
    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return BundledSha256.Hex(stream);
    }

    // --env-file 清理：删件恒做（尽力）；父目录仅本编排契约的 pkg-* 暂存名才删
    //（非递归——非空即弃），外来目录不碰。
    private static void CleanupEnvFile(string? envFile)
    {
        if (envFile is null)
        {
            return;
        }
        try
        {
            File.Delete(envFile);
        }
        catch (Exception)
        {
        }
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(envFile));
            if (dir is not null &&
                Path.GetFileName(dir).StartsWith("pkg-", StringComparison.Ordinal))
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception)
        {
        }
    }

    private sealed record InstallStep(
        string Tool, string[] Arguments, Dictionary<string, string>? Environment);

    // 前端退化链：逐级 command -v 预检，安装失败逐级退化。
    // deb 两级恒带 DEBIAN_FRONTEND=noninteractive 与 --force-confdef/--force-confold——
    // apt-get 不认裸 dpkg 旗标，经 -o Dpkg::Options 透传才成立"两个都带"的语义兼容。
    private static IEnumerable<InstallStep> InstallChain(PkgApplyOptions options, string pkg)
    {
        switch (options.Format)
        {
            case "deb":
                var debEnv = new Dictionary<string, string>
                {
                    ["DEBIAN_FRONTEND"] = "noninteractive",
                };
                yield return new InstallStep("apt-get",
                    ["install", "-y",
                     "-o", "Dpkg::Options::=--force-confdef",
                     "-o", "Dpkg::Options::=--force-confold",
                     // dpkg 锁等待有界：别家包管理操作持锁时 apt-get 内部轮转
                     // 等待是有界的，不叠加本层 Exec 超时才败（sh 侧同款 -o 行）。
                     "-o", "DPkg::Lock::Timeout=120", pkg],
                    debEnv);
                yield return new InstallStep("dpkg",
                    ["-i", "--force-confdef", "--force-confold", pkg],
                    debEnv);
                break;
            case "rpm":
                yield return new InstallStep("dnf", ["install", "-y", pkg], null);
                yield return new InstallStep("zypper", ["install", "-y", pkg], null);
                // --rollback 子形态：rpm 退化链末位换 --oldpackage；
                // deb/apt/apk 同命令重放旧包（回滚工件由客户端留存）。
                yield return new InstallStep("rpm",
                    options.Rollback ? ["-U", "--oldpackage", pkg] : ["-U", pkg],
                    null);
                break;
            default: // "apk"
                yield return new InstallStep("apk", ApkArguments(pkg), null);
                break;
        }
    }

    private static string[] ApkArguments(string pkg)
    {
        // /etc/apk/keys 有预置公钥才免 --allow-untrusted；目录缺席/空一律加。
        // BUNDLER_UPDATER_APK_KEYS_DIR 是双侧同名测试缝（sh 支路同名变量）。
        var keysDir = GetEnv("BUNDLER_UPDATER_APK_KEYS_DIR") ?? "/etc/apk/keys";
        var hasKeys = false;
        try
        {
            hasKeys = Directory.Exists(keysDir)
                && Directory.EnumerateFileSystemEntries(keysDir).Any();
        }
        catch (Exception)
        {
        }
        return hasKeys ? ["add", pkg] : ["add", "--allow-untrusted", pkg];
    }

    // 降权重启：取原用户 uid 后按 setpriv → sudo → runuser → su 退化链
    // 降权拉起 --app；uid 缺席=本来就 root 直跑/非特权直跑，按当前权限 spawn。
    // env 一律经内层 `env K=V … app` 构造（sudo env_reset 会把外层环境剥光），
    // 外层 nohup 分离——引导件退出/终端挂断不该带走新装应用（apply 腿同策略）。
    private static bool Restart(string app, PkgApplyOptions options, long? uid,
        PasswdEntry? passwd, List<string>? envLines, Action<string> log)
    {
        if (!AppLaunchable(app))
        {
            Console.Error.WriteLine(
                $"bundler-updater: restart target '{app}' is missing or not executable.");
            return false;
        }
        var workingDir = Path.GetDirectoryName(app) ?? ".";
        // env 对构建（含"无可用对/无 env 件"WARN）先于 nohup 解析——
        // 与 sh _pa_restart 同序（WARN 序双侧一致）。
        var envPairs = BuildEnvPairs(envLines, options.EnvFile, uid, passwd, log);

        if (OperatingSystem.IsWindows() && SpawnProbe is null)
        {
            // 降权链是 POSIX 构件——Windows 上 pkg-apply 现实不可达（包管理器链必先
            // 失败），保留直 spawn 兜底；探针缝在场时按 POSIX 腿求值，跨宿主同断言。
            log($"bundler-updater: restart '{app}'");
            return Spawn(new ExecSpec
            {
                FileName = app,
                Arguments = [],
                WorkingDirectory = workingDir,
                Environment = ToEnvMap(envPairs),
            });
        }

        var nohup = ResolveCommand("nohup");
        if (nohup is null)
        {
            log("bundler-updater: nohup not found — cannot detach restart");
            return false;
        }
        if (uid is null)
        {
            log($"bundler-updater: restart '{app}'");
            return Spawn(new ExecSpec
            {
                FileName = nohup,
                Arguments = ["env", .. envPairs, app],
                WorkingDirectory = workingDir,
            });
        }

        var gid = ParseUid(GetEnv("SUDO_GID")) ?? passwd?.Gid ?? uid.Value;
        log($"bundler-updater: restart '{app}' as uid {uid.Value}");
        string? tool;
        // 每级先同步预检最小 argv 再分离 spawn：分离只证 nohup 分叉成，证不了
        // 降权器收下本链 argv 形——busybox setpriv 等在 PATH 里却只认
        // capabilities 子集，预检缺席时 spawn"成功"返真、子进程即刻参数错死
        // 在分离层，重启被静默吃掉（rc0 假成功）。预检 rc≠0 如实降级下一级。
        if ((tool = ResolveCommand("setpriv")) is not null &&
            DropProbe(tool,
                [$"--reuid={uid.Value}", $"--regid={gid}", "--clear-groups",
                 "/bin/sh", "-c", ":"], workingDir, log) &&
            TrySpawn(nohup,
                [tool, $"--reuid={uid.Value}", $"--regid={gid}", "--clear-groups",
                 "env", .. envPairs, app],
                workingDir, log))
        {
            return true;
        }
        // sudo env_reset 会剥外层环境——env 回灌只能在降权后内层 env 上重建。
        // 预检加 -n 免密闸：要口令的 sudo 在真实 spawn 里同样败于 EOF，
        // -n 只是把同一结局提前确定（sh 支路同口径）。
        if ((tool = ResolveCommand("sudo")) is not null &&
            DropProbe(tool, ["-n", "-u", $"#{uid.Value}", "/bin/sh", "-c", ":"],
                workingDir, log) &&
            TrySpawn(nohup, [tool, "-u", $"#{uid.Value}", "env", .. envPairs, app],
                workingDir, log))
        {
            return true;
        }
        // runuser/su 只收用户名不收 uid——passwd 查名，查不到这两条腿不可用。
        if (passwd?.Name is { Length: > 0 } name)
        {
            if ((tool = ResolveCommand("runuser")) is not null &&
                DropProbe(tool, ["-u", name, "--", "/bin/sh", "-c", ":"],
                    workingDir, log) &&
                TrySpawn(nohup, [tool, "-u", name, "--", "env", .. envPairs, app],
                    workingDir, log))
            {
                return true;
            }
            if ((tool = ResolveCommand("su")) is not null &&
                DropProbe(tool, ["-s", "/bin/sh", "-c", ":", name],
                    workingDir, log) &&
                TrySpawn(nohup, [tool, "-s", "/bin/sh", "-c", SuCommand(envPairs, app), name],
                    workingDir, log))
            {
                return true;
            }
        }
        log($"bundler-updater: cannot drop privileges to uid {uid.Value} — " +
            "no setpriv/sudo/runuser/su available");
        return false;
    }

    // 降权器可用性=最小 argv 实跑通（同 ExecProbe 缝），三向钉死不透
    // 调用方 stdio；被拒记 WARN 交给退化链下一级。
    private static bool DropProbe(string tool, string[] probeArgv, string workingDir,
        Action<string> log)
    {
        var rc = ExecSilent(new ExecSpec
        {
            FileName = tool,
            Arguments = probeArgv,
            WorkingDirectory = workingDir,
        });
        if (rc == 0)
        {
            return true;
        }
        log($"bundler-updater: WARN drop tool '{Path.GetFileName(tool)}' " +
            $"rejected probe (exit {rc}) — trying next");
        return false;
    }

    // WorkingDirectory 由 Process.Start 同步设定——工作目录缺席/不可进在
    // spawn 点即抛→false→降下一级，与 sh 侧 spawn 前显式 (cd "$_d") 同步
    // 探测同价（sh 是后台子壳 cd 失败被 & 吞需预检，AOT 天然同步拒）。
    private static bool TrySpawn(string nohup, string[] argv, string workingDir, Action<string> log)
    {
        var spawned = Spawn(new ExecSpec
        {
            FileName = nohup,
            Arguments = argv,
            WorkingDirectory = workingDir,
        });
        if (!spawned)
        {
            log($"bundler-updater: WARN spawn via '{argv[0]}' failed — trying next");
        }
        return spawned;
    }

    // su 只走 -c 串——逐 token 单引号转义（与 sh 支路 _pa_shq 同算法），
    // 值里的空格/单引号不透进 shell 解析。
    private static string SuCommand(IReadOnlyList<string> envPairs, string app)
    {
        var command = new StringBuilder("exec env");
        foreach (var pair in envPairs)
        {
            command.Append(' ').Append(ShQuote(pair));
        }
        return command.Append(' ').Append(ShQuote(app)).ToString();
    }

    private static string ShQuote(string value) => UpdateArgv.ShQuote(value);

    // 会话 env 回灌：env 件行集逐行收 KEY=value（键必须 [A-Za-z_][A-Za-z0-9_]*，
    // 坏行静默跳过——与 sh 支路同款过滤）；件缺省按最小集 HOME+DISPLAY 处理并记
    // 日志。解析出零有效对同样退化最小集——空对内层 `env` 零覆盖，会把调用方
    // 全套 env（pkexec 语境=root env）原样灌给降权 app。
    // envLines==null 只在 --env-file 未给时出现——件给了但校验失败走 rc3 在
    // 装包前已拒绝（ReadEnvFile），到不了这里。
    private static List<string> BuildEnvPairs(List<string>? envLines,
        string? envFile, long? uid, PasswdEntry? passwd, Action<string> log)
    {
        if (envLines is not null)
        {
            var pairs = new List<string>();
            foreach (var line in envLines)
            {
                if (IsEnvAssignment(line))
                {
                    pairs.Add(line);
                }
            }
            if (pairs.Count > 0)
            {
                return pairs;
            }
            // 消息用规范化全路径——sh 侧 PA_ENV 同已绝对化，WARN 文案一致。
            log($"bundler-updater: WARN env file '{Path.GetFullPath(envFile!)}' " +
                "has no usable assignments — minimal session env");
        }
        else
        {
            log("bundler-updater: no --env-file — restart uses minimal session env");
        }
        var minimal = new List<string>();
        // 降权场景 HOME 必须是目标用户的家目录（root 的 $HOME 不可回灌）；
        // 直跑场景才按本进程 HOME。
        var home = uid is null ? GetEnv("HOME") : passwd?.Home;
        var display = GetEnv("DISPLAY");
        if (!string.IsNullOrEmpty(home))
        {
            minimal.Add("HOME=" + home);
        }
        if (!string.IsNullOrEmpty(display))
        {
            minimal.Add("DISPLAY=" + display);
        }
        return minimal;
    }

    // --env-file 校验+读定（TOCTOU 收口，rc3 拒绝口径——sh _pa_env_validate 同义）：
    // POSIX 经 libSystem.Native PAL 走 O_RDONLY|O_NOFOLLOW|O_CLOEXEC 打开
    //（PAL_O_NOFOLLOW 由 C 侧转译成各 ABI 原生值），fstat 已开描述符复核
    // S_IFREG + st_uid==调用 uid（uid 缺席按本进程 euid）——三重全过才把同一
    // 描述符包成 FileStream 读。O_NOFOLLOW 让链接偷换直接 ELOOP；PAL 旗标集
    // 不导 O_NONBLOCK，fifo 阻塞窗由带 5s 时限的线程内 open 收口
    //（sh `timeout 5 cat` 同口径——裸 open 阻塞性 fifo 会挂死本进程等写端）。
    // 任一步不过一律抛 UsageException 归 rc3——不静默降级"件不存在"：
    // 件显式给了就按校验输，内容永不进降权 app 的 env。
    private static List<string>? ReadEnvFile(string? envFile, long? uid)
    {
        // 空串按"件未给"处理——与 sh 侧 `-n "$PA_ENV"` 门同口径（否则
        // GetFullPath("") 抛 ArgumentException 会归 rc4 与 sh 分叉）。
        if (string.IsNullOrEmpty(envFile))
        {
            return null;
        }
        // 规范化/读段任一步失败同归 rc3：sh 侧 cat 读败也落 rc3，双侧一致。
        string full;
        try
        {
            full = Path.GetFullPath(envFile);
        }
        catch (Exception)
        {
            throw new UsageException($"--env-file '{envFile}' is not a valid path.");
        }
        using var stream = OpenEnvFile(full, uid);
        if (stream is null)
        {
            throw new UsageException(
                $"--env-file '{full}' is not a readable regular file owned by the calling user.");
        }
        var lines = new List<string>();
        try
        {
            using (var reader = new StreamReader(stream))
            {
                // \r/\r\n/\n 三终结符与 sh `tr '\r' '\n'` 折行同口径
                //（TextReader.ReadLine 把裸 \r 也当终结符）。
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    lines.Add(line);
                }
            }
        }
        catch (Exception)
        {
            throw new UsageException($"--env-file '{full}' could not be read.");
        }
        return lines;
    }

    // 校验开件：POSIX 走 open+fstat 三重（见上）；Windows/pkg-apply 现实不可达的
    // 宿主退 IsRegularFile 口径（stat 同源判据，无 uid 面可复核——
    // 该腿只在测试宿主可触达）。
    private static FileStream? OpenEnvFile(string path, long? callerUid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return IsRegularFile(path)
                    ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
        var fd = NativeMethods.OpenWithDeadline(path);
        if (fd == NativeMethods.InvalidFd)
        {
            return null;
        }
        // fstat 已开描述符——校验对象是 open 钉住的同一 inode，窗内换件骗不过。
        if (!NativeMethods.TryFStat(fd, out var status) ||
            !NativeMethods.IsRegularMode(status) ||
            !NativeMethods.OwnerMatches(status, callerUid))
        {
            NativeMethods.Close(fd);
            return null;
        }
        return new FileStream(
            new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true),
            FileAccess.Read);
    }

    private static bool IsEnvAssignment(string line)
    {
        var equals = line.IndexOf('=');
        if (equals <= 0 || !(char.IsAsciiLetter(line[0]) || line[0] == '_'))
        {
            return false;
        }
        for (var i = 1; i < equals; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(line[i]) || line[i] == '_'))
            {
                return false;
            }
        }
        return true;
    }

    // 与 sh 支路 *[!0-9]* 同口径：只收全数字（uid_t 是 uint32，int 装不下；
    // 空白/符号形拒收——sh 侧模式匹配同样拒）。
    private static long? ParseUid(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        foreach (var c in raw)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }
        return long.TryParse(raw, out var uid) ? uid : null;
    }

    // runuser/su 的用名与 setpriv 的 gid 都出在 passwd：
    // 与 sh 支路 awk -F: 'NF>=7 && $3==u {print $1":"$4":"$6}' 同字段口径。
    // BUNDLER_UPDATER_PASSWD_FILE 是双侧同名测试缝（stub 垫片场景写虚构 passwd）。
    private readonly record struct PasswdEntry(string Name, long? Gid, string? Home);

    private static PasswdEntry? LookupPasswd(long uid)
    {
        var file = GetEnv("BUNDLER_UPDATER_PASSWD_FILE") ?? "/etc/passwd";
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var fields = line.Split(':');
                if (fields.Length >= 7 &&
                    long.TryParse(fields[2], out var entryUid) && entryUid == uid)
                {
                    return new PasswdEntry(fields[0],
                        long.TryParse(fields[3], out var gid) ? gid : null,
                        fields[5]);
                }
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static bool AppLaunchable(string app)
    {
        if (!IsRegularFile(app))
        {
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            return true;
        }
        try
        {
            var mode = File.GetUnixFileMode(app);
            return mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // "仅普通文件"口径（sh -f 同义，顺链同规）：fifo/设备/套接件 File.Exists
    // 照收（只排目录）——BCL 公开面无类型位判据（File.GetAttributes 对特殊件
    // 同报 Normal，GetUnixFileMode 只回权限位，.NET 10/Linux 实测）；stat(2)
    // S_IFMT=S_IFREG 走 UnixFileTypes（打包 staging 同源链接件、PAL stat 判），
    // 其"无法判定放行"语义使 stat 不可达宿主退化 File.Exists 口径。
    private static bool IsRegularFile(string path) =>
        File.Exists(path) && UnixFileTypes.IsRegularFile(path);

    // command -v 存在性预检：BUNDLER_UPDATER_CMD_<TOOL> env 覆盖先行
    //（双侧同名测试缝），否则 CommandProbe/PATH 实查。
    // 覆盖值由 root 直 exec——必须"存在+普通文件+有执行位"才权威（不设防
    // 等于把任意路径提权为权威命令）；不可用覆盖按未设置退化下一解析源，
    // 与 sh _pa_cmd 的 -f/-x 校验同口径。
    // env 不在本解析面：spawn argv 里的内层 `env K=V` 由 nohup 子进程经
    // execvp 按自己的 PATH 解析，从不经本函数——sh 侧原 env) 死支路已删（A4），
    // AOT 本就无对应 case，此处仅登记对齐事实。
    internal static string? ResolveCommand(string name)
    {
        var overridePath = GetEnv("BUNDLER_UPDATER_CMD_" + CommandEnvName(name));
        if (!string.IsNullOrEmpty(overridePath) && IsExecutableFile(overridePath))
        {
            return overridePath;
        }
        if (CommandProbe is { } probe)
        {
            return probe(name);
        }
        return FindOnPath(name);
    }

    // 覆盖路径执行性判据：存在+普通文件（IsRegularFile/stat 同源——fifo/目录
    // 不算数），POSIX 另要任一执行位；Windows 无执行位面按普通文件即足
    //（pkg-apply POSIX 限定腿，该判据只在测试宿主落到 Windows）。
    private static bool IsExecutableFile(string path) =>
        IsRegularFile(path) &&
        (OperatingSystem.IsWindows() || HasExecuteBit(path));

    private static string CommandEnvName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }
        return builder.ToString();
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = GetEnv("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // 空元素已被 RemoveEmptyEntries 剥掉；相对段（PATH="bin:..."）与
            // 空元素同源问题——candidate 会解析到 cwd 下同名件，sh 侧
            // command -v 结果要求 /* 同口径：只收绝对段。
            if (!Path.IsPathRooted(dir))
            {
                continue;
            }
            var candidate = Path.Combine(dir, name);
            if (OperatingSystem.IsWindows())
            {
                var extensions = (GetEnv("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var ext in extensions)
                {
                    if (File.Exists(candidate + ext))
                    {
                        return candidate + ext;
                    }
                }
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            else if (File.Exists(candidate) && IsRegularFile(candidate) &&
                     HasExecuteBit(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool HasExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            var mode = File.GetUnixFileMode(path);
            return mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? GetEnv(string name) =>
        EnvProbe is { } probe ? probe(name) : Environment.GetEnvironmentVariable(name);

    private static int Exec(ExecSpec spec)
    {
        if (ExecProbe is { } probe)
        {
            return probe(spec);
        }
        try
        {
            using var process = ProcessWait.Start(BuildStartInfo(spec))
                ?? throw new InvalidOperationException($"spawn timed out: {spec.FileName}");
            // 安装腿有界：pm 挂死（dpkg 锁竞态外的死锁/前端自身卡死）会把特权
            // 进程拖成永驻——默认 30min，BUNDLER_UPDATER_PKG_TIMEOUT 秒数覆盖
            //（sh 侧 timeout 包裹同名同缝）；超时杀进程返 124（timeout(1) 同码，
            // 对调用方是可分类的"安装腿超时"）。
            if (!ProcessWait.WaitForExit(process, ExecTimeoutMilliseconds()))
            {
                try
                {
                    // 整树杀：apt-get/dnf 的 dpkg/rpm 孙进程不能成孤儿继续持锁——
                    // 双侧超时后都无遗留 pm。善后口径刻意不同：sh `timeout -k 30`
                    // 给组发 TERM+30s 善后窗再 KILL；.NET Process 无 TERM/组信号
                    // 面（子进程与本进程同组，TERM 组发会自尽；TERM 只给直子又会
                    // 让孙进程孤儿化）——AOT 直 SIGKILL 整树是唯一能同时满足
                    // "有界 + 孙进程必死"的形态，善后窗差异登记为已知实现差。
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
                try
                {
                    ProcessWait.WaitForExit(process, 30_000);
                }
                catch (Exception)
                {
                }
                return 124;
            }
            return process.ExitCode;
        }
        catch (Exception)
        {
            // command -v 与 exec 之间的竞态（二进制被摘走）按"该级失败"退化下一级。
            return 127;
        }
    }

    private static int ExecTimeoutMilliseconds()
    {
        var raw = GetEnv("BUNDLER_UPDATER_PKG_TIMEOUT");
        return raw is { Length: > 0 } && int.TryParse(raw, out var seconds) && seconds > 0
            ? (int)Math.Min((long)seconds * 1000, int.MaxValue)
            : 30 * 60 * 1000;
    }

    // 预检用同步执行：stdin 立即钉死（防降权器读调用方终端/管道），
    // out/err 后台抽干到 Null——用法文本、PAM/auth 噪声不透调用方面。
    // 预检 argv 恒为"工具+最小参数+sh -c :"零交互形（stdin 已钉 EOF 无读面、命令
    // 即刻返回），工具自身挂死无现实入口——但进程面仍要有界：macOS CI 实证过
    // .NET Unix spawn/reap 全局锁死锁（子件僵尸不回收、spawn 不返回）；轮询旗标
    // 保底 5min（远超预检真实耗时），spawn 本身同走 60s 界。
    private static int ExecSilent(ExecSpec spec)
    {
        if (ExecProbe is { } probe)
        {
            return probe(spec);
        }
        try
        {
            var startInfo = BuildStartInfo(spec);
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            using var process = ProcessWait.Start(startInfo)
                ?? throw new InvalidOperationException($"spawn timed out: {spec.FileName}");
            process.StandardInput.Dispose();
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            if (!ProcessWait.WaitForExit(process, 5 * 60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                ProcessWait.WaitForExit(process, 10_000);
                return 127;
            }
            return process.ExitCode;
        }
        catch (Exception)
        {
            return 127;
        }
    }

    private static bool Spawn(ExecSpec spec)
    {
        if (SpawnProbe is { } probe)
        {
            return probe(spec);
        }
        try
        {
            return StartDetached(spec);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 分离 spawn 的 stdio 必须与调用方三向断开：pkexec 语境 stdout 常是管道——
    // 分离进程继承写端会让调用方 ReadToEnd 永等 EOF，裸继承还会把目标进程输出
    // 串到调用方面上（sh 支路全腿 `</dev/null >/dev/null 2>&1` 同价）。
    // POSIX 经 /bin/sh `exec "$@"` 包裹把三向钉到 /dev/null——argv 逐参透传
    // 不经 shell 重解析；Windows 兜底腿无 shell 钉法，改接管道：stdin 立即关
    // =对端 EOF，out/err 后台抽干到 Null——子进程不再持有调用方句柄。
    // spawn 超时按 false 上报走退化下一腿——迟到子件可能随后真落地、与退化腿
    // 构成双重拉起的竞态角是刻意接受项：锁死面下次腿 spawn 同样会在界内失败，
    // 失败可观测优先于静默报成（Spawn 报 true 会吞掉整个降级链且无留痕）。
    private static bool StartDetached(ExecSpec spec)
    {
        if (OperatingSystem.IsWindows())
        {
            var startInfo = BuildStartInfo(spec);
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            var process = ProcessWait.Start(startInfo, killLateSpawned: false);
            if (process is null)
            {
                return false;
            }
            process.StandardInput.Dispose();
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            return true;
        }
        var wrapped = new ExecSpec
        {
            FileName = "/bin/sh",
            Arguments =
            [
                "-c", "exec \"$@\" </dev/null >/dev/null 2>&1",
                "bundler-updater-spawn", spec.FileName, .. spec.Arguments,
            ],
            WorkingDirectory = spec.WorkingDirectory,
            Environment = spec.Environment,
        };
        return ProcessWait.Start(BuildStartInfo(wrapped), killLateSpawned: false) is not null;
    }

    private static ProcessStartInfo BuildStartInfo(ExecSpec spec)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (spec.WorkingDirectory is { } workingDirectory)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }
        if (spec.Environment is { } environment)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        return startInfo;
    }

    private static Dictionary<string, string>? ToEnvMap(IReadOnlyList<string> pairs)
    {
        if (pairs.Count == 0)
        {
            return null;
        }
        var map = new Dictionary<string, string>();
        foreach (var pair in pairs)
        {
            map[pair[..pair.IndexOf('=')]] = pair[(pair.IndexOf('=') + 1)..];
        }
        return map;
    }

    private static PkgApplyOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "pkg-apply")
        {
            throw new UsageException("expected the 'pkg-apply' command.");
        }
        var options = new PkgApplyOptions();
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--rollback")
            {
                options.Rollback = true;
                continue;
            }
            var value = i + 1 < args.Length ? args[++i]
                : throw new UsageException($"option '{name}' requires a value.");
            switch (name)
            {
                case "--pkg": options.PackagePath = value; break;
                case "--sha256": options.PackageSha256 = value; break;
                case "--format": options.Format = value; break;
                case "--app": options.AppPath = value; break;
                case "--env-file": options.EnvFile = value; break;
                case "--log": options.LogFile = value; break;
                default: throw new UsageException($"unknown option '{name}'.");
            }
        }
        if (options.PackageSha256 is { Length: > 0 } sha && !IsSha256Hex(sha))
        {
            throw new UsageException("--sha256 must be exactly 64 hexadecimal characters.");
        }
        if (options.PackagePath.Length == 0)
        {
            throw new UsageException("--pkg is required.");
        }
        if (options.AppPath.Length == 0)
        {
            throw new UsageException("--app is required.");
        }
        if (options.Format is not ("deb" or "rpm" or "apk"))
        {
            throw new UsageException("--format must be deb, rpm or apk.");
        }
        return options;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  bundler-updater pkg-apply --pkg <file> --format <deb|rpm|apk> --app <path>");
        writer.WriteLine("      [--sha256 <hex>] [--env-file <file>] [--rollback] [--log <file>]");
    }

    // --sha256 词法校验：64 位十六进制——非该形状按用法错拒（sh 侧同判 rc3）。
    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed class PkgApplyOptions
{
    public string PackagePath = "";
    public string PackageSha256 = "";
    public string Format = "";
    public string AppPath = "";
    public string? EnvFile;
    public string? LogFile;
    public bool Rollback;
}

internal sealed class ExecSpec
{
    public required string FileName { get; init; }
    public required string[] Arguments { get; init; }
    public Dictionary<string, string>? Environment { get; init; }
    public string? WorkingDirectory { get; init; }
}

// --env-file 校验的 POSIX 原生面：libSystem.Native PAL（SystemNative_*）。
// 这些符号链进每个 .NET 进程映像（BCL 文件 IO 全靠它们），静态 musl AOT
// 件里同样可解析——裸 DllImport("libc") 在 static-pie 件中没有 .dynsym
// 可解析目标，P/Invoke 一步都走不到 syscall（syspkg 冻件腿 rc3 实证）。
// PAL 的 FileStatus 布局由 C 侧对各 ABI 归一化（Mode/Uid 槽位定死），
// 内核 struct stat 的架构偏移表与 __fxstat/inode64 退化面整体不需要；
// PAL_O_NOFOLLOW 由 C 侧转译各 ABI 的 O_NOFOLLOW 原生值，旗标表也不存在
//（arm 紧凑表/generic 表之分消失在 PAL 内部）。
internal static class NativeMethods
{
    // PAL OpenFlags（pal_io.h 定值，与 POSIX 数值无关的固定中间值）：
    // PAL_O_RDONLY=0x0000 | PAL_O_CLOEXEC=0x0010 | PAL_O_NOFOLLOW=0x0200。
    // PAL 旗标集不导 O_NONBLOCK——fifo 阻塞窗由 OpenWithDeadline 的
    // 5s 线程内 open 收口（sh `timeout 5 cat` 同口径）。
    internal const int EnvOpenFlags = 0x0010 | 0x0200;
    internal const int OpenDeadlineMs = 5000;
    internal static readonly IntPtr InvalidFd = new IntPtr(-1);

    private const int TypeMask = 0xF000;    // PAL_S_IFMT
    private const int RegularFile = 0x8000; // PAL_S_IFREG

    // pal_io.h FileStatus 的同形镜像——PAL 归一化布局，Mode@4/Uid@8 定死。
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStatus
    {
        internal int Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Dev;
        internal long RDev;
        internal long Ino;
        internal uint UserFlags;
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr SysOpen(string path, int flags, int mode);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Close", SetLastError = true)]
    private static extern int SysClose(IntPtr fd);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int SysFStat(IntPtr fd, out FileStatus output);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_GetEUid")]
    private static extern uint SysGetEUid();

    // env 件 open：PAL 无 O_NONBLOCK 旗标，fifo 阻塞窗用带时限的后台线程
    // 收口（sh `timeout 5 cat` 同口径 5s）——超时按 InvalidFd 拒；被弃
    // 线程仍卡在 open，随短命引导进程退出收尸。P/Invoke 解析失败（静态
    // 件理论不可达 PAL、或符号缺失）一律 InvalidFd——fail closed。
    internal static IntPtr OpenWithDeadline(string path)
    {
        var fd = InvalidFd;
        // gate 不 using：超时被弃的线程若在 gate 释放后解除阻塞，
        // finally 对已 Dispose 的 MRES 抛 ObjectDisposedException——
        // 后台线程未处理异常会带走整个进程（干净 rc3 变 crash）。
        // 不 Dispose 由 GC 收，无句柄面。
        var gate = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                fd = SysOpen(path, EnvOpenFlags, 0);
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    gate.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        })
        { IsBackground = true };
        try
        {
            worker.Start();
        }
        catch (Exception)
        {
            // 线程起不来（极端）按校验失败拒——不冒泡成 rc4。
            return InvalidFd;
        }
        return gate.Wait(OpenDeadlineMs) ? fd : InvalidFd;
    }

    internal static void Close(IntPtr fd)
    {
        try
        {
            SysClose(fd);
        }
        catch (Exception)
        {
        }
    }

    // fstat 失败一律 false——无法复核 inode 属性即按校验失败拒（fail closed，
    // 与 UnixFileTypes"无法判定放行"口径刻意不同：这里是安全边界不是拷贝过滤）。
    internal static bool TryFStat(IntPtr fd, out FileStatus status)
    {
        try
        {
            return SysFStat(fd, out status) == 0;
        }
        catch (Exception)
        {
            status = default;
            return false;
        }
    }

    internal static bool IsRegularMode(FileStatus status) =>
        (status.Mode & TypeMask) == RegularFile;

    // st_uid 复核：期望 uid=已解析调用 uid，缺席按本进程 euid——
    // geteuid 拿不到（不该发生）按校验失败拒。
    internal static bool OwnerMatches(FileStatus status, long? callerUid)
    {
        long expected;
        if (callerUid is { } uid)
        {
            expected = uid;
        }
        else
        {
            try
            {
                expected = SysGetEUid();
            }
            catch (Exception)
            {
                return false;
            }
        }
        return status.Uid == expected;
    }
}
