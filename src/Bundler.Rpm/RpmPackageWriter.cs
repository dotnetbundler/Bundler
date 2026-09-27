using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Core;

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
        internal byte[]? Content;           // in-memory content for generated files
        internal string LinkTarget = "";    // for symlinks
        internal int Size;
        internal int FileFlags;             // RPMFILE_* bits (%doc=2, %license=128, ...)

        internal bool IsDirectory => (Mode & 0xF000) == 0x4000;
        internal bool IsSymlink => (Mode & 0xF000) == 0xA000;
        internal byte[] ReadBytes() =>
            Content ?? (IsSymlink ? Encoding.UTF8.GetBytes(LinkTarget) : File.ReadAllBytes(SourcePath!));
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
        foreach (var (knob, value) in new[]
        {
            ("License", settings.License), ("Group", settings.Group), ("Url", settings.Url)
        })
        {
            if (value is { } v && v.IndexOf('\n') >= 0)
            {
                throw new ArgumentException($"The .rpm {knob} must be a single line.");
            }
        }

        if (settings.Compression is { Length: > 0 } compression &&
            !string.Equals(compression, "gzip", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The .rpm payload compression '{compression}' is not supported; only 'gzip' is available.");
        }

        var payload = CollectPayload(bundle, item, installRoot, binLink, packageName, settings);
        var cpio = CpioWriter.Write(payload.Select(ToCpioEntry).ToList());
        var compressedPayload = Gzip(cpio);

        var mainHeader = RpmHeaderWriter.Write(MainHeaderEntries(
            bundle, item, payload, packageName, mapped, architecture, installRoot, vendor,
            settings, cpio, compressedPayload), 63);
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
        RpmBundleConfiguration settings,
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
            fileFlags[i] = entry.FileFlags;
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
            RpmHeaderWriter.Str(1014, settings.License ?? "Unspecified"),  // LICENSE
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
        };
        var group = settings.Group ?? "Unspecified";
        if (group.Length > 0)
        {
            entries.Add(RpmHeaderWriter.Strings(1016, [group], type: 9));  // GROUP i18n
        }
        var url = settings.Url ?? bundle.Homepage;
        if (url is { Length: > 0 })
        {
            entries.Add(RpmHeaderWriter.Str(1020, url));                   // URL
        }
        if (mapped.Epoch > 0)
        {
            entries.Add(RpmHeaderWriter.Int32s(1003, mapped.Epoch));       // EPOCH
        }
        // Self provide: "<name> = <evr>" plus the arch-qualified form, then
        // any caller-supplied Provides clauses.
        var provideNames = new List<string> { packageName, packageName + "(" + architecture + ")" };
        var provideFlags = new List<int> { RpmDependency.Equal, RpmDependency.Equal };
        var provideVersions = new List<string> { evr, evr };
        foreach (var clause in settings.Provides ?? [])
        {
            var parsed = RpmDependency.Parse(clause);
            provideNames.Add(parsed.Name);
            provideFlags.Add(parsed.Flags);
            provideVersions.Add(parsed.Version);
        }
        entries.Add(RpmHeaderWriter.Strings(1047, provideNames));          // PROVIDENAME
        entries.Add(RpmHeaderWriter.Int32s(1112, provideFlags.ToArray())); // PROVIDEFLAGS
        entries.Add(RpmHeaderWriter.Strings(1113, provideVersions));       // PROVIDEVERSION
        // rpmlib self-dependencies every package must declare, then Requires.
        var requireNames = new List<string>
        {
            "rpmlib(CompressedFileNames)", "rpmlib(FileDigests)",
            "rpmlib(PayloadFilesHavePrefix)"
        };
        var requireFlags = new List<int>
        {
            RpmDependency.Rpmlib | RpmDependency.Less | RpmDependency.Equal,
            RpmDependency.Rpmlib | RpmDependency.Less | RpmDependency.Equal,
            RpmDependency.Rpmlib | RpmDependency.Less | RpmDependency.Equal
        };
        var requireVersions = new List<string> { "3.0.4-1", "4.6.0-1", "4.0-1" };
        foreach (var clause in settings.Requires ?? [])
        {
            var parsed = RpmDependency.Parse(clause);
            requireNames.Add(parsed.Name);
            requireFlags.Add(parsed.Flags);
            requireVersions.Add(parsed.Version);
        }
        entries.Add(RpmHeaderWriter.Strings(1049, requireNames));          // REQUIRENAME
        entries.Add(RpmHeaderWriter.Int32s(1048, requireFlags.ToArray())); // REQUIREFLAGS
        entries.Add(RpmHeaderWriter.Strings(1050, requireVersions));       // REQUIREVERSION
        RpmDependency.Emit(entries, settings.Conflicts, 1054, 1053, 1055);   // CONFLICT*
        RpmDependency.Emit(entries, settings.Obsoletes, 1090, 1114, 1115);   // OBSOLETE*
        RpmDependency.Emit(entries, settings.Recommends, 5046, 5048, 5047);  // RECOMMEND*
        RpmDependency.Emit(entries, settings.Suggests, 5049, 5051, 5050);    // SUGGEST*
        EmitScriptlets(entries, settings);
        return entries;
    }

    // rpm scriptlets are interpreter-fed bodies: the PROG tag carries the
    // interpreter (default /bin/sh) and the script tag carries the body with
    // no shebang line. A managed systemd unit appends a daemon-reload epilogue
    // to %post and %postun.
    private static void EmitScriptlets(
        List<RpmHeaderWriter.Entry> entries, RpmBundleConfiguration settings)
    {
        var daemonReload = settings.SystemdServiceFile is { Length: > 0 };
        var epilogue = daemonReload
            ? "\n# Bundler: systemd unit installed; reload unit definitions.\n" +
              "systemctl daemon-reload > /dev/null 2>&1 || :\n"
            : null;
        Emit(entries, 1023, 1085, settings.PreInstallFile, settings.PreInstallProgram,
            "PreInstallFile", epilogue: null);
        Emit(entries, 1024, 1086, settings.PostInstallFile, settings.PostInstallProgram,
            "PostInstallFile", epilogue);
        Emit(entries, 1025, 1087, settings.PreUninstallFile, settings.PreUninstallProgram,
            "PreUninstallFile", epilogue: null);
        Emit(entries, 1026, 1088, settings.PostUninstallFile, settings.PostUninstallProgram,
            "PostUninstallFile", epilogue);

        static void Emit(
            List<RpmHeaderWriter.Entry> entries, int scriptTag, int progTag,
            string? file, string? program, string knob, string? epilogue)
        {
            var (body, shebangProg) = file is { Length: > 0 }
                ? ReadScriptlet(file, knob)
                : (null, null);
            if (body is null && epilogue is null)
            {
                return;
            }
            if (epilogue is not null)
            {
                body = (body ?? "") + epilogue;
            }
            entries.Add(RpmHeaderWriter.Str(scriptTag, body!));
            entries.Add(RpmHeaderWriter.Str(progTag, program ?? shebangProg ?? "/bin/sh"));
        }
    }

    // Reads a caller-supplied scriptlet file: LF only; a "#!" first line is
    // stripped from the body and supplies the interpreter when no explicit
    // *Program knob is set.
    private static (string? body, string? prog) ReadScriptlet(string path, string knob)
    {
        var full = FreedesktopFiles.RequireExisting(path, knob);
        var text = File.ReadAllText(full);
        if (text.IndexOf('\r') >= 0)
        {
            throw new ArgumentException(
                $"The .rpm scriptlet '{knob}' must use LF line endings: {full}");
        }
        if (!text.StartsWith("#!", StringComparison.Ordinal))
        {
            return (text, null);
        }
        var lineEnd = text.IndexOf('\n');
        var prog = (lineEnd < 0 ? text : text.Substring(0, lineEnd))
            .Substring(2).Trim();
        if (prog.Length == 0)
        {
            throw new ArgumentException(
                $"The .rpm scriptlet '{knob}' has an empty shebang: {full}");
        }
        var body = lineEnd < 0 ? "" : text.Substring(lineEnd + 1);
        return (body, prog);
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
        string binLink,
        string packageName,
        RpmBundleConfiguration settings)
    {
        var entries = new List<PayloadEntry>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);

        // Directories the distro owns and a leaf package must not claim — rpm's
        // own convention: a package owns the directories only it creates (e.g.
        // /usr/share/licenses/<pkg>) but not shared parents like /usr/share/doc
        // or anything under /usr/share/icons (hicolor-icon-theme owns those).
        var sharedDirs = new HashSet<string>(StringComparer.Ordinal)
        {
            "/", "/bin", "/sbin", "/lib", "/lib64", "/etc", "/opt", "/var",
            "/usr", "/usr/bin", "/usr/sbin", "/usr/lib", "/usr/lib64",
            "/usr/local", "/usr/share", "/usr/share/applications",
            "/usr/share/metainfo", "/usr/share/doc", "/usr/share/licenses",
            "/usr/share/man", "/usr/share/info", "/usr/share/icons",
            "/usr/lib/systemd", "/usr/lib/systemd/system"
        };

        // rpm owns the directories it creates (explicit dir entries); claiming
        // walks ancestors from the leaf up to the nearest shared directory.
        void ClaimDirectory(string path)
        {
            var current = path;
            while (current.Length > 0 && !sharedDirs.Contains(current))
            {
                if (current.StartsWith("/usr/share/icons/", StringComparison.Ordinal))
                {
                    break; // hicolor subtree belongs to hicolor-icon-theme
                }
                directories.Add(current);
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
                Size = (int)info.Length,
                FileFlags = FileFlagsFor(path)
            });
        }

        void AddGeneratedFile(string path, byte[] content, int mode)
        {
            var dir = path.Substring(0, path.LastIndexOf('/'));
            ClaimDirectory(dir);
            if (!claimed.Add(path))
            {
                throw new InvalidOperationException(
                    $"Two payload sources map to the same .rpm path: '{path}'.");
            }
            entries.Add(new PayloadEntry
            {
                ArchivePath = path,
                Mode = mode | 0x8000,
                Content = content,
                Size = content.Length,
                FileFlags = FileFlagsFor(path)
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

        // Desktop integration (shared with the .deb backend): a generated or
        // caller-supplied .desktop entry, hicolor icons and optional metainfo.
        foreach (var file in FreedesktopFiles.Collect(
            bundle, packageName, installRoot, mainExecutable, binLink,
            new FreedesktopFiles.Options
            {
                DesktopFile = settings.DesktopFile,
                MetainfoFile = settings.MetainfoFile,
                Categories = settings.Categories,
                Format = "rpm"
            }))
        {
            var path = "/" + file.ArchivePath;
            if (file.SourcePath is { } sourcePath)
            {
                AddFile(path, sourcePath, file.Mode);
            }
            else
            {
                AddGeneratedFile(path, file.Content!, file.Mode);
            }
        }

        if (settings.ChangelogFile is { Length: > 0 } changelogSource)
        {
            AddGeneratedFile(
                "/usr/share/doc/" + packageName + "/changelog.gz",
                Gzip(File.ReadAllBytes(
                    FreedesktopFiles.RequireExisting(changelogSource, "ChangelogFile"))),
                420 /* 0644 */);
        }
        if (bundle.LicenseFile is { Length: > 0 } license && license is not null)
        {
            var source = FreedesktopFiles.RequireExisting(license, "LicenseFile");
            AddFile("/usr/share/licenses/" + packageName + "/" + Path.GetFileName(source),
                source, 420 /* 0644 */);
        }

        foreach (var file in settings.Files ?? [])
        {
            var destination = FreedesktopFiles.NormalizeAbsoluteDestination(
                file.Destination, "rpm");
            AddFile(destination,
                FreedesktopFiles.RequireExisting(file.Source, "RpmFile"), 420 /* 0644 */);
        }

        if (settings.SystemdServiceFile is { Length: > 0 } unit)
        {
            AddFile("/usr/lib/systemd/system/" + packageName + ".service",
                FreedesktopFiles.RequireExisting(unit, "SystemdServiceFile"), 420 /* 0644 */);
        }

        // %config(noreplace): /etc destinations of RpmFile entries are marked
        // via FileFlagsFor; explicit ConfigFiles paths are resolved here and
        // must already exist as regular files in the payload.
        var byPath = new Dictionary<string, PayloadEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byPath[entry.ArchivePath] = entry;
        }
        foreach (var path in settings.ConfigFiles ?? [])
        {
            var destination = FreedesktopFiles.NormalizeAbsoluteDestination(path, "rpm");
            if (!byPath.TryGetValue(destination, out var entry) ||
                entry.IsDirectory || entry.IsSymlink)
            {
                throw new ArgumentException(
                    $"The ConfigFiles entry '{path}' has no regular file in the payload.");
            }
            entry.FileFlags |= 17; // RPMFILE_CONFIG | RPMFILE_NOREPLACE
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
            // cpio member names carry the "./" prefix the rpmlib
            // PayloadFilesHavePrefix dependency advertises; absolute member
            // names break tools that extract the payload verbatim (rpmlint).
            Name = "." + entry.ArchivePath,
            Mode = entry.Mode,
            Data = entry.IsDirectory ? [] : entry.ReadBytes(),
            Inode = 0
        };
    }

    // ---- helpers ------------------------------------------------------------

    // RPMFILE_* bits the package sets itself: files under the doc/man trees are
    // documentation, files under /usr/share/licenses are license texts, files
    // under /etc are %config(noreplace).
    private static int FileFlagsFor(string archivePath)
    {
        var flags = 0;
        if (archivePath.StartsWith("/usr/share/licenses/", StringComparison.Ordinal))
        {
            flags |= 128; // RPMFILE_LICENSE
        }
        if (archivePath.StartsWith("/usr/share/doc/", StringComparison.Ordinal) ||
            archivePath.StartsWith("/usr/share/man/", StringComparison.Ordinal))
        {
            flags |= 2; // RPMFILE_DOC
        }
        if (archivePath.StartsWith("/etc/", StringComparison.Ordinal))
        {
            flags |= 17; // RPMFILE_CONFIG | RPMFILE_NOREPLACE
        }
        return flags;
    }

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
