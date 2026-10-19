using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotNet.Bundler.Core;

/// <summary>
/// Reads symbolic-link targets across hosts: <c>readlink(2)</c> on Unix via
/// libSystem.Native, and the reparse-point PrintName on Windows. The value is
/// the raw stored target — possibly relative to the link's parent directory —
/// and on Windows possibly with backslashes; callers resolve or normalize it
/// for their own purpose.
/// </summary>
internal static class LinkProbe
{
    /// <summary>
    /// Reads the link target of <paramref name="path"/>. Returns false when the
    /// path is not a reparse point or the host cannot provide the target (a
    /// non-symlink reparse point on Windows, a failing readlink, or an
    /// unsupported host) — callers treat that as "not a readable link".
    /// </summary>
    internal static bool TryReadTarget(string path, out string target)
    {
        target = "";
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            return TryReadReparsePoint(path, out target);
        }
        return TryReadLink(path, out target);
    }

    private static bool TryReadLink(string path, out string target)
    {
        target = "";
        var buffer = new byte[4096];
        int length;
        try
        {
            length = SystemNative_ReadLink(path, buffer, buffer.Length);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // 边缘宿主缺 libSystem.Native——按"读不到链接"处理而不是炸管线。
            return false;
        }
        if (length <= 0)
        {
            return false;
        }
        target = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        return true;
    }

    // Windows implementation: FSCTL_GET_REPARSE_POINT + REPARSE_DATA_BUFFER.
    // PrintNameOffset/Length live at bytes 12/14; the PathBuffer payload starts
    // at 20 for symlinks (extra Flags field) and 16 for mount points.

    private const int FsctlGetReparsePoint = 0x000900A8;
    private const int FileFlagOpenReparsePoint = 0x00200000;
    private const int FileFlagBackupSemantics = 0x02000000;
    private const uint IoReparseTagSymlink = 0xA000000C;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    private static bool TryReadReparsePoint(string path, out string target)
    {
        target = "";
        SafeFileHandle handle;
        try
        {
            handle = CreateFileW(
                path,
                0 /* metadata only */,
                0x00000007 /* FileShare.ReadWrite | FileShare.Delete */,
                IntPtr.Zero,
                3 /* FileMode.Open */,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        using (handle)
        {
            if (handle.IsInvalid)
            {
                return false;
            }

        var buffer = new byte[16384];
        // 最小记录 = 8B 头 + 2×4B name offset/length（mountpoint）；symlink 另带 4B flags。
        if (!DeviceIoControl(handle, FsctlGetReparsePoint,
                IntPtr.Zero, 0, buffer, buffer.Length, out var bytesReturned, IntPtr.Zero) ||
            bytesReturned < 16)
        {
            return false;
        }
        var tag = BitConverter.ToUInt32(buffer, 0);
        var pathBufferOffset = tag switch
        {
            IoReparseTagSymlink => 20,
            IoReparseTagMountPoint => 16,
            _ => 0
        };
        if (pathBufferOffset == 0 || bytesReturned < pathBufferOffset)
        {
            return false;
        }
        var printNameOffset = BitConverter.ToUInt16(buffer, 12);
        var printNameLength = BitConverter.ToUInt16(buffer, 14);
        var start = pathBufferOffset + printNameOffset;
        if (printNameLength == 0 || start + printNameLength > bytesReturned)
        {
            return false;
        }
        var raw = System.Text.Encoding.Unicode.GetString(buffer, start, printNameLength);
        // \??\ and \\?\ prefixes mark kernel-style targets — strip for callers.
        target = raw.StartsWith("\\??\\", StringComparison.Ordinal) ? raw.Substring(4)
            : raw.StartsWith("\\\\?\\", StringComparison.Ordinal) ? raw.Substring(4)
            : raw;
        return target.Length > 0;
        }
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_ReadLink", CharSet = CharSet.Ansi,
        SetLastError = true)]
    private static extern int SystemNative_ReadLink(string path, byte[] buffer, int bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        int dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        int dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
