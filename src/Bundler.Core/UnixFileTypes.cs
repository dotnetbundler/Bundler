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

    /// <summary>
    /// True when <paramref name="path"/> resolves (symlinks followed) to a
    /// regular file — the only entry kind the packagers can read. Also true
    /// on non-Unix hosts and when stat itself fails, so the copy path keeps
    /// surfacing its own errors for missing or dangling entries.
    /// </summary>
    internal static bool IsRegularFile(string path)
    {
        var linux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        if (!linux && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return true;
        }
        var status = new byte[256];
        if (Stat(path, status) != 0)
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
    // layout every other Linux arch uses puts it at 16. macOS (u32 st_dev +
    // u16 st_mode) puts it at 4.
    private static int LinuxModeOffset()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        return architecture == Architecture.X64 || (int)architecture is 5 /* S390x */ or 7 /* Ppc64le */
            ? 24
            : 16;
    }
}
