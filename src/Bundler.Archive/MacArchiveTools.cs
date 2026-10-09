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
        try
        {
            Materialize(entries, stagingRoot);
            var ok = format == PackageFormat.Zip
                ? Run("/usr/bin/ditto", "-c -k --sequesterRsrc " +
                    ShellQuote(stagingRoot) + " " + ShellQuote(outputPath),
                    ScaledTimeoutMs(entries)) == 0
                : Run("/usr/bin/tar", "-czf " + ShellQuote(outputPath) + " -C " +
                    ShellQuote(stagingRoot) + " " + ShellQuote(TopStem(entries)),
                    ScaledTimeoutMs(entries)) == 0;
            if (!ok)
            {
                logger.Log(BundleLogLevel.Warning,
                    "ditto/tar archival failed; falling back to managed writer (xattrs will be lost).");
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            else
            {
                logger.Log(BundleLogLevel.Information,
                    "extended attributes detected — archived via host tool to preserve xattrs/symlinks.");
            }
            return ok;
        }
        catch (Exception)
        {
            logger.Log(BundleLogLevel.Warning,
                "host-tool archival unavailable; falling back to managed writer (xattrs will be lost).");
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
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
        try
        {
            var ok = Run("/usr/bin/ditto", "-c -k --sequesterRsrc --keepParent " +
                ShellQuote(appDirectory) + " " + ShellQuote(outputPath),
                ScaledTimeoutMs(appDirectory)) == 0;
            if (!ok && File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            if (ok)
            {
                logger.Log(BundleLogLevel.Information,
                    "extended attributes detected — app transport zip written via ditto.");
            }
            return ok;
        }
        catch (Exception)
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
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
                    Require(Run("/bin/ln", "-sfn " + ShellQuote(entry.LinkTarget) +
                        " " + ShellQuote(destination)),
                        $"ln -sfn '{entry.LinkTarget}' '{destination}'");
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
                        Require(Run("/bin/cp", "-p " + ShellQuote(entry.SourcePath!) +
                            " " + ShellQuote(destination)),
                            $"cp -p '{entry.SourcePath}' '{destination}'");
                    }
                    Require(Run("/bin/chmod", Convert.ToString(entry.Mode, 8) +
                        " " + ShellQuote(destination)),
                        $"chmod {Convert.ToString(entry.Mode, 8)} '{destination}'");
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

    private static void Require(int rc, string what)
    {
        if (rc != 0)
        {
            throw new IOException($"host tool step failed rc={rc}: {what}");
        }
    }

    private static string TopStem(IReadOnlyList<ArchiveTree.Entry> entries) =>
        entries[0].ArchivePath.Split('/')[0];

    // 参数整词加引并转义（netstandard2.0 无 ArgumentList）：引号、空格、前导 '-'
    // 在 .NET Unix 参数解析下均按字面传给工具，不产生参数解析副作用。
    private static string ShellQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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

    // 超时即杀——不许半成品工具与回退写出器并发改同一目标。
    private static int Run(string file, string arguments, int timeoutMilliseconds = 60_000)
    {
        using var process = Process.Start(
            new ProcessStartInfo(file, arguments) { UseShellExecute = false });
        if (process is null)
        {
            return -1;
        }
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(); } catch (Exception) { }
            try { process.WaitForExit(10_000); } catch (Exception) { }
            return -1;
        }
        return process.ExitCode;
    }
}
