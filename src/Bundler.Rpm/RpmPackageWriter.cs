using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler;

namespace DotNet.Bundler.Rpm;

/// <summary>
/// Emits a .rpm entirely in managed code: a 96-byte lead, a signature header
/// (SIZE/SHA256HEADER and friends), the main header, and a cpio-newc payload
/// compressed with gzip. No host packaging tools are invoked, so the writer
/// runs on any .NET host.
/// </summary>
internal static class RpmPackageWriter
{
    internal const long BuildTime = 315532800L; // 1980-01-01 UTC, deterministic

    internal sealed class Result
    {
        internal string OutputPath = "";
        internal string PackageName = "";
        internal string Version = "";
        internal string Release = "";
        internal string Architecture = "";
    }

    private sealed class PayloadEntry
    {
        internal string ArchivePath = "";   // "/usr/lib/pkg/app", absolute, no trailing slash
        internal int Mode;                  // st_mode including type bits
        internal string? SourcePath;        // host file for regular files
        internal string LinkTarget = "";    // for symlinks
        internal int Size;

        internal bool IsDirectory => (Mode & 0xF000) == 0x4000;
        internal bool IsSymlink => (Mode & 0xF000) == 0xA000;
        internal byte[] ReadBytes() =>
            IsSymlink ? Encoding.UTF8.GetBytes(LinkTarget) : File.ReadAllBytes(SourcePath!);
    }

    internal static Result Build(
        BundleConfiguration bundle,
        BundlePlanItem item,
        RpmBundleConfiguration settings,
        IBundleLogger logger)
    {
        var packageName = settings.PackageName ?? RpmName.Sanitize(bundle.ProductName);
        RpmName.Validate(packageName);
        var mapped = RpmVersion.Map(bundle.Version, settings);
        var architecture = settings.Architecture ?? MapArchitecture(item.Target.Architecture);
        ValidateArchitecture(architecture);
        var installRoot = NormalizeInstallRoot(settings.InstallRoot, packageName);
        var binLink = settings.BinLink ?? packageName;
        if (string.Equals(binLink, "none", StringComparison.OrdinalIgnoreCase))
        {
            binLink = "";
        }
        if (binLink.Length > 0)
        {
            ValidateBinLinkName(binLink);
        }
        var vendor = settings.Vendor ?? bundle.Publisher ?? bundle.Identifier;
        if (string.IsNullOrWhiteSpace(vendor) || vendor.IndexOf('\n') >= 0)
        {
            throw new ArgumentException(
                "The .rpm vendor must be a non-empty single line; set Publisher or Vendor.");
        }

        var payload = CollectPayload(bundle, item, installRoot, binLink);
        var cpio = CpioWriter.Write(payload.Select(ToCpioEntry).ToList());
        var compressedPayload = Gzip(cpio);

        var mainHeader = RpmHeaderWriter.Write(MainHeaderEntries(
            bundle, item, payload, packageName, mapped, architecture, installRoot, vendor,
            cpio, compressedPayload), 63);
        var signatureHeader = RpmHeaderWriter.Write(SignatureEntries(
            mainHeader, compressedPayload, cpio), 62);
        signatureHeader = Align8(signatureHeader);

        var fileName = packageName + "-" + mapped.Version + "-" + mapped.Release +
            "." + architecture + ".rpm";
        var outputPath = Path.Combine(item.OutputDirectory, fileName);
        var sidecarPath = outputPath + ".sha256";
        try
        {
            Directory.CreateDirectory(item.OutputDirectory);
            using (var stream = File.Create(outputPath))
            {
                WriteLead(stream, packageName + "-" + mapped.Version + "-" + mapped.Release);
                stream.Write(signatureHeader, 0, signatureHeader.Length);
                stream.Write(mainHeader, 0, mainHeader.Length);
                stream.Write(compressedPayload, 0, compressedPayload.Length);
            }
            var hash = Sha256Hex(File.ReadAllBytes(outputPath));
            File.WriteAllText(sidecarPath,
                hash + "  " + fileName + "\n", new UTF8Encoding(false));
            logger.Log(BundleLogLevel.Information, $"Wrote {fileName} (sha256 {hash}).");
            return new Result
            {
                OutputPath = outputPath,
                PackageName = packageName,
                Version = mapped.Version,
                Release = mapped.Release,
                Architecture = architecture
            };
        }
        catch
        {
            TryDelete(outputPath);
            TryDelete(sidecarPath);
            throw;
        }
    }

