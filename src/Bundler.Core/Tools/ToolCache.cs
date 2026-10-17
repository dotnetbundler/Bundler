using System.Diagnostics;
using System.Text;

namespace DotNet.Bundler.Core;

public sealed record ToolFileSpec(string RelativePath, string Sha256, long Length = -1);

public sealed record ToolFileSet(
    string Name,
    string Version,
    string ContentId,
    IReadOnlyList<ToolFileSpec> Files);

public sealed record ResolvedTool(string DirectoryPath);

/// <summary>
/// 内嵌工具的通用物化缓存：钉死哈希 + 内容寻址目录 + 跨进程锁 + 暂存原子落位 +
/// 清单逐件校验。zip 归档与裸内嵌资源走同一条管线（见 <see cref="ZipToolCache"/>）。
/// </summary>
public static class ToolCache
{
    internal const string ManifestFileName = ".bundler-tool-manifest";

    /// <summary>
    /// 全后端统一的默认缓存根：可写 LocalApplicationData（退化 TempPath）下的
    /// DotNetBundler/tools；调用方显式配置的目录优先。
    /// </summary>
    public static string ResolveCacheDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured!);
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.Combine(localData, "DotNetBundler", "tools");
    }

    /// <summary>
    /// 裸文件集合的内容标识：按路径排序后逐行 "path:sha" 的 SHA-256。
    /// 同一组钉死哈希永远得到同一 ContentId——缓存目录名据此内容寻址。
    /// </summary>
    public static string ComputeContentId(IEnumerable<ToolFileSpec> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.RelativePath, PathComparer))
        {
            builder.Append(file.RelativePath).Append(':').Append(file.Sha256.ToLowerInvariant()).Append('\n');
        }
        return Hashes.Sha256Hex(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>
    /// 校验并解析工具目录内的相对路径为绝对路径（拒绝越界）。
    /// </summary>
    public static string ResolveToolPath(string toolDirectory, string relativePath) =>
        ValidateRelativePath(toolDirectory, relativePath, nameof(relativePath));

    /// <summary>
    /// 物化一组钉死哈希的文件到 {name}-{version}-{contentId12} 缓存目录。
    /// <paramref name="openFile"/> 提供每件内容的只读流；写入时逐件校验哈希。
    /// </summary>
    public static async Task<ResolvedTool> ResolveAsync(
        string cacheDirectory,
        ToolFileSet set,
        Func<ToolFileSpec, CancellationToken, Task<Stream>> openFile,
        CancellationToken cancellationToken = default)
    {
        ValidateSegment(set.Name, nameof(set.Name));
        ValidateSegment(set.Version, nameof(set.Version));
        ValidateSha256(set.ContentId, nameof(set.ContentId));
        cacheDirectory = Path.GetFullPath(cacheDirectory);
        var toolDirectory = Path.Combine(
            cacheDirectory,
            $"{set.Name}-{set.Version}-{set.ContentId.Substring(0, 12).ToLowerInvariant()}");
        var plan = set.Files
            .Select(file => (
                File: file,
                Expected: new ManifestEntry(
                    NormalizeRelativePath(toolDirectory, file.RelativePath, nameof(set.Files)),
                    file.Length,
                    file.Sha256)))
            .OrderBy(pair => pair.Expected.RelativePath, PathComparer)
            .ToArray();
        if (plan.Any(p => p.Expected.RelativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Tool file set must not contain the reserved manifest path.", nameof(set.Files));
        }
        var expectedFiles = plan.Select(p => p.Expected).ToArray();
        if (expectedFiles.Select(e => e.RelativePath).Distinct(PathComparer).Count() != expectedFiles.Length)
        {
            throw new ArgumentException("Tool file set contains duplicate relative paths.", nameof(set.Files));
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
        if (!ValidateCache(toolDirectory, set.ContentId, expectedFiles))
        {
            SafeDeleteTree(toolDirectory);
            await InstallVerifiedAsync(
                cacheDirectory, toolDirectory, set, plan, expectedFiles, openFile, cancellationToken);
        }

        return ValidateCache(toolDirectory, set.ContentId, expectedFiles)
            ? new ResolvedTool(toolDirectory)
            : throw new InvalidOperationException(
                $"{set.Name} materialization completed without producing a valid cache entry.");
    }

    private static async Task InstallVerifiedAsync(
        string cacheDirectory,
        string toolDirectory,
        ToolFileSet set,
        IReadOnlyList<(ToolFileSpec File, ManifestEntry Expected)> plan,
        IReadOnlyList<ManifestEntry> expectedFiles,
        Func<ToolFileSpec, CancellationToken, Task<Stream>> openFile,
        CancellationToken cancellationToken)
    {
        // 崩溃遗留的同名暂存目录先清扫：.{name}-{version}-{sha12}-* 只由本函数创建；
        // glob 带 sha12——同 (name,version) 不同 sha 的并发 Resolve 互不清扫在跑暂存。
        var sha12 = set.ContentId.Substring(0, 12).ToLowerInvariant();
        foreach (var stale in Directory.GetDirectories(
                     cacheDirectory, $".{set.Name}-{set.Version}-{sha12}-*"))
        {
            try
            {
                Directory.Delete(stale, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        var stagingDirectory = Path.Combine(cacheDirectory, $".{set.Name}-{set.Version}-{sha12}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            foreach (var (file, expected) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(stagingDirectory,
                    expected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = await openFile(file, cancellationToken);
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, 81920, cancellationToken);
                }
                // 源流刚写完即核：钉死哈希不符立即作废整个暂存。
                var actual = Hashes.Sha256HexFile(destination);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Tool file '{file.RelativePath}' failed its pinned SHA-256 check ({actual}).");
                }
            }

            WriteManifest(stagingDirectory, set.ContentId, expectedFiles);
            if (!ValidateCache(stagingDirectory, set.ContentId, expectedFiles))
            {
                throw new InvalidDataException($"The materialized {set.Name} files failed integrity validation.");
            }
            Directory.Move(stagingDirectory, toolDirectory);
        }
        finally
        {
            SafeDeleteTree(stagingDirectory);
        }
    }

    /// <summary>50ms 轮询排他文件锁；资源物化等场景也共享此件。
    /// 有界等待（与 <see cref="ToolRunner.ProcessTimeout"/> 同限）——持锁方挂死不再造成无限自旋。</summary>
    internal static async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        TimeSpan? missingSince = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (File.Exists(lockPath))
                {
                    missingSince = null;   // 争用：锁文件被持方占着
                }
                else
                {
                    // 建不出文件：释放+删文件竞态窗（WixBundleBackend finally 删锁件）给 1s
                    // 缺席宽限——窗内下一次 OpenOrCreate 会自己重建成功；持续缺席=确定性
                    // 非争用错误（路径/权限/目录缺），直抛不报超时。
                    missingSince ??= started.Elapsed;
                    if (started.Elapsed - missingSince.Value >= TimeSpan.FromSeconds(1))
                    {
                        throw;
                    }
                }
                if (started.Elapsed >= ToolRunner.ProcessTimeout)
                {
                    throw new TimeoutException(
                        $"Tool cache lock '{lockPath}' was not released within {ToolRunner.ProcessTimeout}.");
                }
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static bool ValidateCache(
        string toolDirectory,
        string contentId,
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
            if (!ReadManifest(toolDirectory, contentId, expectedFiles))
            {
                return false;
            }
            foreach (var expected in expectedFiles)
            {
                var path = Path.Combine(toolDirectory, expected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(path);
                if ((expected.Length >= 0 && info.Length != expected.Length) ||
                    !Hashes.Sha256HexFile(path).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
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
        string contentId,
        IReadOnlyList<ManifestEntry> entries)
    {
        using var writer = new BinaryWriter(File.Create(Path.Combine(toolDirectory, ManifestFileName)));
        writer.Write(1);
        writer.Write(contentId.ToUpperInvariant());
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
        string contentId,
        IReadOnlyList<ManifestEntry> expected)
    {
        var path = Path.Combine(toolDirectory, ManifestFileName);
        if (!File.Exists(path) || IsReparsePoint(path))
        {
            return false;
        }
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadInt32() != 1 ||
            !reader.ReadString().Equals(contentId, StringComparison.OrdinalIgnoreCase) ||
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

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string ValidateRelativePath(string toolDirectory, string relativePath, string paramName)
    {
        var normalized = NormalizeRelativePath(toolDirectory, relativePath, paramName);
        return Path.GetFullPath(Path.Combine(toolDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    internal static string NormalizeRelativePath(string toolDirectory, string relativePath, string paramName)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Tool cache paths must be non-empty relative paths.", paramName);
        }
        var fullPath = Path.GetFullPath(Path.Combine(toolDirectory, relativePath));
        var prefix = toolDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathStringComparison))
        {
            throw new ArgumentException("Tool cache paths must stay inside the cache entry directory.", paramName);
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

    internal static void ValidateSha256(string value, string paramName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 pins must contain exactly 64 hexadecimal characters.", paramName);
        }
    }

    internal static void ValidateSegment(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value is "." or "..")
        {
            throw new ArgumentException("Tool cache segments must be valid file names.", paramName);
        }
    }

    internal sealed record ManifestEntry(string RelativePath, long Length, string Sha256);

    internal static StringComparer PathComparer =>
        Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathStringComparison =>
        Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
