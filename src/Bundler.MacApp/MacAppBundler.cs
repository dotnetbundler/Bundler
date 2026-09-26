using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.MacApp;

public sealed class MacAppBundler
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

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
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
        if (bundle.FileAssociations.Count > 0)
        {
            throw new NotSupportedException(".app file associations arrive with MAC-APP-2.");
        }
        if (bundle.UrlProtocols.Count > 0)
        {
            throw new NotSupportedException(".app URL schemes arrive with MAC-APP-2.");
        }
        if (bundle.LicenseFile is not null)
        {
            throw new NotSupportedException(
                ".app has no license-file payload contract; managed installation arrives with MAC-PKG.");
        }
        if (bundle.Targets.Any(target => target.SigningFiles.Count > 0))
        {
            throw new NotSupportedException("Payload signing arrives with MAC-APP-3.");
        }

        // Fail on invalid metadata, mappings, and icons before touching the file system.
        MacAppMetadata.Resolve(bundle, _configuration);
        MacAppBundleBackend.ResolveContentsMappings(_configuration);
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
                !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(".app icons accept .icns or .png files only.");
        }

        return await new BundlePipeline(
            [new MacAppBundleBackend(_configuration)], _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}
