using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Archive;

/// <summary>Windows 侧 reparse point 目标读取：FSCTL_GET_REPARSE_POINT 拿 PrintName（相对链接保原样）。</summary>
internal static class WindowsLinks
{
    private static readonly IntPtr InvalidHandle = new(-1);

    private const uint FileShareAll = 0x00000007; // READ|WRITE|DELETE
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint ReparseTagSymlink = 0xA000000C;
    private const uint ReparseTagMountPoint = 0xA0000003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    /// <summary>符号链接/挂载点目标的显示名（PrintName）；非 reparse、非可命名类型或失败时返回 null。</summary>
    internal static string? ReadReparseTarget(string path)
    {
        var raw = CreateFile(
            path, 0, FileShareAll, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (raw == InvalidHandle || raw == IntPtr.Zero)
        {
            return null;
        }
        using var handle = new SafeFileHandle(raw, ownsHandle: true);
        var buffer = new byte[16384];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint,
                IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero))
        {
            return null;
        }
        var tag = BitConverter.ToUInt32(buffer, 0);
        // 两种布局只在 PathBuffer 起点差 4 字节（symlink 多一个 Flags 字段）。
        var pathBufferOffset = tag switch
        {
            ReparseTagSymlink => 20,
            ReparseTagMountPoint => 16,
            _ => 0
        };
        if (pathBufferOffset == 0)
        {
            return null;
        }
        var printNameOffset = BitConverter.ToUInt16(buffer, 12);
        var printNameLength = BitConverter.ToUInt16(buffer, 14);
        var start = pathBufferOffset + printNameOffset;
        if (printNameLength == 0 || start + printNameLength > buffer.Length)
        {
            return null;
        }
        return Encoding.Unicode.GetString(buffer, start, printNameLength);
    }
}
