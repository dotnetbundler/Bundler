using System.IO.Compression;

namespace DotNet.Bundler.Core;

public sealed record ZipToolArchive(
    string Name,
    string Version,
    string Sha256,
    string ExecutableRelativePath,
    IReadOnlyList<string>? RequiredRelativePaths = null);

public sealed record ResolvedZipTool(string DirectoryPath, string ExecutablePath);

/// <summary>
/// zip 形态内嵌工具的物化入口：校验整包 SHA-256 后从归档逐项读清单，
/// 实际物化走 <see cref="ToolCache"/> 的共享管线（锁、暂存、清单校验全同）。
/// </summary>
public static class ZipToolCache
{
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
        ToolCache.ValidateSegment(archive.Name, nameof(archive.Name));
        ToolCache.ValidateSegment(archive.Version, nameof(archive.Version));
        ToolCache.ValidateSha256(archive.Sha256, nameof(archive));
        archivePath = Path.GetFullPath(archivePath);
        cacheDirectory = Path.GetFullPath(cacheDirectory);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The bundled tool archive is missing.", archivePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var toolDirectory = Path.Combine(
            cacheDirectory,
            $"{archive.Name}-{archive.Version}-{archive.Sha256.Substring(0, 12).ToLowerInvariant()}");
        VerifyArchive(archivePath, archive.Sha256);
        var expectedFiles = ReadArchiveManifest(archivePath);
        var requiredPaths = (archive.RequiredRelativePaths ?? Array.Empty<string>())
            .Append(archive.ExecutableRelativePath)
            .Select(relativePath => ToolCache.NormalizeRelativePath(toolDirectory, relativePath, nameof(archive)))
            .Distinct(ToolCache.PathComparer)
            .ToArray();
        foreach (var requiredPath in requiredPaths)
        {
            if (!expectedFiles.Any(file => ToolCache.PathComparer.Equals(file.RelativePath, requiredPath)))
            {
                throw new InvalidDataException($"The {archive.Name} archive does not contain '{requiredPath}'.");
            }
        }

        var fileSet = new ToolFileSet(
            archive.Name,
            archive.Version,
            archive.Sha256,
            expectedFiles
                .Select(e => new ToolFileSpec(e.RelativePath, e.Sha256, e.Length))
                .ToArray());
        // openFile 首次落盘时才打开归档句柄：缓存命中/等锁期间不占文件锁。
        ZipArchive? zip = null;
        Dictionary<string, ZipArchiveEntry>? entriesByPath = null;
        ResolvedTool resolved;
        try
        {
            resolved = await ToolCache.ResolveAsync(
                cacheDirectory,
                fileSet,
                (file, _) =>
                {
                    zip ??= ZipFile.OpenRead(archivePath);
                    entriesByPath ??= zip.Entries
                        .Where(e => !IsDirectoryEntry(e))
                        .ToDictionary(e => NormalizeArchiveEntry(e.FullName), ToolCache.PathComparer);
                    return Task.FromResult<Stream>(entriesByPath[file.RelativePath].Open());
                },
                cancellationToken);
        }
        finally
        {
            zip?.Dispose();
        }
        var executablePath = ToolCache.ResolveToolPath(resolved.DirectoryPath, archive.ExecutableRelativePath);
        return new ResolvedZipTool(resolved.DirectoryPath, executablePath);
    }

    private static IReadOnlyList<ToolCache.ManifestEntry> ReadArchiveManifest(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var paths = new HashSet<string>(ToolCache.PathComparer);
        var entries = new List<ToolCache.ManifestEntry>();
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
            if (relativePath.Equals(ToolCache.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Tool archive path '{entry.FullName}' is reserved.");
            }
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException($"Tool archive contains duplicate path '{entry.FullName}'.");
            }
            using var stream = entry.Open();
            entries.Add(new ToolCache.ManifestEntry(relativePath, entry.Length, Hashes.Sha256Hex(stream)));
        }
        return entries.OrderBy(entry => entry.RelativePath, ToolCache.PathComparer).ToArray();
    }

    internal static string NormalizeArchiveEntry(string entryName)
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
        // netstandard2.0 的 ZipArchiveEntry 没有 ExternalAttributes API——
        // 归档链接检测在该资产下不可用，属 API 面差异而非选择。
        return false;
#endif
    }

    private static void VerifyArchive(string archivePath, string expectedHash)
    {
        var actualHash = Hashes.Sha256HexFile(archivePath);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Tool archive checksum mismatch. Expected {expectedHash}, got {actualHash}.");
        }
    }
}
