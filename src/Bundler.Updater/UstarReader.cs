using System.Text;

namespace DotNet.Bundler.Updater;

/// <summary>
/// 最小 POSIX ustar 提取器：只读本仓 TarWriter 的产面（prefix 长名、pax `path=`/
/// `linkpath=`、GNU 'L' 续名防兼容），普通文件/目录/符号链接三型，块到块流式，
/// 零依赖。linkpath 与 path 同列消费——超长链接目标由 pax 承载而非 ustar 截断值。
/// </summary>
internal static class UstarReader
{
    internal static void Extract(Stream stream, string destinationRoot, Action<string>? log = null)
    {
        var header = new byte[512];
        var longName = (string?)null;    // GNU 'L' 续名
        var longLink = (string?)null;    // GNU 'K' 长链名
        var paxPath = (string?)null;     // pax path= 覆盖
        var paxLinkPath = (string?)null; // pax linkpath= 覆盖（>100B 链接目标）
        var globalLinkPath = (string?)null; // pax 'g' 全局 linkpath——后续条目默认
        while (true)
        {
            if (!ReadBlock(stream, header))
            {
                break; // 流尾即档案尾（我们的产出以零块收尾，遇 EOF 也安全）
            }
            if (IsZeroBlock(header))
            {
                break;
            }
            var name = ReadString(header, 0, 100);
            var size = ReadOctal(header, 124, 12);
            var mode = (int)ReadOctal(header, 100, 8);
            var type = (char)header[156];
            var prefix = ReadString(header, 345, 155);

            // 伪条目只累积不复位——POSIX/bsdtar 口径是挂起覆盖值（pax path/
            // linkpath、GNU 续名/续链名）跨伪头合并到下一个真实条目，同键
            // 后写胜；'g' 全局头落在所有后续条目默认层。伪条目处复位会让
            // x(linkpath)→L→'2' 构造档的覆盖值丢在半空。
            switch (type)
            {
                case 'x': // pax：数据块是 "len key=value\n" 记录流，取 path=/linkpath=
                {
                    var (path, linkPath) = ReadPaxOverrides(stream, size);
                    if (path is not null)
                    {
                        paxPath = path;
                    }
                    if (linkPath is not null)
                    {
                        paxLinkPath = linkPath;
                    }
                    continue;
                }
                case 'g': // pax 全局头：linkpath 落到后续条目默认位（per-x 覆盖之）
                {
                    var (_, linkPath) = ReadPaxOverrides(stream, size);
                    if (linkPath is not null)
                    {
                        globalLinkPath = linkPath;
                    }
                    continue;
                }
                case 'L': // GNU 续名：数据块即文件名
                    longName = ReadDataString(stream, size);
                    continue;
                case 'K': // GNU 长链名：数据块即下一条目的链接目标
                    longLink = ReadDataString(stream, size);
                    continue;
            }

            var fullName = paxPath ?? longName ??
                (prefix.Length > 0 ? prefix + "/" + name : name);
            // 消费端优先级与 bsdtar 对齐：per-x linkpath > K 长链名 > g 全局 > 字段。
            var linkTargetOverride = paxLinkPath ?? longLink ?? globalLinkPath;
            paxPath = null;
            paxLinkPath = null;
            longName = null;
            longLink = null;

            switch (type)
            {
                case '2': // 符号链接：mode 0755，pax/GNU 覆盖值优先于 ustar 截断字段
                {
                    var target = linkTargetOverride ?? ReadString(header, 157, 100);
                    SkipData(stream, size);
                    var linkPath = SafePath(destinationRoot, fullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                    // 与 zip 腿（RestoreSymlinkEntries）同款 WARN——链接还原失败
                    // 静默降级会把载荷语义丢在看不见的地方。
                    if (!CreateSymlink(target, linkPath, destinationRoot))
                    {
                        log?.Invoke($"update: warning: failed to restore symlink '{fullName}' → '{target}'.");
                    }
                    break;
                }
                case '5': // 目录
                    SkipData(stream, size);
                    Directory.CreateDirectory(SafePath(destinationRoot, fullName));
                    break;
                default: // '0'/'\0'/其他文件型
                {
                    var filePath = SafePath(destinationRoot, fullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    using (var output = new FileStream(filePath, FileMode.Create,
                               FileAccess.Write, FileShare.None))
                    {
                        CopySize(stream, output, size);
                    }
                    if ((mode & 73) != 0)
                    {
                        TryChmod(filePath, mode);
                    }
                    break;
                }
            }
        }
    }

    // 路径逃逸防线：解出路径必须落在目标根内（"../" 越界即拒）。
    private static string SafePath(string root, string archiveName)
    {
        var combined = Path.GetFullPath(Path.Combine(root, archiveName));
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        if (!combined.StartsWith(fullRoot, StringComparison.Ordinal) &&
            !string.Equals(combined, fullRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            throw new UpdateException($"archive entry escapes destination: '{archiveName}'.");
        }
        return combined;
    }

    private static bool ReadBlock(Stream stream, byte[] block)
    {
        var read = 0;
        while (read < block.Length)
        {
            var n = stream.Read(block, read, block.Length - read);
            if (n == 0) return read != 0;
            read += n;
        }
        return true;
    }

    private static bool IsZeroBlock(byte[] block)
    {
        for (var i = 0; i < block.Length; i++)
        {
            if (block[i] != 0) return false;
        }
        return true;
    }

    private static void CopySize(Stream input, Stream output, long size)
    {
        var buffer = new byte[8192];
        var remaining = size;
        while (remaining > 0)
        {
            var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n == 0) throw new UpdateException("truncated tar payload.");
            output.Write(buffer, 0, n);
            remaining -= n;
        }
        SkipPadding(input, size);
    }

    private static void SkipData(Stream stream, long size)
    {
        var buffer = new byte[8192];
        var remaining = size;
        while (remaining > 0)
        {
            var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n == 0) throw new UpdateException("truncated tar payload.");
            remaining -= n;
        }
        SkipPadding(stream, size);
    }

    private static void SkipPadding(Stream stream, long size)
    {
        var padding = (512 - (size % 512)) % 512;
        while (padding > 0)
        {
            var n = stream.Read(new byte[512], 0, (int)padding);
            if (n == 0) throw new UpdateException("truncated tar padding.");
            padding -= n;
        }
    }

    // "len key=value\n" 记录流——len 计的是字节数，多字节名按字符切会越界。
    // 只取本仓产面用到的两个键：path= 覆盖名、linkpath= 覆盖链接目标；
    // 纯元数据记录返回 (null,null)，调用侧 ?? 回落 ustar 字段。
    private static (string? Path, string? LinkPath) ReadPaxOverrides(Stream stream, long size)
    {
        var data = new byte[size];
        var read = 0;
        while (read < size)
        {
            var n = stream.Read(data, read, (int)(size - read));
            if (n == 0) throw new UpdateException("truncated pax header.");
            read += n;
        }
        SkipPadding(stream, size);
        string? path = null;
        string? linkPath = null;
        foreach (var record in ParsePaxRecords(data))
        {
            if (record.StartsWith("path=", StringComparison.Ordinal))
            {
                path = record.Substring(5);
            }
            else if (record.StartsWith("linkpath=", StringComparison.Ordinal))
            {
                linkPath = record.Substring(9);
            }
        }
        return (path, linkPath);
    }

    private static IEnumerable<string> ParsePaxRecords(byte[] data)
    {
        var index = 0;
        while (index < data.Length)
        {
            var space = -1;
            for (var i = index; i < data.Length && i < index + 21; i++)
            {
                if (data[i] == (byte)' ')
                {
                    space = i;
                    break;
                }
            }
            if (space < 0 ||
                !int.TryParse(Encoding.ASCII.GetString(data, index, space - index),
                    out var length) || length <= 0)
            {
                yield break;
            }
            var valueStart = space + 1;
            var valueLength = length - (space - index) - 2; // 去 "len " 与尾 '\n'
            if (valueLength < 0 || valueStart + valueLength > data.Length)
            {
                yield break;
            }
            yield return Encoding.UTF8.GetString(data, valueStart, valueLength);
            index += length;
        }
    }

    private static string ReadDataString(Stream stream, long size)
    {
        var data = new byte[size];
        var read = 0;
        while (read < size)
        {
            var n = stream.Read(data, read, (int)(size - read));
            if (n == 0) throw new UpdateException("truncated tar name entry.");
            read += n;
        }
        SkipPadding(stream, size);
        return Encoding.UTF8.GetString(data).TrimEnd('\0');
    }

    private static string ReadString(byte[] header, int offset, int length)
    {
        var end = offset;
        var limit = offset + length;
        while (end < limit && header[end] != 0) end++;
        return Encoding.UTF8.GetString(header, offset, end - offset);
    }

    private static long ReadOctal(byte[] header, int offset, int length)
    {
        long value = 0;
        for (var i = offset; i < offset + length; i++)
        {
            var c = header[i];
            if (c == 0 || c == (byte)' ') break;
            value = (value << 3) + (c - (byte)'0');
        }
        return value;
    }

    // 可执行位走外部 chmod——BCL 在 ns2.0 没有文件模式 API；失败不致命。
    private static void TryChmod(string path, int mode)
    {
        try
        {
            var chmodInfo = new ToolProcessStartInfo { FileName = "/bin/chmod" };
            chmodInfo.Arguments.AddRange(new[] { Convert.ToString(mode, 8), path });
            using var process = ToolProcess.Start(chmodInfo);
            if (process is not null)
            {
                process.WaitForExit(10_000);
            }
        }
        catch (Exception)
        {
        }
    }

    // 链接目标必须留在提取根内：绝对或 ../ 逃逸目标落地后，后续条目
    // 虽过 SafePath 字面检查，写盘时会顺已物化的链接写出暂存区。
    // 预扫描（ContainsEscapingLink）与创建期检查共用同一判定。
    internal static bool LinkEscapesRoot(string target, string linkPath, string containmentRoot)
    {
        var resolvedTarget = Path.IsPathRooted(target)
            ? Path.GetFullPath(target)
            : Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(linkPath) ?? string.Empty, target));
        var rootPrefix = Path.GetFullPath(containmentRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !resolvedTarget.StartsWith(rootPrefix, comparison) &&
            !string.Equals(resolvedTarget,
                rootPrefix.TrimEnd(Path.DirectorySeparatorChar), comparison);
    }

