using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;
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

    /// <summary>
    /// Configuration-time checks the facade also runs during multi-format
    /// pre-validation; everything past this point needs the build environment.
    /// Returns the resolved volume name.
    /// </summary>
    internal static string ValidateConfiguration(MacDmgBundleConfiguration settings, BundleConfiguration bundle)
    {
        var volumeName = settings.VolumeName ?? MacAppBundleBackend.SanitizeFileName(bundle.ProductName);
        if (volumeName.Trim().Length == 0)
        {
            throw new ArgumentException("The .dmg volume name must not be empty.");
        }
        ValidateSigning(settings.Signing);
        return volumeName;
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        var isMacOs = HostCheck?.Invoke() ?? RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (!isMacOs)
        {
            throw new PlatformNotSupportedException(
                ".dmg creation requires a macOS host (hdiutil/osascript are not cross-host).");
        }

        var bundle = context.Configuration;
        var item = context.Item;
        var logger = context.Logger;
        var volumeName = ValidateConfiguration(settings, bundle);

        var applicationName = MacAppBundleBackend.SanitizeFileName(bundle.ProductName) + ".app";
        var appPath = Path.Combine(
            bundle.OutputDirectory, item.Target.RuntimeIdentifier, "app", applicationName);
        if (!Directory.Exists(appPath))
        {
            throw new DirectoryNotFoundException(
                "The .dmg backend expects the intermediate .app at " + appPath +
                " (the planner adds it automatically when 'dmg' is requested).");
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
            CopyTree(appPath, Path.Combine(stageDirectory, applicationName), logger);
            await MacDmgProcessRunner.RunAsync(
                "ln", ["-s", "/Applications", Path.Combine(stageDirectory, "Applications")],
                workDirectory, cancellationToken);

            // Read-write master image, sized automatically from -srcfolder contents.
            // hdiutil create 偶发 "Resource busy"（diskimages-helper/mds 抢新镜像）——定向短重试；
            // 其他错误立即抛出，-ov 使重试幂等
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await MacDmgProcessRunner.RunAsync(
                        "hdiutil",
                        ["create", "-srcfolder", stageDirectory, "-volname", volumeName,
                         "-format", "UDRW", "-ov", readWriteImage],
                        workDirectory, cancellationToken);
                    break;
                }
                catch (InvalidOperationException ex)
                    when (attempt < 5 && ex.Message.IndexOf("Resource busy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    await Task.Delay(1000 * attempt, cancellationToken);
                }
            }

            // Layout/branding extras need headroom that -srcfolder auto-sizing doesn't leave.
            if (!settings.SkipWindowLayout || settings.VolumeIconFile is not null)
            {
                // Relative "+64m" is rejected on -srcfolder-sized images; grow via -limits math.
                var limits = await MacDmgProcessRunner.TryRunAsync(
                    "hdiutil", ["resize", "-limits", readWriteImage],
                    workDirectory, cancellationToken);
                var fields = limits is { ExitCode: 0 }
                    ? limits.StandardOutput.Trim().Split(
                        new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    : Array.Empty<string>();
                MacDmgProcessRunner.Result? resized = null;
                if (fields.Length >= 2 && long.TryParse(fields[1], out var currentSectors))
                {
                    resized = await MacDmgProcessRunner.TryRunAsync(
                        "hdiutil",
                        ["resize", "-sectors", (currentSectors + 131072).ToString(), readWriteImage],
                        workDirectory, cancellationToken);
                }
                if (resized is not { ExitCode: 0 })
                {
                    logger.Log(
                        BundleLogLevel.Warning,
                        "hdiutil resize failed; layout extras may not fit in the image. " +
                        (resized is { } r ? r.StandardError.Trim()
                            : limits is { } l ? l.StandardError.Trim()
                            : "hdiutil could not be started"));
                }
            }

            // -nobrowse hides the volume from Finder — drop it when the layout pass runs.
            var attachArgs = new List<string>
            {
                "attach", readWriteImage, "-readwrite", "-noverify"
            };
            if (settings.SkipWindowLayout)
            {
                attachArgs.Add("-nobrowse");
            }
            attachArgs.Add("-mountpoint");
            attachArgs.Add(mountDirectory);
            await MacDmgProcessRunner.RunAsync(
                "hdiutil", attachArgs, workDirectory, cancellationToken);
            mounted = true;

            // 不设 Finder 扩展隐藏位：SetFile -a E 会把 com.apple.FinderInfo xattr 写到 .app 根，
            // 随拖放安装跟到用户机，`codesign --verify --deep --strict` 判 detritus 拒绝。

            await ApplyBrandingAsync(
                mountDirectory, applicationName, workDirectory,
                cancellationToken, logger);

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

            // SLA resources land on the converted UDIF image (udifrez rejects read-write images).
            if (bundle.LicenseFile is { Length: > 0 } licenseFile)
            {
                if (!File.Exists(licenseFile))
                {
                    throw new FileNotFoundException(
                        $"The .dmg license file does not exist: {licenseFile}", licenseFile);
                }
                var slaPlist = Path.Combine(workDirectory, "sla.plist");
                File.WriteAllText(
                    slaPlist, MacDmgLicenseResources.BuildPlist(licenseFile, logger));
                await MacDmgProcessRunner.RunAsync(
                    "hdiutil", ["udifrez", "-xml", slaPlist, "-image", outputPath],
                    workDirectory, cancellationToken);
            }

            await SignImageAsync(outputPath, workDirectory, cancellationToken, logger);

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

    private async Task ApplyBrandingAsync(
        string mountDirectory,
        string applicationName,
        string workDirectory,
        CancellationToken cancellationToken,
        IBundleLogger logger)
    {
        string? backgroundItemName = null;
        if (!settings.SkipWindowLayout)
        {
            // Window background goes into the hidden .background folder on the volume.
            if (settings.BackgroundFile is { Length: > 0 } backgroundFile)
            {
                if (!File.Exists(backgroundFile))
                {
                    throw new FileNotFoundException(
                        $"The .dmg background image does not exist: {backgroundFile}", backgroundFile);
                }
                var backgroundDirectory = Path.Combine(mountDirectory, ".background");
                Directory.CreateDirectory(backgroundDirectory);
                backgroundItemName = Path.GetFileName(backgroundFile);
                File.Copy(backgroundFile, Path.Combine(backgroundDirectory, backgroundItemName), overwrite: true);
            }
            await ApplyWindowLayoutAsync(
                mountDirectory, applicationName, backgroundItemName,
                workDirectory, cancellationToken, logger);
        }
        else
        {
            // -nobrowse 挂载不跑 Finder 布局，背景图拷进去只会成无人引用的孤儿文件。
            if (settings.BackgroundFile is { Length: > 0 })
            {
                logger.Log(
                    BundleLogLevel.Warning,
                    "BackgroundFile is ignored: SkipWindowLayout attaches the volume -nobrowse " +
                    "and the Finder layout pass that references .background/ never runs.");
            }
            logger.Log(
                BundleLogLevel.Information,
                "Skipping the Finder window layout (SkipWindowLayout).");
        }

        // Volume icon: .VolumeIcon.icns + custom-icon flag on the volume root.
        // Written after the Finder pass: opening the volume window makes Finder strip a
        // pre-staged .VolumeIcon.icns and clear the custom-icon bit on modern macOS.
        if (settings.VolumeIconFile is { Length: > 0 } volumeIconFile)
        {
            if (!File.Exists(volumeIconFile))
            {
                throw new FileNotFoundException(
                    $"The .dmg volume icon does not exist: {volumeIconFile}", volumeIconFile);
            }
            File.Copy(volumeIconFile, Path.Combine(mountDirectory, ".VolumeIcon.icns"), overwrite: true);
            var flag = await MacDmgProcessRunner.TryRunAsync(
                "SetFile", ["-a", "C", mountDirectory], workDirectory, cancellationToken);
            if (flag is not { ExitCode: 0 })
            {
                logger.Log(
                    BundleLogLevel.Warning,
                    "SetFile is unavailable; the custom volume icon flag was not set.");
            }
        }
    }

    private async Task ApplyWindowLayoutAsync(
        string mountDirectory,
        string applicationName,
        string? backgroundItemName,
        string workDirectory,
        CancellationToken cancellationToken,
        IBundleLogger logger)
    {
        var script = BuildFinderLayoutScript(mountDirectory, applicationName, backgroundItemName);
        var scriptArgs = script.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(line => (string[])["-e", line])
            .ToArray();
        // Finder can take a moment to register a freshly attached volume; retry before degrading.
        MacDmgProcessRunner.Result? layout = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            layout = await MacDmgProcessRunner.TryRunAsync(
                "osascript", scriptArgs, workDirectory, cancellationToken);
            if (layout is null or { ExitCode: 0 })
            {
                break;
            }
            await Task.Delay(800, cancellationToken);
        }
        if (layout is not { ExitCode: 0 })
        {
            logger.Log(
                BundleLogLevel.Warning,
                "Finder layout skipped (no GUI session, e.g. headless CI): " +
                (layout is null ? "osascript could not be started" : layout.StandardError.Trim()));
            return;
        }

        // Finder persists the layout to .DS_Store asynchronously; detaching before it lands
        // silently drops the configured window state, so wait briefly for the file to appear.
        var dsStore = Path.Combine(mountDirectory, ".DS_Store");
        for (var wait = 0; wait < 40 && !File.Exists(dsStore); wait++)
        {
            await Task.Delay(250, cancellationToken);
        }
        if (File.Exists(dsStore))
        {
            // Let Finder finish flushing the tail of the write.
            await Task.Delay(250, cancellationToken);
        }
    }

    private string BuildFinderLayoutScript(
        string mountDirectory, string applicationName, string? backgroundItemName)
    {
        // Finder names a volume mounted at a custom -mountpoint after the mount directory,
        // not the filesystem volume name, so resolve the disk through the mount point itself.
        var script = new System.Text.StringBuilder();
        script.AppendLine("tell application \"Finder\"");
        script.AppendLine(
            $"  set theDisk to disk (name of (POSIX file \"{EscapeAppleScript(mountDirectory)}\" as alias))");
        script.AppendLine("  tell theDisk");
        script.AppendLine("    open");
        script.AppendLine("    set current view of container window to icon view");
        script.AppendLine("    set toolbar visible of container window to false");
        script.AppendLine("    set statusbar visible of container window to false");
        script.AppendLine(
            $"    set the bounds of container window to {{{settings.WindowX}, {settings.WindowY}, " +
            $"{settings.WindowX + settings.WindowWidth}, {settings.WindowY + settings.WindowHeight}}}");
        script.AppendLine("    set theViewOptions to the icon view options of container window");
        script.AppendLine("    set arrangement of theViewOptions to not arranged");
        script.AppendLine($"    set icon size of theViewOptions to {settings.IconSize}");
        if (backgroundItemName is not null)
        {
            script.AppendLine(
                $"    set background picture of theViewOptions to file \".background:{EscapeAppleScript(backgroundItemName)}\"");
        }
        script.AppendLine(
            $"    set position of item \"{EscapeAppleScript(applicationName)}\" of container window to " +
            $"{{{settings.AppIconX}, {settings.AppIconY}}}");
        script.AppendLine(
            "    set position of item \"Applications\" of container window to " +
            $"{{{settings.ApplicationsIconX}, {settings.ApplicationsIconY}}}");
        script.AppendLine("    update without registering applications");
        script.AppendLine("    close");
        script.AppendLine("    open");
        script.AppendLine("  end tell");
        script.AppendLine("end tell");
        return script.ToString();
    }

    private static string EscapeAppleScript(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void ValidateSigning(MacDmgSigningConfiguration? signing)
    {
        if (signing is null)
        {
            return;
        }
        if (signing.Identity is not null && !string.IsNullOrEmpty(signing.TemporaryCertificatePath))
        {
            throw new ArgumentException(
                "DMG SignIdentity and TemporaryCertificatePath are mutually exclusive.");
        }
        if (signing.Identity is { Length: 0 })
        {
            throw new ArgumentException("DMG SignIdentity cannot be empty (use \"-\" for ad-hoc).");
        }
        if (signing.TemporaryCertificatePath is { Length: > 0 } certificatePath &&
            !File.Exists(Path.GetFullPath(certificatePath)))
        {
            throw new FileNotFoundException(
                "DMG TemporaryCertificatePath does not exist.", certificatePath);
        }
    }

    private async Task SignImageAsync(
        string imagePath,
        string workDirectory,
        CancellationToken cancellationToken,
        IBundleLogger logger)
    {
        var signing = settings.Signing;
        if (signing is null ||
            (signing.Identity is null && string.IsNullOrEmpty(signing.TemporaryCertificatePath)))
        {
            return;
        }

        MacAppSigning.TemporaryKeychain? keychain = null;
        string identity;
        try
        {
            if (signing.TemporaryCertificatePath is { Length: > 0 } certificatePath)
            {
                keychain = await MacAppSigning.TemporaryKeychain.CreateAsync(
                    workDirectory, Path.GetFullPath(certificatePath),
                    signing.TemporaryCertificatePassword ?? "", logger, cancellationToken);
                identity = keychain.Identity;
            }
            else
            {
                identity = signing.Identity!;
            }

            logger.Log(
                BundleLogLevel.Information,
                identity == "-"
                    ? "Signing the .dmg ad-hoc (codesign -s -)."
                    : $"Signing the .dmg with identity '{identity}'.");
            await MacDmgProcessRunner.RunAsync(
                "codesign",
                ["--force", "--sign", identity,
                 identity == "-" ? "--timestamp=none" : "--timestamp", imagePath],
                workDirectory, cancellationToken);
            await MacDmgProcessRunner.RunAsync(
                "codesign", ["--verify", imagePath], workDirectory, cancellationToken);
        }
        finally
        {
            if (keychain is not null)
            {
                await keychain.DisposeAsync(workDirectory, cancellationToken);
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

    internal static void CopyTree(string source, string destination, IBundleLogger log, int depth = 0)
    {
        // 目录符号链接环会让递归失控——深度封顶显式报错。
        if (depth > 64)
        {
            throw new InvalidDataException(
                "Directory nesting too deep inside the .dmg payload (possible symlink loop): " + source);
        }
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            // Sockets, FIFOs and device nodes cannot be copied; skip them.
            if (!UnixFileTypes.IsRegularFile(file))
            {
                log.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
                continue;
            }
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)), log, depth + 1);
        }
    }
}
