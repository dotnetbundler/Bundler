using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;
using DotNet.Bundler.MacApp;

namespace DotNet.Bundler.MacPkg;

public sealed class MacPkgBundler
{
    private readonly MacPkgBundleConfiguration _pkgConfiguration;
    private readonly MacAppBundleConfiguration _appConfiguration;
    private readonly MacPkgBundlerOptions _options;

    public MacPkgBundler(
        MacPkgBundleConfiguration? pkgConfiguration = null,
        MacAppBundleConfiguration? appConfiguration = null,
        MacPkgBundlerOptions? options = null)
    {
        _pkgConfiguration = pkgConfiguration ?? new MacPkgBundleConfiguration();
        _appConfiguration = appConfiguration ?? new MacAppBundleConfiguration();
        _options = options ?? new MacPkgBundlerOptions();
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.Pkg))
        {
            var unsupported = bundle.Targets
                .SelectMany(target => target.Formats)
                .First(format => format != PackageFormat.Pkg);
            throw new NotSupportedException(
                $"DotNet.Bundler.MacPkg accepts Pkg targets only; '{unsupported}' requires another backend package.");
        }
        var isMacOs = MacPkgBundleBackend.HostCheck?.Invoke() ?? RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (!isMacOs)
        {
            throw new NotSupportedException(
                ".pkg creation requires a macOS host (pkgbuild/productbuild are not cross-host).");
        }
        return await new BundlePipeline(
            [
                new MacAppBundleBackend(_appConfiguration),
                new MacPkgBundleBackend(_pkgConfiguration)
            ],
            _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}
