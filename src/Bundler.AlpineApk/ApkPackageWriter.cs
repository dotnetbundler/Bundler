using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// Emits an .apk entirely in managed code. An apk v2 file is three concatenated
/// gzip streams — signature tar, control tar (.PKGINFO + scripts), data tar
/// (payload). The signature and control tars omit the final pair of zero
/// blocks; the data tar keeps them. Gzip output is deterministic (mtime 0),
/// so identical inputs produce identical bytes.
/// </summary>
internal static class ApkPackageWriter
{
    internal sealed class Result
    {
        internal string Path = "";
    }

    private sealed class PayloadEntry
    {
        internal string ArchivePath = "";      // tar-relative, no leading '.'
        internal TarEntryKind Kind;
        internal string? SourcePath;            // host path when Kind=File
        internal int Mode;
        internal string LinkTarget = "";        // for Kind=Symlink
        internal long Size;                     // file size for the PKGINFO 'size' field

        internal byte[] ReadBytes() => File.ReadAllBytes(SourcePath!);
    }

    internal static Result Build(
        BundleConfiguration bundle,
        BundlePlanItem item,
        AlpineApkBundleConfiguration settings,
        IBundleLogger logger)
    {
        var packageName = settings.PackageName ?? ApkIdentity.SanitizeName(bundle.ProductName);
        ApkIdentity.ValidateName(packageName);
        var upstream = ApkIdentity.MapVersion(settings.Version ?? bundle.Version);
        var pkgver = upstream + "-r0";
        var architecture = settings.Architecture ?? ApkIdentity.MapArchitecture(item.Target.Architecture);
        ApkIdentity.ValidateArchitecture(architecture);
        var origin = settings.Origin ?? packageName;
        var description = settings.Description ?? bundle.Description ?? "";
        if (description.Length == 0)
        {
            description = bundle.ProductName;
        }
        var url = settings.Url ?? bundle.Homepage ?? "";
        var installRoot = "usr/lib/" + packageName;
        var binLink = settings.BinLink ?? packageName;

        var payload = CollectPayload(bundle, item, installRoot, binLink, packageName, logger);

        // Data segment first: .PKGINFO carries the sha256 of its gzip stream.
        var dataTar = TarData(payload, omitEndOfArchive: false);
        var dataGzip = Gzip(dataTar);
        var dataHash = Sha256Hex(dataGzip);
        var installedSize = payload
            .Where(entry => entry.Kind == TarEntryKind.File)
            .Sum(entry => entry.Size);

        var pkginfo = PackageInfo(
            packageName, pkgver, description, url, architecture, origin, installedSize, dataHash);
        var controlEntries = new List<TarEntry>
        {
            new()
            {
                Name = ".PKGINFO",
                Kind = TarEntryKind.File,
                Mode = 420, // 0644
                Content = new UTF8Encoding(false).GetBytes(pkginfo)
            }
        };
        var controlGzip = Gzip(TarData(controlEntries, omitEndOfArchive: true));

        Directory.CreateDirectory(item.OutputDirectory);
        var fileName = packageName + "-" + pkgver + ".apk";
        var outputPath = Path.Combine(item.OutputDirectory, fileName);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        logger.Log(BundleLogLevel.Information, $"Writing apk → {fileName}");
        try
        {
            using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(controlGzip, 0, controlGzip.Length);
                stream.Write(dataGzip, 0, dataGzip.Length);
            }
        }
        catch
        {
            // No half-written package is left behind on failure.
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            throw;
        }
        WriteSha256Sidecar(outputPath);
        return new Result { Path = outputPath };
    }

    private static string PackageInfo(
        string packageName,
        string pkgver,
        string description,
        string url,
        string architecture,
        string origin,
        long installedSize,
        string dataHash)
    {
        var builder = new StringBuilder();
        void Field(string key, string value)
        {
            if (value.Contains('\n') || value.Contains('\r'))
            {
                throw new ArgumentException(
                    $"The .PKGINFO field '{key}' cannot contain line breaks.");
            }
            builder.Append(key).Append(" = ").Append(value).Append('\n');
        }
        Field("pkgname", packageName);
        Field("pkgver", pkgver);
        Field("pkgdesc", description);
        if (url.Length > 0)
        {
            Field("url", url);
        }
        Field("arch", architecture);
        Field("origin", origin);
        Field("builddate", "0");
        Field("size", installedSize.ToString());
        Field("datahash", dataHash);
        return builder.ToString();
    }

    private static List<PayloadEntry> CollectPayload(
        BundleConfiguration bundle,
        BundlePlanItem item,
        string installRoot,
        string binLink,
        string packageName,
        IBundleLogger logger)
    {
        var entries = new List<PayloadEntry>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var input = Path.GetFullPath(item.InputDirectory);

        void ClaimDirectory(string path)
        {
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i <= segments.Length; i++)
            {
                var directory = string.Join("/", segments.Take(i));
                if (claimed.Add(directory))
                {
                    entries.Add(new PayloadEntry
                    {
                        ArchivePath = directory,
                        Kind = TarEntryKind.Directory,
                        Mode = 493 /* 0755 */
                    });
                }
            }
        }

        void AddFile(string archivePath, string sourcePath, int mode)
        {
            if (!claimed.Add(archivePath))
            {
                throw new InvalidOperationException(
                    $"Two payload entries target the same path: '{archivePath}'.");
            }
            ClaimDirectory(archivePath.Substring(0, archivePath.LastIndexOf('/')));
            entries.Add(new PayloadEntry
            {
                ArchivePath = archivePath,
                Kind = TarEntryKind.File,
                SourcePath = sourcePath,
                Mode = mode,
                Size = new FileInfo(sourcePath).Length
            });
        }

        var mainExecutable = item.MainExecutable.Replace('\\', '/');
        foreach (var file in Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            // Sockets, FIFOs and device nodes cannot be packaged; skip them.
            if (!UnixFileTypes.IsRegularFile(file))
            {
                logger.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
                continue;
            }
            var relative = ToPosixPath(RelativePath(input, file));
            var mode = string.Equals(relative, mainExecutable, StringComparison.Ordinal)
                ? 493 /* 0755 */
                : 420 /* 0644 */;
            AddFile(installRoot + "/" + relative, file, mode);
        }

        foreach (var resource in bundle.Resources)
        {
            var target = resource.TargetPath.Replace('\\', '/').Trim('/');
            if (target.Length == 0 || target.Split('/').Contains(".."))
            {
                throw new ArgumentException(
                    $"The resource target must stay inside the payload: '{resource.TargetPath}'.");
            }
            var source = Path.GetFullPath(resource.Source);
            if (Directory.Exists(source))
            {
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (!UnixFileTypes.IsRegularFile(file))
                    {
                        logger.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
                        continue;
                    }
                    var relative = ToPosixPath(RelativePath(source, file));
                    AddFile(installRoot + "/" + target + "/" + relative, file, 420 /* 0644 */);
                }
            }
            else if (File.Exists(source))
            {
                AddFile(installRoot + "/" + target, source, 420 /* 0644 */);
            }
            else
            {
                throw new FileNotFoundException(
                    $"The resource source does not exist: {source}", source);
            }
        }

        if (binLink.Length > 0)
        {
            var linkPath = "usr/bin/" + binLink;
            if (binLink.Contains('/'))
            {
                throw new ArgumentException(
                    $"The bin-link name must be a plain file name: '{binLink}'.");
            }
            if (!claimed.Add(linkPath))
            {
                throw new InvalidOperationException(
                    $"A payload file already occupies the bin-link path: '{linkPath}'.");
            }
            ClaimDirectory("usr/bin");
            entries.Add(new PayloadEntry
            {
                ArchivePath = linkPath,
                Kind = TarEntryKind.Symlink,
                Mode = 511, /* 0777 */
                LinkTarget = "../lib/" + packageName + "/" + mainExecutable
            });
        }

        return entries;
    }

    private static byte[] TarData(IEnumerable<PayloadEntry> entries, bool omitEndOfArchive)
    {
        using var buffer = new MemoryStream();
        TarWriter.Write(buffer, entries
            .OrderBy(entry => entry.ArchivePath, StringComparer.Ordinal)
            .Select(entry => new TarEntry
            {
                Name = entry.ArchivePath,
                Kind = entry.Kind,
                Mode = entry.Mode,
                Content = entry.Kind == TarEntryKind.File
                    ? entry.ReadBytes()
                    : [],
                LinkTarget = entry.LinkTarget,
                PaxRecords = entry.Kind == TarEntryKind.File
                    ? [ChecksumRecord(entry)]
                    : null
            }), omitEndOfArchive);
        return buffer.ToArray();
    }

    private static byte[] TarData(IEnumerable<TarEntry> entries, bool omitEndOfArchive)
    {
        using var buffer = new MemoryStream();
        TarWriter.Write(buffer, entries, omitEndOfArchive);
        return buffer.ToArray();
    }

    private static KeyValuePair<string, string> ChecksumRecord(PayloadEntry entry)
    {
        using var sha1 = SHA1.Create();
        return new("APK-TOOLS.checksum.SHA1", Convert.ToBase64String(sha1.ComputeHash(entry.ReadBytes())));
    }

    private static byte[] Gzip(byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content, 0, content.Length);
        }
        return buffer.ToArray();
    }

    private static void WriteSha256Sidecar(string path)
    {
        var hash = Sha256Hex(File.ReadAllBytes(path));
        File.WriteAllText(path + ".sha256",
            hash + "  " + Path.GetFileName(path) + "\n", new UTF8Encoding(false));
    }

    internal static string Sha256Hex(byte[] content)
    {
        using var sha256 = SHA256.Create();
        return Hex(sha256.ComputeHash(content));
    }

    private static string Hex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    private static string RelativePath(string baseDirectory, string path)
    {
        var prefix = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A payload path escapes its base directory: '{path}'.");
        }
        return full.Substring(prefix.Length);
    }

    private static string ToPosixPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
