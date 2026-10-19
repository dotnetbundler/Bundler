using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacApp;

namespace DotNet.Bundler.MacDmg;

internal sealed class MacDmgBundleBackend(MacDmgBundleConfiguration settings) : IBundleBackend
{
    private const int DetachMaxAttempts = 5;
    // 连续两次 graceful 失败后递进 -force（卷被 Finder/diskarbitration 占持时的标准处置）
    private const int DetachForceAttempt = 3;
    private static readonly int[] DetachBackoffMilliseconds = { 250, 500, 1000, 2000 };
    private const int ConvertTransientMaxAttempts = 9;
    // 连续 EAGAIN 到本次数说明 diskimages-helper 簿记大概率已卡死——先踢掉再睡再试
    private const int ConvertUnwedgeAttempt = 3;
    // 共享 CI runner 上 diskimages-helper 资源竞争可持续一两分钟——
    // 退避顶到 60s、总预算约三分钟（181s），仍属"有限重试"而非死等。
    private static readonly int[] ConvertTransientBackoffMilliseconds = { 1000, 2000, 4000, 8000, 16000, 30000, 60000, 60000 };

    public PackageFormat Format => PackageFormat.Dmg;
    public TargetOperatingSystem OperatingSystem => TargetOperatingSystem.MacOS;
    public LibC? RequiredLibC => null;

    /// <summary>Test seam: overrides the macOS host check.</summary>
    internal static Func<bool>? HostCheck;

    /// <summary>
    /// Configuration-time checks the facade also runs during multi-format
    /// pre-validation; everything past this point needs the build environment.
    /// Returns the resolved volume name.
    /// </summary>
    internal static string ValidateConfiguration(MacDmgBundleConfiguration settings, BundleConfiguration bundle)
    {
        var volumeName = (settings.VolumeName ?? MacAppBundleBackend.SanitizeFileName(bundle.ProductName)).Trim();
        if (volumeName.Length == 0)
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
            bundle.OutputLayout == OutputLayout.ByFormat
                ? Path.Combine(bundle.OutputDirectory, "app")
                : bundle.OutputDirectory,
            ArtifactNaming.FileName(
                MacAppBundleBackend.SanitizeFileName(bundle.ProductName), bundle.Version, item.Target, PackageFormat.App));
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
        var imageName = ArtifactNaming.FileName(
            MacAppBundleBackend.SanitizeFileName(bundle.ProductName), bundle.Version, item.Target, PackageFormat.Dmg);
        var outputPath = Path.Combine(item.OutputDirectory, imageName);
        var mounted = false;

