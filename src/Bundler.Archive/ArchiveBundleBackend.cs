using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;

namespace DotNet.Bundler.Archive;

/// <summary>
/// Backend for one (OS, format) pair — archives are OS- and libc-agnostic so the facade
/// registers one instance per TargetOperatingSystem for each of
/// <see cref="PackageFormat.Zip"/> and <see cref="PackageFormat.TarGz"/>.
/// </summary>
internal sealed class ArchiveBundleBackend(
    ArchiveBundleConfiguration settings,
    TargetOperatingSystem operatingSystem,
    PackageFormat format) : IBundleBackend
{
    public PackageFormat Format => format;
    public TargetOperatingSystem OperatingSystem => operatingSystem;
    public LibC? RequiredLibC => null;

    public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        var packageName = ArchiveIdentity.PackageName(context.Configuration, settings);
        var version = settings.Version ?? context.Configuration.Version;
        var stem = ArchiveIdentity.ArchiveStem(settings, packageName, version, context.Item);
        var entries = ArchiveTree.UnderStem(ArchiveTree.Build(
            context.Configuration, context.Item, settings, context.WorkDirectory, context.Logger),
            stem, context.Item.InputDirectory).ToList();
        if (context.Configuration.Update is { } update)
        {
            // 身份旁车写进工作目录后作为额外条目随归档顶层目录进包；
            // 输入树若自带同名条目一律剔除——打包期身份以本构建为准，重复名进包会导致解包歧义。
            UpdateIdentitySidecar.WriteIfEnabled(
                context.WorkDirectory, update, format, context.Item.Target.Target);
            var target = context.Item.Target.Target;
            UpdateBootstrapper.TryResolve(update, target, out var bootstrapper);
            var injected = new HashSet<string>(StringComparer.Ordinal)
            {
                stem + "/" + UpdateIdentitySidecar.FileName,
            };
            if (bootstrapper is { Length: > 0 })
            {
                injected.Add(stem + "/" + UpdateBootstrapper.FileNameFor(target));
            }
            entries.RemoveAll(e => injected.Contains(e.ArchivePath));
            entries.Add(new ArchiveTree.Entry
            {
                ArchivePath = stem + "/" + UpdateIdentitySidecar.FileName,
                Kind = TarEntryKind.File,
                Mode = 420 /* 0644 */,
                SourcePath = Path.Combine(context.WorkDirectory, UpdateIdentitySidecar.FileName),
            });
            if (bootstrapper is { Length: > 0 })
            {
                entries.Add(new ArchiveTree.Entry
                {
                    ArchivePath = stem + "/" + UpdateBootstrapper.FileNameFor(target),
                    Kind = TarEntryKind.File,
                    Mode = 493 /* 0755 */,
                    SourcePath = bootstrapper,
                });
            }
        }
        var extension = format == PackageFormat.Zip ? ".zip" : ".tar.gz";
        Directory.CreateDirectory(context.Item.OutputDirectory);
        var outputPath = Path.Combine(context.Item.OutputDirectory, stem + extension);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        context.Logger.Log(BundleLogLevel.Information,
            $"Writing {format} archive → {stem + extension}");
        // macOS 上载荷带扩展属性（签名 xattr 等）时走系统工具保真——managed 写出器丢 xattr。
        var hostToolWritten = MacArchiveTools.TryWriteWithHostTools(
            entries, context.Item.InputDirectory, format, outputPath,
            context.WorkDirectory, context.Logger);
        try
        {
            if (!hostToolWritten)
            {
                using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (format == PackageFormat.Zip)
                    {
                        ZipWriter.Write(stream, entries.Select(ArchiveTree.ToZipEntry));
                    }
                    else
                    {
                        using var gzip = new System.IO.Compression.GZipStream(
                            stream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
                        TarWriter.Write(gzip, entries.Select(ArchiveTree.ToTarEntry));
                    }
                }
            }
        }
        catch
        {
            // No half-written archive is left behind on failure.
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            throw;
        }
        WriteSha256Sidecar(outputPath);
        return Task.FromResult<IReadOnlyList<BundleArtifact>>(
            [new BundleArtifact(format, context.Item.Target.Target, outputPath)]);
    }

    private static void WriteSha256Sidecar(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        File.WriteAllText(path + ".sha256",
            Hashes.Sha256Hex(stream) + "  " + Path.GetFileName(path) + "\n");
    }
}
