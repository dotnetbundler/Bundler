using System.Runtime.InteropServices;

namespace DotNet.Bundler.Core;

/// <summary>
/// stat(2) file-type probing for Unix payload trees: publish inputs can hold
/// entries <see cref="System.IO.File.Copy"/> cannot read (sockets block or
/// fail with ENXIO, FIFOs block open() waiting for a writer, device nodes are
/// unreadable), so staging must copy regular files only.
/// </summary>
internal static class UnixFileTypes
{
    private const int TypeMask = 0xF000;   // S_IFMT
    private const int RegularFile = 0x8000; // S_IFREG

    [DllImport("libc", EntryPoint = "stat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Stat(string path, byte[] buffer);

    // macOS 上裸 "stat" 符号在 x86_64 绑到遗留 non-INODE64 变体（st_dev,st_ino,...
    // st_mode 在偏移 8），arm64 无遗留变体恰是 inode64——同名符号两架构不同布局。
    // 显式绑 stat$INODE64 拿到确定的 inode64 布局（st_mode 恒在偏移 4）；
    // 符号缺失（理论上 arm64 已无二名）退回 "stat" 亦同布局，双保险。
    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int StatInode64(string path, byte[] buffer);

    /// <summary>
    /// True when <paramref name="path"/> resolves (symlinks followed) to a
    /// regular file — the only entry kind the packagers can read. Also true
    /// on non-Unix hosts and when stat itself fails, so the copy path keeps
    /// surfacing its own errors for missing or dangling entries.
    /// </summary>
    internal static bool IsRegularFile(string path)
    {
        var linux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (!linux && !mac)
        {
            return true;
        }
        var status = new byte[256];
        int rc;
        if (mac)
        {
            try
            {
                rc = StatInode64(path, status);
            }
            catch (EntryPointNotFoundException)
            {
                rc = Stat(path, status);
            }
        }
        else
        {
            rc = Stat(path, status);
        }
        if (rc != 0)
        {
            return true;
        }
        var mode = linux
            ? BitConverter.ToInt32(status, LinuxModeOffset())
            : BitConverter.ToUInt16(status, 4);
        return (mode & TypeMask) == RegularFile;
    }

    // struct stat st_mode offset: 64-bit ABIs that give st_nlink a full
    // machine word (x86_64, s390x, ppc64le) put st_mode at 24; the asm-generic
    // layout every other Linux arch uses puts it at 16. macOS inode64 (u32
    // st_dev + u16 st_mode) puts it at 4. An ABI not covered above defaults
    // to the asm-generic offset on a best-effort basis — a wrong guess reads
    // garbage mode bits and can either pass a special file through the filter
    // or wrongly skip a regular file; neither outcome is verified safe, the
    // caller must tolerate both.
    private static int LinuxModeOffset()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        return architecture == Architecture.X64 || (int)architecture is 5 /* S390x */ or 7 /* Ppc64le */
            ? 24
            : 16;
    }
}
