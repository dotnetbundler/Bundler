using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Deb;

/// <summary>
/// Public entry point for Linux .deb packaging. The backend writes the ar/tar/gzip
/// container itself, so a build works on any host (Linux, Windows, macOS CI).
/// </summary>
public sealed class DebBundler
{
    private readonly DebBundleConfiguration _configuration;
    private readonly DebBundlerOptions _options;

    public DebBundler(
        DebBundleConfiguration? configuration = null,
        DebBundlerOptions? options = null)
    {
        _configuration = configuration ?? new DebBundleConfiguration();
        _options = options ?? new DebBundlerOptions();
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.Deb))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.Deb);
            throw new NotSupportedException(
                $"DotNet.Bundler.Deb accepts Deb targets only; '{unsupported}' requires another backend package.");
        }
        return await new BundlePipeline(
            [new DebBundleBackend(_configuration)],
            _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}
