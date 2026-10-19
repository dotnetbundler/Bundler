using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;

namespace DotNet.Bundler.AppImage;

/// <summary>
/// Assembles the <c>&lt;name&gt;.AppDir</c> tree appimagetool packs:
/// <c>usr/lib/&lt;pkg&gt;/</c> payload + <c>usr/bin/&lt;name&gt;</c> relative
/// symlink, shared freedesktop entries, a generated script <c>AppRun</c>, and
/// root <c>&lt;name&gt;.desktop</c>/<c>&lt;name&gt;.png</c>/<c>.DirIcon</c>
/// links (the upstream convention: root entries point into <c>usr/</c>).
/// </summary>
internal static class AppDirBuilder
{
    internal sealed class Result
    {
        internal string AppDirPath = "";
        internal string PackageName = "";
        internal string Version = "";
        internal string EnvironmentArchitecture = "";
        internal string FileArchitecture = "";
    }

    internal static Result Build(
        BundleConfiguration bundle,
        BundlePlanItem item,
        AppImageBundleConfiguration settings,
        string workDirectory,
        IBundleLogger logger)
    {
        var packageName = AppImageIdentity.PackageName(settings.PackageName, bundle.ProductName);
        var version = (settings.Version ?? bundle.Version).Trim();
        if (version.Length == 0 || version.IndexOfAny(new[] { '/', '\\', ' ', '\n', '\r' }) >= 0)
        {
            throw new ArgumentException(
                $"The AppImage version must be a single token usable in a file name, got '{version}'.");
        }
        var envArch = settings.Architecture is { Length: > 0 } override_
            ? AppImageIdentity.NormalizeArchitecture(override_)
            : AppImageIdentity.EnvironmentArchitecture(item.Target);

        // installRoot/mainExecutable 都会插值进 AppRun 的双引号 shell 行——
        // 与 binLink 同规净化：空白、引号、$、`、\ 一律拒（$( )/反引号在运行时仍展开）。
        static bool HasUnsafeShellChar(string segment) =>
            segment.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '$' or '`' or '\\');
        var installRoot = (settings.InstallRoot ?? "usr/lib/" + packageName)
            .Replace('\\', '/').Trim('/');
        if (installRoot.Length == 0 ||
            installRoot.Split('/').Any(segment =>
                segment is "" or "." or ".." || HasUnsafeShellChar(segment)) ||
            (settings.InstallRoot is { } explicit_ && explicit_.TrimStart().StartsWith("/")))
        {
            throw new ArgumentException(
                $"The AppDir install root must be a relative path inside the AppDir, got '{settings.InstallRoot}'.");
        }

        var binLink = settings.BinLink ?? packageName;
        // 与 deb/rpm/apk 同规："" 或 "none" 关闭链接。
        if (string.Equals(binLink, "none", StringComparison.OrdinalIgnoreCase))
        {
            binLink = "";
        }
        if (binLink.Length > 0)
        {
            binLink = LinuxLinkName.Validate(binLink, "AppImageBinLink");
        }
        var mainExecutable = item.MainExecutable.Replace('\\', '/');
        if (mainExecutable.Length == 0 ||
            mainExecutable.Split('/').Any(segment =>
                segment is "" or "." or ".." || HasUnsafeShellChar(segment)))
        {
            throw new ArgumentException(
                $"The main executable must be a relative path without shell metacharacters, got '{item.MainExecutable}'.");
        }
        // MainExecutable 可以嵌套子目录：存在性/chmod/Exec 都用完整相对路径。

        var appDir = Path.Combine(workDirectory, packageName + ".AppDir");
        if (Directory.Exists(appDir))
        {
            Directory.Delete(appDir, recursive: true);
        }
        Directory.CreateDirectory(appDir);

