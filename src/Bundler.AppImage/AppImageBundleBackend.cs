using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AppImage;

internal sealed class AppImageBundleBackend(
    AppImageBundleConfiguration settings,
    AppImageBundlerOptions options) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.AppImage;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.Linux;

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        var built = AppDirBuilder.Build(
            context.Configuration, context.Item, settings, context.WorkDirectory);
        var toolset = AppImageToolset.Resolve(
            built.EnvironmentArchitecture, options.ToolsetCacheDirectory, cancellationToken);

        var fileName = $"{built.PackageName}_{built.Version}_{built.FileArchitecture}.AppImage";
        Directory.CreateDirectory(context.Item.OutputDirectory);
        var outputPath = Path.Combine(context.Item.OutputDirectory, fileName);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        // The pinned appimagetool's mksquashfs only supports zstd, so no
        // compression knob is exposed; the runtime is always embedded.
        var arguments = new List<string>
        {
            "--appimage-extract-and-run",
            "--no-appstream",
            "--runtime-file", toolset.RuntimePath,
            built.AppDirPath
        };
        arguments.Add(outputPath);

        context.Logger.Log(BundleLogLevel.Information,
            $"Running appimagetool ({built.EnvironmentArchitecture}) → {fileName}");
        try
        {
            await AppImageProcessRunner.RunAsync(
                toolset.ToolPath, arguments, context.WorkDirectory, cancellationToken,
                new Dictionary<string, string>
                {
                    ["ARCH"] = built.EnvironmentArchitecture,
                    ["APPIMAGE_EXTRACT_AND_RUN"] = "1"
                });
        }
        catch
        {
            // No half-written .AppImage is left behind on tool failure.
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            throw;
        }
        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"appimagetool exited cleanly but produced no '{outputPath}'.");
        }
        AppImageToolset.Chmod(outputPath, "+x");
        WriteSha256Sidecar(outputPath);

        return [new BundleArtifact(
            PackageFormat.AppImage, context.Item.Target.RuntimeIdentifier, outputPath)];
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