        try
        {
            // Stage the volume contents: the .app plus the /Applications drop link.
            Directory.CreateDirectory(stageDirectory);
            Directory.CreateDirectory(mountDirectory);
            CopyTree(appPath, Path.Combine(stageDirectory, applicationName), logger);
            await ToolRunner.RunAsync(
                "ln", ["-s", "/Applications", Path.Combine(stageDirectory, "Applications")],
                workDirectory, cancellationToken);

            // Read-write master image, sized automatically from -srcfolder contents.
            // hdiutil create 偶发 "Resource busy"（diskimages-helper/mds 抢新镜像）——定向短重试；
            // 其他错误立即抛出，-ov 使重试幂等
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await ToolRunner.RunAsync(
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
            if (settings.Layout is not null || settings.VolumeIconFile is not null)
            {
                // Relative "+64m" is rejected on -srcfolder-sized images; grow via -limits math.
                var limits = await ToolRunner.TryRunAsync(
                    "hdiutil", ["resize", "-limits", readWriteImage],
                    workDirectory, cancellationToken);
                var baseSectors = limits is { ExitCode: 0 }
                    ? ResizeBaseSectors(limits.StandardOutput)
                    : null;
                ToolRunner.Result? resized = null;
                if (baseSectors is { } currentSectors)
                {
                    resized = await ToolRunner.TryRunAsync(
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
            if (settings.Layout is null)
            {
                attachArgs.Add("-nobrowse");
            }
            attachArgs.Add("-mountpoint");
            attachArgs.Add(mountDirectory);
            await ToolRunner.RunAsync(
                "hdiutil", attachArgs, workDirectory, cancellationToken);
            mounted = true;
            // 挂载事实落 Information 日志（MSBuild 侧映射 High，正常发布输出可见）——
            // 挂载点+镜像路径入消息便于关联后续 branding/detach 失败域。
            logger.Log(
                BundleLogLevel.Information,
                $"Mounted DMG volume at '{mountDirectory}' from '{readWriteImage}'.");

            // 不设 Finder 扩展隐藏位：SetFile -a E 会把 com.apple.FinderInfo xattr 写到 .app 根，
            // 随拖放安装跟到用户机，`codesign --verify --deep --strict` 判 detritus 拒绝。

            await ApplyBrandingAsync(
                mountDirectory, applicationName, workDirectory,
                cancellationToken, logger);

            await EnsureDetachedAsync(
                mountDirectory, workDirectory, cancellationToken, logger, mandatory: true);
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
            // hdiutil convert 在高载宿主上偶发 EAGAIN（"Resource temporarily
            // unavailable"）——工具级瞬态，有限重试即可恢复，不是输入错误。
            // 若源镜像的簿记状态在原文件上卡死（macOS 已知痼疾），换用字节相同的
            // 副本继续 convert 即可绕开。
            var convertSource = readWriteImage;
            string? convertSourceCopy = null;
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await ToolRunner.RunAsync(
                            "hdiutil",
                            ["convert", convertSource, "-format", compressionArgument, "-o", outputPath],
                            workDirectory, cancellationToken);
                        break;
                    }
                    catch (InvalidOperationException e)
                        when (attempt < ConvertTransientMaxAttempts
                              && e.Message.IndexOf("Resource temporarily unavailable", StringComparison.Ordinal) >= 0)
                    {
                        var delay = ConvertTransientBackoffMilliseconds[
                            Math.Min(attempt - 1, ConvertTransientBackoffMilliseconds.Length - 1)];
                        logger.Log(
                            BundleLogLevel.Warning,
                            $"hdiutil convert transient failure (attempt {attempt}/{ConvertTransientMaxAttempts}); retrying in {delay} ms.");
                        if (attempt >= ConvertUnwedgeAttempt)
                        {
                            // diskimages-helper 是单用户 XPC 服务：卡死后每个后续 convert 都
                            // 接续 EAGAIN（簿记不随进程退出自愈）；killall 后随下次 hdiutil
                            // 请求自动重生。killall 无匹配进程返回非零——TryRun 忽略结果。
                            await ToolRunner.TryRunAsync(
                                "killall", ["-9", "diskimages-helper"],
                                workDirectory, cancellationToken);
                            logger.Log(
                                BundleLogLevel.Warning,
                                "diskimages-helper appears wedged; killed it so the next attempt respawns a fresh helper.");
                        }
                        if (File.Exists(outputPath))
                        {
                            File.Delete(outputPath);
                        }
                        if (convertSourceCopy is null)
                        {
                            // 源镜像可能仍被 diskimages-helper / convert 子进程持柄，
                            // 副本建不起来也属瞬态——留给下一轮重试再建。
                            try
                            {
                                var copy = readWriteImage + ".convert-copy.dmg";
                                File.Copy(readWriteImage, copy, overwrite: true);
                                convertSourceCopy = copy;
                                convertSource = copy;
                            }
                            catch (IOException)
                            {
                            }
                        }
                        await Task.Delay(delay, cancellationToken);
                    }
                }
            }
            finally
            {
                if (convertSourceCopy is not null && File.Exists(convertSourceCopy))
                {
                    File.Delete(convertSourceCopy);
                }
            }

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
                await ToolRunner.RunAsync(
                    "hdiutil", ["udifrez", "-xml", slaPlist, "-image", outputPath],
                    workDirectory, cancellationToken);
            }

            await SignImageAsync(outputPath, workDirectory, cancellationToken, logger);

            return [new BundleArtifact(PackageFormat.Dmg, item.Target.Target, outputPath)];
        }
        catch
        {
            if (mounted)
            {
                try
                {
                    // 失败路复用同一 detach 序列（graceful→-force 递进+info 复核），
                    // 非强制档：尽力清理不留挂载残留，但绝不掩盖真实失败。
                    await EnsureDetachedAsync(
                        mountDirectory, workDirectory, CancellationToken.None, logger,
                        mandatory: false);
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
        if (settings.Layout is not null)
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
                    "BackgroundFile is ignored: layout is null attaches the volume -nobrowse " +
                    "and the Finder layout pass that references .background/ never runs.");
            }
            logger.Log(
                BundleLogLevel.Information,
                "Skipping the Finder window layout (layout is null).");
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
            var flag = await ToolRunner.TryRunAsync(
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
        ToolRunner.Result? layout = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            layout = await ToolRunner.TryRunAsync(
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
        var layout = settings.Layout ?? new MacDmgLayoutConfiguration();
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
            $"    set the bounds of container window to {{{layout.WindowX}, {layout.WindowY}, " +
            $"{layout.WindowX + layout.WindowWidth}, {layout.WindowY + layout.WindowHeight}}}");
        script.AppendLine("    set theViewOptions to the icon view options of container window");
        script.AppendLine("    set arrangement of theViewOptions to not arranged");
        script.AppendLine($"    set icon size of theViewOptions to {layout.IconSize}");
        if (backgroundItemName is not null)
        {
            script.AppendLine(
                $"    set background picture of theViewOptions to file \".background:{EscapeAppleScript(backgroundItemName)}\"");
        }
        script.AppendLine(
            $"    set position of item \"{EscapeAppleScript(applicationName)}\" of container window to " +
            $"{{{layout.AppIconX}, {layout.AppIconY}}}");
        script.AppendLine(
            "    set position of item \"Applications\" of container window to " +
            $"{{{layout.ApplicationsIconX}, {layout.ApplicationsIconY}}}");
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
        MacSigningSupport.ValidateIdentityAndCertificate(
            signing.Identity, signing.TemporaryCertificateFile, allowAdHoc: true, ".dmg");
    }

    private async Task SignImageAsync(
        string imagePath,
        string workDirectory,
        CancellationToken cancellationToken,
        IBundleLogger logger)
    {
        var signing = settings.Signing;
        if (signing is null ||
            (signing.Identity is null && string.IsNullOrEmpty(signing.TemporaryCertificateFile)))
        {
            return;
        }

        MacSigningSupport.TemporaryKeychain? keychain = null;
        string identity;
        try
        {
            (identity, keychain) = await MacSigningSupport.ResolveIdentityAsync(
                workDirectory, logger, signing.Identity, signing.TemporaryCertificateFile,
                signing.TemporaryCertificatePassword, cancellationToken);

            logger.Log(
                BundleLogLevel.Information,
                identity == "-"
                    ? "Signing the .dmg ad-hoc (codesign -s -)."
                    : $"Signing the .dmg with identity '{identity}'.");
            await ToolRunner.RunAsync(
                "codesign",
                MacSigningSupport.CodesignArguments(
                    identity, hardenedRuntime: false, entitlementsFile: null,
                    applyEntitlements: false, keychain, imagePath),
                workDirectory, cancellationToken);
            await ToolRunner.RunAsync(
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

    /// <summary>
    /// hdiutil resize -limits 打印三段扇区数（行序随宿主版本在 cur/min/max 与
    /// min/cur/max 间漂移）。-srcfolder 自动定寸的镜像 min≈cur，取前两字段较大者
    /// 在两种序下都还原出当前尺寸；解析失败返回 null 走警告降级路。
    /// </summary>
    internal static long? ResizeBaseSectors(string limitsOutput)
    {
        var fields = limitsOutput.Trim().Split(
            new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 ||
            !long.TryParse(fields[0], out var first) ||
            !long.TryParse(fields[1], out var second))
        {
            return null;
        }
        return Math.Max(first, second);
    }

    /// <summary>
    /// 统一卸载序列：graceful detach → 中途递进 -force → 每次成功后以 hdiutil info
    /// 复核挂载点确已消失（diskimages-helper 簿记有滞后）。mandatory 档穷尽后抛
    /// InvalidOperationException（成功路径）；非强制档穷尽后记警告返回（清理路径
    /// 不掩盖真实失败）。两条路径共用同一序列，消灭行为分叉。
    /// </summary>
    private static async Task EnsureDetachedAsync(
        string mountDirectory,
        string workingDirectory,
        CancellationToken cancellationToken,
        IBundleLogger logger,
        bool mandatory)
    {
        for (var attempt = 1; ; attempt++)
        {
            var detachArgs = new List<string> { "detach", mountDirectory };
            if (attempt >= DetachForceAttempt)
            {
                detachArgs.Add("-force");
            }
            var result = await ToolRunner.TryRunAsync(
                "hdiutil", detachArgs, workingDirectory, cancellationToken);
            if (result is { ExitCode: 0 }
                // 卷已被其它仲裁者（Finder/diskarbitration）抢先卸下——ENOENT 即目标态。
                || (result is not null
                    && result.StandardError.IndexOf("No such file or directory", StringComparison.Ordinal) >= 0))
            {
                // detach 返回成功只代表卸载动作已发出——diskimages-helper 簿记
                // 有滞后，hdiutil info 可能仍短暂列出挂载点；确认消失才算完成，
                // 否则紧随其后的 convert / hdiutil info 断言会撞上残留簿记。
                // info 自身跑不起来同样不能放行——簿记未确认就当未完成重试。
                var info = await ToolRunner.TryRunAsync(
                    "hdiutil", ["info"], workingDirectory, cancellationToken);
                if (info is { ExitCode: 0 }
                    && info.StandardOutput.IndexOf(mountDirectory, StringComparison.Ordinal) < 0)
                {
                    logger.Log(
                        BundleLogLevel.Information,
                        $"Detached DMG volume at '{mountDirectory}'.");
                    return;
                }
                result = null;
            }
            if (attempt >= DetachMaxAttempts)
            {
                var detail = result is null
                    ? "mount point still listed by 'hdiutil info'"
                    : $"exit {result.ExitCode}: {result.StandardError}";
                var message =
                    $"hdiutil detach failed after {DetachMaxAttempts} attempts ({detail}).";
                if (mandatory)
                {
                    throw new InvalidOperationException(message);
                }
                logger.Log(BundleLogLevel.Warning, message);
                return;
            }
            var delay = DetachBackoffMilliseconds[Math.Min(attempt - 1, DetachBackoffMilliseconds.Length - 1)];
            logger.Log(
                BundleLogLevel.Warning,
                $"hdiutil detach busy/failed (attempt {attempt}/{DetachMaxAttempts}); retrying in {delay} ms" +
                (attempt + 1 >= DetachForceAttempt ? " (escalating to -force)" : "") + ".");
            await Task.Delay(delay, cancellationToken);
        }
    }

    internal static void CopyTree(string source, string destination, IBundleLogger log, int depth = 0) =>
        FileTree.CopyTree(source, destination, log, depth,
            maxDepth: 64,
            loopErrorMessage: "Directory nesting too deep inside the .dmg payload (possible symlink loop)",
            links: TreeLinkPolicy.Traverse);
}
