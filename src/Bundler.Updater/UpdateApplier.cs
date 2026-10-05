using System.Diagnostics;
using System.IO.Compression;
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
                // 单文件载荷：暂存为 {file} 目录 → 与目录换包同一通道。
                var staging = PrepareStaging(options);
                var staged = Path.Combine(staging, Path.GetFileName(artifactPath));
                File.Copy(artifactPath, staged, overwrite: true);
                MakeExecutable(staged);
                return RunBootstrapper(staging, installDirectory, options, log);
            }
            default:
                throw new UpdateException($"format '{artifact.Format}' has no apply semantics.");
        }
    }

    /// <summary>回滚钩子：引导程序 `--rollback` 模式——备份复制回安装目录且不二次备份。</summary>
    internal static Process Rollback(
        string installDirectory, ApplyOptions options, Action<string>? log)
    {
        var backup = installDirectory.TrimEnd('/', '\\') + ".bundler-backup";
        log?.Invoke("update: rolling back to '" + backup + "'");
        return RunBootstrapper(null, installDirectory, options, log, rollback: true);
    }

    // 引导程序 = 换包执行体；从安装目录/包内取件复制到临时目录执行，防自锁。
    private static Process RunBootstrapper(
        string? payloadDirectory, string installDirectory,
        ApplyOptions options, Action<string>? log, bool rollback = false)
    {
        var runDir = Path.Combine(options.StagingRoot, "bootstrap");
        Directory.CreateDirectory(runDir);
        var arguments =
            "apply --install-dir \"" + installDirectory + "\"" +
            (payloadDirectory is { Length: > 0 } payload ? " --payload \"" + payload + "\"" : "") +
            (rollback ? " --rollback" : "") +
            (options.WaitPid is { } pid ? " --wait-pid " + pid : "") +
            (options.AppPath is { Length: > 0 } app ? " --app \"" + app + "\"" : "") +
            (options.KeepPayload ? " --keep-payload" : "") +
            (options.LogFile is { Length: > 0 } lf ? " --log \"" + lf + "\"" : "");

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
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            archive.ExtractToDirectory(staging);
        }
        log?.Invoke($"update: extracted '{zipPath}' → '{staging}'");
        return SingleTopDirectory(staging, zipPath);
    }

    private static string ExtractTarGz(string tarGzPath, ApplyOptions options, Action<string>? log)
    {
        var staging = PrepareStaging(options);
        using (var input = new FileStream(tarGzPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        {
            UstarReader.Extract(gzip, staging);
        }
        log?.Invoke($"update: extracted '{tarGzPath}' → '{staging}'");
        return SingleTopDirectory(staging, tarGzPath);
    }

    private static string SingleTopDirectory(string staging, string sourcePath)
    {
        var directories = Directory.GetDirectories(staging);
        if (directories.Length == 1)
        {
            return directories[0];
        }
        // 无顶层目录的归档：暂存根本身即载荷。
        if (Directory.GetFiles(staging).Length > 0)
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
    /// <summary>引导件显式路径——默认探测安装目录内注入件。</summary>
    public string? BootstrapperPath { get; init; }
    /// <summary>暂存根——默认系统临时目录下的 bundler-update。</summary>
    public string StagingRoot { get; init; } =
        Path.Combine(Path.GetTempPath(), "bundler-update");
    /// <summary>引导程序日志文件。</summary>
    public string? LogFile { get; init; }
}
