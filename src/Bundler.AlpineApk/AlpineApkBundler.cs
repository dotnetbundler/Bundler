using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.AlpineApk;

/// <summary>
/// Public entry point for Alpine .apk packaging. The backend writes the
/// three-segment gzip container itself, so a build works on any host
/// (Linux, Windows, macOS CI). Only linux-musl-* targets accept the format.
/// </summary>
public sealed class AlpineApkBundler : IFormatBundler
{
    private readonly AlpineApkBundleConfiguration _configuration;
    private readonly AlpineApkBundlerOptions _options;

    public AlpineApkBundler(
        AlpineApkBundleConfiguration? configuration = null,
        AlpineApkBundlerOptions? options = null)
    {
        _configuration = configuration ?? new AlpineApkBundleConfiguration();
        _options = options ?? new AlpineApkBundlerOptions();
    }

    public void Validate(BundleConfiguration bundle)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.AlpineApk))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.AlpineApk);
            throw new NotSupportedException(
                $"DotNet.Bundler.AlpineApk accepts AlpineApk targets only; '{unsupported}' requires another backend package.");
        }
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        Validate(bundle);
        return await new BundlePipeline(
            [new AlpineApkBundleBackend(_configuration)],
            _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}
