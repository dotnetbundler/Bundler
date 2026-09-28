using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacApp;

namespace DotNet.Bundler.MacPkg;

internal sealed class MacPkgBundleBackend(MacPkgBundleConfiguration settings) : IBundleBackend
{
    public PackageFormat Format => PackageFormat.Pkg;
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
                ".pkg creation requires a macOS host (pkgbuild/productbuild are not cross-host).");
        }

        var bundle = context.Configuration;
        var item = context.Item;
        var logger = context.Logger;

        var identifier = settings.Identifier ?? bundle.Identifier;
        if (identifier.Trim().Length == 0)
        {
            throw new ArgumentException("The .pkg identifier must not be empty.");
        }
        var version = settings.Version ?? bundle.Version;
        if (version.Trim().Length == 0)
        {
            throw new ArgumentException("The .pkg version must not be empty.");
        }
        var installLocation = settings.InstallLocation;
        if (!installLocation.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The .pkg install location must be an absolute path: '{installLocation}'.");
        }

        MacPkgSigning.Validate(settings.Signing);
        var scriptsDirectory = settings.ScriptsDirectory;
        if (scriptsDirectory is { Length: > 0 } &&
            !Directory.Exists(Path.GetFullPath(scriptsDirectory)))
        {
            throw new DirectoryNotFoundException(
                $"The .pkg scripts directory does not exist: {Path.GetFullPath(scriptsDirectory)}");
        }

        var workDirectory = context.WorkDirectory;
        var stageDirectory = Path.Combine(workDirectory, "pkg-root");
        var packageName = MacAppBundleBackend.SanitizeFileName(bundle.ProductName) + ".pkg";
        var outputPath = Path.Combine(item.OutputDirectory, packageName);
        MacAppSigning.TemporaryKeychain? keychain = null;
        var identity = "";

        try
        {
            if (MacPkgSigning.Configured(settings.Signing))
            {
                (identity, keychain) = await MacPkgSigning.ResolveIdentityAsync(
                    context, settings.Signing, cancellationToken);
            }
            Directory.CreateDirectory(stageDirectory);

            var payloadItems = settings.PayloadItems ?? Array.Empty<MacPkgPayloadItem>();
            var applicationName = MacAppBundleBackend.SanitizeFileName(bundle.ProductName) + ".app";
            var appPath = Path.Combine(
                bundle.OutputDirectory, item.Target.RuntimeIdentifier, "app", applicationName);
            if (Directory.Exists(appPath))
            {
                CopyTree(appPath, Path.Combine(stageDirectory, applicationName), logger);
            }
            else if (payloadItems.Count == 0)
            {
                throw new DirectoryNotFoundException(
                    "The .pkg backend expects the intermediate .app at " + appPath +
                    " or an explicit payload (the planner adds the .app automatically " +
                    "when 'pkg' is requested).");
            }
            else
            {
                logger.Log(
                    BundleLogLevel.Information,
                    "Intermediate .app not found; packaging the explicit payload only.");
            }

            foreach (var payload in payloadItems)
            {
                var source = Path.GetFullPath(payload.Source);
                if (!File.Exists(source) && !Directory.Exists(source))
                {
                    throw new FileNotFoundException(
                        $"The .pkg payload source does not exist: {source}", source);
                }
                var destination = Path.Combine(
                    stageDirectory,
                    (payload.Destination ?? Path.GetFileName(source.TrimEnd('/', '\\')))
                        .TrimStart('/'));
                if (Directory.Exists(source))
                {
                    CopyTree(source, destination, logger);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: true);
                }
            }

            var licenseFile = bundle.LicenseFile;
            var useDistribution = settings.Title != null
                || settings.WelcomeFile != null
                || settings.ConclusionFile != null
                || licenseFile != null
                || settings.Domain != MacPkgInstallDomain.System;

            var packagePath = useDistribution
                ? Path.Combine(workDirectory, "component.pkg")
                : outputPath;

            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            var pkgbuildArguments = new List<string>
            {
                "--root", stageDirectory,
                "--install-location", installLocation,
                "--identifier", identifier,
                "--version", version,
                "--ownership", "recommended"
            };
            if (scriptsDirectory is { Length: > 0 })
            {
                pkgbuildArguments.Add("--scripts");
                pkgbuildArguments.Add(Path.GetFullPath(scriptsDirectory));
            }
            // Component packages are signed inside pkgbuild; distribution packages are
            // signed by productsign after productbuild emits the unsigned product archive.
            if (identity.Length > 0 && !useDistribution)
            {
                pkgbuildArguments.AddRange(MacPkgSigning.PkgbuildSignArguments(identity, keychain));
            }
            pkgbuildArguments.Add(packagePath);
            await MacPkgProcessRunner.RunAsync(
                "pkgbuild", pkgbuildArguments, workDirectory, cancellationToken);

            if (useDistribution)
            {
                var distributionPath = Path.Combine(workDirectory, "distribution.xml");
                var resourcesDirectory = Path.Combine(workDirectory, "pkg-resources");
                WriteDistributionXml(
                    distributionPath,
                    resourcesDirectory,
                    bundle,
                    identifier,
                    version,
                    installLocation,
                    Path.GetFileName(packagePath),
                    ArchitectureName(item.Target.Architecture),
                    logger,
                    settings);
                await MacPkgProcessRunner.RunAsync(
                    "productbuild",
                    ["--distribution", distributionPath,
                     "--package-path", workDirectory,
                     "--resources", resourcesDirectory,
                     outputPath],
                    workDirectory, cancellationToken);
                if (identity.Length > 0)
                {
                    await MacPkgSigning.SignProductAsync(
                        context, outputPath, identity, keychain, cancellationToken);
                }
            }

            if (settings.Signing.Notarize)
            {
                await MacPkgSigning.NotarizeAsync(
                    context, outputPath, settings.Signing, cancellationToken);
            }
            if (keychain is not null)
            {
                await keychain.DisposeAsync(workDirectory, cancellationToken);
            }

            return [new BundleArtifact(PackageFormat.Pkg, item.Target.RuntimeIdentifier, outputPath)];
        }
        catch
        {
            if (keychain is not null)
            {
                await keychain.DisposeAsync(workDirectory, CancellationToken.None);
            }
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            throw;
        }
    }

    // Writes distribution.xml plus a resources directory holding the page files.
    // Returns void for clarity; validation failures throw ArgumentException.
    internal static void WriteDistributionXml(
        string distributionPath,
        string resourcesDirectory,
        BundleConfiguration bundle,
        string identifier,
        string version,
        string installLocation,
        string componentFileName,
        string architecture,
        IBundleLogger logger,
        MacPkgBundleConfiguration? settings = null)
    {
        settings ??= new MacPkgBundleConfiguration();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<installer-gui-script minSpecVersion=\"1\">");
        sb.AppendLine($"    <title>{XmlEscape(settings.Title ?? bundle.ProductName)}</title>");

        var resources = new List<(string element, string file)>();
        AddPage(resources, "welcome", settings.WelcomeFile);
        AddPage(resources, "conclusion", settings.ConclusionFile);
        AddPage(resources, "license", bundle.LicenseFile);
        foreach (var (element, source) in resources)
        {
            var full = Path.GetFullPath(source);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException(
                    $"The .pkg {element} page file does not exist: {full}", full);
            }
            var name = element + Path.GetExtension(source);
            Directory.CreateDirectory(resourcesDirectory);
            File.Copy(full, Path.Combine(resourcesDirectory, name), overwrite: true);
            sb.AppendLine($"    <{element} file=\"{XmlEscape(name)}\" mime-type=\"{MimeType(source)}\"/>");
        }

        if (settings.Domain == MacPkgInstallDomain.CurrentUserHome)
        {
            sb.AppendLine(
                "    <domains enable_anywhere=\"false\" enable_currentUserHome=\"true\" " +
                "enable_localSystem=\"false\"/>");
        }

        // The installer gates on the declared architectures: without
        // hostArchitectures an Apple Silicon host wrongly demands Rosetta 2.
        sb.AppendLine(
            $"    <options customize=\"never\" allow-external-scripts=\"no\" " +
            $"hostArchitectures=\"{XmlEscape(architecture)}\"/>");

        var choiceId = identifier + ".choice";
        sb.AppendLine("    <choices-outline>");
        sb.AppendLine($"        <line choice=\"{XmlEscape(choiceId)}\"/>");
        sb.AppendLine("    </choices-outline>");
        sb.AppendLine($"    <choice id=\"{XmlEscape(choiceId)}\" title=\"{XmlEscape(bundle.ProductName)}\">");
        sb.AppendLine($"        <pkg-ref id=\"{XmlEscape(identifier)}\"/>");
        sb.AppendLine("    </choice>");
        sb.AppendLine(
            $"    <pkg-ref id=\"{XmlEscape(identifier)}\" version=\"{XmlEscape(version)}\" " +
            $"install-location=\"{XmlEscape(installLocation)}\">{XmlEscape(componentFileName)}</pkg-ref>");
        sb.AppendLine("</installer-gui-script>");
        File.WriteAllText(distributionPath, sb.ToString());
    }

    private static void AddPage(List<(string element, string file)> pages, string element, string? file)
    {
        if (file != null)
        {
            pages.Add((element, file));
        }
    }

    private static string MimeType(string file) =>
        Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html",
            ".rtf" or ".rtfd" => "text/richtext",
            _ => "text/plain",
        };

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static string ArchitectureName(CpuArchitecture architecture) => architecture switch
    {
        CpuArchitecture.Arm64 => "arm64",
        CpuArchitecture.X64 => "x86_64",
        _ => "i386",
    };

    private static void CopyTree(string source, string destination, IBundleLogger log)
    {
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
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)), log);
        }
    }
}
