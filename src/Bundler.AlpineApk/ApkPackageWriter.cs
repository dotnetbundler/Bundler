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
        var release = settings.Release ?? "0";
        ApkIdentity.ValidateNonNegativeInteger(release, "Release");
        var pkgver = upstream + "-r" + release;
        var architecture = settings.Architecture ?? ApkIdentity.MapArchitecture(item.Target.Architecture);
        ApkIdentity.ValidateArchitecture(architecture);
        var origin = settings.Origin ?? packageName;
        var description = settings.Description ?? bundle.Description ?? "";
        if (description.Length == 0)
        {
            description = bundle.ProductName;
        }
        var url = settings.Url ?? bundle.Homepage ?? "";
        var license = settings.License ?? "";
        var buildDate = settings.BuildDate ?? "0";
        ApkIdentity.ValidateNonNegativeInteger(buildDate, "BuildDate");
        var installRoot = "usr/lib/" + packageName;
        var binLink = settings.BinLink ?? packageName;

        var payload = CollectPayload(bundle, item, installRoot, binLink, packageName, settings, logger);

        // Data segment first: .PKGINFO carries the sha256 of its gzip stream.
        var dataTar = TarData(payload, omitEndOfArchive: false);
        var dataGzip = Gzip(dataTar);
        var dataHash = Sha256Hex(dataGzip);
        var installedSize = payload
            .Where(entry => entry.Kind == TarEntryKind.File)
            .Sum(entry => entry.Size);

        var pkginfo = PackageInfo(
            packageName, pkgver, description, url, architecture, origin, license, buildDate,
            installedSize, dataHash, settings);
        var controlEntries = new List<TarEntry>
        {
            new()
            {
                Name = ".PKGINFO",
                Kind = TarEntryKind.File,
                Mode = 420, // 0644
                Content = new UTF8Encoding(false).GetBytes(pkginfo),
                PaxRecords = TimestampRecords
            }
        };
        EmitScripts(controlEntries, settings);
        var controlGzip = Gzip(TarData(controlEntries, omitEndOfArchive: true));

        // Optional signature segment: a tar holding .SIGN.RSA.<key name>.rsa.pub
        // whose content is the raw PKCS1v15 RSA-SHA1 signature over the control
        // gzip stream. It precedes the control segment.
        byte[]? signatureGzip = null;
        if (settings.SigningKeyFile is { Length: > 0 } keyFile)
        {
            if (!File.Exists(keyFile))
            {
                throw new FileNotFoundException(
                    $"The .apk signing key does not exist: {keyFile}", keyFile);
            }
            var signature = ApkSigner.Sign(controlGzip, keyFile, settings.SigningKeyPassphrase);
            var memberName = ".SIGN.RSA." + Path.GetFileName(keyFile) + ".rsa.pub";
            signatureGzip = Gzip(TarData(
            [
                new TarEntry
                {
                    Name = memberName,
                    Kind = TarEntryKind.File,
                    Mode = 420, // 0644
                    Content = signature,
                    PaxRecords = TimestampRecords
                }
            ], omitEndOfArchive: true));
        }
        else if (settings.SigningKeyPassphrase is { Length: > 0 })
        {
            throw new ArgumentException(
                "SigningKeyPassphrase requires SigningKeyFile to point at a PEM RSA private key.");
        }

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
                if (signatureGzip is not null)
                {
                    stream.Write(signatureGzip, 0, signatureGzip.Length);
                }
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

    private static readonly string[] BuiltInFields =
    [
        "pkgname", "pkgver", "pkgdesc", "url", "arch", "origin", "license",
        "depend", "provides", "triggers", "builddate", "size", "datahash"
    ];

    private static string PackageInfo(
        string packageName,
        string pkgver,
        string description,
        string url,
        string architecture,
        string origin,
        string license,
        string buildDate,
        long installedSize,
        string dataHash,
        AlpineApkBundleConfiguration settings)
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
        if (license.Length > 0)
        {
            Field("license", license);
        }
        foreach (var depend in settings.Depends ?? [])
        {
            ApkIdentity.ValidateListEntry(depend, nameof(settings.Depends));
            Field("depend", depend);
        }
        foreach (var provide in settings.Provides ?? [])
        {
            ApkIdentity.ValidateListEntry(provide, nameof(settings.Provides));
            Field("provides", provide);
        }
        if (settings.Triggers is { Count: > 0 } triggers)
        {
            foreach (var trigger in triggers)
            {
                if (!trigger.StartsWith("/", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Trigger directories must be absolute paths: '{trigger}'.");
                }
                ApkIdentity.ValidateListEntry(trigger, nameof(settings.Triggers));
            }
            Field("triggers", string.Join(" ", triggers));
        }
        Field("builddate", buildDate);
        Field("size", installedSize.ToString());
        Field("datahash", dataHash);
        if (settings.ExtraPkgInfo is not null)
        {
            foreach (var pair in settings.ExtraPkgInfo)
            {
                if (!pair.Key.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-') ||
                    pair.Key.Length == 0)
                {
                    throw new ArgumentException(
                        $"Invalid .PKGINFO key: '{pair.Key}'.");
                }
                if (BuiltInFields.Contains(pair.Key, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"The .PKGINFO key '{pair.Key}' collides with a built-in field.");
                }
                Field(pair.Key, pair.Value);
            }
        }
        return builder.ToString();
    }

    // apk install scripts live in the control segment as dotted names
    // (.pre-install, ...). Like rpm scriptlets they must use LF line endings.
    private static void EmitScripts(List<TarEntry> controlEntries, AlpineApkBundleConfiguration settings)
    {
        (string? path, string name)[] scripts =
        [
            (settings.PreInstallScript, ".pre-install"),
            (settings.PostInstallScript, ".post-install"),
            (settings.PreDeinstallScript, ".pre-deinstall"),
            (settings.PostDeinstallScript, ".post-deinstall"),
            (settings.PreUpgradeScript, ".pre-upgrade"),
            (settings.PostUpgradeScript, ".post-upgrade")
        ];
        foreach (var (path, name) in scripts)
        {
            if (path is null)
            {
                continue;
            }
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException(
                    $"The .apk script '{name}' does not exist: {full}", full);
            }
            var bytes = File.ReadAllBytes(full);
            if (bytes.Length == 0 ||
                Encoding.UTF8.GetString(bytes).Contains('\r'))
            {
                throw new ArgumentException(
                    $"The .apk script '{name}' must be non-empty with LF line endings: {full}");
            }
            controlEntries.Add(new TarEntry
            {
                Name = name,
                Kind = TarEntryKind.File,
                Mode = 493, // 0755
                Content = bytes,
                PaxRecords = TimestampRecords
            });
        }
    }

    private static List<PayloadEntry> CollectPayload(
        BundleConfiguration bundle,
        BundlePlanItem item,
        string installRoot,
        string binLink,
        string packageName,
        AlpineApkBundleConfiguration settings,
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

        foreach (var file in settings.Files ?? [])
        {
            var destination = FreedesktopFiles.NormalizeAbsoluteDestination(
                file.Destination, "apk");
            var source = Path.GetFullPath(file.Source);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    $"The .apk mapped file does not exist: {source}", source);
            }
            if (!UnixFileTypes.IsRegularFile(source))
            {
                throw new ArgumentException(
                    $"The .apk mapped file must be a regular file: {source}");
            }
            AddFile(destination.TrimStart('/'), source, 420 /* 0644 */);
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
                PaxRecords = PayloadPaxRecords(entry)
            }), omitEndOfArchive);
        return buffer.ToArray();
    }

    private static byte[] TarData(IEnumerable<TarEntry> entries, bool omitEndOfArchive)
    {
        using var buffer = new MemoryStream();
        TarWriter.Write(buffer, entries, omitEndOfArchive);
        return buffer.ToArray();
    }

    // Every apk entry carries atime/ctime = 0 like abuild output; regular
    // files add the APK-TOOLS.checksum.SHA1 pax record (hex sha1 of content,
    // hex sha1 of the link target for symlinks) that apk requires to extract.
    private static readonly KeyValuePair<string, string>[] TimestampRecords =
    [
        new("ctime", "0"),
        new("atime", "0")
    ];

    private static KeyValuePair<string, string>[] PayloadPaxRecords(PayloadEntry entry)
    {
        if (entry.Kind is not (TarEntryKind.File or TarEntryKind.Symlink))
        {
            return TimestampRecords;
        }
        using var sha1 = SHA1.Create();
        var content = entry.Kind == TarEntryKind.Symlink
            ? new UTF8Encoding(false).GetBytes(entry.LinkTarget)
            : entry.ReadBytes();
        return
        [
            new("ctime", "0"),
            new("atime", "0"),
            new("APK-TOOLS.checksum.SHA1", Hex(sha1.ComputeHash(content)))
        ];
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
