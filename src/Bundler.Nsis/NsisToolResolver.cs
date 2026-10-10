using System.Runtime.InteropServices;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Nsis;

internal sealed record NsisToolset(string CompilerPath, string? DataDirectory);

internal static class NsisToolResolver
{
    public const string Version = "3.12-r1";
    public const string ArchiveSha256 = "41F15B7F7E3A0349185606EDE939C7B2E5B31FF76F0EB479143D6659EF1EDDBC";

    public static async Task<NsisToolset> ResolveAsync(
        string archivePath,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        var compilerRelativePath = GetCompilerRelativePath();
        var requiredPaths = new List<string>
        {
            Path.Combine("common", "nsisconf.nsh"),
            Path.Combine("common", "Include", "MUI2.nsh"),
            Path.Combine("common", "Plugins", "x86-unicode", "System.dll"),
            Path.Combine("common", "Stubs", "lzma-x86-unicode")
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            requiredPaths.Add(Path.Combine("hosts", "windows-i686", "zlib1.dll"));
        }

        var resolved = await ZipToolCache.ResolveToolAsync(
            archivePath,
            cacheDirectory,
            new ZipToolArchive(
                "nsis-toolset",
                Version,
                ArchiveSha256,
                compilerRelativePath,
                requiredPaths),
            cancellationToken);
        EnsureExecutable(resolved.ExecutablePath);
        return new NsisToolset(
            resolved.ExecutablePath,
            Path.Combine(resolved.DirectoryPath, "common"));
    }

    internal static string GetCompilerRelativePath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetCompilerRelativePath(OSPlatform.Windows, RuntimeInformation.ProcessArchitecture);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return GetCompilerRelativePath(OSPlatform.Linux, RuntimeInformation.ProcessArchitecture);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return GetCompilerRelativePath(OSPlatform.OSX, RuntimeInformation.ProcessArchitecture);
        }

        throw new PlatformNotSupportedException("NSIS compilation requires Windows, Linux, or macOS.");
    }

    internal static string GetCompilerRelativePath(OSPlatform operatingSystem, Architecture processArchitecture)
    {
        if (operatingSystem == OSPlatform.Windows)
        {
            return Path.Combine("hosts", "win-x86", "makensis.exe");
        }

        var architecture = processArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"NSIS compilation is not supported on {processArchitecture} hosts.")
        };
        if (operatingSystem == OSPlatform.Linux)
        {
            return Path.Combine("hosts", $"linux-{architecture}", "makensis");
        }
        if (operatingSystem == OSPlatform.OSX)
        {
            return Path.Combine("hosts", $"osx-{architecture}", "makensis");
        }

        throw new PlatformNotSupportedException("NSIS compilation requires Windows, Linux, or macOS.");
    }

    private static void EnsureExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        const uint ownerReadWriteExecuteGroupAndOtherReadExecute = 0x1ED; // 0755
        if (chmod(path, ownerReadWriteExecuteGroupAndOtherReadExecute) != 0)
        {
            throw new IOException(
                $"Unable to mark the NSIS compiler executable: {path}",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int chmod(string path, uint mode);
}
