using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace Bundler.Core.Tools;

public static class NsisToolResolver
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

        archivePath = Path.GetFullPath(archivePath);
        cacheDirectory = Path.GetFullPath(cacheDirectory);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The bundled NSIS archive is missing.", archivePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        VerifyArchive(archivePath);

        var toolDirectory = Path.Combine(cacheDirectory, $"nsis-{Version}-{ArchiveSha256.Substring(0, 12).ToLowerInvariant()}");
        var compilerPath = Path.Combine(toolDirectory, $"nsis-{Version}", "makensis.exe");
        if (File.Exists(compilerPath))
        {
            return Task.FromResult(compilerPath);
        }

        Directory.CreateDirectory(cacheDirectory);
        var stagingDirectory = Path.Combine(cacheDirectory, $".nsis-{Version}-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory);
            var stagedCompiler = Path.Combine(stagingDirectory, $"nsis-{Version}", "makensis.exe");
            if (!File.Exists(stagedCompiler))
            {
                throw new InvalidDataException("The NSIS archive does not contain makensis.exe at the expected path.");
            }

            try
            {
                Directory.Move(stagingDirectory, toolDirectory);
            }
            catch (IOException) when (File.Exists(compilerPath))
            {
                // Another concurrent build populated the same verified cache.
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }

        return File.Exists(compilerPath)
            ? Task.FromResult(compilerPath)
            : throw new InvalidOperationException("NSIS extraction completed without producing makensis.exe.");
    }

    private static void VerifyArchive(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        var actualHash = BitConverter.ToString(hash).Replace("-", string.Empty);
        if (!actualHash.Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"NSIS archive checksum mismatch. Expected {ArchiveSha256}, got {actualHash}.");
        }
    }
}
