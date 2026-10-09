using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.MacApp;

public sealed class MacAppBundler : IFormatBundler
{
    private readonly MacAppBundleConfiguration _configuration;
    private readonly MacAppBundlerOptions _options;

    public MacAppBundler(
        MacAppBundleConfiguration? configuration = null,
        MacAppBundlerOptions? options = null)
    {
        _configuration = configuration ?? new MacAppBundleConfiguration();
        _options = options ?? new MacAppBundlerOptions();
    }

    public void Validate(BundleConfiguration bundle)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.App))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.App);
            throw new NotSupportedException(
                $"DotNet.Bundler.MacApp accepts App targets only; '{unsupported}' requires another backend package.");
        }
        foreach (var target in bundle.Targets)
        {
            if (!string.IsNullOrWhiteSpace(target.MainExecutable) &&
                (target.MainExecutable!.Contains('/') || target.MainExecutable.Contains('\\')))
            {
                throw new ArgumentException(
                    "The .app main executable must be a file directly inside the input directory; " +
                    $"got '{target.MainExecutable}'.");
            }
        }
        if (bundle.LicenseFile is not null)
        {
            throw new NotSupportedException(
                ".app has no license-file payload contract; managed installation arrives with MAC-PKG.");
        }
        if (bundle.Targets.Any(target => target.SigningFiles.Count > 0))
        {
            throw new NotSupportedException(
                ".app signing covers every Mach-O inside the bundle automatically; " +
                "per-file SigningFiles has no macOS meaning. Use MacAppBundleConfiguration.Signing.");
        }
        MacAppSigning.Validate(_configuration.Signing);

        // Fail on invalid metadata, mappings, integration config, and icons before touching
        // the file system.
        MacAppMetadata.Resolve(bundle, _configuration);
        MacAppBundleBackend.ResolveContentsMappings(_configuration);
        MacAppDesktopIntegration.ResolveDocumentTypes(bundle, _configuration);
        MacAppDesktopIntegration.ResolveUrlTypes(bundle, _configuration);
        if (_configuration.ExceptionDomain is { } domain && domain.Trim().Length == 0)
        {
            throw new ArgumentException("ExceptionDomain must not be empty.");
        }
        if (_configuration.InfoPlistFile is not null && _configuration.InfoPlistXml is not null)
        {
            throw new ArgumentException("InfoPlistFile and InfoPlistXml are mutually exclusive.");
        }
        if (_configuration.InfoPlistFile is { } plistFile && !File.Exists(Path.GetFullPath(plistFile)))
        {
            throw new FileNotFoundException("The caller Info.plist does not exist.", plistFile);
        }
        foreach (var framework in _configuration.Frameworks)
        {
            var name = Path.GetFileName(framework);
            if (!name.EndsWith(".framework", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Contents/Frameworks accepts .framework bundles and .dylib files only: {framework}");
            }
        }
        if (bundle.Icons.Any(icon =>
                !icon.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) &&
                !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !icon.EndsWith(".car", StringComparison.OrdinalIgnoreCase) &&
                !icon.EndsWith(".icon", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                ".app icons accept .icns/.png bitmaps, an Icon Composer .icon directory, or a .car file.");
        }
        if (bundle.Icons.Count(icon => icon.EndsWith(".car", StringComparison.OrdinalIgnoreCase)) > 1 ||
            bundle.Icons.Count(icon => icon.EndsWith(".icon", StringComparison.OrdinalIgnoreCase)) > 1)
        {
            throw new ArgumentException(".app icons accept a single .car or .icon input.");
        }
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        Validate(bundle);
        return await new BundlePipeline(
            [new MacAppBundleBackend(_configuration)], _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}
