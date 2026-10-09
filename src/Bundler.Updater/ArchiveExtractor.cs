using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Updater;

/// <summary>
/// 更新载荷解包：zip 在 macOS 宿主走 `ditto -x -k`（xattr+符号链接一并还原，
/// Sparkle/electron-updater 同款），targz 走系统 `tar`（bsdtar 自动把 `._*`
/// AppleDouble 条目还原为 xattr）；其余宿主用 managed 路径——zip 符号链接
/// 条目（S_IFLNK）经 ExternalAttributes 手工还原。
/// </summary>
internal static class ArchiveExtractor
{
    internal static string ExtractZipToDirectory(string zipPath, string staging, Action<string>? log)
    {
        if (!TryDittoExtractZip(zipPath, staging, log))
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                archive.ExtractToDirectory(staging);
                RestoreSymlinkEntries(archive, zipPath, staging, log);
            }
        }
#if NET10_0_OR_GREATER
        SweepEscapedLinks(staging, log);
#endif
        RemoveAppleDoubleTree(staging, log);
        return staging;
    }

    internal static string ExtractTarGzToDirectory(string tarGzPath, string staging, Action<string>? log)
    {
        if (!TryBsdTarExtract(tarGzPath, staging, log))
        {
            using (var input = new FileStream(tarGzPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                UstarReader.Extract(gzip, staging, log);
            }
        }
#if NET10_0_OR_GREATER
        SweepEscapedLinks(staging, log);
#endif
        RemoveAppleDoubleTree(staging, log);
        return staging;
    }

    // ditto -x -k 还原 AppleDouble xattr 与符号链接；失败回 managed（会 WARN 丢 xattr）。
    private static bool TryDittoExtractZip(string zipPath, string staging, Action<string>? log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }
        if (Run("/usr/bin/ditto", "-x -k " + ShellQuote(zipPath) + " " + ShellQuote(staging)) == 0)
        {
            log?.Invoke("update: extracted via ditto (xattrs/symlinks preserved).");
            return true;
        }
        // ditto 可能已半提取（非法 __MACOSX 中途 File exists）——残渣会让 managed
        // 回退撞 IOException，先清空暂存再回退。
        ClearDirectory(staging);
        log?.Invoke("update: ditto extraction failed — managed fallback (xattrs lost).");
        return false;
    }

    // macOS 系统 tar=bsdtar：`._*` AppleDouble 条目自动还原为 xattr。
    private static bool TryBsdTarExtract(string tarGzPath, string staging, Action<string>? log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }
        if (Run("/usr/bin/tar", "-xzf " + ShellQuote(tarGzPath) + " -C " + ShellQuote(staging)) == 0)
        {
            log?.Invoke("update: extracted via bsdtar (xattrs/symlinks preserved).");
            return true;
        }
        ClearDirectory(staging);
        log?.Invoke("update: bsdtar extraction failed — managed fallback (xattrs lost).");
        return false;
    }

#if NET10_0_OR_GREATER
    // ditto/bsdtar 原生解包自行物化符号链接，绕过 CreateSymlink 的暂存圈定——
    // 事后扫一遍：解析目标越出 staging 的链删节点自身+WARN，合法链不动。
    // 枚举不下钻目录链（ReparsePoint 不进 SearchOption 递归），删链不触目标。
    private static void SweepEscapedLinks(string staging, Action<string>? log)
    {
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     staging, "*", SearchOption.AllDirectories))
        {
            var info = Directory.Exists(entry)
                ? (FileSystemInfo)new DirectoryInfo(entry)
                : new FileInfo(entry);
            if (info.LinkTarget is not { Length: > 0 } target)
            {
                continue;
            }
            var resolved = Path.IsPathRooted(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(entry) ?? string.Empty, target));
            if (!resolved.StartsWith(root, StringComparison.Ordinal) &&
                !string.Equals(resolved, root.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.Ordinal))
            {
                File.Delete(entry);
                log?.Invoke($"update: dropped escaping symlink '{entry}' → '{target}'.");
            }
        }
    }
