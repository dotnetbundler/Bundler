using System.Runtime.InteropServices;

// 引导件/客户端同源链接编译（Bundler.Updater.Bootstrap 与 Bundler.Updater 均引本文件）——
// 命名空间随宿主切换，与 Update/* 协议件双栖先例同规。
#if BUNDLER_UPDATER_CLIENT
namespace DotNet.Bundler.Updater;
#elif BUNDLER_UPDATER_LINK
namespace DotNet.Bundler.Updater.Bootstrap;
#else
namespace DotNet.Bundler.Core;
#endif

/// <summary>
/// stat(2) file-type probing for Unix payload trees: publish inputs can hold
/// entries <see cref="System.IO.File.Copy"/> cannot read (sockets block or
/// fail with ENXIO, FIFOs block open() waiting for a writer, device nodes are
/// unreadable), so staging must copy regular files only.
/// </summary>
internal static class UnixFileTypes
{
    private const int TypeMask = 0xF000;   // PAL_S_IFMT
    private const int RegularFile = 0x8000; // PAL_S_IFREG

    // libSystem.Native PAL：SystemNative_* 符号链进每个 .NET 进程映像（BCL
    // 文件 IO 全靠它们）——静态 musl AOT 引导件（static-pie、.dynsym 无 libc
    // 符号可解析目标）内同样可达；裸 DllImport("libc") 在那里恒
    // DllNotFoundException 退化"无法判定放行"（syspkg 冻件腿实证）。
    // FileStatus 布局由 PAL C 侧对各 ABI 归一化（Mode@4 定死）——内核
    // struct stat 的架构偏移表、__xstat/stat$INODE64 退化面整体不需要。
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int SysStat(string path, out FileStatus output);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_GetEUid")]
    private static extern uint SysGetEUid();

    // pal_io.h FileStatus 的同形镜像（前 4 槽为本判据所需，余者占位对齐）。
    // 尾字段 HardLinkCount 是 main 分支后加项：v10 出货结构止于 UserFlags
    //（恒读 0），托管缓冲按新头满尺寸预留防未来 runtime 写满踩尾。
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        internal int Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Dev;
        internal long RDev;
        internal long Ino;
        internal uint UserFlags;
        internal uint HardLinkCount;
    }

    /// <summary>
    /// True when <paramref name="path"/> resolves (symlinks followed) to a
    /// regular file — the only entry kind the packagers can read. Also true
    /// on non-Unix hosts and when stat itself fails, so the copy path keeps
    /// surfacing its own errors for missing or dangling entries.
    /// </summary>
    internal static bool IsRegularFile(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return true;
        }
        // stat 不可得（PAL 缺席的路径名奇异宿主）按"无法判定"放行——
        // 拷贝路径自己对缺失/悬空件报错，fail-open 口径保留。
        try
        {
            if (SysStat(path, out var status) != 0)
            {
                return true;
            }
            return (status.Mode & TypeMask) == RegularFile;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// stat 后检查任一执行位（user/group/other）。非 POSIX 宿主无法判定按 true 放行；
    /// stat 失败按"无法判定=不信任"返回 false——供 macOS pkgbuild 脚本位预检这类
    /// 宿主已保证 POSIX 的失败导向调用方如实拒绝。
    /// </summary>
    internal static bool HasAnyExecuteBit(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return true;
        }
        try
        {
            if (SysStat(path, out var status) != 0)
            {
                return false;
            }
            return (status.Mode & 0x49) != 0; // S_IXUSR|S_IXGRP|S_IXOTH
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// stat 后返回 <paramref name="path"/> 的属主 uid（symlink 跟随到目标）。
    /// 与 <see cref="IsRegularFile"/> 的 fail-open 相反——属主判据用于
    /// "共享目录是否可信"的安全判定，stat 不可得按"无法判定=不信任"返回 false。
    /// </summary>
    internal static bool TryGetOwnerUid(string path, out uint uid)
    {
        uid = 0;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }
        try
        {
            if (SysStat(path, out var status) != 0)
            {
                return false;
            }
            uid = status.Uid;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 本进程 euid（geteuid(2)）——用户名缺席的宿主上区分共享 /tmp 槽位的
    /// 兜底身份。仅 POSIX 宿主可得；取不到按"无法判定"返 false。
    /// </summary>
    internal static bool TryGetEUid(out uint uid)
    {
        uid = 0;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }
        try
        {
            uid = SysGetEUid();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
