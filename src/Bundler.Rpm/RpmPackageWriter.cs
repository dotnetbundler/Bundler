using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;

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
        string workDirectory,
        IBundleLogger logger,
        CancellationToken cancellationToken = default)
    {
        // 与门面 Validate 同一条校验链——直调 writer 也得到一致异常契约。
        RpmValidation.Settings(settings, bundle);
        var resolved = RpmValidation.Target(settings, bundle, item.Target);
        var packageName = resolved.PackageName;
        var mapped = resolved.Version;
        var architecture = resolved.Architecture;
        var installRoot = resolved.InstallRoot;
        var binLink = resolved.BinLink;
        var vendor = resolved.Vendor;

        var payload = CollectPayload(bundle, item, installRoot, binLink, packageName, settings, logger);
        // cpio 直写 gzip→workDirectory 临时文件（/tmp 小卷不堵构建——输出卷
        // 反正得装下成品），全程不驻内存；未压缩 cpio 的尺寸与 sha256
        // （PAYLOADSIZE / PAYLOADDIGESTALT）由穿透流边写边算。
        Directory.CreateDirectory(item.OutputDirectory);
        var payloadPath = Path.Combine(workDirectory,
            ".bundler-rpm-" + Guid.NewGuid().ToString("N") + ".cpio.gz");
        try
        {
            long cpioSize;
            string cpioSha256;
            using (var raw = File.Create(payloadPath))
            {
                var cpioHash = SHA256.Create();
                using (var gzip = Gzip.WrapWrite(raw))
                {
                    var digest = new DigestWriteStream(gzip, cpioHash);
                    CpioWriter.Write(digest, payload.Select(ToCpioEntry).ToList(), cancellationToken);
                    cpioSize = digest.BytesWritten;
                    digest.FinishHash();
                }
                cpioSha256 = Hashes.ToLowerHex(cpioHash.Hash!);
            }
            var compressedSize = new FileInfo(payloadPath).Length;
            var payloadSha256 = Hashes.Sha256HexFile(payloadPath);

            var mainHeader = RpmHeaderWriter.Write(MainHeaderEntries(
                bundle, item, payload, packageName, mapped, architecture, installRoot, vendor,
                settings, payloadSha256, cpioSha256), 63);
            byte[]? pgpSignature = null;
            byte[]? rsaSignature = null;
            if (settings.Signing.KeyFile is { Length: > 0 } keyFile)
            {
                rsaSignature = RpmSigner.Sign(
                    mainHeader, keyFile, settings.Signing.Passphrase);
                using (var payloadStream = File.OpenRead(payloadPath))
                {
                    pgpSignature = RpmSigner.Sign(
                        mainHeader, payloadStream, keyFile,
                        settings.Signing.Passphrase);
                }
                logger.Log(BundleLogLevel.Information,
                    "Signing package (RPMSIGTAG_RSA + RPMSIGTAG_PGP).");
            }
            var signatureHeader = RpmHeaderWriter.Write(SignatureEntries(
                mainHeader, compressedSize, cpioSize, payloadPath, rsaSignature, pgpSignature,
                cancellationToken), 62);
            signatureHeader = Align8(signatureHeader);

            var fileName = packageName + "-" + mapped.Version + "-" + mapped.Release +
                "." + architecture + ".rpm";
            var outputPath = Path.Combine(item.OutputDirectory, fileName);
            try
            {
                Directory.CreateDirectory(item.OutputDirectory);
                using (var stream = File.Create(outputPath))
                {
                    WriteLead(stream, packageName + "-" + mapped.Version + "-" + mapped.Release, architecture);
                    stream.Write(signatureHeader, 0, signatureHeader.Length);
                    stream.Write(mainHeader, 0, mainHeader.Length);
                    using (var payloadStream = File.OpenRead(payloadPath))
                    {
                        payloadStream.CopyTo(stream);
                    }
                }
                logger.Log(BundleLogLevel.Information, $"Wrote {fileName}.");
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
                FileTree.TryDeleteFile(outputPath);
                FileTree.TryDeleteFile(outputPath + ".sha256");
                throw;
            }
        }
        finally
        {
            FileTree.TryDeleteFile(payloadPath);
        }
    }

    // 把写进下游流的字节同步喂给哈希并计数——cpio 的未压缩
    // 摘要与长度不需要再物化一份。
    private sealed class DigestWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly HashAlgorithm _hash;
        private long _bytes;

        internal DigestWriteStream(Stream inner, HashAlgorithm hash)
        {
            _inner = inner;
            _hash = hash;
        }

        internal long BytesWritten => _bytes;

        internal void FinishHash() =>
            _hash.TransformFinalBlock([], 0, 0);

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        // CpioWriter 的 Pad4/Pad512 按 output.Length 对齐——Length 即已写字节数。
        public override long Length => _bytes;
        public override long Position
        {
            get => _bytes;
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.TransformBlock(buffer, offset, count, null, 0);
            _inner.Write(buffer, offset, count);
            _bytes += count;
        }
    }

    // ---- lead ---------------------------------------------------------------

    private static void WriteLead(Stream stream, string nameVersionRelease, string architecture)
    {
        var lead = new byte[96];
        lead[0] = 0xED; lead[1] = 0xAB; lead[2] = 0xEE; lead[3] = 0xDB;
        lead[4] = 3;  // major version
        lead[5] = 0;  // minor
        // type 0 (binary) at 6..7；archnum 按架构映射写 8..9（rpmrc arch_canon）。
        lead[9] = (byte)ArchNumber(architecture);
        // osnum 1 = linux at 76..77（此前从未写入恒为 0）。
        lead[77] = 1;
        var nameBytes = Encoding.ASCII.GetBytes(nameVersionRelease);
        Array.Copy(nameBytes, 0, lead, 10, Math.Min(nameBytes.Length, 65));
        // signature type 5 = RPMSIG_HEADERSIG at 78..79
        lead[79] = 5;
        stream.Write(lead, 0, lead.Length);
    }

    // rpmrc arch_canon 编号：x86_64/i686 同族为 1，aarch64 为 19。
    private static int ArchNumber(string architecture) => architecture switch
    {
        "x86_64" or "i686" or "i586" or "i486" or "i386" => 1,
        "aarch64" => 19,
        _ => 0,
    };

    // ---- headers ------------------------------------------------------------

    private static List<RpmHeaderWriter.Entry> SignatureEntries(
        byte[] mainHeader, long compressedSize, long cpioSize, string payloadPath,
        byte[]? rsaSignature, byte[]? pgpSignature, CancellationToken cancellationToken)
    {
        // RPMSIGTAG_SIZE = main header bytes + compressed payload bytes
        var packageSize = (long)mainHeader.Length + compressedSize;
        // RPMSIGTAG_MD5 覆盖主头+压缩载荷——载荷分块流喂，不整档物化。
        var md5 = MD5.Create();
        md5.TransformBlock(mainHeader, 0, mainHeader.Length, null, 0);
        using (var payloadStream = File.OpenRead(payloadPath))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = payloadStream.Read(chunk, 0, chunk.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                md5.TransformBlock(chunk, 0, read, null, 0);
            }
        }
        md5.TransformFinalBlock([], 0, 0);
        var entries = new List<RpmHeaderWriter.Entry>
        {
            RpmHeaderWriter.Bin(1004, md5.Hash!),                         // RPMSIGTAG_MD5
            RpmHeaderWriter.Str(269, Hashes.Sha1Hex(mainHeader)),   // SHA1HEADER
            RpmHeaderWriter.Str(273, Hashes.Sha256Hex(mainHeader)), // SHA256HEADER
        };
        // 尺寸标签 int32 封顶 ~2GiB：超出时发 RPM 4.15+ 的 LONG 变体而不是截断。
        if (packageSize <= int.MaxValue)
        {
            entries.Add(RpmHeaderWriter.Int32s(1000, (int)packageSize));  // RPMSIGTAG_SIZE
        }
        else
        {
            entries.Add(RpmHeaderWriter.Int64s(270, packageSize));        // RPMSIGTAG_LONGSIZE
        }
        if (cpioSize <= int.MaxValue)
        {
            entries.Add(RpmHeaderWriter.Int32s(1007, (int)cpioSize));     // RPMSIGTAG_PAYLOADSIZE
        }
        else
        {
            entries.Add(RpmHeaderWriter.Int64s(271, cpioSize));           // RPMSIGTAG_LONGARCHIVESIZE
        }
        if (rsaSignature is not null)
        {
            // RPMSIGTAG_RSA (268): v3 signature packet over the main header
            // alone — the tag libzypp/zypper requires to see a package as
            // signed. The format requires PGP to accompany it.
            entries.Add(RpmHeaderWriter.Bin(268, rsaSignature));
        }
        if (pgpSignature is not null)
        {
            // RPMSIGTAG_PGP (1002): OpenPGP binary-document signature packet
            // over main header + payload — what `rpm -K` verifies.
            entries.Add(RpmHeaderWriter.Bin(1002, pgpSignature));
        }
        return entries;
    }

    /// <summary>
    /// RPMTAG_SIZE（记全部已装文件总字节）：单件 &gt;2GiB 已在 AddFile 拒，但件数
    /// 一多总和照样越 int32——超出时换 RPM 4.15+ 的 LONGSIZE 变体（签名头
    /// RPMSIGTAG_SIZE/LONGSIZE 同款取舍），不再 checked 溢出炸异常。
    /// </summary>
    internal static RpmHeaderWriter.Entry SizeEntry(long installedSize) =>
        installedSize <= int.MaxValue
            ? RpmHeaderWriter.Int32s(1009, (int)installedSize)             // SIZE
            : RpmHeaderWriter.Int64s(5009, installedSize);                 // LONGSIZE

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
        string payloadSha256,
        string cpioSha256)
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
            fileDigests[i] = entry.IsDirectory ? "" : FileDigest(entry);
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
            RpmHeaderWriter.Strings(5092, [payloadSha256]),                // PAYLOADDIGEST
            RpmHeaderWriter.Int32s(5093, 8),                               // PAYLOADDIGESTALGO
            RpmHeaderWriter.Strings(5097, [cpioSha256]),                   // PAYLOADDIGESTALT
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
        entries.Add(SizeEntry(installedSize));
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
        foreach (var clause in settings.Depends ?? [])
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
    // *Program knob is set. Shared with RpmValidation so `bundler validate`
    // catches bad script files before any payload work.
    internal static (string? body, string? prog) ReadScriptlet(string path, string knob)
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

    private static string SummaryOf(BundleConfiguration bundle)
    {
        // SUMMARY 是单行 i18n 字段——多行描述取第一个非空行，超长或缺失回退产品名+版本。
        var firstLine = (bundle.Description ?? "")
            .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .FirstOrDefault(line => line.Trim().Length > 0)?.Trim();
        return firstLine is { Length: > 0, Length: <= 80 } ? firstLine
            : bundle.ProductName + " " + bundle.Version;
    }

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
        RpmBundleConfiguration settings,
        IBundleLogger logger)
    {
        var entries = new List<PayloadEntry>();
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
        var book = new PayloadPathBook(
            sharedBoundary: path =>
                sharedDirs.Contains(path) ||
                path.StartsWith("/usr/share/icons/", StringComparison.Ordinal),
            systemLeaf: path => sharedDirs.Contains(path));

        void AddFile(string path, string sourcePath, int mode)
        {
            book.ClaimFile(path);
            var info = new FileInfo(sourcePath);
            // rpm FILESIZES 与 cpio newc 都记 int32 尺寸：>2GiB 截断产坏包，显式拒绝。
            if (info.Length > int.MaxValue)
            {
                throw new ArgumentException(
                    $"The payload file '{sourcePath}' exceeds the 2 GiB per-file limit of the rpm format.");
            }
            entries.Add(new PayloadEntry
            {
                ArchivePath = path,
                Mode = mode | 0x8000,
                SourcePath = sourcePath,
                Size = (int)info.Length,
                FileFlags = FileFlagsFor(path)
            });
        }

        void AddLink(string path, string linkTarget)
        {
            book.ClaimFile(path);
            entries.Add(new PayloadEntry
            {
                ArchivePath = path,
                Mode = 0xA000 | 511 /* 0120777 */,
                LinkTarget = linkTarget,
                // cpio 体按 UTF-8 字节写（FileBytes 同源）、FILEDIGESTS 也哈希
                // UTF-8 字节——字符数在非 ASCII 目标下与两者同时错位。
                Size = Encoding.UTF8.GetByteCount(linkTarget)
            });
        }

        void AddGeneratedFile(string path, byte[] content, int mode)
        {
            book.ClaimFile(path);
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
        foreach (var sourceItem in PayloadSource.CollectInput(input, installRoot, mainExecutable, logger))
        {
            if (sourceItem.Kind == PayloadSourceKind.Directory)
            {
                book.ClaimDirectory(sourceItem.ArchivePath);
            }
            else if (sourceItem.Kind == PayloadSourceKind.Symlink)
            {
                AddLink(sourceItem.ArchivePath, sourceItem.LinkTarget!);
            }
            else
            {
                AddFile(sourceItem.ArchivePath, sourceItem.SourcePath!, sourceItem.Mode);
            }
        }

        // includeDirectories=false 时只产 File/Symlink 项；改 true 须先处理 Directory 项。
        foreach (var sourceItem in PayloadSource.CollectResources(bundle.Resources, installRoot, logger))
        {
            System.Diagnostics.Debug.Assert(sourceItem.Kind != PayloadSourceKind.Directory);
            if (sourceItem.Kind == PayloadSourceKind.Symlink)
            {
                AddLink(sourceItem.ArchivePath, sourceItem.LinkTarget!);
            }
            else
            {
                AddFile(sourceItem.ArchivePath, sourceItem.SourcePath!, sourceItem.Mode);
            }
        }

        // 更新面：身份旁车（含 pkg 包名——换包按它定位已装单元）+ 引导件（linux 统一
        // musl 静态件，glibc 目标经 musl 姊妹解析）随包装进安装根，与用户载荷同权：
        // 撞名走 claimed 冲突显式拒绝，不静默覆盖。
        if (bundle.Update is { } update)
        {
            var target = item.Target.Target;
            AddGeneratedFile(
                installRoot + "/" + UpdateInstallIdentity.FileName,
                UpdateIdentitySidecar.Serialize(update, PackageFormat.Rpm, target, packageName),
                420 /* 0644 */);
            // C-N5：ResolveOrThrow 失败路径会复跑整条探测链产出带探测位的诊断
            // （TryResolve 刚跑过同一条链）——开销可忽略，登记。
            AddFile(
                installRoot + "/" + UpdateBootstrapper.FileNameFor(target),
                UpdateBootstrapper.ResolveOrThrow(update, target), 493 /* 0755 */);
        }

        // Desktop integration (shared with the .deb backend): a generated or
        // caller-supplied .desktop entry, hicolor icons and optional metainfo.
        foreach (var file in FreedesktopFiles.Collect(
            bundle, packageName, installRoot, mainExecutable, binLink,
            new FreedesktopFiles.Options
            {
                DesktopFile = settings.DesktopFile,
                MetainfoFile = settings.MetainfoFile,
                Categories = settings.Categories is { Count: > 0 } categories ? string.Join(";", categories) : null,
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
                Gzip.Compress(File.ReadAllBytes(
                    FreedesktopFiles.RequireExisting(changelogSource, "ChangelogFile"))),
                420 /* 0644 */);
        }
        if (bundle.LicenseFile is { Length: > 0 } license)
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
        // via FileFlagsFor; explicit ConfigLocations paths are resolved here and
        // must already exist as regular files in the payload.
        var byPath = new Dictionary<string, PayloadEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byPath[entry.ArchivePath] = entry;
        }
        foreach (var path in settings.ConfigLocations ?? [])
        {
            var destination = FreedesktopFiles.NormalizeAbsoluteDestination(path, "rpm");
            if (!byPath.TryGetValue(destination, out var entry) ||
                entry.IsDirectory || entry.IsSymlink)
            {
                throw new ArgumentException(
                    $"The ConfigLocations entry '{path}' has no regular file in the payload.");
            }
            // C1：更新自持件显式列进 ConfigLocations 也不打 noreplace——
            // 升级必须能替换身份旁车与引导件。
            if (!IsUpdateSelfFile(entry.ArchivePath))
            {
                entry.FileFlags |= 17; // RPMFILE_CONFIG | RPMFILE_NOREPLACE
            }
        }

        // Emit the owned directory entries (install root plus everything below it).
        book.ClaimDirectory(installRoot);
        foreach (var dir in book.Directories.OrderBy(d => d, StringComparer.Ordinal))
        {
            entries.Add(new PayloadEntry { ArchivePath = dir, Mode = 0x4000 | 493 /* 040755 */ });
        }

        if (binLink.Length > 0)
        {
            var target = installRoot.StartsWith("/usr/", StringComparison.Ordinal)
                ? "../" + installRoot.Substring("/usr/".Length) + "/" + mainExecutable
                : installRoot + "/" + mainExecutable;
            AddLink("/usr/bin/" + binLink, target);
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
            Data = entry.IsDirectory || entry.SourcePath is not null
                ? []
                : entry.ReadBytes(),
            // 常规宿主文件按流写——大载荷不再整档驻内存。
            OpenContent = entry.IsDirectory || entry.SourcePath is null
                ? null
                : () => File.OpenRead(entry.SourcePath),
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
        if (archivePath.StartsWith("/etc/", StringComparison.Ordinal) &&
            !IsUpdateSelfFile(archivePath))
        {
            flags |= 17; // RPMFILE_CONFIG | RPMFILE_NOREPLACE
        }
        return flags;
    }

    // C1：更新自持件豁免 %config(noreplace)——bundler-update.json（feedUrl/pkg/
    // publicKey）与 bundler-updater（换包执行体）标了 noreplace 后升级永不替换，
    // 旧身份/旧引导件会随包管理器"保留原配置"语义滞留。按归档路径叶名判定
    //（InstallRoot 可配 /etc 下，注入件与用户 /etc 件走同一打标面）。
    private static bool IsUpdateSelfFile(string archivePath) =>
        archivePath.EndsWith("/" + UpdateInstallIdentity.FileName, StringComparison.Ordinal) ||
        archivePath.EndsWith("/bundler-updater", StringComparison.Ordinal);

    internal static string MapArchitecture(BundleTarget target) =>
        ArtifactNaming.ArchToken(target, PackageFormat.Rpm)
        ?? throw new NotSupportedException($"No rpm architecture mapping for '{target.Architecture}'.") ;

    private static byte[] Align8(byte[] header)
    {
        var pad = (8 - header.Length % 8) % 8;
        if (pad == 0) return header;
        var result = new byte[header.Length + pad];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        return result;
    }

    // FILEDIGESTS 逐文件 sha256：源流式喂哈希，整文件不再驻内存。
    private static string FileDigest(PayloadEntry entry)
    {
        using var sha256 = SHA256.Create();
        if (entry.IsSymlink)
        {
            return Hashes.ToLowerHex(sha256.ComputeHash(Encoding.UTF8.GetBytes(entry.LinkTarget)));
        }
        if (entry.Content is { } content)
        {
            return Hashes.ToLowerHex(sha256.ComputeHash(content));
        }
        using var stream = File.OpenRead(entry.SourcePath!);
        return Hashes.ToLowerHex(sha256.ComputeHash(stream));
    }
}
