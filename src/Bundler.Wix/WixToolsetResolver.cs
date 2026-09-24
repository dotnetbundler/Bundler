using System.Security.Cryptography;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Wix;

internal sealed record WixToolset(string CandlePath, string LightPath);

internal static class WixToolsetResolver
{
    internal const string Version = "3.14.1";
    internal const string ArchiveSha256 = "25AE0BB2A21FAC6B486C4B06155C9F463F2D845E7036BE0E9B1C98F4E48EA494";
    private const string ResourceName = "DotNet.Bundler.Wix.Resources.wix3141-tools.zip";

    internal static async Task<WixToolset> ResolveAsync(
        string cacheDirectory,
        string? archiveOverride,
        CancellationToken cancellationToken)
    {
        var archivePath = archiveOverride is null
            ? await MaterializeEmbeddedArchiveAsync(cacheDirectory, cancellationToken)
            : Path.GetFullPath(archiveOverride);
        var resolved = await ZipToolCache.ResolveToolAsync(
            archivePath,
            cacheDirectory,
            new ZipToolArchive(
                "wix-toolset",
                Version,
                ArchiveSha256,
                "candle.exe",
                ["light.exe", "wix.dll", "wconsole.dll", "winterop.dll", "darice.cub",
                 "Microsoft.Deployment.Compression.dll", "Microsoft.Deployment.Compression.Cab.dll",
                 "Microsoft.Deployment.Resources.dll", "Microsoft.Deployment.WindowsInstaller.dll",
                 "Microsoft.Deployment.WindowsInstaller.Package.dll", "LICENSE.TXT"]),
            cancellationToken);
        return new WixToolset(resolved.ExecutablePath, Path.Combine(resolved.DirectoryPath, "light.exe"));
    }

    private static async Task<string> MaterializeEmbeddedArchiveAsync(
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        using var resource = typeof(WixToolsetResolver).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException("The embedded WiX 3.14.1 archive is missing.");
        using var memory = new MemoryStream();
        await resource.CopyToAsync(memory, 81920, cancellationToken);
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(memory.ToArray())).Replace("-", "");
        if (!hash.Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The embedded WiX tool archive does not match the pinned SHA-256.");
        }

        cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        CheckDirectory(cacheDirectory);
        var resourcesDirectory = Path.Combine(cacheDirectory, "resources");
        Directory.CreateDirectory(resourcesDirectory);
        CheckDirectory(resourcesDirectory);
        var directory = Path.Combine(resourcesDirectory, "wix-3.14.1");
        Directory.CreateDirectory(directory);
        CheckDirectory(directory);
        var path = Path.Combine(directory, "wix3141-tools.zip");
        var lockPath = path + ".lock";
        if (File.Exists(lockPath) && IsReparse(lockPath))
        {
            throw new InvalidDataException("WiX resource lock is a reparse point.");
        }
        using var resourceLock = await AcquireLockAsync(lockPath, cancellationToken);
        if (File.Exists(path) && IsReparse(path))
        {
            throw new InvalidDataException("WiX resource archive is a reparse point.");
        }
        if (File.Exists(path) && HashFile(path).Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                memory.Position = 0;
                await memory.CopyToAsync(stream, 81920, cancellationToken);
            }
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporary, path);
            return path;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    private static void CheckDirectory(string path)
    {
        if (IsReparse(path))
        {
            throw new InvalidDataException("WiX resource cache directory is a reparse point: " + path);
        }
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }
}
