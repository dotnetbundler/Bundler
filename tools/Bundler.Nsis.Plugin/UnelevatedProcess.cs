using System.Runtime.InteropServices;
using System.Text;

namespace DotNet.Bundler.Nsis.Plugin;

internal static class UnelevatedProcess
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint LogonWithProfile = 0x00000001;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    internal static int Start(string executable, string arguments)
    {
        // 当前用户安装器本身没有提权，也没有 CreateProcessWithTokenW 所需的权限；
        // 此时直接创建子进程即可保持同一用户和完整性级别。
        if (!IsCurrentProcessElevated())
        {
            return StartWithCurrentToken(executable, arguments);
        }

        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return Win32Error.FileNotFound;
        }

        _ = GetWindowThreadProcessId(shellWindow, out var shellProcessId);
        using var shellProcess = new SafeKernelHandle(OpenProcess(ProcessQueryLimitedInformation, false, shellProcessId));
        if (shellProcess.IsInvalid)
        {
            return Marshal.GetLastWin32Error();
        }

        if (!OpenProcessToken(
                shellProcess.DangerousGetHandle(),
                TokenAssignPrimary | TokenDuplicate | TokenQuery,
                out var shellToken))
        {
            return Marshal.GetLastWin32Error();
        }

        using (shellToken)
        {
            // UAC 已禁用（EnableLUA=0）或外壳自身已提升时，不存在可降级的桌面
            // 令牌；此时当前令牌就代表该用户，直接用它启动。
            if (IsTokenElevated(shellToken))
            {
                return StartWithCurrentToken(executable, arguments);
            }

            var commandLine = BuildCommandLine(executable, arguments);
            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>()
            };

            if (!CreateEnvironmentBlock(out var environment, shellToken.DangerousGetHandle(), false))
            {
                return Marshal.GetLastWin32Error();
            }

            try
            {
                if (!CreateProcessWithTokenW(
                        shellToken.DangerousGetHandle(),
                        LogonWithProfile,
                        executable,
                        commandLine,
                        CreateUnicodeEnvironment,
                        environment,
                        Path.GetDirectoryName(executable),
                        ref startupInfo,
                        out var processInformation))
                {
                    return Marshal.GetLastWin32Error();
                }

                CloseHandle(processInformation.Thread);
                CloseHandle(processInformation.Process);
                return Win32Error.Success;
            }
            finally
            {
                DestroyEnvironmentBlock(environment);
            }
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            // 无法证明当前进程未提权时采用保守路径，避免意外以管理员身份启动应用。
            return true;
        }

        using (token)
        {
            return IsTokenElevated(token);
        }
    }

    private static bool IsTokenElevated(SafeKernelHandle token)
    {
        var elevation = new TokenElevation();
        var size = Marshal.SizeOf<TokenElevation>();
        // 读取失败时按已提升处理，沿用提升路径的保守策略。
        return !GetTokenInformation(
                   token.DangerousGetHandle(),
                   TokenInformationClass.Elevation,
                   ref elevation,
                   size,
                   out _) || elevation.IsElevated != 0;
    }

    private static int StartWithCurrentToken(string executable, string arguments)
    {
        var commandLine = BuildCommandLine(executable, arguments);
        var startupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0,
                IntPtr.Zero,
                Path.GetDirectoryName(executable),
                ref startupInfo,
                out var processInformation))
        {
            return Marshal.GetLastWin32Error();
        }

        CloseHandle(processInformation.Thread);
        CloseHandle(processInformation.Process);
        return Win32Error.Success;
    }

    private static StringBuilder BuildCommandLine(string executable, string arguments)
    {
        var commandLine = new StringBuilder(Quote(executable));
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            commandLine.Append(' ').Append(arguments);
        }
        return commandLine;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static class Win32Error
    {
        internal const int Success = 0;
        internal const int FileNotFound = 2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int IsElevated;
    }

    private static class TokenInformationClass
    {
        internal const int Elevation = 20;
    }

    private sealed class SafeKernelHandle : SafeHandle
    {
        private SafeKernelHandle() : base(IntPtr.Zero, true)
        {
        }

        internal SafeKernelHandle(IntPtr handle) : base(IntPtr.Zero, true)
        {
            SetHandle(handle);
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeKernelHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token,
        int tokenInformationClass,
        ref TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        uint logonFlags,
        string? applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
