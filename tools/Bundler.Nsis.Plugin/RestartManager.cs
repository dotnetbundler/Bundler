using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Nsis.Plugin;

internal static class RestartManager
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int MaxSessionKeyLength = 32;
    private const uint ForceShutdown = 0x1;

    internal static int GetLockingProcessCount(string paths)
    {
        var resources = ExistingFiles(paths);
        if (resources.Length == 0)
        {
            return 0;
        }

        var result = StartAndRegister(resources, out var session);
        if (result != ErrorSuccess)
        {
            return -result;
        }

        try
        {
            uint needed = 0;
            uint count = 0;
            var rebootReasons = 0u;
            result = RmGetList(session, out needed, ref count, IntPtr.Zero, ref rebootReasons);
            if (result != ErrorSuccess && result != ErrorMoreData)
            {
                return -result;
            }

            return checked((int)needed);
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    internal static int ShutdownLockingProcesses(string paths)
    {
        var resources = ExistingFiles(paths);
        if (resources.Length == 0)
        {
            return 0;
        }

        var result = StartAndRegister(resources, out var session);
        if (result != ErrorSuccess)
        {
            return result;
        }

        try
        {
            // 保持现有安装器的强制关闭语义，但只关闭实际占用目标文件的进程。
            return RmShutdown(session, ForceShutdown, IntPtr.Zero);
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static string[] ExistingFiles(string paths) => paths
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Path.GetFullPath)
        .Where(File.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static int StartAndRegister(string[] paths, out uint session)
    {
        session = 0;
        if (paths.Length == 0)
        {
            return 87;
        }

        var sessionKey = new StringBuilder(MaxSessionKeyLength + 1);
        var result = RmStartSession(out session, 0, sessionKey);
        if (result != ErrorSuccess)
        {
            return result;
        }

        result = RmRegisterResources(
            session,
            checked((uint)paths.Length),
            paths,
            0,
            IntPtr.Zero,
            0,
            IntPtr.Zero);
        if (result != ErrorSuccess)
        {
            _ = RmEndSession(session);
            session = 0;
        }

        return result;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(
        out uint sessionHandle,
        int sessionFlags,
        StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] fileNames,
        uint applicationCount,
        IntPtr applications,
        uint serviceCount,
        IntPtr serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfoCount,
        IntPtr processInfo,
        ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint sessionHandle, uint actionFlags, IntPtr statusCallback);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);
}
