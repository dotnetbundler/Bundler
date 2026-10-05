using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;

namespace DotNet.Bundler.Archive;

/// <summary>
/// Backend for one (OS, format) pair — archives are OS-agnostic so the facade
/// registers one instance per DesktopOperatingSystem for each of
/// <see cref="PackageFormat.Zip"/> and <see cref="PackageFormat.TarGz"/>.
/// </summary>
internal sealed class ArchiveBundleBackend(
    ArchiveBundleConfiguration settings,
    ArchiveBundlerOptions options,
    DesktopOperatingSystem operatingSystem,
    PackageFormat format) : IBundleBackend
{
    public PackageFormat Format => format;
    public DesktopOperatingSystem OperatingSystem => operatingSystem;

    public Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        var packageName = ArchiveIdentity.PackageName(context.Configuration, settings);
        var version = settings.Version ?? context.Configuration.Version;
        var stem = ArchiveIdentity.ArchiveStem(settings, packageName, version, context.Item);
        var entries = ArchiveTree.UnderStem(ArchiveTree.Build(
            context.Configuration, context.Item, settings, context.WorkDirectory, context.Logger), stem).ToList();
        if (context.Configuration.Update is { } update)
        {
            // 身份旁车写进工作目录后作为额外条目随归档顶层目录进包。
            UpdateIdentitySidecar.WriteIfEnabled(
                context.WorkDirectory, update, format, context.Item.Target.RuntimeIdentifier);
            entries.Add(new ArchiveTree.Entry
            {
                ArchivePath = stem + "/" + UpdateIdentitySidecar.FileName,
                Kind = TarEntryKind.File,
                Mode = 420 /* 0644 */,
                SourcePath = Path.Combine(context.WorkDirectory, UpdateIdentitySidecar.FileName),
            });
            var rid = context.Item.Target.RuntimeIdentifier;
            if (UpdateBootstrapper.TryResolve(update, rid, out var bootstrapper))
            {
                entries.Add(new ArchiveTree.Entry
                {
                    ArchivePath = stem + "/" + UpdateBootstrapper.FileNameFor(rid),
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
        try
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
        _ = options;
        return Task.FromResult<IReadOnlyList<BundleArtifact>>(
            [new BundleArtifact(format, context.Item.Target.RuntimeIdentifier, outputPath)]);
    }

    private static void WriteSha256Sidecar(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = sha.ComputeHash(stream);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }
        File.WriteAllText(path + ".sha256",
            builder + "  " + Path.GetFileName(path) + "\n");
    }
}
