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
    /// 工具缺席或失败返回 false——调用方回退 managed 写出器并告警（宿主探测降级惯例）。
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
                ? Run("/usr/bin/ditto",
                    "-c -k --sequesterRsrc \"" + stagingRoot + "\" \"" + outputPath + "\"") == 0
                : Run("/usr/bin/tar",
                    "-czf \"" + outputPath + "\" -C \"" + stagingRoot + "\" \"" +
                    TopStem(entries) + "\"") == 0;
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
            var ok = Run("/usr/bin/ditto",
                "-c -k --sequesterRsrc --keepParent \"" + appDirectory +
                "\" \"" + outputPath + "\"") == 0;
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

    // 条目集落成与归档结构一致的暂存树：cp -p 保 xattr/模式，ln -s 立链接。
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
                    break;
                case TarEntryKind.Symlink:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    Run("/bin/ln", "-sfn \"" + entry.LinkTarget + "\" \"" + destination + "\"");
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
                        Run("/bin/cp", "-p \"" + entry.SourcePath + "\" \"" + destination + "\"");
                    }
                    Chmod(entry.Mode, destination);
                    break;
            }
        }
    }

    private static void Chmod(int mode, string path) =>
        Run("/bin/chmod", Convert.ToString(mode, 8) + " \"" + path + "\"");

    private static string TopStem(IReadOnlyList<ArchiveTree.Entry> entries) =>
        entries[0].ArchivePath.Split('/')[0];

    private static int Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        process?.WaitForExit(60_000);
        return process?.ExitCode ?? -1;
    }
}
