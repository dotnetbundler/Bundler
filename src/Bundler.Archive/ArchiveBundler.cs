using DotNet.Bundler.Core;

namespace DotNet.Bundler.Archive;

/// <summary>
/// Public entry point for .zip/.tar.gz archive bundling. Both formats are
/// written entirely in managed code — no external tools — so any build host
/// OS can produce them for any target OS.
/// </summary>
public sealed class ArchiveBundler : IFormatBundler
{
    private readonly ArchiveBundleConfiguration _configuration;
    private readonly ArchiveBundlerOptions _options;

    public ArchiveBundler(
        ArchiveBundleConfiguration? configuration = null,
        ArchiveBundlerOptions? options = null)
    {
        _configuration = configuration ?? new ArchiveBundleConfiguration();
        _options = options ?? new ArchiveBundlerOptions();
    }

    public void Validate(BundleConfiguration bundle)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        // PackageFormat.Nsis == 0：不能用 FirstOrDefault(...)+ != default 判定。
        if (bundle.Targets.SelectMany(target => target.Formats).Any(
            format => format is not (PackageFormat.Zip or PackageFormat.TarGz)))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format is not (PackageFormat.Zip or PackageFormat.TarGz));
            throw new NotSupportedException(
                $"DotNet.Bundler.Archive accepts Zip/TarGz targets only; '{unsupported}' requires another backend package.");
        }
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        Validate(bundle);
        var backends = new List<IBundleBackend>();
        foreach (var os in new[]
        {
            DesktopOperatingSystem.Windows,
            DesktopOperatingSystem.MacOS,
            DesktopOperatingSystem.Linux,
            DesktopOperatingSystem.LinuxMusl
        })
        {
            backends.Add(new ArchiveBundleBackend(_configuration, _options, os, PackageFormat.Zip));
            backends.Add(new ArchiveBundleBackend(_configuration, _options, os, PackageFormat.TarGz));
        }
        return await new BundlePipeline(backends, _options.Logger)
            .BuildAsync(bundle, cancellationToken);
    }
}

