using System.Runtime.InteropServices;
using System.Text;
using DotNet.Bundler;

namespace DotNet.Bundler.MacApp;

internal sealed class MacAppBundleBackend(MacAppBundleConfiguration settings) : IBundleBackend
{
    private static readonly HashSet<string> ReservedTopLevelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MacOS", "Resources", "Frameworks", "Info.plist", "PkgInfo"
    };

    public PackageFormat Format => PackageFormat.App;
    public DesktopOperatingSystem OperatingSystem => DesktopOperatingSystem.MacOS;

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleBuildContext context, CancellationToken cancellationToken = default)
    {
        var bundle = context.Configuration;
        var item = context.Item;
        var metadata = MacAppMetadata.Resolve(bundle, settings);
        var contents = ResolveContentsMappings(settings);

        var applicationName = SanitizeFileName(bundle.ProductName) + ".app";
        var staging = Path.Combine(context.WorkDirectory, applicationName);
        var contentsDirectory = Path.Combine(staging, "Contents");
        var executablesDirectory = Path.Combine(contentsDirectory, "MacOS");
        var resourcesDirectory = Path.Combine(contentsDirectory, "Resources");
        var frameworksDirectory = Path.Combine(contentsDirectory, "Frameworks");
        Directory.CreateDirectory(executablesDirectory);
        Directory.CreateDirectory(resourcesDirectory);

        // destination -> source bookkeeping catches collisions across every channel.
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CopyTree(item.InputDirectory, executablesDirectory, destinations);

        var executablePath = Path.Combine(executablesDirectory, item.MainExecutable);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "The .app main executable is missing from the input directory.", executablePath);
        }
        if (!MachO.IsMachO(executablePath))
        {
            throw new InvalidDataException(
                "The .app main executable must be a Mach-O binary: " + executablePath);
        }

        foreach (var resource in bundle.Resources)
        {
            var target = Path.Combine(resourcesDirectory, NormalizeNestedPath(resource.TargetPath, "resources"));
            CopyPayloadEntry(resource.Source, target, destinations);
        }
        foreach (var mapping in contents)
        {
            CopyPayloadEntry(mapping.Source,
                Path.Combine(contentsDirectory, mapping.TargetPath.Replace('/', Path.DirectorySeparatorChar)),
                destinations);
        }
        foreach (var framework in settings.Frameworks)
        {
            var name = Path.GetFileName(framework);
            CopyPayloadEntry(framework, Path.Combine(frameworksDirectory, name), destinations);
        }

        var plistValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CFBundleDevelopmentRegion"] = "en",
            ["CFBundleDisplayName"] = metadata.DisplayName,
            ["CFBundleExecutable"] = item.MainExecutable,
            ["CFBundleIdentifier"] = bundle.Identifier,
            ["CFBundleInfoDictionaryVersion"] = "6.0",
            ["CFBundleName"] = metadata.BundleName,
            ["CFBundlePackageType"] = "APPL",
            ["CFBundleShortVersionString"] = metadata.ShortVersion,
            ["CFBundleVersion"] = metadata.BuildVersion,
            ["CFBundleSignature"] = "????",
            ["NSHighResolutionCapable"] = "true"
        };
        var iconFileName = metadata.IconFileName;
        if (iconFileName is not null)
        {
            plistValues["CFBundleIconFile"] = iconFileName;
            WriteIcon(bundle, iconFileName, resourcesDirectory);
        }
        if (metadata.MinimumSystemVersion is not null)
        {
            plistValues["LSMinimumSystemVersion"] = metadata.MinimumSystemVersion;
        }
        if (metadata.Category is not null)
        {
            plistValues["LSApplicationCategoryType"] = metadata.Category;
        }
        if (bundle.Copyright is { Length: > 0 } copyright)
        {
            plistValues["NSHumanReadableCopyright"] = copyright;
        }

        var plistPath = Path.Combine(contentsDirectory, "Info.plist");
        InfoPlist.Write(plistPath, plistValues);
        File.WriteAllText(Path.Combine(contentsDirectory, "PkgInfo"), "APPL????", Encoding.ASCII);

        await ApplyExecutablePermissions(context, contentsDirectory, cancellationToken);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await MacProcessRunner.RunAsync(
                "plutil", ["-lint", plistPath], context.WorkDirectory, cancellationToken);
        }
        else
        {
            context.Logger.Log(BundleLogLevel.Trace,
                "plutil lint skipped: the build host is not macOS.");
        }

        var outputPath = Path.Combine(item.OutputDirectory, applicationName);
        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        Directory.Move(staging, outputPath);
        return [new BundleArtifact(Format, item.Target.RuntimeIdentifier, outputPath)];
    }

    internal static IReadOnlyList<MacAppContentConfiguration> ResolveContentsMappings(
        MacAppBundleConfiguration settings)
    {
        var resolved = new List<MacAppContentConfiguration>();
        for (var index = 0; index < settings.Contents.Count; index++)
        {
            var mapping = settings.Contents[index];
            var target = NormalizeContentsPath(mapping.TargetPath, $"contents[{index}]");
            resolved.Add(new MacAppContentConfiguration
            {
                Source = Path.GetFullPath(mapping.Source),
                TargetPath = target
            });
        }
        return resolved;
    }

    private static string NormalizeContentsPath(string path, string displayName)
    {
        var normalized = (path ?? "").Replace('\\', '/').Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{displayName}: a Contents target path is required.");
        }
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw new ArgumentException($"{displayName}: the Contents target path must be relative: {path}");
        }
        var segments = normalized.Split('/');
        if (segments.Contains("..", StringComparer.Ordinal) || segments.Any(segment => segment.Length == 0))
        {
            throw new ArgumentException($"{displayName}: the Contents target path must stay inside Contents/: {path}");
        }
        if (ReservedTopLevelNames.Contains(segments[0]))
        {
            throw new ArgumentException(
                $"{displayName}: '{segments[0]}' is a reserved Contents entry; use the dedicated channel " +
                "(input tree -> MacOS/, resources -> Resources/, frameworks -> Frameworks/).");
        }
        return string.Join("/", segments);
    }

    private static string NormalizeNestedPath(string path, string displayName)
    {
        var normalized = (path ?? "").Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':') ||
            normalized.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException($"{displayName}: the target path must stay inside its channel: {path}");
        }
        return normalized;
    }

    private static void CopyTree(string sourceDirectory, string destinationDirectory, HashSet<string> destinations)
    {
        var root = Path.GetFullPath(sourceDirectory);
        CheckReparse(root);
        Visit(root, destinationDirectory);

        void Visit(string source, string destination)
        {
            CheckReparse(source);
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                Visit(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
            foreach (var file in Directory.EnumerateFiles(source)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                CopyFileEntry(file, Path.Combine(destination, Path.GetFileName(file)), destinations);
            }
        }
    }

    private static void CopyPayloadEntry(string source, string destination, HashSet<string> destinations)
    {
        var fullSource = Path.GetFullPath(source);
        CheckReparse(fullSource);
        if (File.Exists(fullSource))
        {
            CopyFileEntry(fullSource, destination, destinations);
            return;
        }
        if (Directory.Exists(fullSource))
        {
            CopyTree(fullSource, destination, destinations);
            return;
        }
        throw new FileNotFoundException("The .app payload source does not exist.", fullSource);
    }

    private static void CopyFileEntry(string source, string destination, HashSet<string> destinations)
    {
        CheckReparse(source);
        if (!destinations.Add(Path.GetFullPath(destination)))
        {
            throw new ArgumentException("Two .app payload entries map to the same bundle path: " + destination);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.Copy(source, destination);
    }

    private static void CheckReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(".app payload must not contain reparse points or symlinks: " + path);
        }
    }

    private static void WriteIcon(
        BundleConfiguration bundle, string iconFileName, string resourcesDirectory)
    {
        var icons = bundle.Icons.Select(Path.GetFullPath).ToArray();
        var icns = icons.Where(icon => icon.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)).ToArray();
        var pngs = icons.Where(icon => icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (icns.Length > 1 || (icns.Length == 1 && pngs.Length > 0))
        {
            throw new ArgumentException(
                ".app icons accept either a single .icns or PNG bitmaps; do not mix .icns with other sources.");
        }
        if (icns.Length == 0 && pngs.Length != icons.Length)
        {
            throw new ArgumentException(".app icons accept .icns or .png files only.");
        }

        var destination = Path.Combine(resourcesDirectory, iconFileName);
        Directory.CreateDirectory(resourcesDirectory);
        if (icns.Length == 1)
        {
            File.Copy(icns[0], destination);
            return;
        }
        File.WriteAllBytes(destination, IcnsBuilder.BuildFromPngs(pngs.Select(File.ReadAllBytes).ToArray()));
    }

    private async Task ApplyExecutablePermissions(
        BundleBuildContext context, string contentsDirectory, CancellationToken cancellationToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            context.Logger.Log(BundleLogLevel.Warning,
                "The build host cannot set executable bits inside the .app; deliver it through a " +
                "permission-preserving archive (zip/tar) before launching on macOS.");
            return;
        }
        foreach (var file in Directory.EnumerateFiles(contentsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MachO.IsMachO(file))
            {
                await MacProcessRunner.RunAsync("chmod", ["755", file], context.WorkDirectory, cancellationToken);
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = name.Replace(':', '-').Replace('/', '-');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }
        return sanitized.Trim();
    }
}
