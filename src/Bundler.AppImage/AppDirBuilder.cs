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
            : AppImageIdentity.EnvironmentArchitecture(item.Target.RuntimeIdentifier);

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

        var binLink = settings.BinLink is { Length: > 0 } ? settings.BinLink : packageName;
        if (string.Equals(binLink, "none", StringComparison.OrdinalIgnoreCase))
        {
            binLink = "";
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
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        CopyTree(input, payloadRoot, logger, claimed);
        if (bundle.Update is { } update)
        {
            UpdateIdentitySidecar.WriteIfEnabled(
                payloadRoot, update, PackageFormat.AppImage, item.Target.RuntimeIdentifier);
            claimed.Add(Path.GetFullPath(
                Path.Combine(payloadRoot, UpdateIdentitySidecar.FileName)));
            if (UpdateBootstrapper.Inject(payloadRoot, update, item.Target.RuntimeIdentifier) is { } injected)
            {
                claimed.Add(Path.GetFullPath(Path.Combine(payloadRoot, injected)));
            }
        }
        var mainHostPath = Path.Combine(
            payloadRoot, mainExecutable.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(mainHostPath))
        {
            throw new FileNotFoundException(
                $"The main executable '{mainExecutable}' was not found in '{input}'.", mainHostPath);
        }
        AppImageToolset.Chmod(mainHostPath, "+x");

        foreach (var resource in bundle.Resources)
        {
            var target = resource.Destination.Replace('\\', '/').Trim('/');
            if (target.Length == 0 || target.Split('/').Contains(".."))
            {
                throw new ArgumentException(
                    $"The resource target must stay inside the payload: '{resource.Destination}'.");
            }
            var source = Path.GetFullPath(resource.Source);
            var destinationDir = Path.Combine(payloadRoot, target.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(source))
            {
                CopyTree(source, destinationDir, logger, claimed);
            }
            else if (File.Exists(source))
            {
                if (Directory.Exists(destinationDir) ||
                    !claimed.Add(Path.GetFullPath(destinationDir)))
                {
                    throw new InvalidOperationException(
                        $"The resource target '{resource.Destination}' collides with an existing payload entry.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destinationDir)!);
                File.Copy(source, destinationDir, overwrite: true);
            }
            else
            {
                throw new FileNotFoundException(
                    $"The resource source does not exist: {source}", source);
            }
        }

        var binDir = Path.Combine(appDir, "usr", "bin");
        Directory.CreateDirectory(binDir);
        if (binLink.Length > 0)
        {
            if (binLink is "." or ".." ||
                binLink.IndexOfAny(['/', '\\', ' ', '\t', '"', '\'', '$']) >= 0)
            {
                throw new ArgumentException(
                    $"The AppImage bin-link must be a plain file name: '{binLink}'.");
            }
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
            var reserved = new HashSet<string>(StringComparer.Ordinal)
            {
                "AppRun", ".DirIcon",
                packageName + ".desktop", packageName + ".png"
            };
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
                if (reserved.Contains(destination))
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
                Directory.CreateDirectory(Path.GetDirectoryName(hostDestination)!);
                File.Copy(source, hostDestination);
            }
        }

        return new Result
        {
            AppDirPath = appDir,
            PackageName = packageName,
            Version = version,
            EnvironmentArchitecture = envArch,
            FileArchitecture = AppImageIdentity.FileArchitectureForEnv(envArch)
        };
    }

    private static void CopyTree(
        string source, string destination, IBundleLogger log,
        HashSet<string>? claimed = null)
    {
        Directory.CreateDirectory(destination);
        // 手工递归：AllDirectories 枚举会穿透目录链接，祖先链接导致重复展开。
        foreach (var directory in Directory.GetDirectories(source).OrderBy(d => d, StringComparer.Ordinal))
        {
            var dirTarget = Path.Combine(destination, Path.GetFileName(directory));
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                // 目录链接不展开：穿透会把目标树复制两遍，祖先链接会无界递归。
                // netstandard2.0 无 LinkTarget/CreateSymbolicLink API，按跳过处理。
                log.Log(BundleLogLevel.Warning,
                    $"Skipping directory symlink: {directory}");
                continue;
            }
            CopyTree(directory, dirTarget, log, claimed);
        }
        foreach (var file in Directory.GetFiles(source).OrderBy(f => f, StringComparer.Ordinal))
        {
            // Sockets, FIFOs and device nodes cannot be copied; skip them like
            // the archive formats do.
            if (!UnixFileTypes.IsRegularFile(file))
            {
                log.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
                continue;
            }
            var target = Path.Combine(
                destination, PathRelative(source, file));
            if (claimed is not null && !claimed.Add(Path.GetFullPath(target)))
            {
                throw new InvalidOperationException(
                    $"A resource file collides with an existing payload entry: '{Path.GetFullPath(target)}'.");
            }
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string PathRelative(string baseDir, string path)
    {
        var baseUri = new Uri(baseDir.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar, UriKind.Absolute);
        var pathUri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString());
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