    // ---- lead ---------------------------------------------------------------

    private static void WriteLead(Stream stream, string nameVersionRelease)
    {
        var lead = new byte[96];
        lead[0] = 0xED; lead[1] = 0xAB; lead[2] = 0xEE; lead[3] = 0xDB;
        lead[4] = 3;  // major version
        lead[5] = 0;  // minor
        // type 0 (binary), archnum 0, osnum 1 (linux) at 8..9
        lead[9] = 1;
        var nameBytes = Encoding.ASCII.GetBytes(nameVersionRelease);
        Array.Copy(nameBytes, 0, lead, 10, Math.Min(nameBytes.Length, 65));
        // signature type 5 = RPMSIG_HEADERSIG at 78..79
        lead[79] = 5;
        stream.Write(lead, 0, lead.Length);
    }

    // ---- headers ------------------------------------------------------------

    private static List<RpmHeaderWriter.Entry> SignatureEntries(
        byte[] mainHeader, byte[] compressedPayload, byte[] cpio)
    {
        // RPMSIGTAG_SIZE = main header bytes + compressed payload bytes
        var packageSize = mainHeader.Length + compressedPayload.Length;
        var md5 = MD5.Create();
        var md5Bytes = new byte[mainHeader.Length + compressedPayload.Length];
        Buffer.BlockCopy(mainHeader, 0, md5Bytes, 0, mainHeader.Length);
        Buffer.BlockCopy(compressedPayload, 0, md5Bytes, mainHeader.Length, compressedPayload.Length);
        return
        [
            RpmHeaderWriter.Int32s(1000, packageSize),                    // RPMSIGTAG_SIZE
            RpmHeaderWriter.Bin(1004, md5.ComputeHash(md5Bytes)),         // RPMSIGTAG_MD5
            RpmHeaderWriter.Int32s(1007, cpio.Length),                    // RPMSIGTAG_PAYLOADSIZE
            RpmHeaderWriter.Str(269, Hex(SHA1.Create().ComputeHash(mainHeader))),   // SHA1HEADER
            RpmHeaderWriter.Str(273, Hex(SHA256.Create().ComputeHash(mainHeader))), // SHA256HEADER
        ];
    }

    private static List<RpmHeaderWriter.Entry> MainHeaderEntries(
        BundleConfiguration bundle,
        BundlePlanItem item,
        List<PayloadEntry> payload,
        string packageName,
        RpmVersion.Mapped mapped,
        string architecture,
        string installRoot,
        string vendor,
        byte[] cpio,
        byte[] compressedPayload)
    {
        var fileCount = payload.Count;
        var baseNames = new List<string>(fileCount);
        var dirNames = new List<string>();
        var dirIndexes = new int[fileCount];
        var fileSizes = new int[fileCount];
        var fileModes = new int[fileCount];
        var fileRdevs = new int[fileCount];
        var fileMtimes = new int[fileCount];
        var fileDigests = new string[fileCount];
        var fileLinkTos = new string[fileCount];
        var fileFlags = new int[fileCount];
        var fileUsers = new string[fileCount];
        var fileGroups = new string[fileCount];
        var fileVerifyFlags = new int[fileCount];
        var fileDevices = new int[fileCount];
        var fileInodes = new int[fileCount];
        var fileLangs = new string[fileCount];
        var installedSize = 0L;

        var dirNameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < fileCount; i++)
        {
            var entry = payload[i];
            var slash = entry.ArchivePath.LastIndexOf('/');
            baseNames.Add(entry.ArchivePath.Substring(slash + 1));
            var dir = entry.ArchivePath.Substring(0, slash + 1);
            if (!dirNameIndex.TryGetValue(dir, out var dirIndex))
            {
                dirIndex = dirNames.Count;
                dirNameIndex.Add(dir, dirIndex);
                dirNames.Add(dir);
            }
            dirIndexes[i] = dirIndex;
            fileSizes[i] = entry.IsDirectory ? 0 : entry.Size;
            fileModes[i] = entry.Mode;
            fileRdevs[i] = 0;
            fileMtimes[i] = (int)BuildTime;
            // Dirs get an empty digest; symlinks hash the link-target string;
            // regular files hash their bytes — matching rpm's own rules.
            fileDigests[i] = entry.IsDirectory ? "" : Sha256Hex(entry.ReadBytes());
            fileLinkTos[i] = entry.IsSymlink ? entry.LinkTarget : "";
            fileFlags[i] = 0;
            fileUsers[i] = "root";
            fileGroups[i] = "root";
            fileVerifyFlags[i] = -1;
            fileDevices[i] = 1;
            fileInodes[i] = i + 1;
            fileLangs[i] = "";
            installedSize += entry.Size;
        }

