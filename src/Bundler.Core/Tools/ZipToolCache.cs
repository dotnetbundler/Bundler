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
    private const string ManifestFileName = ".bundler-tool-manifest";

    public static async Task<string> ResolveAsync(
        string archivePath,
        string cacheDirectory,
        ZipToolArchive archive,
        CancellationToken cancellationToken = default)
        => (await ResolveToolAsync(archivePath, cacheDirectory, archive, cancellationToken)).ExecutablePath;

    public static async Task<ResolvedZipTool> ResolveToolAsync(
        string archivePath,
        string cacheDirectory,
        ZipToolArchive archive,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(archive.Name, nameof(archive.Name));
        ValidateSegment(archive.Version, nameof(archive.Version));
        ValidateSha256(archive.Sha256, nameof(archive));
        archivePath = Path.GetFullPath(archivePath);
        cacheDirectory = Path.GetFullPath(cacheDirectory);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The bundled tool archive is missing.", archivePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        VerifyArchive(archivePath, archive.Sha256);
        var expectedFiles = ReadArchiveManifest(archivePath);
        var toolDirectory = Path.Combine(
            cacheDirectory,
            $"{archive.Name}-{archive.Version}-{archive.Sha256.Substring(0, 12).ToLowerInvariant()}");
        var executablePath = ValidateRelativePath(toolDirectory, archive.ExecutableRelativePath, nameof(archive));
        var requiredPaths = (archive.RequiredRelativePaths ?? Array.Empty<string>())
            .Append(archive.ExecutableRelativePath)
            .Select(relativePath => NormalizeRelativePath(toolDirectory, relativePath, nameof(archive)))
            .Distinct(PathComparer)
            .ToArray();
        foreach (var requiredPath in requiredPaths)
        {
            if (!expectedFiles.Any(file => PathComparer.Equals(file.RelativePath, requiredPath)))
            {
                throw new InvalidDataException($"The {archive.Name} archive does not contain '{requiredPath}'.");
            }
        }

        Directory.CreateDirectory(cacheDirectory);
        if (IsReparsePoint(cacheDirectory))
        {
            throw new InvalidDataException($"Tool cache root must not be a reparse point: '{cacheDirectory}'.");
        }
        var lockPath = toolDirectory + ".lock";
        if (File.Exists(lockPath) && IsReparsePoint(lockPath))
        {
            throw new InvalidDataException($"Tool cache lock must not be a reparse point: '{lockPath}'.");
        }
        using var cacheLock = await AcquireLockAsync(lockPath, cancellationToken);
        if (!ValidateCache(toolDirectory, archive.Sha256, expectedFiles))
        {
            SafeDeleteTree(toolDirectory);
            await ExtractVerifiedAsync(
                archivePath,
                cacheDirectory,
                toolDirectory,
                archive,
                expectedFiles,
                cancellationToken);
        }

        return ValidateCache(toolDirectory, archive.Sha256, expectedFiles)
            ? new ResolvedZipTool(toolDirectory, executablePath)
            : throw new InvalidOperationException(
                $"{archive.Name} extraction completed without producing a valid cache entry.");
    }

    private static async Task ExtractVerifiedAsync(
        string archivePath,
        string cacheDirectory,
        string toolDirectory,
        ZipToolArchive archive,
        IReadOnlyList<ManifestEntry> expectedFiles,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = Path.Combine(cacheDirectory, $".{archive.Name}-{archive.Version}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsArchiveLink(entry))
                    {
                        throw new InvalidDataException($"Tool archives must not contain links: '{entry.FullName}'.");
                    }
                    if (IsDirectoryEntry(entry))
                    {
                        if (!string.IsNullOrEmpty(entry.FullName))
                        {
                            Directory.CreateDirectory(ValidateArchiveEntryPath(stagingDirectory, entry.FullName));
                        }
                        continue;
                    }

                    var destination = ValidateArchiveEntryPath(stagingDirectory, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, 81920, cancellationToken);
                }
            }

            WriteManifest(stagingDirectory, archive.Sha256, expectedFiles);
            if (!ValidateCache(stagingDirectory, archive.Sha256, expectedFiles))
            {
                throw new InvalidDataException($"The extracted {archive.Name} archive failed integrity validation.");
            }
            Directory.Move(stagingDirectory, toolDirectory);
        }
        finally
        {
            SafeDeleteTree(stagingDirectory);
        }
    }

    private static async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static IReadOnlyList<ManifestEntry> ReadArchiveManifest(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var paths = new HashSet<string>(PathComparer);
        var entries = new List<ManifestEntry>();
        foreach (var entry in zip.Entries)
        {
            if (IsArchiveLink(entry))
            {
                throw new InvalidDataException($"Tool archives must not contain links: '{entry.FullName}'.");
            }
            if (IsDirectoryEntry(entry))
            {
                continue;
            }
            var relativePath = NormalizeArchiveEntry(entry.FullName);
            if (relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Tool archive path '{entry.FullName}' is reserved.");
            }
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException($"Tool archive contains duplicate path '{entry.FullName}'.");
            }
            using var stream = entry.Open();
            entries.Add(new ManifestEntry(relativePath, entry.Length, HashStream(stream)));
        }
        return entries.OrderBy(entry => entry.RelativePath, PathComparer).ToArray();
    }

    private static bool ValidateCache(
        string toolDirectory,
        string archiveSha256,
        IReadOnlyList<ManifestEntry> expectedFiles)
    {
        try
        {
            if (!Directory.Exists(toolDirectory) || IsReparsePoint(toolDirectory))
            {
                return false;
            }
            var actualFiles = EnumerateSafeFiles(toolDirectory)
                .Select(path => RelativePath(toolDirectory, path))
                .Where(path => !path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, PathComparer)
                .ToArray();
            if (!actualFiles.SequenceEqual(expectedFiles.Select(entry => entry.RelativePath), PathComparer))
            {
                return false;
            }
            if (!ReadManifest(toolDirectory, archiveSha256, expectedFiles))
            {
                return false;
            }
            foreach (var expected in expectedFiles)
            {
                var path = Path.Combine(toolDirectory, expected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(path);
                if (info.Length != expected.Length ||
                    !HashFile(path).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (IsReparsePoint(directory))
            {
                throw new InvalidDataException($"Tool cache contains a reparse point: '{directory}'.");
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsReparsePoint(path))
                {
                    throw new InvalidDataException($"Tool cache contains a reparse point: '{path}'.");
                }
                if (Directory.Exists(path))
                {
                    pending.Push(path);
                }
                else
                {
                    yield return path;
                }
            }
        }
    }

    private static void WriteManifest(
        string toolDirectory,
        string archiveSha256,
        IReadOnlyList<ManifestEntry> entries)
    {
        using var writer = new BinaryWriter(File.Create(Path.Combine(toolDirectory, ManifestFileName)));
        writer.Write(1);
        writer.Write(archiveSha256.ToUpperInvariant());
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.RelativePath);
            writer.Write(entry.Length);
            writer.Write(entry.Sha256);
        }
    }

    private static bool ReadManifest(
        string toolDirectory,
        string archiveSha256,
        IReadOnlyList<ManifestEntry> expected)
    {
        var path = Path.Combine(toolDirectory, ManifestFileName);
        if (!File.Exists(path) || IsReparsePoint(path))
        {
            return false;
        }
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadInt32() != 1 ||
            !reader.ReadString().Equals(archiveSha256, StringComparison.OrdinalIgnoreCase) ||
            reader.ReadInt32() != expected.Count)
        {
            return false;
        }
        foreach (var entry in expected)
        {
            if (!reader.ReadString().Equals(entry.RelativePath, PathStringComparison) ||
                reader.ReadInt64() != entry.Length ||
                !reader.ReadString().Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return reader.BaseStream.Position == reader.BaseStream.Length;
    }

    private static void DeleteWithRetry(Action delete)
    {
        // Windows 上刚退出的工具进程可能短暂持有原生 dll/文件锁——瞬态删除冲突用有限重试吸收
        const int attempts = 10;
        for (var i = 0; ; i++)
        {
            try
            {
                delete();
                return;
            }
            catch (Exception ex) when (i < attempts - 1 &&
                (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void SafeDeleteTree(string path)
    {
        if (File.Exists(path) && !Directory.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            DeleteWithRetry(() => File.Delete(path));
            return;
        }
        if (!Directory.Exists(path))
        {
            return;
        }
        if (IsReparsePoint(path))
        {
            DeleteWithRetry(() => Directory.Delete(path));
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (Directory.Exists(entry))
            {
                SafeDeleteTree(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                DeleteWithRetry(() => File.Delete(entry));
            }
        }
        DeleteWithRetry(() => Directory.Delete(path));
    }

    private static string ValidateArchiveEntryPath(string root, string entryName)
    {
        var normalized = NormalizeArchiveEntry(entryName);
        var destination = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, PathStringComparison))
        {
            throw new InvalidDataException($"Tool archive path escapes its extraction directory: '{entryName}'.");
        }
        return destination;
    }

    private static string NormalizeArchiveEntry(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0 ||
            entryName.StartsWith("/", StringComparison.Ordinal) ||
            entryName.StartsWith("\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Tool archive contains an invalid path: '{entryName}'.");
        }
        var normalized = entryName.Replace('\\', '/').TrimEnd('/');
        if (normalized.Split('/').Any(component => component is "" or "." or ".."))
        {
            throw new InvalidDataException($"Tool archive path must stay inside its extraction directory: '{entryName}'.");
        }
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            throw new InvalidDataException($"Tool archive contains a rooted path: '{entryName}'.");
        }
        return normalized;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
        entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static bool IsArchiveLink(ZipArchiveEntry entry)
    {
#if NET8_0_OR_GREATER
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixType == 0xA000 ||
            (((FileAttributes)entry.ExternalAttributes) & FileAttributes.ReparsePoint) != 0;
#else
        return false;
#endif
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string ValidateRelativePath(string toolDirectory, string relativePath, string parameterName)
    {
        var normalized = NormalizeRelativePath(toolDirectory, relativePath, parameterName);
        return Path.GetFullPath(Path.Combine(toolDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string NormalizeRelativePath(string toolDirectory, string relativePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Tool archive paths must be non-empty relative paths.", parameterName);
        }
        var fullPath = Path.GetFullPath(Path.Combine(toolDirectory, relativePath));
        var prefix = toolDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathStringComparison))
        {
            throw new ArgumentException("Tool archive paths must stay inside the extracted archive directory.", parameterName);
        }
        return RelativePath(toolDirectory, fullPath);
    }

    private static string RelativePath(string root, string path)
    {
        var prefix = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(prefix, PathStringComparison))
        {
            throw new ArgumentException("Path must stay inside its root.", nameof(path));
        }
        return fullPath.Substring(prefix.Length).Replace('\\', '/');
    }

    private static void VerifyArchive(string archivePath, string expectedHash)
    {
        using var stream = File.OpenRead(archivePath);
        var actualHash = HashStream(stream);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Tool archive checksum mismatch. Expected {expectedHash}, got {actualHash}.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return HashStream(stream);
    }

    private static string HashStream(Stream stream)
    {
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Tool archive SHA-256 must contain exactly 64 hexadecimal characters.", parameterName);
        }
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value is "." or "..")
        {
            throw new ArgumentException("Tool cache segments must be valid file names.", parameterName);
        }
    }

    private sealed record ManifestEntry(string RelativePath, long Length, string Sha256);

    private static StringComparer PathComparer =>
        Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathStringComparison =>
        Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
