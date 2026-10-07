using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Archive;

/// <summary>Minimal libc readlink for symlink preservation on Unix payloads.</summary>
internal static class UnixLinks
{
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int readlink(string path, byte[] buffer, int bufferSize);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int access(string path, int mode);

    // macOS 原型是 listxattr(path, buf, size, options)；Linux 是 llistxattr(path, buf, size)。
    // 只问"有没有"——空缓冲取长度即可，>0 即存在扩展属性。
    [DllImport("libc", SetLastError = true, EntryPoint = "listxattr", CharSet = CharSet.Ansi)]
    private static extern long ListXattrDarwin(string path, IntPtr list, UIntPtr size, int options);

    [DllImport("libc", SetLastError = true, EntryPoint = "llistxattr", CharSet = CharSet.Ansi)]
    private static extern long ListXattrLinux(string path, IntPtr list, UIntPtr size);

    /// <summary>Returns the symlink target, or null when unsupported/failed.</summary>
    internal static string? ReadLink(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }
        var buffer = new byte[4096];
        var length = readlink(path, buffer, buffer.Length);
        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }

    /// <summary>
    /// True when the host reports the file executable (libc access(X_OK));
    /// null off Unix so callers can fall back to a magic-bytes probe.
    /// </summary>
    internal static bool? IsExecutable(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }
        try
        {
            return access(path, 1 /* X_OK */) == 0;
        }
        catch (EntryPointNotFoundException) { return null; }
        catch (DllNotFoundException) { return null; }
    }

    /// <summary>宿主报任意扩展属性（xattr）即为 true；非 Unix 恒 false。</summary>
    internal static bool HasExtendedAttributes(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return ListXattrDarwin(path, IntPtr.Zero, UIntPtr.Zero, 1 /* XATTR_NOFOLLOW */) > 0;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return ListXattrLinux(path, IntPtr.Zero, UIntPtr.Zero) > 0;
            }
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        return false;
    }

    /// <summary>整树任一节（目录/文件/链接自身）带扩展属性即为 true，首个命中即返回。</summary>
    internal static bool TreeHasExtendedAttributes(string root)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (HasExtendedAttributes(node))
            {
                return true;
            }
            foreach (var dir in Directory.EnumerateDirectories(node))
            {
                // 符号链接目录只查链自身的 xattr——沿链下钻会重复计数且遇环不收敛。
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(dir);
                }
                else if (HasExtendedAttributes(dir))
                {
                    return true;
                }
            }
            foreach (var file in Directory.EnumerateFiles(node))
            {
                if (HasExtendedAttributes(file))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
