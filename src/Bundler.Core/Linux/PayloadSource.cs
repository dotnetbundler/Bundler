using DotNet.Bundler;

namespace DotNet.Bundler.Core;

internal enum PayloadSourceKind
{
    Directory,
    File,
    /// <summary>In-tree symbolic link preserved as a link entry (0777 + target).</summary>
    Symlink
}

/// <summary>
/// A neutral payload item produced by <see cref="PayloadSource"/> walks; each
/// backend maps items onto its own entry/dedup semantics.
/// </summary>
internal sealed record PayloadSourceItem(
    PayloadSourceKind Kind,
    string ArchivePath,
    string? SourcePath,
    int Mode,
    string? LinkTarget = null);

/// <summary>
/// Shared input-tree/resource enumeration for POSIX package backends (deb, rpm,
/// apk): ordered deterministic walks, non-regular files warn+skipped, the main
/// executable gets 0755, resource destinations must stay inside the payload.
/// Link policy is uniform: links whose resolved target stays inside the walked
/// tree are preserved as symlink entries (the target is re-emitted relative to
/// the link's directory so the package stays relocatable); links pointing
/// outside, unreadable links, and dangling links reject — a package cannot
/// silently inherit host content or embed dead references.
/// </summary>
internal static class PayloadSource
{
    private const int DirMode = 493;   // 0755
    private const int FileMode = 420;  // 0644
    private const int LinkMode = 511;  // 0777 — symlink entries carry the conventional mode

    /// <summary>Input tree: ordered directory items first, then ordered file/link items.</summary>
    internal static IEnumerable<PayloadSourceItem> CollectInput(
        string inputDirectory,
        string installRoot,
        string mainExecutable,
        IBundleLogger logger)
    {
        mainExecutable = mainExecutable.Replace('\\', '/');
        var root = CheckedTreeRoot(inputDirectory, "input directory");
        var directories = new List<PayloadSourceItem>();
        var leaves = new List<PayloadSourceItem>();
        WalkTree(root, root, installRoot, includeDirectories: true,
            relative => string.Equals(relative, mainExecutable, StringComparison.Ordinal)
                ? DirMode : FileMode,
            directories, leaves, logger);
        foreach (var item in directories.OrderBy(item => item.ArchivePath, StringComparer.Ordinal))
        {
            yield return item;
        }
        foreach (var item in leaves.OrderBy(item => item.ArchivePath, StringComparer.Ordinal))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Resource walk: destination validated to stay inside the payload; directory
    /// sources map recursively, file sources map directly.
    /// <paramref name="includeDirectories"/> preserves empty resource directories.
    /// </summary>
    internal static IEnumerable<PayloadSourceItem> CollectResources(
        IReadOnlyList<BundleResourceConfiguration> resources,
        string installRoot,
        IBundleLogger logger,
        bool includeDirectories = false)
    {
        foreach (var resource in resources)
        {
            var target = resource.Destination.Replace('\\', '/').Trim('/');
            if (target.Length == 0 || target.Split('/').Contains(".."))
            {
                throw new ArgumentException(
                    $"The resource target must stay inside the payload: '{resource.Destination}'.");
            }
            var source = Path.GetFullPath(resource.Source);
            if (IsLink(source))
            {
                // 先判链接再判存在性——悬空链接也能拿到统一的链接专错。
                throw new ArgumentException(
                    $"The resource source '{resource.Source}' is a link — " +
                    "point the resource at the real file/directory it refers to.");
            }
            if (Directory.Exists(source))
            {
                var directories = new List<PayloadSourceItem>();
                var leaves = new List<PayloadSourceItem>();
                WalkTree(source, source, installRoot + "/" + target, includeDirectories,
                    _ => FileMode, directories, leaves, logger);
                var ordered = includeDirectories
                    ? directories.OrderBy(item => item.ArchivePath, StringComparer.Ordinal)
                        .Concat(leaves.OrderBy(item => item.ArchivePath, StringComparer.Ordinal))
                    : leaves.OrderBy(item => item.ArchivePath, StringComparer.Ordinal);
                foreach (var item in ordered)
                {
                    yield return item;
                }
            }
            else if (File.Exists(source))
            {
                if (!UnixFileTypes.IsRegularFile(source))
                {
                    logger.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {source}");
                    continue;
                }
                yield return new PayloadSourceItem(
                    PayloadSourceKind.File, installRoot + "/" + target, source, FileMode);
            }
            else
            {
                throw new FileNotFoundException(
                    $"The resource source does not exist: {source}", source);
            }
        }
    }

    /// <summary>
    /// Depth-first walk of <paramref name="hostRoot"/> collecting directory
    /// items (when <paramref name="includeDirectories"/>) and leaf items
    /// (files + preserved links) into two lists. Reparse points are classified
    /// by <see cref="ResolveInTreeTarget"/>; anything else that is neither a
    /// readable directory nor a regular file warns and is skipped.
    /// </summary>
    private static void WalkTree(
        string treeRoot,
        string hostDirectory,
        string archiveBase,
        bool includeDirectories,
        Func<string, int> modeFor,
        List<PayloadSourceItem> directories,
        List<PayloadSourceItem> leaves,
        IBundleLogger logger)
    {
        foreach (var name in Directory.GetFileSystemEntries(hostDirectory)
                     .Select(Path.GetFileName)
                     .OrderBy(entry => entry, StringComparer.Ordinal))
        {
            var entry = Path.Combine(hostDirectory, name!);
            var relative = RelativePath(treeRoot, entry); // 仅供 modeFor 判主执行文件
            var archivePath = archiveBase + "/" + name;
            var attributes = AttributesOrReparse(entry, out var isLink);
            if (isLink)
            {
                // 目录链接在 Windows 宿主上无法区分 junction——统一拒绝；
                // 其余链接判树内外，树内保留为符号链接条目。
                if (Directory.Exists(entry) &&
                    Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    throw new ArgumentException(
                        $"The payload path '{entry}' is a directory link on a Windows host — " +
                        "junctions cannot be expressed as package symlinks; use the real directory.");
                }
                if (!LinkProbe.TryReadTarget(entry, out var raw) || raw.Length == 0)
                {
                    // Windows 的非链接 reparse 点（云端占位、去重 chunk 等）维持
                    // 基线的警告跳过；POSIX 上读不到目标的就是坏链接，拒绝。
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        logger.Log(BundleLogLevel.Warning,
                            $"Skipping non-link reparse point: {entry}");
                        continue;
                    }
                    throw new ArgumentException(
                        $"The payload path '{entry}' is a link whose target cannot be read — " +
                        "links are only supported when they resolve inside the payload tree.");
                }
                var target = ResolveInTreeTarget(treeRoot, entry, raw);
                // 链接是叶条目（与 includeDirectories 无关）——恒入 leaves。
                leaves.Add(new PayloadSourceItem(
                    PayloadSourceKind.Symlink, archivePath, entry, LinkMode, target));
                continue;
            }
            if ((attributes & FileAttributes.Directory) != 0 || Directory.Exists(entry))
            {
                if (includeDirectories)
                {
                    directories.Add(new PayloadSourceItem(
                        PayloadSourceKind.Directory, archivePath, null, DirMode));
                }
                WalkTree(treeRoot, entry, archivePath, includeDirectories,
                    modeFor, directories, leaves, logger);
                continue;
            }
            if (!UnixFileTypes.IsRegularFile(entry))
            {
                logger.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {entry}");
                continue;
            }
            leaves.Add(new PayloadSourceItem(
                PayloadSourceKind.File, archivePath, entry, modeFor(ToPosixPath(relative))));
        }
    }

