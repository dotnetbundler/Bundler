using System.Runtime.InteropServices;
using DotNet.Bundler;
using DotNet.Bundler.Core;

namespace DotNet.Bundler.Wix;

public sealed class WixBundler
{
    private readonly WixBundleConfiguration _settings;
    private readonly WixBundlerOptions _options;

    public WixBundler(WixBundleConfiguration? configuration = null, WixBundlerOptions? options = null)
    {
        _settings = configuration ?? new WixBundleConfiguration();
        _options = options ?? new WixBundlerOptions();
    }

    public async Task<IReadOnlyList<BundleArtifact>> BuildAsync(
        BundleConfiguration bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle is null)
        {
            throw new ArgumentNullException(nameof(bundle));
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WiX 3.14.1 MSI builds require a Windows host.");
        }
        if (bundle.Targets.SelectMany(target => target.Formats).Any(format => format != PackageFormat.Msi))
        {
            throw new NotSupportedException("DotNet.Bundler.Wix accepts MSI targets only.");
        }
        if (bundle.Targets.Any(target => target.SigningFiles.Count > 0))
        {
            throw new NotSupportedException("MSI payload signing is planned for WIN-MSI-3.");
        }
        if (bundle.FileAssociations.Count > 0 || bundle.UrlProtocols.Count > 0)
        {
            throw new NotSupportedException("MSI file associations and URL protocols are planned for WIN-MSI-2.");
        }
        if (!string.IsNullOrWhiteSpace(bundle.LicenseFile))
        {
            throw new NotSupportedException("MSI license UI is planned for WIN-MSI-3.");
        }
        if (_settings.InstallScope != WixInstallScope.CurrentUser)
        {
            throw new NotSupportedException("Per-machine MSI packages are planned for WIN-MSI-2.");
        }
        if (_settings.Codepage <= 0 || _settings.Codepage is 65000 or 65001)
        {
            throw new ArgumentException("WIN-MSI-1 requires a supported Windows ANSI MSI code page; UTF-7/UTF-8 are not supported by WiX 3 MSI UI.");
        }

        foreach (var target in bundle.Targets)
        {
            WixIdentity.Create(bundle.Identifier, bundle.Version, target.RuntimeIdentifier,
                _settings.InstallScope, _settings.UpgradeCode);
        }
        var toolset = await WixToolsetResolver.ResolveAsync(
            _options.ResolveToolCacheDirectory(), _options.ToolsetArchivePath, cancellationToken);
        return await new BundlePipeline(
            [new WixBundleBackend(toolset, _settings)], _options.Logger).BuildAsync(bundle, cancellationToken);
    }
}
