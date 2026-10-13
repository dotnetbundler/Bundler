namespace DotNet.Bundler.Core;

/// <summary>
/// 目录符号链接的处理策略：<see cref="Traverse"/> 沿链递归（调用方用深度封顶防环）、
/// <see cref="WarnSkip"/> 记警告跳过、<see cref="Reject"/> 显式拒绝。
/// </summary>
internal enum TreeLinkPolicy
{
    Traverse,
    WarnSkip,
    Reject
}

/// <summary>
/// 后端共享的文件系统树复制：常规文件过滤、链接策略、可选稳定排序、
/// 可选深度封顶、目标碰撞回调各按调用方语义参数化。
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
    /// 复制目录树。非常规文件（socket/FIFO/设备/链接）记警告跳过。
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
        // 目录符号链接环会让递归失控——Traverse 语义下调用方须传深度封顶。
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
            if (links == TreeLinkPolicy.WarnSkip &&
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                // 目录链接不展开：穿透会把目标树复制两遍，祖先链接会无界递归。
                log.Log(BundleLogLevel.Warning, $"Skipping directory symlink: {directory}");
                continue;
            }
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
}