    /// <summary>
    /// Reads attributes without following the link. A missing/dangling entry
    /// whose target is still readable via readlink reports as a link so it can
    /// be rejected with a proper message instead of a stat failure.
    /// </summary>
    private static FileAttributes AttributesOrReparse(string path, out bool isLink)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            isLink = (attributes & FileAttributes.ReparsePoint) != 0;
            return attributes;
        }
        catch
        {
            // lstat-based reads disagree on dangling links across hosts; a
            // readable link target is enough to classify it as a link.
            isLink = LinkProbe.TryReadTarget(path, out _);
            return (FileAttributes)(-1);
        }
    }

    /// <summary>
    /// Resolves a link target to an absolute path and requires it to stay
    /// inside <paramref name="treeRoot"/> and exist; returns the target
    /// re-emitted relative to the link's parent directory in POSIX form.
    /// </summary>
    private static string ResolveInTreeTarget(string treeRoot, string linkPath, string raw)
    {
        var linkDirectory = Path.GetDirectoryName(Path.GetFullPath(linkPath))!;
        var resolved = Path.GetFullPath(
            Path.IsPathRooted(raw) ? raw : Path.Combine(linkDirectory, raw));
        var root = Path.GetFullPath(treeRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(root, PathText.Comparison))
        {
            throw new ArgumentException(
                $"The payload link '{linkPath}' points outside the payload tree " +
                $"('{raw}') — links must resolve inside the packaged directory.");
        }
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
        {
            throw new ArgumentException(
                $"The payload link '{linkPath}' is dangling ('{raw}') — " +
                "the link target must exist inside the payload tree.");
        }
        var relative = MakeRelative(linkDirectory, resolved);
        // 指向自身所在目录（`ln -s . link`）时相对路径为空——POSIX 上 "." 就是
        // 合法目标文本，原样保留而不是产空 LinkTarget。
        return relative.Length == 0 ? "." : relative;
    }

    /// <summary>POSIX relative path from <paramref name="fromDirectory"/> to <paramref name="toPath"/>.</summary>
    private static string MakeRelative(string fromDirectory, string toPath)
    {
        char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var from = Path.GetFullPath(fromDirectory)
            .TrimEnd(separators).Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var to = Path.GetFullPath(toPath)
            .TrimEnd(separators).Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < from.Length && common < to.Length &&
               string.Equals(from[common], to[common], PathText.Comparison))
        {
            common++;
        }
        return string.Join("/",
            Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common)));
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return LinkProbe.TryReadTarget(path, out _);
        }
    }

    private static string CheckedTreeRoot(string directory, string what)
    {
        var root = Path.GetFullPath(directory);
        if (IsLink(root))
        {
            throw new ArgumentException(
                $"The {what} '{directory}' is itself a link — point it at the real directory " +
                "so link targets can be classified against a stable tree root.");
        }
        return root;
    }

    private static string RelativePath(string baseDirectory, string path)
    {
        var prefix = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(prefix, PathText.Comparison))
        {
            throw new InvalidOperationException(
                $"A payload path escapes its base directory: '{path}'.");
        }
        return full.Substring(prefix.Length);
    }

    private static string ToPosixPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