        var evr = RpmVersion.Evr(mapped);
        var entries = new List<RpmHeaderWriter.Entry>
        {
            RpmHeaderWriter.Strings(100, ["C"]),                           // I18NLOCALES
            RpmHeaderWriter.Str(1000, packageName),                        // NAME
            RpmHeaderWriter.Str(1001, mapped.Version),                     // VERSION
            RpmHeaderWriter.Str(1002, mapped.Release),                     // RELEASE
            RpmHeaderWriter.Strings(1004, [SummaryOf(bundle)], type: 9),   // SUMMARY i18n
            RpmHeaderWriter.Strings(1005, [DescriptionOf(bundle)], type: 9), // DESCRIPTION i18n
            RpmHeaderWriter.Int32s(1006, (int)BuildTime),                  // BUILDTIME
            RpmHeaderWriter.Str(1007, "bundler-build"),                    // BUILDHOST
            RpmHeaderWriter.Int32s(1009, checked((int)installedSize)),     // SIZE
            RpmHeaderWriter.Str(1011, vendor),                             // VENDOR
            RpmHeaderWriter.Str(1014, "Unspecified"),                      // LICENSE
            RpmHeaderWriter.Strings(1016, ["Unspecified"], type: 9),       // GROUP i18n
            RpmHeaderWriter.Str(1021, "linux"),                            // OS
            RpmHeaderWriter.Str(1022, architecture),                       // ARCH
            RpmHeaderWriter.Str(1064, RpmToolVersion()),                   // RPMVERSION
            RpmHeaderWriter.Str(1124, "cpio"),                             // PAYLOADFORMAT
            RpmHeaderWriter.Str(1125, "gzip"),                             // PAYLOADCOMPRESSOR
            RpmHeaderWriter.Str(1126, "9"),                                // PAYLOADFLAGS
            // PAYLOADDIGEST = sha256 of the compressed payload stream;
            // PAYLOADDIGESTALT = sha256 of the uncompressed cpio archive.
            RpmHeaderWriter.Strings(5092, [Sha256Hex(compressedPayload)]), // PAYLOADDIGEST
            RpmHeaderWriter.Int32s(5093, 8),                               // PAYLOADDIGESTALGO
            RpmHeaderWriter.Strings(5097, [Sha256Hex(cpio)]),              // PAYLOADDIGESTALT
            // File manifest.
            RpmHeaderWriter.Int32s(1028, fileSizes),                       // FILESIZES
            RpmHeaderWriter.Int16s(1030, fileModes),                       // FILEMODES
            RpmHeaderWriter.Int16s(1033, fileRdevs),                       // FILERDEVS
            RpmHeaderWriter.Int32s(1034, fileMtimes),                      // FILEMTIMES
            RpmHeaderWriter.Strings(1035, fileDigests),                    // FILEDIGESTS
            RpmHeaderWriter.Strings(1036, fileLinkTos),                    // FILELINKTOS
            RpmHeaderWriter.Int32s(1037, fileFlags),                       // FILEFLAGS
            RpmHeaderWriter.Strings(1039, fileUsers),                      // FILEUSERNAME
            RpmHeaderWriter.Strings(1040, fileGroups),                     // FILEGROUPNAME
            RpmHeaderWriter.Int32s(1045, fileVerifyFlags),                 // FILEVERIFYFLAGS
            RpmHeaderWriter.Int32s(1095, fileDevices),                     // FILEDEVICES
            RpmHeaderWriter.Int32s(1096, fileInodes),                      // FILEINODES
            RpmHeaderWriter.Strings(1097, fileLangs),                      // FILELANGS
            RpmHeaderWriter.Int32s(1116, dirIndexes),                      // DIRINDEXES
            RpmHeaderWriter.Strings(1117, baseNames),                      // BASENAMES
            RpmHeaderWriter.Strings(1118, dirNames),                       // DIRNAMES
            RpmHeaderWriter.Int32s(5011, 8),                               // FILEDIGESTALGO = sha256
            RpmHeaderWriter.Str(5062, "utf-8"),                            // ENCODING
            // Self provide: "<name> = <evr>" plus the arch-qualified form.
            RpmHeaderWriter.Strings(1047, [packageName, packageName + "(" + architecture + ")"]),
            RpmHeaderWriter.Int32s(1112, 8, 8),                            // PROVIDEFLAGS = EQUAL
            RpmHeaderWriter.Strings(1113, [evr, evr]),                     // PROVIDEVERSION
            // rpmlib self-dependencies every package must declare.
            RpmHeaderWriter.Strings(1049,
                ["rpmlib(CompressedFileNames)", "rpmlib(FileDigests)",
                 "rpmlib(PayloadFilesHavePrefix)"]),
            RpmHeaderWriter.Int32s(1048, 16777226, 16777226, 16777226),    // RPMLIB|LESS|EQUAL
            RpmHeaderWriter.Strings(1050, ["3.0.4-1", "4.6.0-1", "4.0-1"]),
        };
        if (mapped.Epoch > 0)
        {
            entries.Add(RpmHeaderWriter.Int32s(1003, mapped.Epoch));       // EPOCH
        }
        if (bundle.Homepage is { Length: > 0 } homepage)
        {
            entries.Add(RpmHeaderWriter.Str(1020, homepage));              // URL
        }
        return entries;
    }

    private static string SummaryOf(BundleConfiguration bundle) =>
        bundle.Description is { Length: > 0 } d && d.Length <= 80
            ? d : bundle.ProductName + " " + bundle.Version;

    private static string DescriptionOf(BundleConfiguration bundle) =>
        bundle.Description ?? bundle.ProductName + " " + bundle.Version;

    private static string RpmToolVersion()
    {
        var version = typeof(RpmBundler).Assembly.GetName().Version;
        return "bundler-rpm/" + (version?.ToString() ?? "0.1.0");
    }

    // ---- payload collection -------------------------------------------------

    private static List<PayloadEntry> CollectPayload(
        BundleConfiguration bundle,
        BundlePlanItem item,
        string installRoot,
        string binLink)
    {
        var entries = new List<PayloadEntry>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);

        // rpm owns the directories it creates (explicit dir entries); the system
        // dirs above the install root ("/usr", "/usr/lib", …) stay unowned.
        void ClaimDirectory(string path)
        {
            var current = path;
            while (current.Length > 0 && current != "/")
            {
                if (current == installRoot || current.StartsWith(installRoot + "/", StringComparison.Ordinal))
                {
                    directories.Add(current);
                }
                var slash = current.LastIndexOf('/');
                current = slash <= 0 ? "" : current.Substring(0, slash);
            }
        }

        void AddFile(string path, string sourcePath, int mode)
        {
            var dir = path.Substring(0, path.LastIndexOf('/'));
            ClaimDirectory(dir);
            if (!claimed.Add(path))
            {
                throw new InvalidOperationException(
                    $"Two payload sources map to the same .rpm path: '{path}'.");
            }
            var info = new FileInfo(sourcePath);
            entries.Add(new PayloadEntry
            {
                ArchivePath = path,
                Mode = mode | 0x8000,
                SourcePath = sourcePath,
                Size = (int)info.Length
            });
        }

        var input = item.InputDirectory;
        var mainExecutable = item.MainExecutable.Replace('\\', '/');
        foreach (var directory in Directory.GetDirectories(input, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = ToPosixPath(RelativePath(input, directory));
            var archivePath = installRoot + "/" + relative;
            ClaimDirectory(archivePath.Substring(0, archivePath.LastIndexOf('/')));
            directories.Add(archivePath);
        }
        foreach (var file in Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = ToPosixPath(RelativePath(input, file));
            var mode = string.Equals(relative, mainExecutable, StringComparison.Ordinal)
                ? 493 /* 0755 */ : 420 /* 0644 */;
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

        // Emit the owned directory entries (install root plus everything below it).
        ClaimDirectory(installRoot);
        foreach (var dir in directories.OrderBy(d => d, StringComparer.Ordinal))
        {
            if (claimed.Add(dir))
            {
                entries.Add(new PayloadEntry { ArchivePath = dir, Mode = 0x4000 | 493 /* 040755 */ });
            }
            else
            {
                throw new InvalidOperationException(
                    $"A payload file collides with a directory: '{dir}'.");
            }
        }

        if (binLink.Length > 0)
        {
            var linkPath = "/usr/bin/" + binLink;
            if (!claimed.Add(linkPath))
            {
                throw new InvalidOperationException(
                    $"The /usr/bin link collides with a payload file: '{linkPath}'.");
            }
            var target = installRoot.StartsWith("/usr/", StringComparison.Ordinal)
                ? "../" + installRoot.Substring("/usr/".Length) + "/" + mainExecutable
                : installRoot + "/" + mainExecutable;
            entries.Add(new PayloadEntry
            {
                ArchivePath = linkPath,
                Mode = 0xA000 | 511 /* 0120777 */,
                LinkTarget = target,
                Size = target.Length
            });
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.ArchivePath, b.ArchivePath));
        return entries;
    }

    private static CpioWriter.Entry ToCpioEntry(PayloadEntry entry)
    {
        return new CpioWriter.Entry
        {
            Name = entry.ArchivePath,
            Mode = entry.Mode,
            Data = entry.IsDirectory ? [] : entry.ReadBytes(),
            Inode = 0
        };
    }

    // ---- helpers ------------------------------------------------------------

    internal static string MapArchitecture(CpuArchitecture architecture) => architecture switch
    {
        CpuArchitecture.X64 => "x86_64",
        CpuArchitecture.Arm64 => "aarch64",
        CpuArchitecture.X86 => "i686",
        _ => throw new NotSupportedException($"No rpm architecture mapping for '{architecture}'.")
    };

    private static void ValidateArchitecture(string architecture)
    {
        if (architecture.Length == 0 || !architecture.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"'{architecture}' is not a valid RPM architecture (letters, digits, '_').");
        }
    }

    private static string NormalizeInstallRoot(string? installRoot, string packageName)
    {
        var root = installRoot ?? "/usr/lib/" + packageName;
        if (!root.StartsWith("/", StringComparison.Ordinal) ||
            root.EndsWith("/", StringComparison.Ordinal) ||
            root.Split('/').Contains("..") || root.Contains(' '))
        {
            throw new ArgumentException(
                $"'{root}' is not a valid install root (absolute, no trailing '/', no '..' or spaces).");
        }
        return root;
    }

    private static void ValidateBinLinkName(string name)
    {
        var normalized = string.Equals(name, "none", StringComparison.OrdinalIgnoreCase) ? "" : name;
        if (normalized.Length == 0) return;
        if (normalized.Contains('/') || normalized.Contains(' ') || normalized == "." || normalized == "..")
        {
            throw new ArgumentException(
                $"'{name}' is not a valid /usr/bin link name (basename only, no '/' or spaces).");
        }
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

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(content, 0, content.Length);
        }
        return output.ToArray();
    }

    private static byte[] Align8(byte[] header)
    {
        var pad = (8 - header.Length % 8) % 8;
        if (pad == 0) return header;
        var result = new byte[header.Length + pad];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        return result;
    }

    internal static string Sha256Hex(byte[] content)
    {
        using var sha256 = SHA256.Create();
        return Hex(sha256.ComputeHash(content));
    }

    private static string Hex(byte[] hash)
    {
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
