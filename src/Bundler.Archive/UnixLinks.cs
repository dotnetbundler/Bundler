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
}
