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
/// silently inherit host content or embed dead references. On top of host-side
/// resolution, <see cref="ValidateLinkCoverage"/> also rejects links whose target
/// would not materialize inside the package itself (e.g. an empty directory when
/// <c>includeDirectories</c> is off, or a skipped non-regular file).
/// </summary>
internal static class PayloadSource
{
    private const int DirMode = 493;   // 0755
    private const int FileMode = 420;  // 0644
    private const int LinkMode = 511;  // 0777 — symlink entries carry the conventional mode
    // 与 FileTree.CopyTree 调用方一致的深度封顶：链接不递归（链成叶），但病态
    // 深目录树仍会把递归栈顶爆——按同口径 64 层拒绝。
    private const int MaxTreeDepth = 64;

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
        ValidateLinkCoverage(directories.Concat(leaves).ToList(),
            leaves.Where(item => item.Kind == PayloadSourceKind.Symlink).ToList());
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
        // 覆盖核查跨资源联合——链接目标目录可能被另一资源的内容填满（如链接
        // 指向 plugins/ 而插件经独立资源装入），逐资源核查会误拒。
        var allItems = new List<PayloadSourceItem>();
        var links = new List<PayloadSourceItem>();
        foreach (var resource in resources)
        {
            var target = resource.Destination.Replace('\\', '/').Trim('/');
            // 空段（a//b）与 '.' 段同拒——出件归档路径带字面 /./ 会与
            // ResolveArchiveTarget 的归一化口径失配，让覆盖核查误拒合法链接。
            if (target.Length == 0 ||
                target.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            {
                throw new ArgumentException(
                    $"The resource target must be a clean path inside the payload: '{resource.Destination}'.");
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
                allItems.AddRange(directories);
                allItems.AddRange(leaves);
                links.AddRange(leaves.Where(item => item.Kind == PayloadSourceKind.Symlink));
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
                var item = new PayloadSourceItem(
                    PayloadSourceKind.File, installRoot + "/" + target, source, FileMode);
                allItems.Add(item);
                yield return item;
            }
            else
            {
                throw new FileNotFoundException(
                    $"The resource source does not exist: {source}", source);
            }
        }
        ValidateLinkCoverage(allItems, links);
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
        IBundleLogger logger,
        int depth = 0)
    {
        if (depth > MaxTreeDepth)
        {
            throw new InvalidDataException(
                $"The payload tree exceeds the depth limit of {MaxTreeDepth}: {hostDirectory}");
        }
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
                    modeFor, directories, leaves, logger, depth + 1);
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
            // 非链接时返回 0 而非 -1：-1 全位置 1 会被 Directory 位检查误判
            // 成目录，让 stat 失败的瞬态项走 WalkTree 炸掉；0 则落非常规件跳过。
            isLink = LinkProbe.TryReadTarget(path, out _);
            return (FileAttributes)0;
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
        var rootNoSlash = Path.GetFullPath(treeRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // 树内 = 严格前缀或就是树根本身（`ln -s .` 指向根的链接合法）。
        if (!resolved.StartsWith(rootNoSlash + Path.DirectorySeparatorChar, PathText.Comparison) &&
            !string.Equals(resolved, rootNoSlash, PathText.Comparison))
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

    /// <summary>
    /// 包内覆盖核查：链接目标（归一到归档路径）必须等于某个出件条目、或落在
    /// 某个出件条目的祖先目录链上——出件的文件/链接解包时隐式物化全部祖先
    /// 目录。includeDirectories=false 下空目录不出件、被跳过的非常规件（fifo
    /// 等）也不出件，指向它们的链接主机侧虽存活、包内照样悬空——按悬空同口径
    /// 拒绝，不产静默死引用。
    /// </summary>
    private static void ValidateLinkCoverage(
        IReadOnlyList<PayloadSourceItem> emittedItems,
        IReadOnlyList<PayloadSourceItem> links)
    {
        if (links.Count == 0)
        {
            return;
        }
        var emitted = emittedItems
            .Select(item => item.ArchivePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        foreach (var link in links)
        {
            var resolved = ResolveArchiveTarget(link.ArchivePath, link.LinkTarget!);
            // 两探：等值命中 resolved（目标是出件文件/目录/链接）；前缀命中
            // resolved+"/" 下界（目标作为出件项的祖先目录被隐式物化）——
            // 序区间 (resolved, resolved+"/") 内的旁路兄弟件（plugins.bak 之类
            // '.'/'-' 续名 < '/' 的项）会盖住 resolved 的插入点，不能直接看 ~idx。
            var covered = emitted.BinarySearch(resolved, StringComparer.Ordinal) >= 0;
            if (!covered)
            {
                var prefixIndex = emitted.BinarySearch(resolved + "/", StringComparer.Ordinal);
                if (prefixIndex < 0)
                {
                    prefixIndex = ~prefixIndex;
                }
                covered = prefixIndex < emitted.Count &&
                    emitted[prefixIndex].StartsWith(resolved + "/", StringComparison.Ordinal);
            }
            if (!covered)
            {
                throw new ArgumentException(
                    $"The payload link '{link.SourcePath}' resolves to '{link.LinkTarget}' " +
                    "which produces no packaged entry — the link would dangle inside " +
                    "the package (empty directory targets are not emitted; point the " +
                    "link at packaged content).");
            }
        }
    }

    /// <summary>
    /// Composes the emitted relative link target onto the link's archive parent
    /// directory and normalizes POSIX '.'/'..' segments; the result must stay
    /// inside the payload (host-side resolution already guarantees it).
    /// </summary>
    private static string ResolveArchiveTarget(string linkArchivePath, string relativeTarget)
    {
        var slash = linkArchivePath.LastIndexOf('/');
        var parent = slash < 0 ? "" : linkArchivePath.Substring(0, slash);
        var segments = new List<string>();
        foreach (var segment in (parent + "/" + relativeTarget).Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new ArgumentException(
                        $"The payload link '{linkArchivePath}' resolves outside the payload.");
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        // 归档路径绝对形（installRoot 以 '/' 起头）——归一化吃掉空段后补回根，
        // 与出件条目的 ArchivePath 保持同形才能比对。
        return "/" + string.Join("/", segments);
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
