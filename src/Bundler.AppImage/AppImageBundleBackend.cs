using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AppImage;

internal sealed class AppImageBundleBackend(
    AppImageBundleConfiguration settings,
    AppImageBundlerOptions options,
    TargetOperatingSystem operatingSystem) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.AppImage;
    public TargetOperatingSystem OperatingSystem => operatingSystem;
    // glibc 与 musl 目标共用同一后端（runtime 为静态 ELF 两族皆可跑），不挑 libc。
    public LibC? RequiredLibC => null;

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken)
    {
        // 宿主门先于 AppDirBuilder：Windows 宿主应在任何 chmod/ln 尝试之前报 PNSE。
        AppImageToolset.RequireLinuxHost();
        var built = AppDirBuilder.Build(
            context.Configuration, context.Item, settings, context.WorkDirectory, context.Logger);
        var toolset = await AppImageToolset.ResolveAsync(
            built.EnvironmentArchitecture, options.ToolsetCacheDirectory, cancellationToken);
        var signing = PrepareSigning(settings, context.Configuration, context.WorkDirectory);

        var fileName = ArtifactNaming.FileName(
            built.PackageName, built.Version, context.Item.Target, PackageFormat.AppImage,
            archToken: built.FileArchitecture);
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
            ["APPIMAGE_EXTRACT_AND_RUN"] = "1",
            // mksquashfs 标准确定性旋钮：文件系统时间戳钉在 epoch，
            // 同宿主同输入重复构建字节一致（RepeatBuildIsByteIdentical 断言）；
            // 跨产方 sha 差异属上游 appimagetool 性质，不在承诺范围（见 docs/design/formats/linux-appimage.md）。
            ["SOURCE_DATE_EPOCH"] = "0"
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
            await ToolRunner.RunAsync(
                toolset.ToolPath, arguments, context.WorkDirectory, cancellationToken,
                environment);
        }
        catch
        {
            // No half-written .AppImage is left behind on tool failure; 同删
            // 陈旧 .sha256 旁车（deb/rpm/apk/archive 同款收口——失败构建
            // 留下的哈希与实际产物不匹配即成静默毒源）。
            FileTree.TryDeleteFile(outputPath);
            FileTree.TryDeleteFile(outputPath + ".sha256");
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

        return [new BundleArtifact(
            PackageFormat.AppImage, context.Item.Target.Target, outputPath)];
    }

    /// <summary>
    /// Configuration-time checks the facade also runs during multi-format
    /// pre-validation; everything past this point needs the build environment.
    /// Mirrors <see cref="AppDirBuilder.Build"/>'s config surface so
    /// <c>bundler validate</c> catches the same errors early.
    /// </summary>
    internal static void ValidateConfiguration(
        AppImageBundleConfiguration settings, BundleConfiguration bundle)
    {
        var hasKey = settings.Signing.KeyFile is { Length: > 0 };
        if (settings.Signing.Passphrase is { Length: > 0 } && !hasKey)
        {
            throw new ArgumentException(
                "Signing.Passphrase requires Signing.KeyFile to point at an OpenPGP secret key.");
        }
        if (hasKey && !File.Exists(settings.Signing.KeyFile!))
        {
            throw new ArgumentException(
                $"Signing.KeyFile '{settings.Signing.KeyFile}' does not exist.");
        }

        var packageName = AppImageIdentity.PackageName(settings.PackageName, bundle.ProductName);
        var version = (settings.Version ?? bundle.Version).Trim();
        if (version.Length == 0 || version.IndexOfAny(new[] { '/', '\\', ' ', '\n', '\r' }) >= 0)
        {
            throw new ArgumentException(
                $"The AppImage version must be a single token usable in a file name, got '{version}'.");
        }
        var installRoot = (settings.InstallRoot ?? "usr/lib/" + packageName)
            .Replace('\\', '/').Trim('/');
        if (installRoot.Length == 0 ||
            installRoot.Split('/').Any(segment =>
                segment is "" or "." or ".." ||
                segment.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '$' or '`' or '\\')) ||
            (settings.InstallRoot is { } explicitRoot && explicitRoot.TrimStart().StartsWith("/")))
        {
            throw new ArgumentException(
                $"The AppDir install root must be a relative path inside the AppDir, got '{settings.InstallRoot}'.");
        }
        // 与 deb/rpm/apk 同规："" 或 "none" 关闭链接。
        var binLink = settings.BinLink ?? packageName;
        if (binLink.Length > 0 &&
            !string.Equals(binLink, "none", StringComparison.OrdinalIgnoreCase))
        {
            LinuxLinkName.Validate(binLink, "AppImageBinLink");
        }
        // mainExecutable 插值进 AppRun 的双引号 shell 行——与 installRoot 同规净化。
        // 校验落到解析后真值：未写时默认取 ProductName（其本身不含 shell 净化），
        // 只看显式旋钮会让默认路径绕过本段直到 AppDirBuilder 才拦。
        foreach (var target in bundle.Targets)
        {
            var mainExecutable = (target.MainExecutable ??
                (BundleTarget.Parse(target.Target).OperatingSystem == TargetOperatingSystem.Windows
                    ? bundle.ProductName + ".exe"
                    : bundle.ProductName)).Replace('\\', '/');
            if (mainExecutable.Length == 0 ||
                mainExecutable.Split('/').Any(segment =>
                    segment is "" or "." or ".." ||
                    segment.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '$' or '`' or '\\')))
            {
                throw new ArgumentException(
                    $"The main executable must be a relative path without shell metacharacters, got '{target.MainExecutable ?? mainExecutable}'.");
            }
        }
        if (settings.Architecture is { Length: > 0 } archOverride)
        {
            AppImageIdentity.NormalizeArchitecture(archOverride);
        }
        else
        {
            foreach (var target in bundle.Targets)
            {
                AppImageIdentity.EnvironmentArchitecture(BundleTarget.Parse(target.Target));
            }
        }

        foreach (var (file, knob) in new (string?, string)[]
        {
            (settings.DesktopFile, "DesktopFile"),
            (settings.MetainfoFile, "MetainfoFile"),
            (settings.IconFile, "IconFile")
        })
        {
            if (file is { Length: > 0 })
            {
                FreedesktopFiles.RequireExisting(file, knob);
            }
        }
        foreach (var entry in settings.Files ?? [])
        {
            var destination = (entry.Destination ?? "").Replace('\\', '/').Trim('/');
            if (destination.Length == 0 ||
                destination.Split('/').Any(segment => segment is "" or "." or "..") ||
                (entry.Destination ?? "").Contains('\\') ||
                (entry.Destination ?? "").TrimStart().StartsWith("/"))
            {
                throw new ArgumentException(
                    $"BundlerAppImageFile destinations must be relative paths inside the AppDir, got '{entry.Destination}'.");
            }
            if (AppDirBuilder.IsReservedEntry(packageName, destination))
            {
                throw new ArgumentException(
                    $"BundlerAppImageFile destination '{destination}' collides with a generated AppDir entry.");
            }
            FreedesktopFiles.RequireExisting(entry.Source, "BundlerAppImageFile");
        }
    }

    private static AppImageSigning.SigningContext? PrepareSigning(
        AppImageBundleConfiguration settings, BundleConfiguration bundle, string workDirectory)
    {
        ValidateConfiguration(settings, bundle);
        if (settings.Signing.KeyFile is not { Length: > 0 })
        {
            return null;
        }
        return AppImageSigning.Prepare(settings.Signing.KeyFile!, workDirectory);
    }

}
