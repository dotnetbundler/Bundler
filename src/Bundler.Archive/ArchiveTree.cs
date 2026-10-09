using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Archive;

/// <summary>
/// Collects the payload into archive entries under the single top-level
/// directory &lt;stem&gt;/: every publish file, directory and symlink keeps its
/// relative path and Unix mode; <see cref="ArchiveBundleConfiguration.Files"/>
/// maps extra host files at archive-relative POSIX destinations.
/// </summary>
internal static class ArchiveTree
{
    internal sealed class Entry
    {
        internal string ArchivePath = "";      // relative POSIX path, no leading stem
        internal TarEntryKind Kind;
        internal int Mode;
        internal string? SourcePath;           // file payload read lazily
        internal byte[]? InlineContent;
        internal string LinkTarget = "";
    }

    internal static List<Entry> Build(
        BundleConfiguration bundle, BundlePlanItem item,
        ArchiveBundleConfiguration settings, string workDirectory,
        IBundleLogger logger)
    {
        var payloadRoot = item.InputDirectory;
        var entries = new List<Entry>();
        Collect(payloadRoot, "", entries, logger);

        if (settings.Files is { Count: > 0 } files)
        {
            var reserved = new HashSet<string>(entries.Select(e => e.ArchivePath), StringComparer.Ordinal);
            foreach (var file in files)
            {
                var destination = ValidateDestination(file.Destination);
                if (!reserved.Add(destination))
                {
                    throw new ArgumentException(
                        $"BundlerArchiveFile destination '{destination}' collides with an existing payload entry.");
                }
                var source = RequireExisting(file.Source);
                entries.Add(new Entry
                {
                    ArchivePath = destination,
                    Kind = TarEntryKind.File,
                    Mode = 420 /* 0644 */,
                    SourcePath = source
                });
            }
        }
        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.ArchivePath, b.ArchivePath));
        return entries;
    }

    /// <summary>
    /// 把任意已成型目录收进 &lt;stem&gt;/ 条目集（模式与软链规则同 <see cref="Build"/>）——
    /// 供更新清单把 .app 目录件打成可分发 zip 运输件。
    /// </summary>
    internal static List<Entry> CollectDirectory(string root, string stem, IBundleLogger log)
    {
        var entries = new List<Entry>
        {
            new()
            {
                ArchivePath = stem,
                Kind = TarEntryKind.Directory,
                Mode = 493 /* 0755 */,
                SourcePath = root
            }
        };
        Collect(root, stem + "/", entries, log);
        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.ArchivePath, b.ArchivePath));
        return entries;
    }

    private static void Collect(string directory, string relativePrefix, List<Entry> entries, IBundleLogger log)
    {
        foreach (var dir in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var rel = relativePrefix + Path.GetFileName(dir);
            // 目录符号链接按链接本体归档：物化会复制两遍，指向祖先的链接会无界递归。
            // readlink 取不到目标的 reparse 点（Windows junction 等）同样不可展开——跳过。
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            {
                if (UnixLinks.ReadLink(dir) is { Length: > 0 } dirTarget)
                {
                    entries.Add(new Entry
                    {
                        ArchivePath = rel,
                        Kind = TarEntryKind.Symlink,
                        Mode = 511 /* 0777 */,
                        SourcePath = dir,
                        LinkTarget = dirTarget
                    });
                }
                else
                {
                    log.Log(BundleLogLevel.Warning,
                        $"Skipping directory reparse point with an unreadable target: {dir}");
                }
                continue;
            }
            entries.Add(new Entry
            {
                ArchivePath = rel,
                Kind = TarEntryKind.Directory,
                Mode = 493 /* 0755 */,
                SourcePath = dir
            });
            Collect(dir, rel + "/", entries, log);
        }
        foreach (var file in Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = relativePrefix + Path.GetFileName(file);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 &&
                UnixLinks.ReadLink(file) is { Length: > 0 } target)
            {
                entries.Add(new Entry
                {
                    ArchivePath = rel,
                    Kind = TarEntryKind.Symlink,
                    Mode = 511 /* 0777 */,
                    SourcePath = file,
                    LinkTarget = target
                });
            }
            else if (UnixFileTypes.IsRegularFile(file))
            {
                entries.Add(new Entry
                {
                    ArchivePath = rel,
                    Kind = TarEntryKind.File,
                    Mode = UnixMode(file),
                    SourcePath = file
                });
            }
            // Sockets, FIFOs and device nodes cannot be archived; skip them.
            else
            {
                log.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
            }
        }
    }

    // netstandard2.0 has no Unix-mode API; on Linux/macOS ask libc whether the
    // file is executable, elsewhere probe ELF/shebang/Mach-O markers — publish
    // payloads mark native entry binaries that way — else 0644.
    private static int UnixMode(string path)
    {
        if (UnixLinks.IsExecutable(path) is { } executable)
        {
            return executable ? 493 /* 0755 */ : 420 /* 0644 */;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[4];
            var read = stream.Read(head, 0, 4);
            if (read >= 2 && HasExecutableMagic(head, read))
            {
                return 493 /* 0755 */;
            }
        }
        catch (IOException) { }
        return 420 /* 0644 */;
    }

    private static bool HasExecutableMagic(byte[] head, int read)
    {
        if ((head[0] == 0x7F && head[1] == (byte)'E') ||              // ELF "\x7fE"
            (head[0] == (byte)'#' && head[1] == (byte)'!'))         // shebang "#!"
        {
            return true;
        }
        if (read < 4)
        {
            return false;
        }
        // Mach-O thin/fat magics in both endiannesses — macOS apphosts and dylibs.
        var magic = ((uint)head[0] << 24) | ((uint)head[1] << 16) | ((uint)head[2] << 8) | head[3];
        return magic is
            0xCEFAEDFE or 0xCFFAEDFE or  // thin, little-endian on disk (x86_64/arm64)
            0xFEEDFACE or 0xFEEDFACF or  // thin, big-endian on disk
            0xCAFEBABE or 0xCAFEBABF or  // fat, big-endian on disk
            0xBEBAFECA or 0xBFBAFECA;    // fat, little-endian on disk
    }

    private static string ValidateDestination(string? destination)
    {
        var normalized = (destination ?? "").Trim('/');
        if (normalized.Length == 0 || (destination ?? "").Contains('\\') ||
            (destination ?? "").TrimStart().StartsWith("/") ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                $"BundlerArchiveFile destination must be a relative POSIX path under the top-level directory; got '{destination}'.");
        }
        return normalized;
    }

    private static string RequireExisting(string source)
    {
        var fullPath = Path.GetFullPath(source ?? "");
        if (!File.Exists(fullPath))
        {
            throw new ArgumentException($"BundlerArchiveFile source does not exist: '{source}'.");
        }
        return fullPath;
    }

    internal static TarEntry ToTarEntry(Entry entry) => new()
    {
        Name = entry.Kind == TarEntryKind.Directory ? entry.ArchivePath + "/" : entry.ArchivePath,
        Kind = entry.Kind,
        Mode = entry.Mode,
        Content = entry.Kind == TarEntryKind.File
            ? entry.InlineContent ?? []
            : [],
        OpenContent = entry.Kind == TarEntryKind.File && entry.InlineContent is null
            ? () => new FileStream(entry.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null,
        LinkTarget = entry.LinkTarget
    };

    internal static ZipEntry ToZipEntry(Entry entry) => new()
    {
        Name = entry.Kind == TarEntryKind.Directory ? entry.ArchivePath + "/" : entry.ArchivePath,
        Kind = entry.Kind switch
        {
            TarEntryKind.Directory => ZipEntryKind.Directory,
            TarEntryKind.Symlink => ZipEntryKind.Symlink,
            _ => ZipEntryKind.File
        },
        Mode = entry.Mode,
        Content = entry.Kind == TarEntryKind.File
            ? entry.InlineContent ?? []
            : [],
        OpenContent = entry.Kind == TarEntryKind.File && entry.InlineContent is null
            ? () => new FileStream(entry.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null,
        LinkTarget = entry.LinkTarget
    };

    /// <summary>Prefixes every entry path with the top-level &lt;stem&gt;/ directory.</summary>
    internal static IEnumerable<Entry> UnderStem(
        IEnumerable<Entry> entries, string stem, string? sourceRoot = null) =>
        new[]
        {
            new Entry
            {
                ArchivePath = stem,
                Kind = TarEntryKind.Directory,
                Mode = 493 /* 0755 */,
                SourcePath = sourceRoot
            }
        }.Concat(entries.Select(e => new Entry
        {
            ArchivePath = stem + "/" + e.ArchivePath,
            Kind = e.Kind,
            Mode = e.Mode,
            SourcePath = e.SourcePath,
            InlineContent = e.InlineContent,
            LinkTarget = e.LinkTarget
        }));
}
