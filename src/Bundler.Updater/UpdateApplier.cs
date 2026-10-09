using System.Diagnostics;
using System.Runtime.InteropServices;
using DotNet.Bundler.Updater.Protocol;

namespace DotNet.Bundler.Updater;

/// <summary>
/// 应用面分派：同格式同通道两大语义——
///   installer-replay：nsis `/UPDATE`（被动默认）/ msiexec major upgrade；
///   file-swap：zip/targz 解包 → 引导程序三段式换包；appimage 单件视为目录内文件同款。
/// 引导件优先取注入安装目录的 `bundler-updater[.exe]`，缺失退 `bundler-updater.sh`；
/// 均先复制到临时目录再启动——引导件自身不能被它要换的目录锁死（Windows）。
/// </summary>
internal sealed class UpdateApplier
{
    internal static Process Apply(
        UpdateFeedArtifact artifact, string artifactPath,
        string installDirectory, ApplyOptions options, Action<string>? log)
    {
        switch (artifact.Format)
        {
            case "nsis":
                return StartReplay(artifactPath,
                    options.SilentInstaller ? "/UPDATE /S" : "/UPDATE", log);
            case "msi":
                return StartReplay(
                    ResolveMsiexec(),
                    "/i \"" + artifactPath + "\" " + (options.SilentInstaller ? "/qn" : "/passive"),
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
    /// 备份位置与换包时的保留决策一致：开启保留指数据区目录，未开启指兄弟位瞬备（多半已删→拒绝）。</summary>
    internal static Process Rollback(
        string installDirectory, ApplyOptions options, Action<string>? log)
    {
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
        var runDir = Path.Combine(
            options.StagingRoot, "bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        var arguments =
            "apply --install-dir \"" + installDirectory + "\"" +
            (payloadDirectory is { Length: > 0 } payload ? " --payload \"" + payload + "\"" : "") +
            (rollback ? " --rollback" : "") +
            (backupDirectory is { Length: > 0 } bd ? " --backup-dir \"" + bd + "\"" : "") +
            (!rollback && options.KeepRollbackBackup
                ? " --retain-backup-to \"" + ResolveRetentionDirectory(installDirectory, options) + "\"" : "") +
            (options.WaitPid is { } pid ? " --wait-pid " + pid : "") +
            // --app 必须在客户端 cwd 绝对化——引导件以临时目录为工作目录，相对路径会静默解析失败。
            (options.AppPath is { Length: > 0 } app ? " --app \"" + Path.GetFullPath(app) + "\"" : "") +
            (options.KeepPayload ? " --keep-payload" : "") +
            // --log 同理按客户端 cwd 绝对化——相对路径会落进引导件临时目录随清理丢失。
            (options.LogFile is { Length: > 0 } lf ? " --log \"" + Path.GetFullPath(lf) + "\"" : "");

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
            return Start("/bin/sh", "\"" + staged + "\" " + arguments, runDir, shellExecute: false);
        }
        throw new UpdateException(
            "no bootstrapper found (bundler-updater[.exe]/bundler-updater.sh) — " +
            "package was not built with update support.");
    }

    // per-RID 二进制优先；次 posix 脚本降级件。
    private static string? ResolveBootstrapper(string? overridePath, string installDirectory)
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

    private static string? ResolveBootstrapperScript(string? overridePath, string installDirectory)
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

    private static string PrepareStaging(ApplyOptions options)
    {
        var staging = Path.Combine(options.StagingRoot, "payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        return staging;
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
        var name = Path.GetFileName(full.TrimEnd('\\', '/'));
        var hashBytes = System.Security.Cryptography.SHA256.Create()
            .ComputeHash(System.Text.Encoding.UTF8.GetBytes(full));
        var hex = new System.Text.StringBuilder(8);
        for (var i = 0; i < 4; i++)
        {
            hex.Append(hashBytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return Path.Combine(RetentionRoot(full), name + "-" + hex);
    }

    private static string RetentionRoot(string installPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var machine = installPath.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase);
            return Path.Combine(
                Environment.GetFolderPath(machine
                    ? Environment.SpecialFolder.CommonApplicationData
                    : Environment.SpecialFolder.LocalApplicationData),
                "DotNet.Bundler", "backups");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var machine = installPath.StartsWith("/Applications", StringComparison.Ordinal) ||
                          installPath.StartsWith("/Library", StringComparison.Ordinal);
            var root = machine
                ? "/Library/Application Support"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support");
            return Path.Combine(root, "DotNet.Bundler", "backups");
        }
        var machineLinux = installPath.StartsWith("/opt", StringComparison.Ordinal) ||
                           installPath.StartsWith("/usr", StringComparison.Ordinal) ||
                           installPath.StartsWith("/snap", StringComparison.Ordinal);
        if (machineLinux)
        {
            return Path.Combine("/var/lib", "dotnet-bundler", "backups");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(
            xdg is { Length: > 0 } ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "dotnet-bundler", "backups");
    }

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
        return Process.Start(startInfo)
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
            using var process = Process.Start(new ProcessStartInfo("/bin/chmod", "+x \"" + path + "\"")
            { UseShellExecute = false });
            process?.WaitForExit(10_000);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>应用参数：等谁退出、重启哪个、引导件/暂存/日志覆盖点。</summary>
public sealed class ApplyOptions
{
    /// <summary>引导程序的等待对象——通常是发起更新的当前进程。</summary>
    public int? WaitPid { get; init; }
    /// <summary>换包完成后重启的应用路径（.app 目录件在 mac 走 open）。</summary>
    public string? AppPath { get; init; }
    /// <summary>安装器式更新是否静默（nsis /S、msiexec /qn）。</summary>
    public bool SilentInstaller { get; init; }
    /// <summary>保留暂存载荷（默认换包后清掉）。</summary>
    public bool KeepPayload { get; init; }
    /// <summary>换包成功后保留回滚备份（默认 false 不保留——备份仅作换包期崩溃恢复载体）。</summary>
    public bool KeepRollbackBackup { get; init; }
    /// <summary>回滚备份显式目录——默认按安装层级解析到用户/机器数据目录下的 DotNet.Bundler/backups。</summary>
    public string? RollbackBackupDirectory { get; init; }
    /// <summary>引导件显式路径——默认探测安装目录内注入件。</summary>
    public string? BootstrapperPath { get; init; }
    /// <summary>暂存根——默认系统临时目录下的 bundler-update。</summary>
    public string StagingRoot { get; init; } =
        Path.Combine(Path.GetTempPath(), "bundler-update");
    /// <summary>引导程序日志文件。</summary>
    public string? LogFile { get; init; }
}