        // usr/lib/<pkg>/ payload, then usr/bin/<link> → ../lib/<pkg>/<main>.
        var input = item.InputDirectory;
        var payloadRoot = Path.Combine(appDir, installRoot.Replace('/', Path.DirectorySeparatorChar));
        // 已落载荷的宿主路径登记册：Resources 撞名一律显式拒绝，不静默覆盖。
        var claimed = new HashSet<string>(PathText.Comparer);
        var update = bundle.Update;
        if (update is not null)
        {
            // 更新件是保留名：先登记碰撞簿再落载荷，用户资源撞名走标准冲突错误
            //（与 deb/rpm/apk/macOS/更新 staging 同一口径）。
            claimed.Add(Path.GetFullPath(
                Path.Combine(payloadRoot, UpdateIdentitySidecar.FileName)));
            claimed.Add(Path.GetFullPath(
                Path.Combine(payloadRoot,
                    UpdateBootstrapper.FileNameFor(item.Target.Target))));
        }
        // 与 deb/rpm/apk 同一链接契约：树内符号链接在 AppDir 内重放为真实
        // symlink（mksquashfs 原样保留），树外/悬空链接拒绝。
        MaterializeTree(
            PayloadSource.CollectInput(input, installRoot, mainExecutable, logger),
            appDir, claimed);
        if (update is not null)
        {
            UpdateIdentitySidecar.WriteIfEnabled(
                payloadRoot, update, PackageFormat.AppImage, item.Target.Target);
            UpdateBootstrapper.InjectOrThrow(
                payloadRoot, update, item.Target.Target);
        }
        var mainHostPath = Path.Combine(
            payloadRoot, mainExecutable.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(mainHostPath))
        {
            throw new FileNotFoundException(
                $"The main executable '{mainExecutable}' was not found in '{input}'.", mainHostPath);
        }
        AppImageToolset.Chmod(mainHostPath, "+x");

        // 资源走同一枚举缝（目的地越界/链接源统一拒），落盘时与已登记条目撞名一致拒绝。
        MaterializeTree(
            PayloadSource.CollectResources(
                bundle.Resources, installRoot, logger, includeDirectories: true),
            appDir, claimed);

        var binDir = Path.Combine(appDir, "usr", "bin");
        Directory.CreateDirectory(binDir);
        if (binLink.Length > 0)
        {
            var linkTarget = RelativePath("usr/bin", installRoot + "/" + mainExecutable);
            AppImageToolset.Symlink(linkTarget, Path.Combine(binDir, binLink));
        }

        // Shared freedesktop staging (.desktop + hicolor icons + metainfo).
        // AppImage always emits Icon= because an icon is guaranteed (default
        // fallback below); AlwaysEmitIcon keeps deb/rpm behavior unchanged.
        var staged = FreedesktopFiles.Collect(
            bundle, packageName, "/" + installRoot, mainExecutable, binLink,
            new FreedesktopFiles.Options
            {
                DesktopFile = settings.DesktopFile,
                MetainfoFile = settings.MetainfoFile,
                // appimagetool hard-requires a Categories= key; "Utility" is the
                // conventional default when the caller does not supply one.
                Categories = settings.Categories is { Count: > 0 } categories ? string.Join(";", categories) : "Utility",
                Format = "appimage",
                AlwaysEmitIcon = true
            });
        string? largestSquareIcon = null;
        var largestSquareSize = 0;
        foreach (var entry in staged)
        {
            var destination = Path.Combine(appDir, entry.ArchivePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, entry.ReadBytes());
            int squareSize;
            if (entry.ArchivePath.StartsWith("usr/share/icons/hicolor/", StringComparison.Ordinal) &&
                TrySquareSize(entry.ArchivePath, out squareSize) && squareSize > largestSquareSize)
            {
                largestSquareSize = squareSize;
                largestSquareIcon = entry.ArchivePath;
            }
        }

