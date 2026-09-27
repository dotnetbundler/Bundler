using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler;

namespace DotNet.Bundler.Deb;

/// <summary>
/// Emits a .deb entirely in managed code:
/// an ar archive holding debian-binary, control.tar.gz and data.tar.gz.
/// No host packaging tools are invoked, so the writer runs on any .NET host.
/// </summary>
internal static class DebPackageWriter
{
    internal sealed class Result
    {
        internal string OutputPath = "";
        internal string PackageName = "";
        internal string Version = "";
        internal string Architecture = "";
    }

    private sealed class PayloadEntry
    {
        internal string ArchivePath = "";   // e.g. "usr/lib/pkg/app" (no leading './')
        internal TarEntryKind Kind;
        internal int Mode;
        internal string? SourcePath;        // host file for Kind=File
        internal string LinkTarget = "";    // for Kind=Symlink
        internal long Size;                 // file size for Installed-Size/md5sums
    }

    internal static Result Build(
        BundleConfiguration bundle,
        BundlePlanItem item,
        DebBundleConfiguration settings,
        IBundleLogger logger)
    {
        var packageName = settings.PackageName ?? DebName.Sanitize(bundle.ProductName);
        DebName.Validate(packageName);
        var version = DebVersion.Map(bundle.Version, settings);
        var architecture = settings.Architecture ?? MapArchitecture(item.Target.Architecture);
        ValidateArchitecture(architecture);
        var installRoot = NormalizeInstallRoot(settings.InstallRoot, packageName);
        var binLink = settings.BinLink ?? packageName;
        if (binLink.Length > 0)
        {
            ValidateBinLinkName(binLink);
        }
        var maintainer = settings.Maintainer ?? bundle.Publisher ?? bundle.Identifier;
        if (string.IsNullOrWhiteSpace(maintainer) || maintainer.IndexOf('\n') >= 0)
        {
            throw new ArgumentException(
                "The .deb maintainer must be a non-empty single line; set Publisher or Maintainer.");
        }

        var payload = CollectPayload(bundle, item, installRoot, binLink, packageName);
        var dataTarGz = Gzip(TarData(payload));
        var controlTarGz = Gzip(TarBytes(ControlEntries(bundle, payload, packageName, version,
            architecture, maintainer)));

        var fileName = packageName + "_" + DebVersion.FileNameVersion(version) + "_" +
            architecture + ".deb";
        var outputPath = Path.Combine(item.OutputDirectory, fileName);
        var sidecarPath = outputPath + ".sha256";
        try
        {
            Directory.CreateDirectory(item.OutputDirectory);
            using (var stream = File.Create(outputPath))
            {
                ArWriter.Write(stream,
                [
                    new ArMember("debian-binary", Encoding.ASCII.GetBytes("2.0\n")),
                    new ArMember("control.tar.gz", controlTarGz),
                    new ArMember("data.tar.gz", dataTarGz)
                ]);
            }
            var hash = Sha256Hex(File.ReadAllBytes(outputPath));
            File.WriteAllText(sidecarPath,
                hash + "  " + fileName + "\n", new UTF8Encoding(false));
            logger.Log(BundleLogLevel.Information, $"Wrote {fileName} (sha256 {hash}).");
            return new Result
            {
                OutputPath = outputPath,
                PackageName = packageName,
                Version = version,
                Architecture = architecture
            };
        }
        catch
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }
            throw;
        }
    }

    private static List<PayloadEntry> CollectPayload(
        BundleConfiguration bundle,
        BundlePlanItem item,
        string installRoot,
        string binLink,
        string packageName)
    {
        var entries = new List<PayloadEntry>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);

        void ClaimDirectory(string path)
        {
            var segments = path.Split('/');
            for (var i = 1; i <= segments.Length; i++)
            {
                var prefix = string.Join("/", segments.Take(i));
                if (directories.Add(prefix))
                {
                    entries.Add(new PayloadEntry
                    {
                        ArchivePath = prefix,
                        Kind = TarEntryKind.Directory,
                        Mode = 493 /* 0755 */
                    });
                }
            }
        }

        void AddFile(string archivePath, string sourcePath, int mode)
        {
            ClaimDirectory(archivePath.Substring(0, archivePath.LastIndexOf('/')));
            if (!claimed.Add(archivePath))
            {
                throw new InvalidOperationException(
                    $"Two payload sources map to the same .deb path: '{archivePath}'.");
            }
            var info = new FileInfo(sourcePath);
            entries.Add(new PayloadEntry
            {
                ArchivePath = archivePath,
                Kind = TarEntryKind.File,
                Mode = mode,
                SourcePath = sourcePath,
                Size = info.Length
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
            if (directories.Add(archivePath))
            {
                entries.Add(new PayloadEntry
                {
                    ArchivePath = archivePath,
                    Kind = TarEntryKind.Directory,
                    Mode = 493 /* 0755 */
                });
            }
        }
        foreach (var file in Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
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
            if (!claimed.Add(linkPath))
            {
                throw new InvalidOperationException(
                    $"A payload file already occupies the bin-link path: '{linkPath}'.");
            }
            ClaimDirectory("usr/bin");
            // Links under /usr/bin use a relative target for usr/* roots and an
            // absolute target for other roots (e.g. /opt/<name>).
            var target = installRoot.StartsWith("usr/", StringComparison.Ordinal)
                ? "../" + installRoot.Substring(4) + "/" + mainExecutable
                : "/" + installRoot + "/" + mainExecutable;
            entries.Add(new PayloadEntry
            {
                ArchivePath = linkPath,
                Kind = TarEntryKind.Symlink,
                Mode = 511 /* 0777 */,
                LinkTarget = target
            });
        }

        return entries;
    }

    private static List<TarEntry> ControlEntries(
        BundleConfiguration bundle,
        List<PayloadEntry> payload,
        string packageName,
        string version,
        string architecture,
        string maintainer)
    {
        var control = new StringBuilder();
        control.Append("Package: ").Append(packageName).Append('\n');
        control.Append("Version: ").Append(version).Append('\n');
        control.Append("Architecture: ").Append(architecture).Append('\n');
        control.Append("Maintainer: ").Append(maintainer).Append('\n');
        control.Append("Installed-Size: ").Append(InstalledSize(payload)).Append('\n');
        control.Append("Priority: optional\n");
        if (!string.IsNullOrWhiteSpace(bundle.Homepage))
        {
            if (bundle.Homepage!.IndexOf('\n') >= 0)
            {
                throw new ArgumentException("The homepage must be a single line.");
            }
            control.Append("Homepage: ").Append(bundle.Homepage).Append('\n');
        }
        var description = (bundle.Description ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = description.Split('\n');
        var shortDescription = lines.FirstOrDefault(line => line.Trim().Length > 0)?.Trim()
            ?? bundle.ProductName;
        control.Append("Description: ").Append(shortDescription).Append('\n');
        var extended = string.Join("\n", lines.SkipWhile(line => line.Trim().Length == 0).Skip(1)
            .Select(line => line.Trim().Length == 0 ? " ." : " " + line.TrimEnd()));
        if (extended.Length > 0)
        {
            control.Append(extended).Append('\n');
        }

        var md5sums = new StringBuilder();
        foreach (var entry in payload.Where(e => e.Kind == TarEntryKind.File))
        {
            var hash = Md5Hex(File.ReadAllBytes(entry.SourcePath!));
            md5sums.Append(hash).Append("  ").Append(entry.ArchivePath).Append('\n');
        }

        return
        [
            new TarEntry
            {
                Name = "./control",
                Kind = TarEntryKind.File,
                Mode = 420 /* 0644 */,
                Content = new UTF8Encoding(false).GetBytes(control.ToString())
            },
            new TarEntry
            {
                Name = "./md5sums",
                Kind = TarEntryKind.File,
                Mode = 420 /* 0644 */,
                Content = new UTF8Encoding(false).GetBytes(md5sums.ToString())
            }
        ];
    }

    private static int InstalledSize(List<PayloadEntry> payload)
    {
        // KiB occupied: files rounded up to a 1 KiB block each, plus one block per
        // directory or link (dpkg's du-style accounting).
        long total = 0;
        foreach (var entry in payload)
        {
            total += entry.Kind == TarEntryKind.File
                ? (entry.Size + 1023) / 1024
                : 1;
        }
        return (int)Math.Max(total, 1);
    }

    private static byte[] TarData(IEnumerable<PayloadEntry> entries)
    {
        using var buffer = new MemoryStream();
        TarWriter.Write(buffer, entries
            .OrderBy(entry => entry.ArchivePath, StringComparer.Ordinal)
            .Select(entry => new TarEntry
            {
                Name = "./" + entry.ArchivePath,
                Kind = entry.Kind,
                Mode = entry.Mode,
                Content = entry.Kind == TarEntryKind.File
                    ? File.ReadAllBytes(entry.SourcePath!)
                    : [],
                LinkTarget = entry.LinkTarget
            }));
        return buffer.ToArray();
    }

    private static byte[] TarBytes(IEnumerable<TarEntry> entries)
    {
        using var buffer = new MemoryStream();
        TarWriter.Write(buffer, entries);
        return buffer.ToArray();
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

    private static string MapArchitecture(CpuArchitecture architecture) => architecture switch
    {
        CpuArchitecture.X64 => "amd64",
        CpuArchitecture.Arm64 => "arm64",
        CpuArchitecture.X86 => "i386",
        _ => throw new NotSupportedException(
            $"No Debian architecture mapping for {architecture}; set Architecture explicitly.")
    };

    private static void ValidateArchitecture(string architecture)
    {
        if (!architecture.All(c => char.IsLower(c) || char.IsDigit(c) || c == '-') ||
            architecture.Length == 0)
        {
            throw new ArgumentException(
                $"'{architecture}' is not a valid Debian architecture (lowercase letters, digits, '-').");
        }
    }

    // Returns the tar-relative path (no leading '/'), e.g. "usr/lib/pkg".
    private static string NormalizeInstallRoot(string? installRoot, string packageName)
    {
        var root = string.IsNullOrWhiteSpace(installRoot)
            ? "/usr/lib/" + packageName
            : installRoot!.Trim();
        if (!root.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The .deb install root must be an absolute path: '{root}'.");
        }
        var normalized = root.Trim('/');
        if (normalized.Length == 0 ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                $"The .deb install root contains an invalid segment: '{root}'.");
        }
        return normalized;
    }

    private static void ValidateBinLinkName(string name)
    {
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            name is "." or ".." ||
            name.Trim().Length != name.Length || name.Length == 0)
        {
            throw new ArgumentException(
                $"'{name}' is not a valid /usr/bin link name (a bare file name).");
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

    private static string Md5Hex(byte[] content)
    {
        using var md5 = MD5.Create();
        return Hex(md5.ComputeHash(content));
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
}
