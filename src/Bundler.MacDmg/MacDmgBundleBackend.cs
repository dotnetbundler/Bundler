using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.MacApp;

namespace DotNet.Bundler.MacDmg;

internal sealed class MacDmgBundleBackend(MacDmgBundleConfiguration settings) : IBundleBackend
{
    private const int DetachMaxAttempts = 5;
    private static readonly int[] DetachBackoffMilliseconds = { 250, 500, 1000, 2000 };

    public PackageFormat Format => PackageFormat.Dmg;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.MacOS;

    /// <summary>Test seam: overrides the macOS host check.</summary>
    internal static Func<bool>? HostCheck;

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        var isMacOs = HostCheck?.Invoke() ?? RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (!isMacOs)
        {
            throw new NotSupportedException(
                ".dmg creation requires a macOS host (hdiutil/osascript are not cross-host).");
        }

        var bundle = context.Configuration;
        var item = context.Item;
        var logger = context.Logger;
        var applicationName = MacAppBundleBackend.SanitizeFileName(bundle.ProductName) + ".app";
        var appPath = Path.Combine(
            bundle.OutputDirectory, item.Target.RuntimeIdentifier, "app", applicationName);
        if (!Directory.Exists(appPath))
        {
            throw new DirectoryNotFoundException(
                "The .dmg backend expects the intermediate .app at " + appPath +
                " (the planner adds it automatically when 'dmg' is requested).");
        }

        var volumeName = settings.VolumeName ?? MacAppBundleBackend.SanitizeFileName(bundle.ProductName);
        if (volumeName.Trim().Length == 0)
        {
            throw new ArgumentException("The .dmg volume name must not be empty.");
        }

        var workDirectory = context.WorkDirectory;
        var stageDirectory = Path.Combine(workDirectory, "dmg-root");
        var mountDirectory = Path.Combine(workDirectory, "dmg-mount");
        var readWriteImage = Path.Combine(workDirectory, "image-rw.dmg");
        var imageName = MacAppBundleBackend.SanitizeFileName(bundle.ProductName) + ".dmg";
        var outputPath = Path.Combine(item.OutputDirectory, imageName);
        var mounted = false;

        try
        {
            // Stage the volume contents: the .app plus the /Applications drop link.
            Directory.CreateDirectory(stageDirectory);
            Directory.CreateDirectory(mountDirectory);
            CopyTree(appPath, Path.Combine(stageDirectory, applicationName));
            await MacDmgProcessRunner.RunAsync(
                "ln", ["-s", "/Applications", Path.Combine(stageDirectory, "Applications")],
                workDirectory, cancellationToken);

            // Read-write master image, sized automatically from -srcfolder contents.
            await MacDmgProcessRunner.RunAsync(
                "hdiutil",
                ["create", "-srcfolder", stageDirectory, "-volname", volumeName,
                 "-format", "UDRW", "-ov", readWriteImage],
                workDirectory, cancellationToken);

            await MacDmgProcessRunner.RunAsync(
                "hdiutil",
                ["attach", readWriteImage, "-readwrite", "-noverify", "-nobrowse",
                 "-mountpoint", mountDirectory],
                workDirectory, cancellationToken);
            mounted = true;

            // Hidden .app extension flag (SetFile ships with Xcode/CLT — degrade to a warning).
            var setFile = await MacDmgProcessRunner.TryRunAsync(
                "SetFile", ["-a", "E", Path.Combine(mountDirectory, applicationName)],
                workDirectory, cancellationToken);
            if (setFile is null || setFile.ExitCode != 0)
            {
                logger.Log(
                    BundleLogLevel.Warning,
                    "SetFile is unavailable (Xcode/CLT tool); the .app extension will stay visible.");
            }

            await DetachWithRetryAsync(mountDirectory, workDirectory, cancellationToken, logger);
            mounted = false;

            var compressionArgument = settings.Compression switch
            {
                MacDmgCompression.Udzo => "UDZO",
                MacDmgCompression.Udbz => "UDBZ",
                _ => "ULMO"
            };
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            await MacDmgProcessRunner.RunAsync(
                "hdiutil",
                ["convert", readWriteImage, "-format", compressionArgument, "-o", outputPath],
                workDirectory, cancellationToken);

            return [new BundleArtifact(PackageFormat.Dmg, item.Target.RuntimeIdentifier, outputPath)];
        }
        catch
        {
            if (mounted)
            {
                try
                {
                    await MacDmgProcessRunner.TryRunAsync(
                        "hdiutil", ["detach", mountDirectory, "-force"],
                        workDirectory, cancellationToken: CancellationToken.None);
                }
                catch
                {
                    // best-effort cleanup; do not mask the real failure
                }
            }
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            throw;
        }
        finally
        {
            if (File.Exists(readWriteImage))
            {
                File.Delete(readWriteImage);
            }
        }
    }

    private static async Task DetachWithRetryAsync(
        string mountDirectory,
        string workingDirectory,
        CancellationToken cancellationToken,
        IBundleLogger logger)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await MacDmgProcessRunner.TryRunAsync(
                "hdiutil", ["detach", mountDirectory], workingDirectory, cancellationToken);
            if (result is { ExitCode: 0 })
            {
                return;
            }
            if (attempt >= DetachMaxAttempts)
            {
                var detail = result is null
                    ? "hdiutil could not be started"
                    : $"exit {result.ExitCode}: {result.StandardError}";
                throw new InvalidOperationException(
                    $"hdiutil detach failed after {DetachMaxAttempts} attempts ({detail}).");
            }
            var delay = DetachBackoffMilliseconds[Math.Min(attempt - 1, DetachBackoffMilliseconds.Length - 1)];
            logger.Log(
                BundleLogLevel.Warning,
                $"hdiutil detach busy/failed (attempt {attempt}/{DetachMaxAttempts}); retrying in {delay} ms.");
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
