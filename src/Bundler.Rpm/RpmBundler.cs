using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Rpm;

/// <summary>
/// Public entry point for Linux .rpm packaging. The backend writes the
/// lead/header/cpio container itself, so a build works on any host
/// (Linux, Windows, macOS CI).
/// </summary>
public sealed class RpmBundler
{
    private readonly RpmBundleConfiguration _configuration;
    private readonly RpmBundlerOptions _options;

    public RpmBundler(
        RpmBundleConfiguration? configuration = null,
        RpmBundlerOptions? options = null)
    {
        _configuration = configuration ?? new RpmBundleConfiguration();
        _options = options ?? new RpmBundlerOptions();
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.Rpm))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.Rpm);
            throw new NotSupportedException(
                $"DotNet.Bundler.Rpm accepts Rpm targets only; '{unsupported}' requires another backend package.");
        }
        return await new BundlePipeline(
            [new RpmBundleBackend(_configuration)],
            _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}
