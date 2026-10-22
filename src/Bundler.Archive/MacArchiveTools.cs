using System.Diagnostics;
using System.Runtime.InteropServices;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Archive;

/// <summary>
/// macOS 保真归档通路：载荷树带扩展属性（`com.apple.cs.*` 代码签名等 xattr）时，
/// managed 写出器丢元数据导致签名失效——改走系统工具：zip 用 `ditto -c -k --sequesterRsrc`
/// （Sparkle/electron-updater 同款，AppleDouble 入 `__MACOSX/`），tar.gz 用系统 bsdtar
/// （xattr 自动编码为 `._*` 条目）。仅在 macOS 宿主启用，其余宿主照旧 managed 写出器。
/// </summary>
internal static class MacArchiveTools
{
    /// <summary>
    /// 载荷树（或任一 SourcePath）含扩展属性时把条目集落盘成暂存树，再用系统工具成包。
    /// 工具缺席、暂存物化或成包失败返回 false——调用方回退 managed 写出器并告警
    /// （宿主探测降级惯例；中途产物在 finally 清掉）。
    /// </summary>
    internal static bool TryWriteWithHostTools(
        IReadOnlyList<ArchiveTree.Entry> entries, string sourceRoot, PackageFormat format,
        string outputPath, string workDirectory, IBundleLogger logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }
        if (!UnixLinks.TreeHasExtendedAttributes(sourceRoot) &&
            !entries.Any(e => e.SourcePath is { } p && UnixLinks.HasExtendedAttributes(p)))
        {
            return false;
        }
        var stagingRoot = Path.Combine(workDirectory, "macfidelity-" + Guid.NewGuid().ToString("N"));
        // 工具先写工作目录暂存件再换到输出位：超时 kill 后未死透的进程继续写也只碰
        // 暂存件（孤儿随 .bundler-work 陈旧清扫走），绝不与回退的 managed 写出器
        // 并发改 outputPath，也不在输出目录留 .bundler-part 孤儿。
        var partPath = Path.Combine(workDirectory,
            "macfidelity-part-" + Guid.NewGuid().ToString("N") +
            (format == PackageFormat.Zip ? ".zip" : ".tar.gz"));
        try
        {
            Materialize(entries, stagingRoot);
            var attempt = format == PackageFormat.Zip
                ? Run(ScaledTimeoutMs(entries), "/usr/bin/ditto",
                    "-c", "-k", "--sequesterRsrc", stagingRoot, partPath)
                : Run(ScaledTimeoutMs(entries), "/usr/bin/tar",
                    "-czf", partPath, "-C", stagingRoot, TopStem(entries));
            if (attempt.Rc != 0)
            {
                logger.Log(BundleLogLevel.Warning,
                    $"ditto/tar archival failed (rc={attempt.Rc}: {attempt.Err.Trim()}); " +
                    "falling back to managed writer (xattrs will be lost).");
                FileTree.TryDeleteFile(partPath);
            }
            else
            {
                File.Move(partPath, outputPath);
                logger.Log(BundleLogLevel.Information,
                    "extended attributes detected — archived via host tool to preserve xattrs/symlinks.");
            }
            return attempt.Rc == 0;
        }
        catch (Exception)
        {
            logger.Log(BundleLogLevel.Warning,
                "host-tool archival unavailable; falling back to managed writer (xattrs will be lost).");
            FileTree.TryDeleteFile(partPath);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>.app 目录件直打 zip 运输件（更新清单用）——xattr 检测后走 ditto --keepParent。</summary>
    internal static bool TryWriteAppZipViaDitto(string appDirectory, string outputPath, IBundleLogger logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            !UnixLinks.TreeHasExtendedAttributes(appDirectory))
        {
            return false;
        }
        // 与 TryWriteWithHostTools 同规：先写暂存件再换名——被 kill 的 ditto
        // 残留进程不会与调用方/重试并发写 outputPath。
        var partPath = outputPath + ".bundler-part";
        try
        {
            var attempt = Run(ScaledTimeoutMs(appDirectory), "/usr/bin/ditto",
                "-c", "-k", "--sequesterRsrc", "--keepParent", appDirectory, partPath);
            if (attempt.Rc == 0)
            {
                File.Move(partPath, outputPath);
                logger.Log(BundleLogLevel.Information,
                    "extended attributes detected — app transport zip written via ditto.");
            }
            else
            {
                FileTree.TryDeleteFile(partPath);
            }
            return attempt.Rc == 0;
        }
        catch (Exception)
        {
            FileTree.TryDeleteFile(partPath);
            return false;
        }
    }

