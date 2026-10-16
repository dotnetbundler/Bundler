using System.Diagnostics;
using System.Text;

namespace DotNet.Bundler.Updater.Bootstrap;

/// <summary>
/// <c>pkg-apply</c> 动词：deb/rpm/apk 本地系统包的特权安装执行体。
/// 由客户端经 pkexec/sudo 以 root 拉起，动词本体只做两件事：
/// <list type="number">
/// <item>按前端退化链调包管理器装本地包——包管理器账本即状态，
/// 不引入三段式搬移/marker/锁/崩溃恢复，也不在引导件重复验签（客户端已验）。</item>
/// <item>装成功后降权重启 <c>--app</c>：<c>PKEXEC_UID</c>→<c>SUDO_UID</c> 取原用户
///（都不在=本来就 root 直跑，按当前权限直接 spawn），
/// setpriv → sudo → runuser → su 退化链降权，回灌 <c>--env-file</c> 会话环境
///（件是一次性输入——读毕即删件与 <c>pkg-*</c> 暂存目录，装失败不消费也删）。</item>
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
        if (!IsRegularFile(pkg))
        {
            throw new UsageException($"package '{options.PackagePath}' does not exist.");
        }

        try
        {
            var attempted = false;
            foreach (var step in InstallChain(options, pkg))
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
                    return Restart(app, options, log) ? 0 : 6;
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
            // --env-file 是客户端 <staging>/pkg-<guid>/session.env 一次性输入——
            // 消费完（或装失败确定不消费）即删件与 pkg-* 暂存目录；客户端拉起即退
            // 后删不了，残件兜底在客户端 Exited 钩与 24h staging 清扫。
            CleanupEnvFile(options.EnvFile);
        }
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
                     "-o", "Dpkg::Options::=--force-confold", pkg],
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
    private static bool Restart(string app, PkgApplyOptions options, Action<string> log)
    {
        if (!AppLaunchable(app))
        {
            Console.Error.WriteLine(
                $"bundler-updater: restart target '{app}' is missing or not executable.");
            return false;
        }
        var workingDir = Path.GetDirectoryName(app) ?? ".";
        var uid = ParseUid(GetEnv("PKEXEC_UID")) ?? ParseUid(GetEnv("SUDO_UID"));
        var passwd = uid is { } id ? LookupPasswd(id) : null;
        var envPairs = LoadEnvPairs(options.EnvFile, uid, passwd, log);

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
        if ((tool = ResolveCommand("setpriv")) is not null &&
            TrySpawn(nohup,
                [tool, $"--reuid={uid.Value}", $"--regid={gid}", "--clear-groups",
                 "env", .. envPairs, app],
                workingDir, log))
        {
            return true;
        }
        // sudo env_reset 会剥外层环境——env 回灌只能在降权后内层 env 上重建。
        if ((tool = ResolveCommand("sudo")) is not null &&
            TrySpawn(nohup, [tool, "-u", $"#{uid.Value}", "env", .. envPairs, app],
                workingDir, log))
        {
            return true;
        }
        // runuser/su 只收用户名不收 uid——passwd 查名，查不到这两条腿不可用。
        if (passwd?.Name is { Length: > 0 } name)
        {
            if ((tool = ResolveCommand("runuser")) is not null &&
                TrySpawn(nohup, [tool, "-u", name, "--", "env", .. envPairs, app],
                    workingDir, log))
            {
                return true;
            }
            if ((tool = ResolveCommand("su")) is not null &&
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

    private static string ShQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    // 会话 env 回灌：env 件逐行收 KEY=value（键必须 [A-Za-z_][A-Za-z0-9_]*，
    // 坏行静默跳过——与 sh 支路同款过滤）；件缺省/非普通文件/不可读按最小集
    // HOME+DISPLAY 处理并记日志。解析出零有效对同样退化最小集——空对内层
    // `env` 零覆盖，会把调用方全套 env（pkexec 语境=root env）原样灌给降权 app。
    private static List<string> LoadEnvPairs(
        string? envFile, long? uid, PasswdEntry? passwd, Action<string> log)
    {
        if (envFile is not null)
        {
            try
            {
                // -f 同口径先判普通文件：fifo 上 ReadLines 会挂等写端——
                // sh -f 判拒同走"读失败"WARN。
                if (!IsRegularFile(envFile))
                {
                    throw new IOException("not a regular file");
                }
                var pairs = new List<string>();
                foreach (var line in File.ReadLines(envFile))
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
                log($"bundler-updater: WARN env file '{envFile}' has no usable assignments — minimal session env");
            }
            catch (Exception)
            {
                log($"bundler-updater: WARN cannot read env file '{envFile}' — minimal session env");
            }
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
    // S_IFMT=S_IFREG 走 UnixFileTypes（打包 staging 同源链接件）判，其"无法
    // 判定放行"语义使 stat 缺席宿主退化 File.Exists 口径。
    private static bool IsRegularFile(string path) =>
        File.Exists(path) && UnixFileTypes.IsRegularFile(path);

    // command -v 存在性预检：BUNDLER_UPDATER_CMD_<TOOL> env 覆盖先行
    //（双侧同名测试缝——设了即视为存在并用该路径），否则 PATH 实查。
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
            else if (File.Exists(candidate) && HasExecuteBit(candidate))
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
            using var process = Process.Start(BuildStartInfo(spec))!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception)
        {
            // command -v 与 exec 之间的竞态（二进制被摘走）按"该级失败"退化下一级。
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
            StartDetached(spec);
            return true;
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
    private static void StartDetached(ExecSpec spec)
    {
        if (OperatingSystem.IsWindows())
        {
            var startInfo = BuildStartInfo(spec);
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            var process = Process.Start(startInfo)!;
            process.StandardInput.Dispose();
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            return;
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
        Process.Start(BuildStartInfo(wrapped));
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
                case "--format": options.Format = value; break;
                case "--app": options.AppPath = value; break;
                case "--env-file": options.EnvFile = value; break;
                case "--log": options.LogFile = value; break;
                default: throw new UsageException($"unknown option '{name}'.");
            }
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
        writer.WriteLine("      [--env-file <file>] [--rollback] [--log <file>]");
    }
}

internal sealed class PkgApplyOptions
{
    public string PackagePath = "";
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