        // Root entries: <name>.desktop symlink, <name>.png + .DirIcon.
        var desktopStaged = "usr/share/applications/" + packageName + ".desktop";
        AppImageToolset.Symlink(desktopStaged, Path.Combine(appDir, packageName + ".desktop"));
        var rootIconSource = settings.IconFile is { Length: > 0 } iconFile
            ? FreedesktopFiles.RequireExisting(iconFile, "IconFile")
            : null;
        if (rootIconSource is not null)
        {
            File.Copy(rootIconSource, Path.Combine(appDir, packageName + ".png"), overwrite: true);
            File.Copy(rootIconSource, Path.Combine(appDir, ".DirIcon"), overwrite: true);
        }
        else if (largestSquareIcon is not null)
        {
            AppImageToolset.Symlink(largestSquareIcon, Path.Combine(appDir, packageName + ".png"));
            AppImageToolset.Symlink(largestSquareIcon, Path.Combine(appDir, ".DirIcon"));
        }
        else
        {
            var fallback = AppImageToolset.DefaultIcon();
            File.WriteAllBytes(Path.Combine(appDir, packageName + ".png"), fallback);
            File.WriteAllBytes(Path.Combine(appDir, ".DirIcon"), fallback);
        }

        // AppRun: upstream uses a precompiled helper; a readable script is
        // equivalent and auditable (APPDIR is set by the runtime; the dirname
        // fallback keeps --appimage-extract squashfs-root runs working).
        var execPath = binLink.Length > 0
            ? "usr/bin/" + binLink
            : installRoot + "/" + mainExecutable;
        var appRun = "#!/bin/sh\n" +
                     "APPDIR=\"${APPDIR:-$(dirname \"$(readlink -f \"$0\")\")}\"\n" +
                     "exec \"$APPDIR/" + execPath + "\" \"$@\"\n";
        var appRunPath = Path.Combine(appDir, "AppRun");
        File.WriteAllText(appRunPath, appRun);
        AppImageToolset.Chmod(appRunPath, "+x");

