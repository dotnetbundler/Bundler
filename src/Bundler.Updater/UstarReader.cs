using System.Text;

namespace DotNet.Bundler.Updater;

/// <summary>
/// 最小 POSIX ustar 提取器：只读本仓 TarWriter 的产面（prefix 长名、pax `path=`、
/// GNU 'L' 续名防兼容），普通文件/目录/符号链接三型，块到块流式，零依赖。
/// </summary>
internal static class UstarReader
{
    internal static void Extract(Stream stream, string destinationRoot)
    {
        var header = new byte[512];
        var longName = (string?)null;   // GNU 'L' 续名
        var paxPath = (string?)null;    // pax path= 覆盖
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
            var fullName = paxPath ?? longName ??
                (prefix.Length > 0 ? prefix + "/" + name : name);
            paxPath = null;
            longName = null;

            switch (type)
            {
                case 'x': // pax：数据块是 "len key=value\n" 记录流，只取 path=
                    paxPath = ReadPaxPath(stream, size);
                    break;
                case 'L': // GNU 续名：数据块即文件名
                    longName = ReadDataString(stream, size);
                    break;
                case '2': // 符号链接：mode 0755，linkname@100..200
                {
                    var target = ReadString(header, 157, 100);
                    SkipData(stream, size);
                    var linkPath = SafePath(destinationRoot, fullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                    CreateSymlink(target, linkPath);
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

    private static string ReadPaxPath(Stream stream, long size)
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
        // "len key=value\n" 记录流——只认 path=。
        var text = Encoding.UTF8.GetString(data);
        foreach (var record in ParsePaxRecords(text))
        {
            if (record.StartsWith("path=", StringComparison.Ordinal))
            {
                return record.Substring(5);
            }
        }
        return "";
    }

    private static IEnumerable<string> ParsePaxRecords(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var space = text.IndexOf(' ', index);
            if (space < 0) yield break;
            if (!int.TryParse(text.Substring(index, space - index), out var length) || length <= 0)
            {
                yield break;
            }
            yield return text.Substring(space + 1, length - (space - index) - 2);
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
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("/bin/chmod",
                    Convert.ToString(mode, 8) + " \"" + path + "\"")
                { UseShellExecute = false });
            process?.WaitForExit(10_000);
        }
        catch (Exception)
        {
        }
    }

    private static void CreateSymlink(string target, string linkPath)
    {
        try
        {
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
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("/bin/ln",
                    "-sfn \"" + target + "\" \"" + linkPath + "\"")
                { UseShellExecute = false });
            process?.WaitForExit(10_000);
        }
        catch (Exception)
        {
            // 宿主无 ln（windows 裸环境）：链接退化为跳过——主要语义不受影响。
        }
    }
}
