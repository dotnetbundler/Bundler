using System.Runtime.InteropServices;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Nsis;

internal static class NsisToolResolver
{
    public const string Version = "3.12";
    public const string ArchiveSha256 = "56581F90DB321581C5381193D796FFFCF2D24B2F8FED2160A6C6A3BAA67F2C4F";

    public static Task<string> ResolveAsync(
        string archivePath,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("The bundled NSIS compiler runs on Windows hosts only.");
        }

        return ZipToolCache.ResolveAsync(
            archivePath,
            cacheDirectory,
            new ZipToolArchive(
                "nsis",
                Version,
                ArchiveSha256,
                Path.Combine($"nsis-{Version}", "makensis.exe")),
            cancellationToken);
    }
}
