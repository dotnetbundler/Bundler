using System.Runtime.InteropServices;
using System.Text;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.Core.Update;

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
        var documentTypes = MacAppDesktopIntegration.ResolveDocumentTypes(bundle, settings);
        var urlTypes = MacAppDesktopIntegration.ResolveUrlTypes(bundle, settings);

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
        var update = bundle.Update;
        if (update is not null)
        {
            // 更新件是保留名：先登记碰撞簿，用户资源撞名走标准冲突错误而不是底层 IO 失败。
            destinations.Add(Path.GetFullPath(
                Path.Combine(resourcesDirectory, UpdateInstallIdentity.FileName)));
            destinations.Add(Path.GetFullPath(
                Path.Combine(executablesDirectory,
                    UpdateBootstrapper.FileNameFor(item.Target.RuntimeIdentifier))));
        }
        CopyTree(item.InputDirectory, executablesDirectory, destinations, context.Logger);

        if (update is not null)
        {
            // 身份旁车落 Resources/（资源密封位：Contents 根的非代码件会被 codesign 判成未签子件）、
            // 引导件落 MacOS/（代码位正常签名）——两者随 bundle 一起被 codesign 覆盖。
            UpdateIdentitySidecar.WriteIfEnabled(
                resourcesDirectory, update, PackageFormat.App, item.Target.RuntimeIdentifier);
            UpdateBootstrapper.Inject(
                executablesDirectory, update, item.Target.RuntimeIdentifier);
        }

        var executablePath = Path.Combine(executablesDirectory, item.MainExecutable);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "The .app main executable is missing from the input directory.", executablePath);
        }
        if (MachO.ReadArchitectures(executablePath).Count == 0)
        {
            throw new InvalidDataException(
                "The .app main executable must be a Mach-O binary: " + executablePath);
        }

        foreach (var resource in bundle.Resources)
        {
            var target = Path.Combine(resourcesDirectory, NormalizeNestedPath(resource.TargetPath, "resources"));
            CopyPayloadEntry(resource.Source, target, destinations, context.Logger);
        }
        foreach (var mapping in contents)
        {
            CopyPayloadEntry(mapping.Source,
                Path.Combine(contentsDirectory, mapping.TargetPath.Replace('/', Path.DirectorySeparatorChar)),
                destinations, context.Logger);
        }
        foreach (var framework in settings.Frameworks)
        {
            var name = Path.GetFileName(framework);
            CopyPayloadEntry(framework, Path.Combine(frameworksDirectory, name), destinations, context.Logger);
        }

        var plistValues = new Dictionary<string, object>(StringComparer.Ordinal)
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
            ["NSHighResolutionCapable"] = true
        };
        MacAppDesktopIntegration.EmitDocumentTypes(plistValues, documentTypes);
        MacAppDesktopIntegration.EmitUrlTypes(plistValues, urlTypes, bundle.Identifier);

        var iconInputs = bundle.Icons.Select(Path.GetFullPath).ToArray();
        var bitmapIcons = iconInputs.Where(IsBitmapIcon).ToArray();
        var iconFileName = metadata.IconFileName;
        if (bitmapIcons.Length > 0 && iconFileName is not null)
        {
            plistValues["CFBundleIconFile"] = iconFileName;
            WriteIcon(bitmapIcons, iconFileName, resourcesDirectory);
        }
        var assetsIconName = await MacAppAssetsCar.BuildAsync(
            iconInputs.Where(IsAssetCatalogInput).ToArray(),
            resourcesDirectory, context.WorkDirectory, context.Logger, cancellationToken);
        if (assetsIconName is not null)
        {
            plistValues["CFBundleIconName"] = assetsIconName;
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
        MacAppDesktopIntegration.EmitAppTransportSecurity(plistValues, settings.ExceptionDomain);

        MacAppDesktopIntegration.MergeCallerPlist(plistValues, settings);
        MacAppDesktopIntegration.EnforceIdentityKeys(plistValues, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CFBundleIdentifier"] = bundle.Identifier,
            ["CFBundleExecutable"] = item.MainExecutable,
            ["CFBundleShortVersionString"] = metadata.ShortVersion,
            ["CFBundleVersion"] = metadata.BuildVersion,
            ["CFBundlePackageType"] = "APPL"
        });

        var plistPath = Path.Combine(contentsDirectory, "Info.plist");
        InfoPlist.Write(plistPath, plistValues);
        File.WriteAllText(Path.Combine(contentsDirectory, "PkgInfo"), "APPL????", Encoding.ASCII);

        await InspectPayload(context, contentsDirectory, item.Target.RuntimeIdentifier, cancellationToken);
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

        // Signing + notarization happen inside the staging directory: a failure leaves no
        // pseudo-success artifact at the final path.
        await MacAppSigning.RunAsync(
            context, staging, item.MainExecutable, settings.Signing, cancellationToken);

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

    private static void CopyTree(string sourceDirectory, string destinationDirectory, HashSet<string> destinations, IBundleLogger log)
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
                // Sockets, FIFOs and device nodes cannot be copied; skip them.
                if (!UnixFileTypes.IsRegularFile(file))
                {
                    log.Log(BundleLogLevel.Warning, $"Skipping non-regular file: {file}");
                    continue;
                }
                CopyFileEntry(file, Path.Combine(destination, Path.GetFileName(file)), destinations);
            }
        }
    }

    private static void CopyPayloadEntry(string source, string destination, HashSet<string> destinations, IBundleLogger log)
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
            CopyTree(fullSource, destination, destinations, log);
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

    private static bool IsBitmapIcon(string icon) =>
        icon.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ||
        icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssetCatalogInput(string icon) =>
        icon.EndsWith(".car", StringComparison.OrdinalIgnoreCase) ||
        icon.EndsWith(".icon", StringComparison.OrdinalIgnoreCase);

    private static void WriteIcon(
        IReadOnlyList<string> bitmapIcons, string iconFileName, string resourcesDirectory)
    {
        var icns = bitmapIcons.Where(icon => icon.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)).ToArray();
        var pngs = bitmapIcons.Where(icon => icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (icns.Length > 1 || (icns.Length == 1 && pngs.Length > 0))
        {
            throw new ArgumentException(
                ".app icons accept either a single .icns or PNG bitmaps; do not mix .icns with other sources.");
        }
        if (icns.Length == 0 && pngs.Length == 0)
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

    /// <summary>
    /// Single payload pass: Mach-O architecture check against the target RID (managed header
    /// parse — the equivalent of `lipo -info`, available on every build host) plus executable-bit
    /// chmod on POSIX hosts.
    /// </summary>
    private async Task InspectPayload(
        BundleBuildContext context, string contentsDirectory, string runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        var requiredArchitectures = RequiredArchitectures(runtimeIdentifier);
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (windows)
        {
            context.Logger.Log(BundleLogLevel.Warning,
                "The build host cannot set executable bits inside the .app; deliver it through a " +
                "permission-preserving archive (zip/tar) before launching on macOS.");
        }
        foreach (var file in Directory.EnumerateFiles(contentsDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var architectures = MachO.ReadArchitectures(file);
            if (architectures.Count == 0)
            {
                continue;
            }
            var missing = requiredArchitectures.Where(
                required => !architectures.Contains(required, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException(
                    $"Mach-O payload '{file}' does not contain '{string.Join(", ", missing)}' required by " +
                    $"{runtimeIdentifier} (architectures: {string.Join(", ", architectures)}). " +
                    "Provide a universal (fat) binary or per-RID input.");
            }
            if (!windows)
            {
                await MacProcessRunner.RunAsync("chmod", ["755", file], context.WorkDirectory, cancellationToken);
            }
        }
    }

    internal static string[] RequiredArchitectures(string runtimeIdentifier) =>
        runtimeIdentifier switch
        {
            "osx" => ["x86_64", "arm64"],
            "osx-arm64" => ["arm64"],
            "osx-x64" => ["x86_64"],
            _ => []
        };

    // .app 是跨宿主产物：按最严的 Windows 字符规则清洗，保证各宿主产物名一致。
    internal static string SanitizeFileName(string name)
    {
        var sanitized = name;
        foreach (var invalid in "<>:\"/\\|?*")
        {
            sanitized = sanitized.Replace(invalid, '-');
        }
        return new string(sanitized
                .Select(character => char.IsControl(character) ? '-' : character).ToArray())
            .Trim();
    }
}