    // 条目集落成与归档结构一致的暂存树：cp -p 保 xattr/模式，ln -s 立链接；
    // 目录与链接自身的 xattr 走 CopyExtendedAttributes 补齐（cp -p 只管文件）。
    // 任一步失败即抛——调用方整体回退 managed 写出器，绝不留半成品进入归档。
    private static void Materialize(IEnumerable<ArchiveTree.Entry> entries, string root)
    {
        foreach (var entry in entries)
        {
            var destination = Path.Combine(root,
                entry.ArchivePath.Replace('/', Path.DirectorySeparatorChar));
            switch (entry.Kind)
            {
                case TarEntryKind.Directory:
                    Directory.CreateDirectory(destination);
                    CopySourceXattrs(entry, destination);
                    break;
                case TarEntryKind.Symlink:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    ToolRunner.RunSync("/bin/ln",
                        ["-sfn", entry.LinkTarget!, destination]);
                    CopySourceXattrs(entry, destination);
                    break;
                default:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (entry.InlineContent is { } inline)
                    {
                        File.WriteAllBytes(destination, inline);
                    }
                    else
                    {
                        // cp -p 在 macOS 上随模式一并复制扩展属性与资源叉。
                        ToolRunner.RunSync("/bin/cp",
                            ["-p", entry.SourcePath!, destination]);
                    }
                    ToolRunner.RunSync("/bin/chmod",
                        [Convert.ToString(entry.Mode, 8), destination]);
                    break;
            }
        }
    }

    private static void CopySourceXattrs(ArchiveTree.Entry entry, string destination)
    {
        if (entry.SourcePath is { } source && UnixLinks.HasExtendedAttributes(source) &&
            !UnixLinks.CopyExtendedAttributes(source, destination))
        {
            throw new IOException($"failed to copy extended attributes '{source}' → '{destination}'.");
        }
    }

    private static string TopStem(IReadOnlyList<ArchiveTree.Entry> entries) =>
        entries[0].ArchivePath.Split('/')[0];

    // 成品命令超时随载荷规模伸缩：固定 60s 会误杀 GB 级载荷（误杀→回退→丢 xattr）。
    // 按 25 MB/s 保守吞吐给 2 倍冗余，下限 60s；取不到长度按 0 计退回下限。
    private static int ScaledTimeoutMs(IEnumerable<ArchiveTree.Entry> entries) =>
        ScaledTimeoutMs(entries
            .Where(e => e.Kind == TarEntryKind.File && e.SourcePath is not null)
            .Sum(e => SafeLength(e.SourcePath!)));

    private static int ScaledTimeoutMs(string directory) =>
        ScaledTimeoutMs(Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(SafeLength));

    private static int ScaledTimeoutMs(long payloadBytes) =>
        (int)Math.Max(60_000, payloadBytes / 25_000_000 * 2_000);

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // 超时即杀语义由 ToolRunner 统一保证——不许半成品工具与回退写出器并发改
    // 同一目标；stderr 带回供告警（旧自管壳丢 stderr，失败只有裸 rc）。
    private static (int Rc, string Err) Run(
        int timeoutMilliseconds, string file, params string[] arguments)
    {
        var result = ToolRunner.TryRunAsync(
            file, arguments, "", CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(timeoutMilliseconds))
            .GetAwaiter().GetResult();
        return (result?.ExitCode ?? -1, result?.StandardError ?? "");
    }
}