        // Arbitrary caller files last; collisions with generated entries are
        // rejected rather than silently shadowed.
        if (settings.Files is { Count: > 0 } files)
        {
            var reserved = ReservedNames(packageName);
            foreach (var entry in files)
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
                if (IsReservedEntry(packageName, destination))
                {
                    throw new ArgumentException(
                        $"BundlerAppImageFile destination '{destination}' collides with a generated AppDir entry.");
                }
                var source = FreedesktopFiles.RequireExisting(entry.Source, "BundlerAppImageFile");
                var hostDestination = Path.Combine(appDir, destination.Replace('/', Path.DirectorySeparatorChar));
                // File.Exists follows symlinks; check the dir listing too so a
                // symlink is spotted even if its target does not exist.
                var hostParent = Path.GetDirectoryName(hostDestination) ?? appDir;
                if (File.Exists(hostDestination) || Directory.Exists(hostDestination) ||
                    (Directory.Exists(hostParent) &&
                     Directory.GetFiles(hostParent, Path.GetFileName(hostDestination)).Length > 0))
                {
                    throw new ArgumentException(
                        $"BundlerAppImageFile destination '{destination}' already exists in the AppDir.");
                }
                EnsureDirectories(appDir, Path.GetDirectoryName(hostDestination)!,
                    path => new ArgumentException(
                        $"BundlerAppImageFile destination '{entry.Destination}' collides " +
                        $"with an existing file at '{path}'."));
                File.Copy(source, hostDestination);
            }
        }

        return new Result
        {
            AppDirPath = appDir,
            PackageName = packageName,
            Version = version,
            EnvironmentArchitecture = envArch,
            FileArchitecture = envArch
        };
    }

    /// <summary>Generated AppDir root entries <c>BundlerAppImageFile</c> may not shadow.</summary>
    internal static HashSet<string> ReservedNames(string packageName) =>
        new(StringComparer.Ordinal)
        {
            "AppRun", ".DirIcon",
            packageName + ".desktop", packageName + ".png"
        };

    /// <summary>
    /// 生成件保留命中——保留名本身或其下的路径（保留名是文件；
    /// <c>AppRun/x</c> 会把 AppRun 落成目录，同样冲突）。POSIX 精确。
    /// </summary>
    internal static bool IsReservedEntry(string packageName, string destination) =>
        ReservedNames(packageName).Any(r =>
            string.Equals(destination, r, StringComparison.Ordinal) ||
            destination.StartsWith(r + '/', StringComparison.Ordinal));

    /// <summary>
    /// 逐级建父目录——任一前缀被已落文件/链接占位时按 <paramref name="occupied"/>
    /// 抛错：载荷落盘腿给 InvalidOperationException（与 claimed 簿同口径），
    /// BundlerAppImageFile 旋钮腿给 ArgumentException（与相邻目的地校验同口径）；
    /// 都比裸 <see cref="Directory.CreateDirectory"/> 的 IOException 可读。
    /// </summary>
    private static void EnsureDirectories(
        string appDir, string parent, Func<string, Exception> occupied)
    {
        var chain = new List<string>();
        for (var dir = parent; dir is not null &&
             !string.Equals(dir, appDir, PathText.Comparison);
             dir = Path.GetDirectoryName(dir))
        {
            chain.Add(dir);
        }
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (File.Exists(chain[i]))
            {
                throw occupied(chain[i]);
            }
            Directory.CreateDirectory(chain[i]);
        }
    }

    /// <summary>
    /// 把 <see cref="PayloadSource"/> 枚举出的条目落到 AppDir：目录创建、
    /// 文件复制、符号链接按目标文本重建；每个宿主路径经 <paramref name="claimed"/>
    /// 登记，与已落载荷（含更新保留名）撞名显式拒绝。
    /// </summary>
    private static void MaterializeTree(
        IEnumerable<PayloadSourceItem> items,
        string appDir,
        HashSet<string> claimed)
    {
        foreach (var item in items)
        {
            var hostPath = Path.Combine(
                appDir, item.ArchivePath.Replace('/', Path.DirectorySeparatorChar));
            var full = Path.GetFullPath(hostPath);
            if (item.Kind == PayloadSourceKind.Directory)
            {
                // 反向碰撞：目录条目落在一个已登记的文件/链接路径上同样拒绝。
                if (File.Exists(hostPath) || !claimed.Add(full))
                {
                    throw new InvalidOperationException(
                        $"A resource directory collides with an existing payload entry: '{full}'.");
                }
                Directory.CreateDirectory(hostPath);
                continue;
            }
            if (Directory.Exists(hostPath) || !claimed.Add(full))
            {
                throw new InvalidOperationException(
                    $"A resource file collides with an existing payload entry: '{full}'.");
            }
            EnsureDirectories(appDir, Path.GetDirectoryName(hostPath)!,
                path => new InvalidOperationException(
                    $"A payload directory collides with an existing file: '{path}'."));
            if (item.Kind == PayloadSourceKind.Symlink)
            {
                AppImageToolset.Symlink(item.LinkTarget!, hostPath);
            }
            else
            {
                File.Copy(item.SourcePath!, hostPath);
            }
        }
    }


    // POSIX relative path between two slash-separated AppDir-relative paths
    // ("usr/bin" → "usr/lib/<pkg>/main" = "../lib/<pkg>/main").
    private static string RelativePath(string baseDir, string path)
    {
        var from = baseDir.Split('/');
        var to = path.Split('/');
        var common = 0;
        while (common < from.Length && common < to.Length &&
               from[common] == to[common])
        {
            common++;
        }
        var ups = Enumerable.Repeat("..", from.Length - common);
        return string.Join("/", ups.Concat(to.Skip(common)));
    }

    private static bool TrySquareSize(string hicolorPath, out int size)
    {
        size = 0;
        var segments = hicolorPath.Split('/');
        // usr/share/icons/hicolor/<size>/apps/<name>.<ext> —— 尺寸段在 hicolor 之后。
        var hicolor = Array.IndexOf(segments, "hicolor");
        if (hicolor < 0 || hicolor + 1 >= segments.Length) return false;
        var dir = segments[hicolor + 1]; // e.g. 256x256 or 48x48@2
        var parts = dir.Split('@')[0].Split('x');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) &&
            w == h)
        {
            size = w;
            return true;
        }
        return false;
    }
}
