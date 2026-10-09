using System.Text;

namespace DotNet.Bundler.Rpm;

/// <summary>
/// Writes a cpio "newc" (070701) archive — the RPM payload container.
/// Every entry carries an inode/mode/timestamp and file data; the archive ends
/// with a TRAILER!!! record and pads to a 512-byte boundary.
/// All mtimes are fixed at 1980-01-01 UTC for deterministic output.
/// </summary>
internal static class CpioWriter
{
    internal const long EntryMtime = 315532800L; // 1980-01-01 UTC (deb-side convention)

    internal sealed class Entry
    {
        internal string Name = "";          // absolute path, e.g. "/usr/lib/pkg/app"
        internal int Mode;                  // st_mode including type bits
        internal byte[] Data = [];          // regular file contents; link target for symlinks
        /// 常规文件可给流式源：设置后优先于 Data——按流 Length 写头、分块拷贝正文，不驻内存。
        internal Func<Stream>? OpenContent;
        internal int Inode;
    }

    internal static byte[] Write(IReadOnlyList<Entry> entries)
    {
        var output = new MemoryStream();
        Write(output, entries);
        return output.ToArray();
    }

    /// <summary>流式写出：载荷大时直接写目标流（如 gzip→文件），不经内存整档。</summary>
    internal static void Write(Stream output, IReadOnlyList<Entry> entries)
    {
        foreach (var entry in entries)
        {
            WriteEntry(output, entry);
        }

        WriteRecord(output, "TRAILER!!!", 0, 0, 1, 0, [], writeData: false);
        var pad = (int)((512 - output.Length % 512) % 512);
        output.Write(new byte[pad], 0, pad);
    }

    private static void WriteEntry(Stream output, Entry entry)
    {
        var isSymlink = (entry.Mode & 0xF000) == 0xA000;
        var isDir = (entry.Mode & 0xF000) == 0x4000;
        Stream? contentStream = null;
        try
        {
            var fileSize = entry.Data.Length;
            if (!isDir && entry.OpenContent is { } openContent)
            {
                contentStream = openContent();
                fileSize = (int)contentStream.Length;
            }
            else if (isDir)
            {
                fileSize = 0;
            }
            WriteRecord(output, entry.Name, entry.Mode, entry.Inode, isDir ? 2 : 1,
                fileSize, entry.Data, contentStream, isSymlink || !isDir);
        }
        finally
        {
            contentStream?.Dispose();
        }
    }

    private static void WriteRecord(
        Stream output, string name, int mode, int inode, int nlink,
        int fileSize, byte[] data, Stream? dataStream = null, bool writeData = true)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var header = new StringBuilder();
        header.Append("070701");
        foreach (var field in new[]
        {
            inode, mode, 0 /* uid */, 0 /* gid */, nlink,
            (int)EntryMtime, fileSize, 0 /* devmajor */, 0 /* devminor */,
            0 /* rdevmajor */, 0 /* rdevminor */, nameBytes.Length + 1, 0 /* check */
        })
        {
            header.Append(field.ToString("x8"));
        }
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        output.Write(headerBytes, 0, headerBytes.Length);
        output.Write(nameBytes, 0, nameBytes.Length);
        output.WriteByte(0);
        Pad4(output);
        if (writeData)
        {
            if (dataStream is not null)
            {
                dataStream.CopyTo(output);
            }
            else if (data.Length > 0)
            {
                output.Write(data, 0, data.Length);
            }
        }
        Pad4(output);
    }

    private static void Pad4(Stream output)
    {
        var pad = (int)((4 - output.Length % 4) % 4);
        output.Write(new byte[pad], 0, pad);
    }
}