    // 原生解包预扫：归档内任一符号链接解析出提取根→true。
    // ditto/bsdtar 自行物化链且后续条目顺链写，只能整档退回 managed 防线。
    internal static bool ContainsEscapingLink(Stream stream, string destinationRoot)
    {
        var header = new byte[512];
        string? paxPath = null;
        string? paxLinkPath = null;
        string? longName = null;
        string? longLink = null;
        string? globalLinkPath = null;
        while (ReadBlock(stream, header))
        {
            if (IsZeroBlock(header))
            {
                break;
            }
            var name = ReadString(header, 0, 100);
            var size = ReadOctal(header, 124, 12);
            var type = (char)header[156];
            var prefix = ReadString(header, 345, 155);

            // 与 Extract 同口径：伪条目累积覆盖值，真实条目消费时复位——
            // 预扫必须按消费端（bsdtar/managed）的合并语义判定，x→L→'2'、
            // 连续 x、'g' 全局头或 'K' 长链名的逃逸值都不能被伪头次序骗丢。
            switch (type)
            {
                case 'x':
                {
                    var (path, linkPath) = ReadPaxOverrides(stream, size);
                    if (path is not null)
                    {
                        paxPath = path;
                    }
                    if (linkPath is not null)
                    {
                        paxLinkPath = linkPath;
                    }
                    continue;
                }
                case 'g':
                {
                    var (_, linkPath) = ReadPaxOverrides(stream, size);
                    if (linkPath is not null)
                    {
                        globalLinkPath = linkPath;
                    }
                    continue;
                }
                case 'L':
                    longName = ReadDataString(stream, size);
                    continue;
                case 'K':
                    longLink = ReadDataString(stream, size);
                    continue;
            }

            var fullName = paxPath ?? longName ??
                (prefix.Length > 0 ? prefix + "/" + name : name);
            var linkTargetOverride = paxLinkPath ?? longLink ?? globalLinkPath;
            paxPath = null;
            paxLinkPath = null;
            longName = null;
            longLink = null;

            switch (type)
            {
                case '2':
                {
                    // 与 Extract 同取值源——pax/GNU 覆盖值存在时 ustar 字段是
                    // 截断值，不看覆盖值会让逃逸目标混过预扫、由消费端按真值物化。
                    var target = linkTargetOverride ?? ReadString(header, 157, 100);
                    SkipData(stream, size);
                    if (LinkEscapesRoot(target, Path.Combine(destinationRoot, fullName),
                            destinationRoot))
                    {
                        return true;
                    }
                    break;
                }
                default:
                    SkipData(stream, size);
                    break;
            }
        }
        return false;
    }

