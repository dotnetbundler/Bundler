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

    private static void Collect(string directory, string relativePrefix, List<Entry> entries, IBundleLogger log)
    {
        foreach (var dir in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var rel = relativePrefix + Path.GetFileName(dir);
            entries.Add(new Entry { ArchivePath = rel, Kind = TarEntryKind.Directory, Mode = 493 /* 0755 */ });
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
    // file is executable, elsewhere probe ELF/shebang markers — publish
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
            if (stream.Read(head, 0, 4) >= 2 &&
                ((head[0] == 0x7F && head[1] == (byte)'E') || head[0] == (byte)'#' && head[1] == (byte)'!'))
            {
                return 493 /* 0755 */;
            }
        }
        catch (IOException) { }
        return 420 /* 0644 */;
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
            ? entry.InlineContent ?? File.ReadAllBytes(entry.SourcePath!)
            : [],
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
            ? entry.InlineContent ?? File.ReadAllBytes(entry.SourcePath!)
            : [],
        LinkTarget = entry.LinkTarget
    };

    /// <summary>Prefixes every entry path with the top-level &lt;stem&gt;/ directory.</summary>
    internal static IEnumerable<Entry> UnderStem(IEnumerable<Entry> entries, string stem) =>
        new[]
        {
            new Entry { ArchivePath = stem, Kind = TarEntryKind.Directory, Mode = 493 /* 0755 */ }
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
