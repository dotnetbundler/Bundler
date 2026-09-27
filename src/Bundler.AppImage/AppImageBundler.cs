using DotNet.Bundler.Core;

namespace DotNet.Bundler.AppImage;

/// <summary>
/// Public entry point for Linux .AppImage packaging. The backend assembles the
/// AppDir in managed code and invokes the bundled appimagetool binary, so a
/// build requires a Linux host (any x86_64/aarch64 Linux works; container and
/// CI hosts without FUSE are supported via the extract-and-run path).
/// </summary>
public sealed class AppImageBundler
{
    private readonly AppImageBundleConfiguration _configuration;
    private readonly AppImageBundlerOptions _options;

    public AppImageBundler(
        AppImageBundleConfiguration? configuration = null,
        AppImageBundlerOptions? options = null)
    {
        _configuration = configuration ?? new AppImageBundleConfiguration();
        _options = options ?? new AppImageBundlerOptions();
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.AppImage))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.AppImage);
            throw new NotSupportedException(
                $"DotNet.Bundler.AppImage accepts AppImage targets only; '{unsupported}' requires another backend package.");
        }
        return await new BundlePipeline(
            [new AppImageBundleBackend(_configuration, _options)],
            _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}
