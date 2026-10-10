using System.Security.Cryptography;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AppImage;

internal sealed class AppImageBundleBackend(
    AppImageBundleConfiguration settings,
    AppImageBundlerOptions options,
    DesktopOperatingSystem operatingSystem) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.AppImage;
    public DesktopOperatingSystem OperatingSystem => operatingSystem;

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        // glibc 与 musl 目标共用同一后端：runtime 为静态 ELF，两族皆可运行。
        // 宿主门先于 AppDirBuilder：Windows 宿主应在任何 chmod/ln 尝试之前报 PNSE。
        AppImageToolset.RequireLinuxHost();
        var built = AppDirBuilder.Build(
            context.Configuration, context.Item, settings, context.WorkDirectory, context.Logger);
        var toolset = AppImageToolset.Resolve(
            built.EnvironmentArchitecture, options.ToolsetCacheDirectory, cancellationToken);
        var signing = PrepareSigning(settings, context.WorkDirectory);

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
        if (signing is not null)
        {
            arguments.Insert(0, "--sign");
        }
        arguments.Add(outputPath);

        var environment = new Dictionary<string, string>
        {
            ["ARCH"] = built.EnvironmentArchitecture,
            ["APPIMAGE_EXTRACT_AND_RUN"] = "1"
        };
        context.Logger.Log(BundleLogLevel.Information,
            $"Running appimagetool ({built.EnvironmentArchitecture}) → {fileName}");
        try
        {
            if (signing is not null)
            {
                environment["GNUPGHOME"] = signing.GnupgHome;
                if (settings.Signing.Passphrase is { Length: > 0 } passphrase)
                {
                    environment["APPIMAGETOOL_SIGN_PASSPHRASE"] = passphrase;
                }
            }
            await AppImageProcessRunner.RunAsync(
                toolset.ToolPath, arguments, context.WorkDirectory, cancellationToken,
                environment);
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
        finally
        {
            signing?.Dispose();
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

    /// <summary>
    /// Configuration-time checks the facade also runs during multi-format
    /// pre-validation; everything past this point needs the build environment.
    /// </summary>
    internal static void ValidateConfiguration(AppImageBundleConfiguration settings)
    {
        var hasKey = settings.Signing.KeyFile is { Length: > 0 };
        if (settings.Signing.Passphrase is { Length: > 0 } && !hasKey)
        {
            throw new ArgumentException(
                "SigningKeyPassphrase requires SigningKeyFile to point at an OpenPGP secret key.");
        }
        if (hasKey && !File.Exists(settings.Signing.KeyFile!))
        {
            throw new ArgumentException(
                $"SigningKeyFile '{settings.Signing.KeyFile}' does not exist.");
        }
    }

    private static AppImageSigning.SigningContext? PrepareSigning(
        AppImageBundleConfiguration settings, string workDirectory)
    {
        ValidateConfiguration(settings);
        if (settings.Signing.KeyFile is not { Length: > 0 })
        {
            return null;
        }
        return AppImageSigning.Prepare(settings.Signing.KeyFile!, workDirectory);
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
