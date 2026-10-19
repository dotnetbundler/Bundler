using DotNet.Bundler;

namespace DotNet.Bundler.Core;

internal enum PayloadSourceKind
{
    Directory,
    File
}

/// <summary>
/// A neutral payload item produced by <see cref="PayloadSource"/> walks; each
/// backend maps items onto its own entry/dedup semantics.
/// </summary>
internal sealed record PayloadSourceItem(
    PayloadSourceKind Kind,
    string ArchivePath,
    string? SourcePath,
    int Mode);

/// <summary>
/// Shared input-tree/resource enumeration for POSIX package backends (deb, rpm,
/// apk): ordered deterministic walks, non-regular files warn+skipped, the main
/// executable gets 0755, resource destinations must stay inside the payload.
/// </summary>
internal static class PayloadSource
{
    /// <summary>Input tree: ordered directory items first, then ordered file items.</summary>
    internal static IEnumerable<PayloadSourceItem> CollectInput(
        string inputDirectory,
        string installRoot,
        string mainExecutable,
        IBundleLogger logger)
    {
        mainExecutable = mainExecutable.Replace('\\', '/');
        foreach (var directory in Directory.GetDirectories(inputDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return new PayloadSourceItem(
                PayloadSourceKind.Directory,
                installRoot + "/" + ToPosixPath(RelativePath(inputDirectory, directory)),
                null, 493 /* 0755 */);
        }
        foreach (var file in Directory.GetFiles(inputDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            // Sockets, FIFOs and device nodes cannot be packaged; skip them.
            if (!UnixFileTypes.IsRegularFile(file))
            {
                logger.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
                continue;
            }
            var relative = ToPosixPath(RelativePath(inputDirectory, file));
            var mode = string.Equals(relative, mainExecutable, StringComparison.Ordinal)
                ? 493 /* 0755 */
                : 420 /* 0644 */;
            yield return new PayloadSourceItem(
                PayloadSourceKind.File,
                installRoot + "/" + relative, file, mode);
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
            if (Directory.Exists(source))
            {
                if (includeDirectories)
                {
                    foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)
                                 .OrderBy(path => path, StringComparer.Ordinal))
                    {
                        yield return new PayloadSourceItem(
                            PayloadSourceKind.Directory,
                            installRoot + "/" + target + "/" + ToPosixPath(RelativePath(source, directory)),
                            null, 493 /* 0755 */);
                    }
                }
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (!UnixFileTypes.IsRegularFile(file))
                    {
                        logger.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
                        continue;
                    }
                    yield return new PayloadSourceItem(
                        PayloadSourceKind.File,
                        installRoot + "/" + target + "/" + ToPosixPath(RelativePath(source, file)),
                        file, 420 /* 0644 */);
                }
            }
            else if (File.Exists(source))
            {
                yield return new PayloadSourceItem(
                    PayloadSourceKind.File,
                    installRoot + "/" + target, source, 420 /* 0644 */);
            }
            else
            {
                throw new FileNotFoundException(
                    $"The resource source does not exist: {source}", source);
            }
        }
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
