using System.Runtime.InteropServices;
using DotNet.Bundler;
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

        var workDirectory = context.WorkDirectory;
        var stageDirectory = Path.Combine(workDirectory, "pkg-root");
        var packageName = MacAppBundleBackend.SanitizeFileName(bundle.ProductName) + ".pkg";
        var outputPath = Path.Combine(item.OutputDirectory, packageName);

        try
        {
            Directory.CreateDirectory(stageDirectory);

            var payloadItems = settings.PayloadItems ?? Array.Empty<MacPkgPayloadItem>();
            var applicationName = MacAppBundleBackend.SanitizeFileName(bundle.ProductName) + ".app";
            var appPath = Path.Combine(
                bundle.OutputDirectory, item.Target.RuntimeIdentifier, "app", applicationName);
            if (Directory.Exists(appPath))
            {
                CopyTree(appPath, Path.Combine(stageDirectory, applicationName));
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
                    CopyTree(source, destination);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: true);
                }
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            await MacPkgProcessRunner.RunAsync(
                "pkgbuild",
                ["--root", stageDirectory,
                 "--install-location", installLocation,
                 "--identifier", identifier,
                 "--version", version,
                 "--ownership", "recommended",
                 outputPath],
                workDirectory, cancellationToken);

            return [new BundleArtifact(PackageFormat.Pkg, item.Target.RuntimeIdentifier, outputPath)];
        }
        catch
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
            throw;
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