    private static bool TargetIsDirectory(string target, string linkPath)
    {
        var resolved = Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(linkPath) ?? string.Empty, target));
        return Directory.Exists(resolved);
    }

    internal static bool CreateSymlink(string target, string linkPath, string containmentRoot)
    {
        try
        {
            if (LinkEscapesRoot(target, linkPath, containmentRoot))
            {
                return false;
            }
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                if (File.GetAttributes(linkPath).HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(linkPath, recursive: true);
                }
                else
                {
                    File.Delete(linkPath);
                }
            }
#if NET10_0_OR_GREATER
            // net10 腿走托管 API，不再拉起 /bin/ln 子进程。Windows 链型分文件/目录
            // 两型且 File.CreateSymbolicLink 恒产文件型——归档里还原出的目录链接
            // 若按文件型落地则无法穿越，按解析后目标实型选型；悬挂目标回退文件型
            // （归档不记链接的目录位，无从确知）。归档统一存 POSIX 形态目标，
            // Windows 需转回 \ 链才可解析（UnixLinks.ReadLink 已把 \\ 归一成 /）。
            var linkTarget = OperatingSystem.IsWindows()
                ? target.Replace('/', '\\')
                : target;
            if (OperatingSystem.IsWindows() && TargetIsDirectory(target, linkPath))
            {
                Directory.CreateSymbolicLink(linkPath, linkTarget);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, linkTarget);
            }
            return true;
#else
            var linkInfo = new ToolProcessStartInfo { FileName = "/bin/ln" };
            linkInfo.Arguments.Add("-sfn");
            linkInfo.Arguments.Add(target);
            linkInfo.Arguments.Add(linkPath);
            using var process = ToolProcess.Start(linkInfo);
            if (process is null)
            {
                return false;
            }
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch (Exception) { }
                return false;
            }
            return process.ExitCode == 0;
#endif
        }
        catch (Exception)
        {
            // 链接退化路径（无权限/宿主无 ln）：链接退化为跳过——主要语义不受影响。
            return false;
        }
    }
}
