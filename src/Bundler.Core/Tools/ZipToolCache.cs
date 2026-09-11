using System.IO.Compression;
using System.Security.Cryptography;

namespace DotNet.Bundler.Core;

public sealed record ZipToolArchive(
    string Name,
    string Version,
    string Sha256,
    string ExecutableRelativePath);

public static class ZipToolCache
{
    public static Task<string> ResolveAsync(
        string archivePath,
        string cacheDirectory,
        ZipToolArchive archive,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(archive.Name, nameof(archive.Name));
        ValidateSegment(archive.Version, nameof(archive.Version));
        archivePath = Path.GetFullPath(archivePath);
        cacheDirectory = Path.GetFullPath(cacheDirectory);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The bundled tool archive is missing.", archivePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        VerifyArchive(archivePath, archive.Sha256);
        var toolDirectory = Path.Combine(
            cacheDirectory,
            $"{archive.Name}-{archive.Version}-{archive.Sha256.Substring(0, 12).ToLowerInvariant()}");
        var executablePath = Path.GetFullPath(Path.Combine(toolDirectory, archive.ExecutableRelativePath));
        if (!executablePath.StartsWith(
                toolDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The tool executable must stay inside the extracted archive directory.", nameof(archive));
        }
        if (File.Exists(executablePath))
        {
            return Task.FromResult(executablePath);
        }

        Directory.CreateDirectory(cacheDirectory);
        var stagingDirectory = Path.Combine(cacheDirectory, $".{archive.Name}-{archive.Version}-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory);
            var stagedExecutable = Path.Combine(stagingDirectory, archive.ExecutableRelativePath);
            if (!File.Exists(stagedExecutable))
            {
                throw new InvalidDataException(
                    $"The {archive.Name} archive does not contain '{archive.ExecutableRelativePath}'.");
            }

            try
            {
                Directory.Move(stagingDirectory, toolDirectory);
            }
            catch (IOException) when (File.Exists(executablePath))
            {
                // Another process populated the same verified cache entry first.
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }

        return File.Exists(executablePath)
            ? Task.FromResult(executablePath)
            : throw new InvalidOperationException(
                $"{archive.Name} extraction completed without producing '{archive.ExecutableRelativePath}'.");
    }

    private static void VerifyArchive(string archivePath, string expectedHash)
    {
        using var stream = File.OpenRead(archivePath);
        using var sha256 = SHA256.Create();
        var actualHash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Tool archive checksum mismatch. Expected {expectedHash}, got {actualHash}.");
        }
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Tool cache segments must be valid file names.", parameterName);
        }
    }
}