#endif

    private static void ClearDirectory(string root)
    {
        foreach (var entry in Directory.GetFileSystemEntries(root))
        {
            // 目录链/联接只删链节点自身（File.Delete 删链不触目标），
            // 绝不顺半提取留下的链递归删到载荷之外的位置。
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    // `__MACOSX/` AppleDouble 目录只是垃圾树——ditto 产 zip 在顶层，载荷内含预解压过的
    // macOS 目录树时嵌套在 wrapper 里，全树清。ditto/bsdtar 优选路径也会把载荷自带的
    // 嵌套 `__MACOSX` 落成真目录，故在任一提取成功后统一执行而非只挂 managed 回退。
    private static void RemoveAppleDoubleTree(string staging, Action<string>? log)
    {
        var removed = false;
        // 递归枚举不穿越目录链——leaf 的 __MACOSX 链节点按目录删也只删链自身。
        // Exists 守卫：`__MACOSX` 内再套 `__MACOSX` 时父目录已连同子树删除，
        // 对已枚举但随之消失的条目跳删，不炸解包。
        foreach (var appleDouble in Directory.GetDirectories(
                     staging, "__MACOSX", SearchOption.AllDirectories))
        {
            if (Directory.Exists(appleDouble))
            {
                Directory.Delete(appleDouble, recursive: true);
                removed = true;
            }
        }
        if (removed)
        {
            log?.Invoke("update: dropped __MACOSX AppleDouble entries.");
        }
    }

    // managed ExtractToDirectory 把 S_IFLNK 条目落成普通文件——改回真链接。
    // netstandard2.0 无 ExternalAttributes 属性面，直接读中央目录记录判链接。
    private static void RestoreSymlinkEntries(ZipArchive archive, string zipPath, string staging,
        Action<string>? log)
    {
        foreach (var linkName in ScanSymlinkEntries(zipPath))
        {
            var entry = archive.GetEntry(linkName);
            if (entry is null)
            {
                continue;
            }
            var destination = SafePath(staging, entry.FullName);
            string target;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
            {
                target = reader.ReadToEnd();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!UstarReader.CreateSymlink(target, destination, staging))
            {
                log?.Invoke($"update: warning: failed to restore symlink '{entry.FullName}' → '{target}'.");
            }
        }
    }

    // EOCD 反向定位→中央目录逐条：取 unix 宿主位（version made by 高字节==3）下的
    // external attributes 高 16 位文件型，S_IFLNK(0120xxx) 即符号链接条目。
    private static List<string> ScanSymlinkEntries(string zipPath)
    {
        var links = new List<string>();
        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var tail = new byte[Math.Min(stream.Length, 66_000)];
            stream.Seek(-tail.Length, SeekOrigin.End);
            var read = stream.Read(tail, 0, tail.Length);
            var eocd = -1;
            for (var i = read - 22; i >= 0; i--)
            {
                if (BitConverter.ToUInt32(tail, i) == 0x06054B50u)
                {
                    eocd = i;
                    break;
                }
            }
            if (eocd < 0)
            {
                return links;
            }
            var cdOffset = (long)BitConverter.ToUInt32(tail, eocd + 16);
            var cdEntries = (long)BitConverter.ToUInt16(tail, eocd + 10);
            if (cdOffset == 0xFFFFFFFF || cdEntries == 0xFFFF)
            {
                // Zip64：EOCD 前 20B 是 locator（sig 0x07064b50，+8 处放 EOCD64 偏移），
                // EOCD64 记录 +24=CD 条目数、+48=CD 起始偏移。
                var loc = eocd - 20;
                if (loc < 0 || BitConverter.ToUInt32(tail, loc) != 0x07064B50u)
                {
                    return links;
                }
                var eocd64Offset = BitConverter.ToInt64(tail, loc + 8);
                stream.Seek(eocd64Offset, SeekOrigin.Begin);
                var rec = new byte[56];
                if (stream.Read(rec, 0, 56) != 56 ||
                    BitConverter.ToUInt32(rec, 0) != 0x06064B50u)
                {
                    return links;
                }
                cdEntries = BitConverter.ToInt64(rec, 24);
                cdOffset = BitConverter.ToInt64(rec, 48);
            }
            stream.Seek(cdOffset, SeekOrigin.Begin);
            var header = new byte[46];
            for (var i = 0; i < cdEntries; i++)
            {
                if (stream.Read(header, 0, 46) != 46 ||
                    BitConverter.ToUInt32(header, 0) != 0x02014B50u)
                {
                    break;
                }
                var hostMadeBy = header[5];
                var external = BitConverter.ToUInt32(header, 38);
                var nameLength = BitConverter.ToUInt16(header, 28);
                var extraLength = BitConverter.ToUInt16(header, 30);
                var commentLength = BitConverter.ToUInt16(header, 32);
                var nameBytes = new byte[nameLength];
                var got = stream.Read(nameBytes, 0, nameLength);
                if (hostMadeBy == 3 && got == nameLength &&
                    ((external >> 16) & 0xF000) == 0xA000)
                {
                    links.Add(Encoding.UTF8.GetString(nameBytes));
                }
                stream.Seek(extraLength + commentLength, SeekOrigin.Current);
            }
        }
        return links;
    }

    private static string SafePath(string root, string archiveName)
    {
        var full = Path.GetFullPath(
            Path.Combine(root, archiveName.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.Ordinal))
        {
            throw new UpdateException($"archive entry escapes staging root: '{archiveName}'.");
        }
        return full;
    }

    private static string ShellQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // 超时即杀——否则超时的 ditto/tar 会继续写暂存目录，与回退提取器并发改同一载荷。
    private static int Run(string file, string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                });
            if (process is null)
            {
                return -1;
            }
            // stderr 并行消费：工具刷大量诊断时写满管道会卡死子进程直到超时。
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(); } catch (Exception) { }
                try { process.WaitForExit(10_000); } catch (Exception) { }
                return -1;
            }
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
