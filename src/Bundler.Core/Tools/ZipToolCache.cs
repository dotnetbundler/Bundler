using System.IO.Compression;
using System.Security.Cryptography;

namespace DotNet.Bundler.Core;

public sealed record ZipToolArchive(
    string Name,
    string Version,
    string Sha256,
    string ExecutableRelativePath,
    IReadOnlyList<string>? RequiredRelativePaths = null);

public sealed record ResolvedZipTool(string DirectoryPath, string ExecutablePath);

public static class ZipToolCache
{
    public static async Task<string> ResolveAsync(
        string archivePath,
        string cacheDirectory,
        ZipToolArchive archive,
        CancellationToken cancellationToken = default)
        => (await ResolveToolAsync(archivePath, cacheDirectory, archive, cancellationToken)).ExecutablePath;

    public static Task<ResolvedZipTool> ResolveToolAsync(
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
                PathComparison))
        {
            throw new ArgumentException("The tool executable must stay inside the extracted archive directory.", nameof(archive));
        }
        var requiredPaths = (archive.RequiredRelativePaths ?? Array.Empty<string>())
            .Append(archive.ExecutableRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var relativePath in requiredPaths)
        {
            ValidateRelativePath(toolDirectory, relativePath, nameof(archive));
        }

        if (HasRequiredFiles(toolDirectory, requiredPaths))
        {
            return Task.FromResult(new ResolvedZipTool(toolDirectory, executablePath));
        }
        Directory.CreateDirectory(cacheDirectory);
        var stagingDirectory = Path.Combine(cacheDirectory, $".{archive.Name}-{archive.Version}-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory);
            var missingPath = requiredPaths.FirstOrDefault(relativePath =>
                !File.Exists(Path.Combine(stagingDirectory, relativePath)));
            if (missingPath is not null)
            {
                throw new InvalidDataException(
                    $"The {archive.Name} archive does not contain '{missingPath}'.");
            }

            try
            {
                Directory.Move(stagingDirectory, toolDirectory);
            }
            catch (IOException) when (HasRequiredFiles(toolDirectory, requiredPaths))
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

        return HasRequiredFiles(toolDirectory, requiredPaths)
            ? Task.FromResult(new ResolvedZipTool(toolDirectory, executablePath))
            : throw new InvalidOperationException(
                $"{archive.Name} extraction completed without producing all required files.");
    }

    private static bool HasRequiredFiles(string toolDirectory, IEnumerable<string> relativePaths) =>
        relativePaths.All(relativePath => File.Exists(Path.Combine(toolDirectory, relativePath)));

    private static void ValidateRelativePath(string toolDirectory, string relativePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Tool archive paths must be non-empty relative paths.", parameterName);
        }

        var fullPath = Path.GetFullPath(Path.Combine(toolDirectory, relativePath));
        var prefix = toolDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison))
        {
            throw new ArgumentException("Tool archive paths must stay inside the extracted archive directory.", parameterName);
        }
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

    private static StringComparison PathComparison =>
        Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
