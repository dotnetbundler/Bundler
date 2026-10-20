namespace DotNet.Bundler.Core;

/// <summary>
/// 目录符号链接的处理策略：<see cref="Traverse"/> 沿链递归（强制深度封顶防环）、
/// <see cref="Reject"/> 显式拒绝。
/// </summary>
internal enum TreeLinkPolicy
{
    Traverse,
    Reject
}

/// <summary>
/// 后端共享的文件系统树复制：常规文件过滤、链接策略、可选稳定排序、
/// 深度封顶（Traverse 下强制）、目标碰撞回调各按调用方语义参数化。
/// </summary>
internal static class FileTree
{
    /// <summary>拒绝 reparse 点（软链/junction），消息形如 "{message}: {path}"。</summary>
    internal static void RejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{message}: {path}");
        }
    }

    /// <summary>
    /// 复制目录树。非常规文件（socket/FIFO/设备节点）记警告跳过；
    /// 软链经 stat 跟随——指向常规文件者按目标内容处理，悬空者由 File.Copy 自报错。
    /// <paramref name="resolveFileDestination"/> 为 null 时目标即同名文件；
    /// 非 null 时负责碰撞检测等调用方语义并可抛自定义异常。
    /// </summary>
    internal static void CopyTree(
        string source,
        string destination,
        IBundleLogger log,
        int depth = 0,
        int maxDepth = int.MaxValue,
        string? loopErrorMessage = null,
        TreeLinkPolicy links = TreeLinkPolicy.Reject,
        string? linkErrorMessage = null,
        bool sortEntries = false,
        Func<string, string, string>? resolveFileDestination = null,
        bool overwrite = true)
    {
        if (links == TreeLinkPolicy.Reject && linkErrorMessage is null)
        {
            throw new ArgumentNullException(nameof(linkErrorMessage));
        }
        if (maxDepth < int.MaxValue && loopErrorMessage is null)
        {
            throw new ArgumentNullException(nameof(loopErrorMessage));
        }
        // 目录符号链接环会让递归失控——Traverse 语义强制要求深度封顶。
        if (links == TreeLinkPolicy.Traverse && maxDepth == int.MaxValue)
        {
            throw new ArgumentException("Traverse requires a maxDepth cap to bound symlink loops.", nameof(maxDepth));
        }
        if (depth > maxDepth)
        {
            throw new InvalidDataException($"{loopErrorMessage}: {source}");
        }
        if (links == TreeLinkPolicy.Reject)
        {
            RejectReparsePoint(source, linkErrorMessage!);
        }
        Directory.CreateDirectory(destination);
        IEnumerable<string> directories = Directory.GetDirectories(source);
        IEnumerable<string> files = Directory.GetFiles(source);
        if (sortEntries)
        {
            directories = directories.OrderBy(d => d, StringComparer.Ordinal);
            files = files.OrderBy(f => f, StringComparer.Ordinal);
        }
        foreach (var directory in directories)
        {
            CopyTree(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)),
                log, depth + 1, maxDepth, loopErrorMessage, links, linkErrorMessage,
                sortEntries, resolveFileDestination, overwrite);
        }
        foreach (var file in files)
        {
            // Sockets, FIFOs and device nodes cannot be copied; skip them.
            if (!UnixFileTypes.IsRegularFile(file))
            {
                log.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
                continue;
            }
            if (links == TreeLinkPolicy.Reject)
            {
                RejectReparsePoint(file, linkErrorMessage!);
            }
            var target = resolveFileDestination is null
                ? Path.Combine(destination, Path.GetFileName(file))
                : resolveFileDestination(file, destination);
            File.Copy(file, target, overwrite);
        }
    }

    /// <summary>
    /// 目录树是否"全旧"：根或任一后代（文件/目录皆可 stat）的 LastWriteTimeUtc
    /// 新于 <paramref name="cutoff"/> 即非陈旧——深层在写的长跑进程顶层目录
    /// mtime 可以很旧，只看顶层会误删在跑的工作目录。reparse 点不递归
    /// （目录链绕回祖先会死递归；链接自身时戳照常计入）。
    /// </summary>
    internal static bool IsStale(string root, DateTime cutoff)
    {
        if (File.GetLastWriteTimeUtc(root) >= cutoff)
        {
            return false;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (File.GetLastWriteTimeUtc(entry) >= cutoff)
            {
                return false;
            }
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) &&
                (attributes & FileAttributes.ReparsePoint) == 0 &&
                !IsStale(entry, cutoff))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 尽力删除单个文件——失败路径清理专用：删除本身失败（权限/占用）不得
    /// 遮蔽触发清理的真实异常，静默吞掉。
    /// </summary>
    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
